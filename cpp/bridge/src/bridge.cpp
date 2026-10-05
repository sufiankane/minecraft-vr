// 5.12 native bridge: a read-only mapping of the dossier 5.6 region
// (`Local\cubeglass.v1.state`) plus the command channel in its reserved header
// window (ADR-0010/R43).
//
// Trust boundary (TD-052): the writer is a same-user process and is not
// authenticated. The reader applies the cheap hardening:
//   - it maps exactly the bytes it needs (`kMinimumRegionSize` as the Windows
//     view length, the exact `fstat` size check on POSIX) and refuses a POSIX
//     region larger than `kMaximumRegionSize`, so a hostile or oversized
//     region cannot widen the mapped surface;
//   - it re-validates `magic`/`abi_version`/`header_size`/`writer_pid` on
//     every read (TD-003), not only at open, mapping a mismatch to
//     `CG_ERR_NOT_READY` (unpublished header or zero writer pid) or
//     `CG_ERR_UNSUPPORTED` (foreign ABI/header size);
//   - a non-zero `writer_pid` that differs from this process is advisory
//     only: the normal writer is a different process, so a mismatch never
//     rejects a region. A nonce/HMAC identity handshake remains the
//     documented residual.
// The payload side never trusts a count: `header_size` must equal
// `kHeaderSize` exactly and each slot is copied as the fixed `sizeof` payload.
//
// `cg_bridge_open` allocates one small handle with `new` (cold path, once per
// process lifetime, explicitly not a hot path) and `cg_bridge_close` frees it;
// after `cg_bridge_close` the handle is invalid and a null handle is ignored.
// The read path allocates nothing. The handle owns both views: the read-only
// payload view and a small writable header view used only by
// `cg_bridge_send_command` (TD-006), so a command writes through the held view
// and never reopens the region by name (it keeps working after the name
// disappears).
//
// Payload copies go through relaxed `std::atomic_ref<std::uint64_t>` word
// accesses with a release/acquire fence around the seqlock counters, the same
// construction as the S5 `PoseSlot`, so every shared byte access is an atomic
// operation and the concurrent tests are TSan-clean by construction. The
// CXX-19 object-model assumption behind that construction is documented in
// `shm_layout.hpp`; the bridge target is compiled with `-fno-strict-aliasing`
// where the compiler supports it.
//
// Staleness applies to both slots through the same 250 ms heartbeat rule. The
// head read keeps the sample and reports `CG_TRACK_LOST`; the hand read has no
// per-hand tracking state, so a stale hand slot reports `CG_ERR_NOT_READY` and
// no frame instead of emitting stale joints.

#include "cg/bridge/shm_layout.hpp"

#include <array>
#include <atomic>
#include <chrono>
#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <memory>
#include <new>
#include <string_view>
#include <thread>

#include "cg_unity_bridge.h"

#if defined(_WIN32)
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>
#elif defined(__unix__) || defined(__APPLE__)
#include <cerrno>
#include <fcntl.h>
#include <sys/mman.h>
#include <sys/stat.h>
#include <unistd.h>
#endif

namespace cg::bridge {
namespace {

/// Bounded retry budget of the 5.6 reader protocol.
constexpr int kMaxReadAttempts = 64;

/// Bounded wait for a writer that is mid-initialisation. The writer publishes
/// `magic` last with release ordering (see `shm_layout.hpp`), so a reader that
/// acquires `magic` sees the complete header; the retry only helps a reader
/// that opened before the writer started initialising (or one racing a writer
/// that died mid-init). It is belt-and-braces, not the synchronisation
/// primitive.
constexpr auto kHeaderInitRetryWindow = std::chrono::milliseconds(50);

/// Fixed capacity of the thread-local header diagnostic (M-6 cold path).
constexpr std::size_t kHeaderErrorCapacity = 160;

/// POSIX has one flat shared-memory namespace; the Windows `Local\` prefix
/// maps to the leading '/' that `shm_open` requires.
#if defined(__unix__) || defined(__APPLE__)
constexpr char kPosixStateName[] = "/cubeglass.v1.state";
#endif

#if defined(_WIN32)
// A wide string literal is the natural form for the Win32 name; the value is
// asserted to mirror the single layout name below.
// NOLINTNEXTLINE(cppcoreguidelines-avoid-c-arrays)
constexpr wchar_t kStateNameW[] = L"Local\\cubeglass.v1.state";
// The wide literal decays into the view on purpose here (a constexpr length
// comparison, not a runtime API call).
// NOLINTNEXTLINE(cppcoreguidelines-pro-bounds-array-to-pointer-decay)
static_assert(std::wstring_view{kStateNameW}.size() == std::string_view{cg::bridge::kStateName}.size(),
              "the wide name mirrors kStateName");
#endif

struct BridgeHandle {
#if defined(_WIN32)
    HANDLE mapping{nullptr};
#elif defined(__unix__) || defined(__APPLE__)
    /// The exact region size reported by `fstat` (POSIX); diagnostics only.
    std::size_t size{0};
#endif
    std::uint8_t *base{nullptr};
    /// Small writable view of the 64-byte header, owned by the handle and used
    /// only by `cg_bridge_send_command` (TD-006).
    std::uint8_t *command_view{nullptr};
};

std::int64_t now_ns() noexcept {
    const auto elapsed = std::chrono::steady_clock::now().time_since_epoch();
    return std::chrono::duration_cast<std::chrono::nanoseconds>(elapsed).count();
}

/// Thread-local diagnostic set by the last rejected open on this thread and
/// exposed through `LastHeaderError()`. Cold path only (open), fixed storage,
/// no allocation. The buffer is mutable by design: `LastHeaderError()` hands
/// out a view of it and the next failed validation on the same thread replaces
/// it.
// NOLINTNEXTLINE(cppcoreguidelines-avoid-non-const-global-variables)
thread_local std::array<char, kHeaderErrorCapacity> g_header_error{};

void clear_header_error() noexcept { g_header_error.front() = '\0'; }

/// The outcome of validating a mapped region header.
enum class HeaderState : std::uint8_t {
    kValid,
    kUninitialised, // magic absent: a fresh or dead-mid-init region
    kMismatchedAbi,
    kMismatchedSize,
};

/// Validates the header by acquiring `magic` first. The writer publishes
/// `magic` last with a release store, so observing `kShmMagic` happens-after
/// every other field store; the remaining fields are then read relaxed, with
/// no torn-open window (I-3). A mismatch records `LastHeaderError()`.
///
/// Re-validation runs on every read (TD-003) and before every command, not
/// only at open. A valid magic with a zero `writer_pid` violates the
/// publication protocol (`writer_pid` is stored before `magic`) and is treated
/// as uninitialised (TD-052). A non-zero pid different from this process is
/// normal (the writer is a separate service) and never rejects the region.
HeaderState classify_header(const BridgeHandle &handle) noexcept {
    // Typed view over the mapped header: the byte layout is the shm_layout
    // contract asserted with static_asserts there.
    // NOLINTNEXTLINE(cppcoreguidelines-pro-type-reinterpret-cast)
    const auto *header = reinterpret_cast<const ShmHeader *>(handle.base);
    const std::uint64_t magic = std::atomic_ref<const std::uint64_t>(header->magic).load(std::memory_order_acquire);
    if (magic != kShmMagic) {
        return HeaderState::kUninitialised;
    }
    const std::uint64_t writer_pid =
        std::atomic_ref<const std::uint64_t>(header->writer_pid).load(std::memory_order_relaxed);
    if (writer_pid == 0) {
        return HeaderState::kUninitialised;
    }
    const std::uint32_t abi = std::atomic_ref<const std::uint32_t>(header->abi_version).load(std::memory_order_relaxed);
    if (abi != kShmAbiVersion) {
        // NOLINTNEXTLINE(cppcoreguidelines-pro-type-vararg, cppcoreguidelines-pro-bounds-array-to-pointer-decay)
        std::snprintf(g_header_error.data(), g_header_error.size(),
                      "cg_bridge: shared-memory abi_version %u is not the expected %u", abi, kShmAbiVersion);
        return HeaderState::kMismatchedAbi;
    }
    const std::uint32_t size =
        std::atomic_ref<const std::uint32_t>(header->header_size).load(std::memory_order_relaxed);
    if (size != kHeaderSize) {
        // NOLINTNEXTLINE(cppcoreguidelines-pro-type-vararg, cppcoreguidelines-pro-bounds-array-to-pointer-decay)
        std::snprintf(g_header_error.data(), g_header_error.size(),
                      "cg_bridge: shared-memory header_size %u is not the expected %u", size, kHeaderSize);
        return HeaderState::kMismatchedSize;
    }
    return HeaderState::kValid;
}

/// Maps a header classification onto the read/command status vocabulary
/// (TD-003): an unpublished or protocol-violating header is soft NotReady,
/// a foreign ABI/header is Unsupported.
cg_status status_for_header(const BridgeHandle &handle) noexcept {
    switch (classify_header(handle)) {
    case HeaderState::kValid:
        return CG_OK;
    case HeaderState::kUninitialised:
        return CG_ERR_NOT_READY;
    case HeaderState::kMismatchedAbi:
    case HeaderState::kMismatchedSize:
        break;
    }
    return CG_ERR_UNSUPPORTED;
}

/// Copies a seqlock payload word-wise through relaxed atomic accesses. The
/// acquire fence orders the payload loads before the caller's `seq_b` load;
/// the payloads sit at 8-byte-aligned slot offsets, so the words are aligned.
///
/// CXX-19 object-model deviation: the payload objects have effective type
/// `cg_head_sample`/`cg_hand_frame`, and accessing their storage through
/// `std::uint64_t` lvalues (including through `atomic_ref`) is formally
/// outside the C++ object model. Both sides use the same layout and every
/// access is 8-byte aligned; the bridge target is compiled with
/// `-fno-strict-aliasing` (GCC/Clang) and MSVC does not exploit the aliasing
/// here, so the pattern is the standard cross-process seqlock construction.
/// See `shm_layout.hpp` for the maintained assumption statement.
template <typename Payload> Payload copy_payload(Payload *shared) noexcept {
    // NOLINTBEGIN(cppcoreguidelines-avoid-c-arrays, cppcoreguidelines-pro-bounds-array-to-pointer-decay,
    // cppcoreguidelines-pro-type-reinterpret-cast, cppcoreguidelines-pro-bounds-pointer-arithmetic,
    // cppcoreguidelines-pro-bounds-constant-array-index)
    constexpr std::size_t kWords = sizeof(Payload) / sizeof(std::uint64_t);
    static_assert(sizeof(Payload) % sizeof(std::uint64_t) == 0, "payload must be 8-byte sized");
    std::uint64_t words[kWords];
    auto *source = reinterpret_cast<std::uint64_t *>(shared);
    for (std::size_t word = 0; word < kWords; ++word) {
        words[word] = std::atomic_ref<std::uint64_t>(source[word]).load(std::memory_order_relaxed);
    }
    std::atomic_thread_fence(std::memory_order_acquire);
    Payload result{};
    std::memcpy(&result, words, sizeof(Payload));
    return result;
    // NOLINTEND(cppcoreguidelines-avoid-c-arrays, cppcoreguidelines-pro-bounds-array-to-pointer-decay,
    // cppcoreguidelines-pro-type-reinterpret-cast, cppcoreguidelines-pro-bounds-pointer-arithmetic,
    // cppcoreguidelines-pro-bounds-constant-array-index)
}

cg_status read_head(const BridgeHandle &handle, cg_head_sample *out) noexcept {
    const cg_status header_status = status_for_header(handle);
    if (header_status != CG_OK) {
        return header_status;
    }
    // Typed slot view at the fixed layout offset; bounded by the region-size
    // static asserts in shm_layout.hpp.
    // NOLINTNEXTLINE(cppcoreguidelines-pro-type-reinterpret-cast, cppcoreguidelines-pro-bounds-pointer-arithmetic)
    auto *slot = reinterpret_cast<HeadSlot *>(handle.base + kHeadSlotOffset);
    for (int attempt = 0; attempt < kMaxReadAttempts; ++attempt) {
        const std::uint64_t seq_a = slot->seq_a.load(std::memory_order_acquire);
        if (seq_a == 0) {
            return CG_ERR_NOT_READY; // the writer has never published
        }
        if ((seq_a & 1U) != 0U) {
            continue; // the writer is mid-publish
        }
        const cg_head_sample sample = copy_payload(&slot->sample);
        if (slot->seq_b.load(std::memory_order_relaxed) != seq_a) {
            continue; // the payload changed under the copy
        }
        // The writer stores seq_b after the payload, so a publish that starts
        // between the seq_a read and the copy can still leave the previous
        // even value in seq_b and pass the check above (ABA). seq_a only ever
        // moves forward by two per publish, so re-reading it proves that no
        // publish was in flight while the payload was copied.
        if (slot->seq_a.load(std::memory_order_acquire) != seq_a) {
            continue;
        }
        *out = sample;
        // NOLINTNEXTLINE(cppcoreguidelines-pro-type-reinterpret-cast)
        auto *header = reinterpret_cast<ShmHeader *>(handle.base);
        const std::int64_t heartbeat =
            std::atomic_ref<std::int64_t>(header->heartbeat_ns).load(std::memory_order_acquire);
        if (is_stale(now_ns(), heartbeat)) {
            out->state = CG_TRACK_LOST;
        }
        return CG_OK;
    }
    return CG_ERR_TIMEOUT;
}

cg_status read_hands(const BridgeHandle &handle, cg_hand_frame *out) noexcept {
    const cg_status header_status = status_for_header(handle);
    if (header_status != CG_OK) {
        return header_status;
    }
    // NOLINTNEXTLINE(cppcoreguidelines-pro-type-reinterpret-cast, cppcoreguidelines-pro-bounds-pointer-arithmetic)
    auto *slot = reinterpret_cast<HandSlot *>(handle.base + kHandSlotOffset);
    for (int attempt = 0; attempt < kMaxReadAttempts; ++attempt) {
        const std::uint64_t seq_a = slot->seq_a.load(std::memory_order_acquire);
        if (seq_a == 0) {
            return CG_ERR_NOT_READY; // the writer has never published
        }
        if ((seq_a & 1U) != 0U) {
            continue;
        }
        const cg_hand_frame frame = copy_payload(&slot->frame);
        if (slot->seq_b.load(std::memory_order_relaxed) != seq_a) {
            continue;
        }
        if (slot->seq_a.load(std::memory_order_acquire) != seq_a) {
            continue; // same ABA guard as the head slot
        }
        // NOLINTNEXTLINE(cppcoreguidelines-pro-type-reinterpret-cast)
        auto *header = reinterpret_cast<ShmHeader *>(handle.base);
        const std::int64_t heartbeat =
            std::atomic_ref<std::int64_t>(header->heartbeat_ns).load(std::memory_order_acquire);
        if (is_stale(now_ns(), heartbeat)) {
            return CG_ERR_NOT_READY; // stale hands are lost hands: no sample
        }
        *out = frame;
        return CG_OK;
    }
    return CG_ERR_TIMEOUT;
}

/// Stores the command word through the handle's held writable header view with
/// release ordering (TD-006). The header is re-validated first (TD-003), so a
/// command against a torn or foreign header is refused instead of written.
cg_status store_command(BridgeHandle &handle, std::uint32_t value) noexcept {
    const cg_status header_status = status_for_header(handle);
    if (header_status != CG_OK) {
        return header_status;
    }
    if (handle.command_view == nullptr) {
        return CG_ERR_UNSUPPORTED;
    }
    // The command word is the first 4 bytes after the header's reserved window
    // in the writable header view; the offset comes from the layout contract.
    // NOLINTNEXTLINE(cppcoreguidelines-pro-type-reinterpret-cast, cppcoreguidelines-pro-bounds-pointer-arithmetic)
    auto *word = reinterpret_cast<std::uint32_t *>(handle.command_view + kCommandOffset);
    std::atomic_ref<std::uint32_t>(*word).store(value, std::memory_order_release);
    return CG_OK;
}

} // namespace

const char *LastHeaderError() noexcept { return g_header_error.data(); }

} // namespace cg::bridge

extern "C" {

cg_status cg_bridge_open(void **out_handle) {
    if (out_handle == nullptr) {
        return CG_ERR_INVALID_ARG;
    }
    using cg::bridge::BridgeHandle;
    std::unique_ptr<BridgeHandle> handle;
#if defined(_WIN32)
    // Open with write access so the small command view can be mapped once and
    // held on the handle (TD-006); the payload view below is still read-only.
    // NOLINTNEXTLINE(cppcoreguidelines-pro-bounds-array-to-pointer-decay)
    HANDLE mapping = OpenFileMappingW(FILE_MAP_READ | FILE_MAP_WRITE, FALSE, cg::bridge::kStateNameW);
    if (mapping == nullptr) {
        const DWORD error = GetLastError();
        return error == ERROR_FILE_NOT_FOUND ? CG_ERR_NOT_READY : CG_ERR_INTERNAL;
    }
    // TD-008/TD-052: map exactly the bytes the reader accepts instead of the
    // whole section. A Windows section size is page-granular, so a sub-page
    // undersized section cannot be distinguished; the view length is therefore
    // treated as the exact accepted size and no read can reach beyond it. An
    // oversized section is never mapped beyond the same length. A failed view
    // means the section cannot cover the required slots.
    void *view = MapViewOfFile(mapping, FILE_MAP_READ, 0, 0, cg::bridge::kMinimumRegionSize);
    if (view == nullptr) {
        CloseHandle(mapping);
        return CG_ERR_UNSUPPORTED;
    }
    void *command_view = MapViewOfFile(mapping, FILE_MAP_WRITE, 0, 0, cg::bridge::kHeaderSize);
    if (command_view == nullptr) {
        UnmapViewOfFile(view);
        CloseHandle(mapping);
        return CG_ERR_INTERNAL;
    }
    handle = std::unique_ptr<BridgeHandle>(new (std::nothrow) BridgeHandle{});
    if (handle == nullptr) {
        UnmapViewOfFile(command_view);
        UnmapViewOfFile(view);
        CloseHandle(mapping);
        return CG_ERR_INTERNAL;
    }
    handle->mapping = mapping;
    handle->base = static_cast<std::uint8_t *>(view);
    handle->command_view = static_cast<std::uint8_t *>(command_view);
#elif defined(__unix__) || defined(__APPLE__)
    const int fd = shm_open(cg::bridge::kPosixStateName, O_RDWR | O_CLOEXEC, 0);
    if (fd < 0) {
        return errno == ENOENT ? CG_ERR_NOT_READY : CG_ERR_INTERNAL;
    }
    struct stat info{};
    if (fstat(fd, &info) != 0) {
        close(fd);
        return CG_ERR_INTERNAL;
    }
    const auto size = static_cast<std::size_t>(info.st_size);
    // POSIX reports the true object size, so the accepted window is exact
    // (TD-008): an undersized region cannot cover the slots and an oversized
    // one is beyond the documented maximum (TD-052). Only the needed bytes are
    // mapped afterwards.
    if (size < cg::bridge::kMinimumRegionSize || size > cg::bridge::kMaximumRegionSize) {
        close(fd);
        return CG_ERR_UNSUPPORTED;
    }
    void *view = mmap(nullptr, cg::bridge::kMinimumRegionSize, PROT_READ, MAP_SHARED, fd, 0);
    if (view == MAP_FAILED) {
        close(fd);
        return CG_ERR_INTERNAL;
    }
    void *command_view = mmap(nullptr, cg::bridge::kHeaderSize, PROT_READ | PROT_WRITE, MAP_SHARED, fd, 0);
    close(fd);
    if (command_view == MAP_FAILED) {
        munmap(view, cg::bridge::kMinimumRegionSize);
        return CG_ERR_INTERNAL;
    }
    handle = std::unique_ptr<BridgeHandle>(new (std::nothrow) BridgeHandle{});
    if (handle == nullptr) {
        munmap(command_view, cg::bridge::kHeaderSize);
        munmap(view, cg::bridge::kMinimumRegionSize);
        return CG_ERR_INTERNAL;
    }
    handle->size = size;
    handle->base = static_cast<std::uint8_t *>(view);
    handle->command_view = static_cast<std::uint8_t *>(command_view);
#else
    return CG_ERR_UNSUPPORTED;
#endif
    // The header is published magic-last with release ordering, so a reader
    // that acquires magic already sees a complete header. The bounded retry
    // below is belt-and-braces for a reader that opened before the writer's
    // first store (or a writer that died mid-init): it is not the
    // synchronisation primitive.
    cg::bridge::HeaderState header_state = cg::bridge::classify_header(*handle);
    if (header_state != cg::bridge::HeaderState::kValid) {
        const auto deadline = std::chrono::steady_clock::now() + cg::bridge::kHeaderInitRetryWindow;
        while (header_state != cg::bridge::HeaderState::kValid && std::chrono::steady_clock::now() < deadline) {
            std::this_thread::sleep_for(std::chrono::milliseconds(1));
            header_state = cg::bridge::classify_header(*handle);
        }
    }
    if (header_state != cg::bridge::HeaderState::kValid) {
        cg_bridge_close(handle.release());
        // An uninitialised region (no magic) is the soft NotReady; anything
        // else is a foreign or incompatible region, and LastHeaderError()
        // names the observed and expected version/header size.
        return header_state == cg::bridge::HeaderState::kUninitialised ? CG_ERR_NOT_READY : CG_ERR_UNSUPPORTED;
    }
    cg::bridge::clear_header_error();
    *out_handle = handle.release(); // written only on CG_OK
    return CG_OK;
}

cg_status cg_bridge_read_head(void *h, cg_head_sample *out) {
    if (h == nullptr || out == nullptr) {
        return CG_ERR_INVALID_ARG;
    }
    return cg::bridge::read_head(*static_cast<cg::bridge::BridgeHandle *>(h), out);
}

cg_status cg_bridge_read_hands(void *h, cg_hand_frame *out) {
    if (h == nullptr || out == nullptr) {
        return CG_ERR_INVALID_ARG;
    }
    return cg::bridge::read_hands(*static_cast<cg::bridge::BridgeHandle *>(h), out);
}

cg_status cg_bridge_send_command(void *h, uint32_t cmd) {
    // cmd + 1 is stored because 0 is the idle word; cmd == UINT32_MAX would
    // wrap back to idle and is rejected.
    if (h == nullptr || cmd == 0 || cmd == UINT32_MAX) {
        return CG_ERR_INVALID_ARG;
    }
    // The handle's held command view is used directly (TD-006): no name lookup
    // and no reopen, so the command still reaches a region whose name has
    // disappeared while a view is held.
    return cg::bridge::store_command(*static_cast<cg::bridge::BridgeHandle *>(h), cmd + 1);
}

void cg_bridge_close(void *h) {
    if (h == nullptr) {
        return;
    }
    const std::unique_ptr<cg::bridge::BridgeHandle> owner{static_cast<cg::bridge::BridgeHandle *>(h)};
    auto *handle = owner.get();
#if defined(_WIN32)
    if (handle->command_view != nullptr) {
        UnmapViewOfFile(handle->command_view);
    }
    if (handle->base != nullptr) {
        UnmapViewOfFile(handle->base);
    }
    if (handle->mapping != nullptr) {
        CloseHandle(handle->mapping);
    }
#elif defined(__unix__) || defined(__APPLE__)
    if (handle->command_view != nullptr) {
        munmap(handle->command_view, cg::bridge::kHeaderSize);
    }
    if (handle->base != nullptr) {
        munmap(handle->base, cg::bridge::kMinimumRegionSize);
    }
#endif
}

} // extern "C"

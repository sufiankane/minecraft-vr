// 5.12 native bridge: a read-only mapping of the dossier 5.6 region
// (`Local\cubeglass.v1.state`) plus the command channel in its reserved header
// window (ADR-0010/R43).
//
// The payload region is mapped read-only on both platforms, matching 5.6's
// "readers are read-only". `cg_bridge_send_command` takes a short-lived
// writable view of the 64-byte header so the command word can be stored with
// release ordering without ever making the data region writable.
//
// `cg_bridge_open` allocates one small handle with `new` (cold path, once per
// process lifetime, explicitly not a hot path) and `cg_bridge_close` frees it;
// after `cg_bridge_close` the handle is invalid and a null handle is ignored.
// The read path allocates nothing.
//
// Payload copies go through relaxed `std::atomic_ref<std::uint64_t>` word
// accesses with a release/acquire fence around the seqlock counters, the same
// construction as the S5 `PoseSlot`, so every shared byte access is an atomic
// operation and the concurrent tests are TSan-clean by construction.
//
// Staleness applies to both slots through the same 250 ms heartbeat rule. The
// head read keeps the sample and reports `CG_TRACK_LOST`; the hand read has no
// per-hand tracking state, so a stale hand slot reports `CG_ERR_NOT_READY` and
// no frame instead of emitting stale joints.

#include "cg/bridge/shm_layout.hpp"

#include <atomic>
#include <chrono>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <new>
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

/// The smallest region this reader accepts: the hand slot is the last one.
constexpr std::size_t kMinimumRegionSize = kHandSlotOffset + sizeof(HandSlot);
static_assert(kMinimumRegionSize == 848, "the 5.6 head and hand slots must fit the accepted region");

/// Bounded wait for a writer that is mid-initialisation: the header fields are
/// stored one by one, so a reader that opens in that window retries until the
/// header is complete instead of failing hard.
constexpr auto kHeaderInitRetryWindow = std::chrono::milliseconds(50);

/// POSIX has one flat shared-memory namespace; the Windows `Local\` prefix
/// maps to the leading '/' that `shm_open` requires.
constexpr char kPosixStateName[] = "/cubeglass.v1.state";

#if defined(_WIN32)
constexpr wchar_t kStateNameW[] = L"Local\\cubeglass.v1.state";
static_assert(sizeof(kStateNameW) / sizeof(wchar_t) == sizeof(kStateName), "the wide name mirrors kStateName");
#endif

struct BridgeHandle {
#if defined(_WIN32)
    HANDLE mapping{nullptr};
#elif defined(__unix__) || defined(__APPLE__)
    std::size_t size{0};
#endif
    std::uint8_t *base{nullptr};
};

std::int64_t now_ns() noexcept {
    const auto elapsed = std::chrono::steady_clock::now().time_since_epoch();
    return std::chrono::duration_cast<std::chrono::nanoseconds>(elapsed).count();
}

bool header_is_valid(const BridgeHandle &handle) noexcept {
    const auto *header = reinterpret_cast<const ShmHeader *>(handle.base);
    return header->magic == kShmMagic && header->abi_version == kShmAbiVersion && header->header_size == kHeaderSize;
}

/// Copies a seqlock payload word-wise through relaxed atomic accesses. The
/// acquire fence orders the payload loads before the caller's `seq_b` load;
/// the payloads sit at 8-byte-aligned slot offsets, so the words are aligned.
template <typename Payload> Payload copy_payload(Payload *shared) noexcept {
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
}

cg_status read_head(const BridgeHandle &handle, cg_head_sample *out) noexcept {
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

/// Stores the command word through a short-lived writable header view with
/// release ordering. Returns CG_ERR_NOT_READY when the region disappeared
/// between `cg_bridge_open` and the command.
cg_status store_command(std::uint32_t value) noexcept {
#if defined(_WIN32)
    HANDLE mapping = OpenFileMappingW(FILE_MAP_WRITE, FALSE, kStateNameW);
    if (mapping == nullptr) {
        return GetLastError() == ERROR_FILE_NOT_FOUND ? CG_ERR_NOT_READY : CG_ERR_INTERNAL;
    }
    void *view = MapViewOfFile(mapping, FILE_MAP_WRITE, 0, 0, kHeaderSize);
    if (view == nullptr) {
        CloseHandle(mapping);
        return CG_ERR_INTERNAL;
    }
    auto *word = reinterpret_cast<std::uint32_t *>(static_cast<std::uint8_t *>(view) + kCommandOffset);
    std::atomic_ref<std::uint32_t>(*word).store(value, std::memory_order_release);
    UnmapViewOfFile(view);
    CloseHandle(mapping);
    return CG_OK;
#elif defined(__unix__) || defined(__APPLE__)
    const int fd = shm_open(kPosixStateName, O_RDWR | O_CLOEXEC, 0);
    if (fd < 0) {
        return errno == ENOENT ? CG_ERR_NOT_READY : CG_ERR_INTERNAL;
    }
    void *view = mmap(nullptr, kHeaderSize, PROT_READ | PROT_WRITE, MAP_SHARED, fd, 0);
    close(fd);
    if (view == MAP_FAILED) {
        return CG_ERR_INTERNAL;
    }
    auto *word = reinterpret_cast<std::uint32_t *>(static_cast<std::uint8_t *>(view) + kCommandOffset);
    std::atomic_ref<std::uint32_t>(*word).store(value, std::memory_order_release);
    munmap(view, kHeaderSize);
    return CG_OK;
#else
    (void)value;
    return CG_ERR_UNSUPPORTED;
#endif
}

} // namespace
} // namespace cg::bridge

extern "C" {

cg_status cg_bridge_open(void **out_handle) {
    if (out_handle == nullptr) {
        return CG_ERR_INVALID_ARG;
    }
    using cg::bridge::BridgeHandle;
    BridgeHandle *handle = nullptr;
#if defined(_WIN32)
    HANDLE mapping = OpenFileMappingW(FILE_MAP_READ, FALSE, cg::bridge::kStateNameW);
    if (mapping == nullptr) {
        const DWORD error = GetLastError();
        return error == ERROR_FILE_NOT_FOUND ? CG_ERR_NOT_READY : CG_ERR_INTERNAL;
    }
    void *view = MapViewOfFile(mapping, FILE_MAP_READ, 0, 0, 0);
    if (view == nullptr) {
        CloseHandle(mapping);
        return CG_ERR_INTERNAL;
    }
    // POSIX checks the exact region size from fstat; a Windows section is
    // page-granular, so the equivalent guard is the mapped view's region size.
    // It rejects a view that cannot cover the head and hand slots.
    MEMORY_BASIC_INFORMATION region{};
    if (VirtualQuery(view, &region, sizeof(region)) == 0) {
        UnmapViewOfFile(view);
        CloseHandle(mapping);
        return CG_ERR_INTERNAL;
    }
    if (region.RegionSize < cg::bridge::kMinimumRegionSize) {
        UnmapViewOfFile(view);
        CloseHandle(mapping);
        return CG_ERR_UNSUPPORTED;
    }
    handle = new (std::nothrow) BridgeHandle{};
    if (handle == nullptr) {
        UnmapViewOfFile(view);
        CloseHandle(mapping);
        return CG_ERR_INTERNAL;
    }
    handle->mapping = mapping;
    handle->base = static_cast<std::uint8_t *>(view);
#elif defined(__unix__) || defined(__APPLE__)
    const int fd = shm_open(cg::bridge::kPosixStateName, O_RDONLY | O_CLOEXEC, 0);
    if (fd < 0) {
        return errno == ENOENT ? CG_ERR_NOT_READY : CG_ERR_INTERNAL;
    }
    struct stat info{};
    if (fstat(fd, &info) != 0) {
        close(fd);
        return CG_ERR_INTERNAL;
    }
    const auto size = static_cast<std::size_t>(info.st_size);
    if (size < cg::bridge::kMinimumRegionSize) {
        close(fd);
        return CG_ERR_UNSUPPORTED;
    }
    void *view = mmap(nullptr, size, PROT_READ, MAP_SHARED, fd, 0);
    close(fd);
    if (view == MAP_FAILED) {
        return CG_ERR_INTERNAL;
    }
    handle = new (std::nothrow) BridgeHandle{};
    if (handle == nullptr) {
        munmap(view, size);
        return CG_ERR_INTERNAL;
    }
    handle->size = size;
    handle->base = static_cast<std::uint8_t *>(view);
#else
    return CG_ERR_UNSUPPORTED;
#endif
    if (!cg::bridge::header_is_valid(*handle)) {
        // A writer stores the header fields one by one, so a reader that opens
        // mid-initialisation sees an incomplete header. Retry for a short
        // bounded window before deciding; the normal case recovers in
        // microseconds.
        const auto deadline = std::chrono::steady_clock::now() + cg::bridge::kHeaderInitRetryWindow;
        while (!cg::bridge::header_is_valid(*handle) && std::chrono::steady_clock::now() < deadline) {
            std::this_thread::sleep_for(std::chrono::milliseconds(1));
        }
        if (!cg::bridge::header_is_valid(*handle)) {
            // A zero magic means the region exists but has not been
            // initialised (or its writer died mid-init): the soft NotReady.
            // Anything else is a foreign or incompatible region.
            const bool magic_present =
                reinterpret_cast<const cg::bridge::ShmHeader *>(handle->base)->magic == cg::bridge::kShmMagic;
            cg_bridge_close(handle);
            return magic_present ? CG_ERR_UNSUPPORTED : CG_ERR_NOT_READY;
        }
    }
    *out_handle = handle; // written only on CG_OK
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
    return cg::bridge::store_command(cmd + 1);
}

void cg_bridge_close(void *h) {
    if (h == nullptr) {
        return;
    }
    auto *handle = static_cast<cg::bridge::BridgeHandle *>(h);
#if defined(_WIN32)
    if (handle->base != nullptr) {
        UnmapViewOfFile(handle->base);
    }
    if (handle->mapping != nullptr) {
        CloseHandle(handle->mapping);
    }
#elif defined(__unix__) || defined(__APPLE__)
    if (handle->base != nullptr) {
        munmap(handle->base, handle->size);
    }
#endif
    delete handle;
}

} // extern "C"

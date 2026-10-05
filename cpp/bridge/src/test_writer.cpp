// TEST-ONLY writer for the dossier 5.6 shared-memory region. See
// `cg/bridge/test_writer.h` for the contract and rationale; this file is on no
// production path. It mirrors the production seqlock protocol: counter stores
// bracket word-wise relaxed atomic payload stores, with the release fence
// before the payload and the even counter store released last.

#include "cg/bridge/test_writer.h"

#include "cg/bridge/shm_layout.hpp"

#include <atomic>
#include <chrono>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <memory>
#include <new>
#include <string_view>

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

/// One 64-byte header plus the head window and the 592-byte hand slot; 1024 is
/// the region size the layout tests cap the slots at.
constexpr std::size_t kTestRegionSize = 1024;
static_assert(kHandSlotOffset + sizeof(HandSlot) <= kTestRegionSize, "the test region must cover both slots");
static_assert(kShmAbiVersion == 2, "the test writer must publish the region ABI version it declares");

#if defined(_WIN32)
// A wide string literal is the natural form for the Win32 name; the length is
// static_asserted against the narrow name below.
// NOLINTNEXTLINE(cppcoreguidelines-avoid-c-arrays)
constexpr wchar_t kStateNameW[] = L"Local\\cubeglass.v1.state";
// The wide literal decays into the view on purpose here (a constexpr length
// comparison, not a runtime API call).
// NOLINTNEXTLINE(cppcoreguidelines-pro-bounds-array-to-pointer-decay)
static_assert(std::wstring_view{kStateNameW}.size() == std::string_view{kStateName}.size(),
              "the wide name mirrors kStateName");
#elif defined(__unix__) || defined(__APPLE__)
constexpr char kPosixStateName[] = "/cubeglass.v1.state";
#endif

struct WriterHandle {
#if defined(_WIN32)
    HANDLE mapping{nullptr};
#elif defined(__unix__) || defined(__APPLE__)
    int fd{-1};
#endif
    std::uint8_t *base{nullptr};
    /// True only for a region `cg_test_writer_create` opened itself: `close`
    /// unlinks the POSIX name for an owned region and leaves a region that was
    /// merely opened (`cg_test_writer_open`) in place (CXX-09).
    bool owns_region{false};
};

/// Single test writer per process; the production service is single-writer too,
/// so this mirrors the production model. Guarded by the public API's
/// single-writer checks rather than by a lock.
// NOLINTNEXTLINE(cppcoreguidelines-avoid-non-const-global-variables)
WriterHandle *g_writer = nullptr;

std::uint64_t current_pid() noexcept {
#if defined(_WIN32)
    return static_cast<std::uint64_t>(GetCurrentProcessId());
#elif defined(__unix__) || defined(__APPLE__)
    return static_cast<std::uint64_t>(getpid());
#else
    return 0;
#endif
}

/// Stores a payload word-wise through relaxed atomic accesses (the writer side
/// of the reader's `copy_payload`). The fixed-size local words buffer and the
/// typed-view casts are the deliberate shm construction documented in
/// `shm_layout.hpp`; the bounds are the compile-time `kWords` static assert.
template <typename Payload> void store_payload(Payload *shared, const Payload &payload) noexcept {
    // NOLINTBEGIN(cppcoreguidelines-avoid-c-arrays, cppcoreguidelines-pro-bounds-array-to-pointer-decay,
    // cppcoreguidelines-pro-type-reinterpret-cast, cppcoreguidelines-pro-bounds-pointer-arithmetic,
    // cppcoreguidelines-pro-bounds-constant-array-index)
    constexpr std::size_t kWords = sizeof(Payload) / sizeof(std::uint64_t);
    static_assert(sizeof(Payload) % sizeof(std::uint64_t) == 0, "payload must be 8-byte sized");
    std::uint64_t words[kWords];
    std::memcpy(words, &payload, sizeof(Payload));
    auto *target = reinterpret_cast<std::uint64_t *>(shared);
    for (std::size_t word = 0; word < kWords; ++word) {
        std::atomic_ref<std::uint64_t>(target[word]).store(words[word], std::memory_order_relaxed);
    }
    // NOLINTEND(cppcoreguidelines-avoid-c-arrays, cppcoreguidelines-pro-bounds-array-to-pointer-decay,
    // cppcoreguidelines-pro-type-reinterpret-cast, cppcoreguidelines-pro-bounds-pointer-arithmetic,
    // cppcoreguidelines-pro-bounds-constant-array-index)
}

/// Publishes one seqlock payload: bump `seq_a` odd, fence, write the payload,
/// fence, store `seq_b`, then store the even `seq_a` last with release order.
template <typename Payload>
void publish_payload(std::atomic<std::uint64_t> &seq_a, std::atomic<std::uint64_t> &seq_b, Payload &shared,
                     const Payload &payload) noexcept {
    const std::uint64_t begin = seq_a.load(std::memory_order_relaxed);
    seq_a.store(begin + 1, std::memory_order_relaxed);
    std::atomic_thread_fence(std::memory_order_release);
    store_payload(&shared, payload);
    std::atomic_thread_fence(std::memory_order_release);
    seq_b.store(begin + 2, std::memory_order_release);
    seq_a.store(begin + 2, std::memory_order_release);
}

void initialize_region(std::uint8_t *base) noexcept {
    // Header publication protocol (shm_layout.hpp, I-3): clear the region,
    // store every header field with relaxed stores, then publish `magic` last
    // with a release store. A reader that acquire-loads `magic` first
    // happens-after these stores and needs no further ordering.
    std::memset(base, 0, kTestRegionSize);
    // NOLINTNEXTLINE(cppcoreguidelines-pro-type-reinterpret-cast)
    auto *header = reinterpret_cast<ShmHeader *>(base);
    std::atomic_ref<std::uint64_t>(header->writer_pid).store(current_pid(), std::memory_order_relaxed);
    std::atomic_ref<std::uint32_t>(header->abi_version).store(kShmAbiVersion, std::memory_order_relaxed);
    std::atomic_ref<std::uint32_t>(header->header_size).store(kHeaderSize, std::memory_order_relaxed);
    // A freshly created region starts with a fresh heartbeat instead of a zero
    // one that reads as indefinitely stale (CXX-09): the reader's staleness
    // rule then reflects "the writer exists but has not published yet", which
    // is what the NOT_READY-before-first-publish tests assert.
    const auto now =
        std::chrono::duration_cast<std::chrono::nanoseconds>(std::chrono::steady_clock::now().time_since_epoch());
    std::atomic_ref<std::int64_t>(header->heartbeat_ns)
        .store(static_cast<std::int64_t>(now.count()), std::memory_order_relaxed);
    std::atomic_ref<std::uint64_t>(header->magic).store(kShmMagic, std::memory_order_release);
}

} // namespace
} // namespace cg::bridge

extern "C" {

cg_status cg_test_writer_create(void) {
    using cg::bridge::WriterHandle;
    if (cg::bridge::g_writer != nullptr) {
        return CG_ERR_INVALID_ARG;
    }
    std::unique_ptr<WriterHandle> writer;
#if defined(_WIN32)
    HANDLE mapping = CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0,
                                        // NOLINTNEXTLINE(cppcoreguidelines-pro-bounds-array-to-pointer-decay)
                                        static_cast<DWORD>(cg::bridge::kTestRegionSize), cg::bridge::kStateNameW);
    if (mapping == nullptr) {
        return CG_ERR_INTERNAL;
    }
    void *view = MapViewOfFile(mapping, FILE_MAP_ALL_ACCESS, 0, 0, 0);
    if (view == nullptr) {
        CloseHandle(mapping);
        return CG_ERR_INTERNAL;
    }
    // TD-030: no braces on the nothrow new. MSVC /analyze's C28182 misfires
    // on a value-initialised nothrow new; default-initialisation is equivalent
    // here because every WriterHandle member has a default member initialiser.
    writer = std::unique_ptr<WriterHandle>(new (std::nothrow) WriterHandle);
    if (writer == nullptr) {
        UnmapViewOfFile(view);
        CloseHandle(mapping);
        return CG_ERR_INTERNAL;
    }
    writer->mapping = mapping;
    writer->base = static_cast<std::uint8_t *>(view);
#elif defined(__unix__) || defined(__APPLE__)
    const int fd = shm_open(cg::bridge::kPosixStateName, O_CREAT | O_RDWR | O_CLOEXEC, 0600);
    if (fd < 0) {
        return CG_ERR_INTERNAL;
    }
    if (ftruncate(fd, static_cast<off_t>(cg::bridge::kTestRegionSize)) != 0) {
        close(fd);
        return CG_ERR_INTERNAL;
    }
    void *view = mmap(nullptr, cg::bridge::kTestRegionSize, PROT_READ | PROT_WRITE, MAP_SHARED, fd, 0);
    if (view == MAP_FAILED) {
        close(fd);
        return CG_ERR_INTERNAL;
    }
    // TD-030: no braces on the nothrow new (see cg_test_writer_create).
    writer = std::unique_ptr<WriterHandle>(new (std::nothrow) WriterHandle);
    if (writer == nullptr) {
        munmap(view, cg::bridge::kTestRegionSize);
        close(fd);
        return CG_ERR_INTERNAL;
    }
    writer->fd = fd;
    writer->base = static_cast<std::uint8_t *>(view);
#else
    return CG_ERR_UNSUPPORTED;
#endif
    writer->owns_region = true;
    cg::bridge::initialize_region(writer->base);
    cg::bridge::g_writer = writer.release();
    return CG_OK;
}

cg_status cg_test_writer_open(void) {
    using cg::bridge::WriterHandle;
    if (cg::bridge::g_writer != nullptr) {
        return CG_ERR_INVALID_ARG;
    }
    std::unique_ptr<WriterHandle> writer;
#if defined(_WIN32)
    // NOLINTNEXTLINE(cppcoreguidelines-pro-bounds-array-to-pointer-decay)
    HANDLE mapping = OpenFileMappingW(FILE_MAP_ALL_ACCESS, FALSE, cg::bridge::kStateNameW);
    if (mapping == nullptr) {
        return GetLastError() == ERROR_FILE_NOT_FOUND ? CG_ERR_NOT_READY : CG_ERR_INTERNAL;
    }
    void *view = MapViewOfFile(mapping, FILE_MAP_ALL_ACCESS, 0, 0, 0);
    if (view == nullptr) {
        CloseHandle(mapping);
        return CG_ERR_INTERNAL;
    }
    // TD-030: no braces on the nothrow new. MSVC /analyze's C28182 misfires
    // on a value-initialised nothrow new; default-initialisation is equivalent
    // here because every WriterHandle member has a default member initialiser.
    writer = std::unique_ptr<WriterHandle>(new (std::nothrow) WriterHandle);
    if (writer == nullptr) {
        UnmapViewOfFile(view);
        CloseHandle(mapping);
        return CG_ERR_INTERNAL;
    }
    writer->mapping = mapping;
    writer->base = static_cast<std::uint8_t *>(view);
#elif defined(__unix__) || defined(__APPLE__)
    const int fd = shm_open(cg::bridge::kPosixStateName, O_RDWR | O_CLOEXEC, 0);
    if (fd < 0) {
        return errno == ENOENT ? CG_ERR_NOT_READY : CG_ERR_INTERNAL;
    }
    struct stat info{};
    if (fstat(fd, &info) != 0) {
        // A failed fstat is an internal error, not an unsupported layout; the
        // size mismatch below is the actual Unsupported case (CXX-09).
        close(fd);
        return CG_ERR_INTERNAL;
    }
    if (static_cast<std::size_t>(info.st_size) < cg::bridge::kTestRegionSize) {
        close(fd);
        return CG_ERR_UNSUPPORTED;
    }
    void *view = mmap(nullptr, cg::bridge::kTestRegionSize, PROT_READ | PROT_WRITE, MAP_SHARED, fd, 0);
    if (view == MAP_FAILED) {
        close(fd);
        return CG_ERR_INTERNAL;
    }
    // TD-030: no braces on the nothrow new (see cg_test_writer_create).
    writer = std::unique_ptr<WriterHandle>(new (std::nothrow) WriterHandle);
    if (writer == nullptr) {
        munmap(view, cg::bridge::kTestRegionSize);
        close(fd);
        return CG_ERR_INTERNAL;
    }
    writer->fd = fd;
    writer->base = static_cast<std::uint8_t *>(view);
#else
    return CG_ERR_UNSUPPORTED;
#endif
    // Assert the region's version before publishing through it: a foreign
    // region (legacy v1, or a future layout) must never be written with this
    // layout. Magic is acquired first, matching the reader protocol.
    // Typed view over the mapped header; the byte offset is the layout
    // contract from shm_layout.hpp.
    // NOLINTNEXTLINE(cppcoreguidelines-pro-type-reinterpret-cast)
    const auto *header = reinterpret_cast<const cg::bridge::ShmHeader *>(writer->base);
    const std::uint64_t magic = std::atomic_ref<const std::uint64_t>(header->magic).load(std::memory_order_acquire);
    const std::uint32_t abi = std::atomic_ref<const std::uint32_t>(header->abi_version).load(std::memory_order_relaxed);
    if (magic != cg::bridge::kShmMagic || abi != cg::bridge::kShmAbiVersion) {
        cg_test_writer_close();
        return CG_ERR_UNSUPPORTED;
    }
    cg::bridge::g_writer = writer.release();
    return CG_OK;
}

void cg_test_writer_close(void) {
    using cg::bridge::WriterHandle;
    if (cg::bridge::g_writer == nullptr) {
        return;
    }
    WriterHandle *writer = cg::bridge::g_writer;
    cg::bridge::g_writer = nullptr;
    // Frees the handle once the platform resources below are released.
    const std::unique_ptr<WriterHandle> owner{writer};
#if defined(_WIN32)
    if (writer->base != nullptr) {
        UnmapViewOfFile(writer->base);
    }
    if (writer->mapping != nullptr) {
        CloseHandle(writer->mapping);
    }
#elif defined(__unix__) || defined(__APPLE__)
    if (writer->base != nullptr) {
        munmap(writer->base, cg::bridge::kTestRegionSize);
    }
    if (writer->fd >= 0) {
        close(writer->fd);
        // Unlink only a region this handle created: a region merely opened by
        // `cg_test_writer_open` (possibly a foreign object or a future service)
        // must survive the close (CXX-09).
        if (writer->owns_region) {
            shm_unlink(cg::bridge::kPosixStateName);
        }
    }
#endif
}

cg_status cg_test_writer_publish_head(const cg_head_sample *sample) {
    using cg::bridge::HeadSlot;
    if (cg::bridge::g_writer == nullptr || sample == nullptr) {
        return CG_ERR_INVALID_ARG;
    }
    // The slot view is the 5.6 layout: a typed view at a fixed byte offset in
    // the mapped region, bounded by the region-size static asserts.
    // NOLINTNEXTLINE(cppcoreguidelines-pro-type-reinterpret-cast, cppcoreguidelines-pro-bounds-pointer-arithmetic)
    auto *slot = reinterpret_cast<HeadSlot *>(cg::bridge::g_writer->base + cg::bridge::kHeadSlotOffset);
    cg::bridge::publish_payload(slot->seq_a, slot->seq_b, slot->sample, *sample);
    return CG_OK;
}

cg_status cg_test_writer_publish_hands(const cg_hand_frame *frame) {
    using cg::bridge::HandSlot;
    if (cg::bridge::g_writer == nullptr || frame == nullptr) {
        return CG_ERR_INVALID_ARG;
    }
    // NOLINTNEXTLINE(cppcoreguidelines-pro-type-reinterpret-cast, cppcoreguidelines-pro-bounds-pointer-arithmetic)
    auto *slot = reinterpret_cast<HandSlot *>(cg::bridge::g_writer->base + cg::bridge::kHandSlotOffset);
    cg::bridge::publish_payload(slot->seq_a, slot->seq_b, slot->frame, *frame);
    return CG_OK;
}

cg_status cg_test_writer_set_heartbeat(cg_time_ns heartbeat_ns) {
    if (cg::bridge::g_writer == nullptr) {
        return CG_ERR_INVALID_ARG;
    }
    // NOLINTNEXTLINE(cppcoreguidelines-pro-type-reinterpret-cast)
    auto *header = reinterpret_cast<cg::bridge::ShmHeader *>(cg::bridge::g_writer->base);
    std::atomic_ref<std::int64_t>(header->heartbeat_ns).store(heartbeat_ns, std::memory_order_release);
    return CG_OK;
}

cg_status cg_test_writer_publish_head_raw(uint64_t seq_a, uint64_t seq_b, const cg_head_sample *sample) {
    using cg::bridge::HeadSlot;
    if (cg::bridge::g_writer == nullptr || sample == nullptr) {
        return CG_ERR_INVALID_ARG;
    }
    // NOLINTNEXTLINE(cppcoreguidelines-pro-type-reinterpret-cast, cppcoreguidelines-pro-bounds-pointer-arithmetic)
    auto *slot = reinterpret_cast<HeadSlot *>(cg::bridge::g_writer->base + cg::bridge::kHeadSlotOffset);
    slot->seq_a.store(seq_a, std::memory_order_relaxed);
    cg::bridge::store_payload(&slot->sample, *sample);
    slot->seq_b.store(seq_b, std::memory_order_relaxed);
    return CG_OK;
}

cg_status cg_test_writer_read_command(uint32_t *out_command, uint32_t *out_ack) {
    if (cg::bridge::g_writer == nullptr || (out_command == nullptr && out_ack == nullptr)) {
        return CG_ERR_INVALID_ARG;
    }
    // NOLINTNEXTLINE(cppcoreguidelines-pro-type-reinterpret-cast)
    auto *header = reinterpret_cast<cg::bridge::ShmHeader *>(cg::bridge::g_writer->base);
    if (out_command != nullptr) {
        *out_command = std::atomic_ref<std::uint32_t>(header->command).load(std::memory_order_acquire);
    }
    if (out_ack != nullptr) {
        *out_ack = std::atomic_ref<std::uint32_t>(header->ack).load(std::memory_order_acquire);
    }
    return CG_OK;
}

cg_status cg_test_writer_ack_command(uint32_t ack) {
    if (cg::bridge::g_writer == nullptr) {
        return CG_ERR_INVALID_ARG;
    }
    // NOLINTNEXTLINE(cppcoreguidelines-pro-type-reinterpret-cast)
    auto *header = reinterpret_cast<cg::bridge::ShmHeader *>(cg::bridge::g_writer->base);
    std::atomic_ref<std::uint32_t>(header->ack).store(ack, std::memory_order_release);
    return CG_OK;
}

} // extern "C"

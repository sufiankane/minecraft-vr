// Allocation gate for the bridge read path (CXX-21).
//
// The shipped cg_bridge target is a DLL on Windows, and a DLL resolves its own
// operator new from the CRT, so replacing the global allocation functions in
// an importing test executable would not observe its allocations. This test
// therefore compiles the same bridge sources (src/bridge.cpp and
// src/test_writer.cpp) directly into the test binary, statically, and replaces
// the allocation functions in this translation unit, so every heap call in the
// measured loop is counted. The measured call is the real `cg_bridge_read_head`
// / `cg_bridge_read_hands` implementation, not a reimplementation.
//
// Each test snapshots the counter around one warmed-up 1M-iteration read loop
// and asserts the delta is zero; the gtest assertions themselves live outside
// the measured region. There is no producer thread, so nothing else allocates
// concurrently.

#include "cg/bridge/test_writer.h"
#include "cg_unity_bridge.h"

#include <atomic>
#include <chrono>
#include <cstddef>
#include <cstdint>
#include <cstdlib>
#include <new>

#if defined(_MSC_VER)
#include <malloc.h>
#endif

#include <gtest/gtest.h>

namespace {

std::atomic<std::size_t> g_allocation_count{0};

std::size_t AllocationCount() noexcept { return g_allocation_count.load(std::memory_order_relaxed); }

void RecordAllocation() noexcept { g_allocation_count.fetch_add(1, std::memory_order_relaxed); }

void *Allocate(std::size_t size) {
    RecordAllocation();
    void *memory = std::malloc(size == 0 ? 1 : size);
    if (memory == nullptr) {
        throw std::bad_alloc();
    }
    return memory;
}

void *AllocateNoThrow(std::size_t size) noexcept {
    RecordAllocation();
    return std::malloc(size == 0 ? 1 : size);
}

#if defined(_MSC_VER)

void *AllocateAligned(std::size_t size, std::size_t alignment) {
    RecordAllocation();
    void *memory = _aligned_malloc(size == 0 ? 1 : size, alignment);
    if (memory == nullptr) {
        throw std::bad_alloc();
    }
    return memory;
}

void *AllocateAlignedNoThrow(std::size_t size, std::size_t alignment) noexcept {
    RecordAllocation();
    return _aligned_malloc(size == 0 ? 1 : size, alignment);
}

void DeallocateAligned(void *memory) noexcept { _aligned_free(memory); }

#else

void *AllocateAligned(std::size_t size, std::size_t alignment) {
    RecordAllocation();
    void *memory = nullptr;
    if (posix_memalign(&memory, alignment, size == 0 ? 1 : size) != 0) {
        throw std::bad_alloc();
    }
    return memory;
}

void *AllocateAlignedNoThrow(std::size_t size, std::size_t alignment) noexcept {
    RecordAllocation();
    void *memory = nullptr;
    if (posix_memalign(&memory, alignment, size == 0 ? 1 : size) != 0) {
        return nullptr;
    }
    return memory;
}

void DeallocateAligned(void *memory) noexcept { std::free(memory); }

#endif

/// A namespace-scope volatile keeps the accumulated results observable without
/// tripping -Wunused-but-set-variable on a local.
volatile std::uint64_t g_consumed_sink = 0;

void Consume(std::uint64_t value) noexcept { g_consumed_sink = value; }

std::int64_t now_ns() {
    const auto elapsed = std::chrono::steady_clock::now().time_since_epoch();
    return std::chrono::duration_cast<std::chrono::nanoseconds>(elapsed).count();
}

constexpr int kWarmupIterations = 100;
constexpr int kMeasuredIterations = 1'000'000;

} // namespace

void *operator new(std::size_t size) { return Allocate(size); }
void *operator new[](std::size_t size) { return Allocate(size); }
void *operator new(std::size_t size, const std::nothrow_t &) noexcept { return AllocateNoThrow(size); }
void *operator new[](std::size_t size, const std::nothrow_t &) noexcept { return AllocateNoThrow(size); }
void *operator new(std::size_t size, std::align_val_t alignment) {
    return AllocateAligned(size, static_cast<std::size_t>(alignment));
}
void *operator new[](std::size_t size, std::align_val_t alignment) {
    return AllocateAligned(size, static_cast<std::size_t>(alignment));
}
void *operator new(std::size_t size, std::align_val_t alignment, const std::nothrow_t &) noexcept {
    return AllocateAlignedNoThrow(size, static_cast<std::size_t>(alignment));
}
void *operator new[](std::size_t size, std::align_val_t alignment, const std::nothrow_t &) noexcept {
    return AllocateAlignedNoThrow(size, static_cast<std::size_t>(alignment));
}

void operator delete(void *memory) noexcept { std::free(memory); }
void operator delete[](void *memory) noexcept { std::free(memory); }
void operator delete(void *memory, std::size_t) noexcept { std::free(memory); }
void operator delete[](void *memory, std::size_t) noexcept { std::free(memory); }
void operator delete(void *memory, const std::nothrow_t &) noexcept { std::free(memory); }
void operator delete[](void *memory, const std::nothrow_t &) noexcept { std::free(memory); }
void operator delete(void *memory, std::align_val_t) noexcept { DeallocateAligned(memory); }
void operator delete[](void *memory, std::align_val_t) noexcept { DeallocateAligned(memory); }
void operator delete(void *memory, std::size_t, std::align_val_t) noexcept { DeallocateAligned(memory); }
void operator delete[](void *memory, std::size_t, std::align_val_t) noexcept { DeallocateAligned(memory); }
void operator delete(void *memory, std::align_val_t, const std::nothrow_t &) noexcept { DeallocateAligned(memory); }
void operator delete[](void *memory, std::align_val_t, const std::nothrow_t &) noexcept { DeallocateAligned(memory); }

namespace {

TEST(BridgeAllocationTest, ReadHeadAndHandsAllocateNothing) {
    ASSERT_EQ(cg_test_writer_create(), CG_OK);

    // Far-future heartbeat: neither slot ever reads as stale during the loop.
    ASSERT_EQ(cg_test_writer_set_heartbeat(now_ns() + 3'600'000'000'000), CG_OK);

    cg_head_sample head{};
    head.host_time = 1;
    head.pose.q = cg_quat{1.0F, 0.0F, 0.0F, 0.0F};
    head.state = CG_TRACK_STABLE;
    head.sequence = 1;
    ASSERT_EQ(cg_test_writer_publish_head(&head), CG_OK);

    cg_hand_frame hands{};
    hands.sequence = 1;
    ASSERT_EQ(cg_test_writer_publish_hands(&hands), CG_OK);

    void *handle = nullptr;
    ASSERT_EQ(cg_bridge_open(&handle), CG_OK);
    ASSERT_NE(handle, nullptr);

    cg_head_sample head_out{};
    cg_hand_frame hands_out{};
    std::size_t ok_reads = 0;
    std::uint64_t sum = 0;
    const auto read_once = [&]() {
        const cg_status head_status = cg_bridge_read_head(handle, &head_out);
        const cg_status hands_status = cg_bridge_read_hands(handle, &hands_out);
        ok_reads += (head_status == CG_OK ? 1U : 0U) + (hands_status == CG_OK ? 1U : 0U);
        sum += head_out.sequence + hands_out.sequence;
    };

    for (int i = 0; i < kWarmupIterations; ++i) {
        read_once();
    }

    const std::size_t before = AllocationCount();
    for (int i = 0; i < kMeasuredIterations; ++i) {
        read_once();
    }
    const std::size_t after = AllocationCount();
    Consume(sum);

    EXPECT_EQ(after - before, 0U) << "the bridge read path allocated " << (after - before) << " times over "
                                  << kMeasuredIterations << " iterations";
    EXPECT_EQ(ok_reads,
              2U * (static_cast<std::size_t>(kWarmupIterations) + static_cast<std::size_t>(kMeasuredIterations)));

    cg_bridge_close(handle);
    cg_test_writer_close();
}

} // namespace

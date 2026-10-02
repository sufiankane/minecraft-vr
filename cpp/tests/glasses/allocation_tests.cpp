// Allocation gate for the wait-free pose read paths (S5 Task 4).
//
// The replaceable global allocation functions are replaced in this translation
// unit so every heap call in the test executable is counted. Each test
// snapshots the counter around one warmed-up 1M-iteration read loop and asserts
// the delta is zero; the gtest assertions themselves live outside the measured
// region. The source under test is stopped before the measurement so no
// producer thread can allocate concurrently and pollute the counter.

#include "cg/glasses/host_clock.hpp"
#include "cg/glasses/pose_slot.hpp"
#include "cg/glasses/viture_head_pose_source.hpp"
#include "ports.hpp"

#include <atomic>
#include <chrono>
#include <cstddef>
#include <cstdlib>
#include <new>
#include <thread>

#include "fake_viture_api.hpp"

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

// The accumulated results of the measured loops land here. This is a
// namespace-scope (not local) volatile, so the stores are observable side
// effects the optimiser must keep, while GCC's -Wunused-but-set-variable
// (which only covers locals) stays quiet.
volatile double g_consumed_sink = 0.0;

// Pins the accumulated result so the optimiser cannot discard the loops.
void Consume(double value) noexcept { g_consumed_sink = value; }

using cg::Duration;
using cg::HeadSample;
using cg::Pose;
using cg::TrackState;
using cg::core_math::Quat;
using cg::core_math::Vec3;
using cg::glasses::ManualHostClock;
using cg::glasses::PoseSlot;
using cg::glasses::VitureHeadPoseSource;

constexpr int kWarmupIterations = 100;
constexpr int kMeasuredIterations = 1'000'000;

HeadSample Placeholder() noexcept {
    return HeadSample{0, Pose{Vec3{0.0, 0.0, 0.0}, Quat::kIdentity}, TrackState::Stable, 0};
}

double SampleSum(const HeadSample &sample) noexcept {
    return sample.pose.position.x + sample.pose.position.y + sample.pose.position.z + sample.pose.rotation.w() +
           sample.pose.rotation.x() + sample.pose.rotation.y() + sample.pose.rotation.z();
}

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

TEST(AllocationTest, PoseSlotTryReadAllocatesNothing) {
    PoseSlot slot;
    slot.Publish(Placeholder());
    HeadSample out = Placeholder();
    std::size_t reads = 0;
    double sum = 0.0;
    for (int i = 0; i < kWarmupIterations; ++i) {
        reads += slot.TryRead(out) ? 1U : 0U;
        sum += SampleSum(out);
    }

    const std::size_t before = AllocationCount();
    for (int i = 0; i < kMeasuredIterations; ++i) {
        reads += slot.TryRead(out) ? 1U : 0U;
        sum += SampleSum(out);
    }
    const std::size_t after = AllocationCount();
    Consume(sum);

    EXPECT_EQ(after - before, 0U) << "PoseSlot::TryRead allocated " << (after - before) << " times over "
                                  << kMeasuredIterations << " iterations";
    EXPECT_EQ(reads, static_cast<std::size_t>(kWarmupIterations) + static_cast<std::size_t>(kMeasuredIterations));
}

TEST(AllocationTest, VitureFakeSourceTryGetLatestAllocatesNothing) {
    cg::glasses::test::FakeVitureApi api;
    cg_head_sample sdk{};
    sdk.host_time = 0;
    sdk.pose.q.w = 1.0F;
    sdk.state = CG_TRACK_STABLE;
    sdk.sequence = 1;
    api.samples.push_back(sdk);

    ManualHostClock clock;
    VitureHeadPoseSource source(api, clock);
    ASSERT_TRUE(source.Start().ok());

    HeadSample newest = Placeholder();
    bool got_first = false;
    const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(5);
    while (!got_first && std::chrono::steady_clock::now() < deadline) {
        got_first = source.TryGetLatest(newest, Duration{0});
        if (!got_first) {
            std::this_thread::sleep_for(std::chrono::milliseconds(1));
        }
    }
    ASSERT_TRUE(got_first) << "the fake source never published a sample";
    source.Stop();

    HeadSample out = Placeholder();
    std::size_t reads = 0;
    double sum = 0.0;
    for (int i = 0; i < kWarmupIterations; ++i) {
        reads += source.TryGetLatest(out, Duration{0}) ? 1U : 0U;
        sum += SampleSum(out);
    }

    const std::size_t before = AllocationCount();
    for (int i = 0; i < kMeasuredIterations; ++i) {
        reads += source.TryGetLatest(out, Duration{0}) ? 1U : 0U;
        sum += SampleSum(out);
    }
    const std::size_t after = AllocationCount();
    Consume(sum);

    EXPECT_EQ(after - before, 0U) << "VitureHeadPoseSource::TryGetLatest allocated " << (after - before)
                                  << " times over " << kMeasuredIterations << " iterations";
    EXPECT_EQ(reads, static_cast<std::size_t>(kWarmupIterations) + static_cast<std::size_t>(kMeasuredIterations));
}

} // namespace

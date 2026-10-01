// Allocation gate for the core-math hot paths (S1-WI2f).
//
// The replaceable global allocation functions are replaced in this translation
// unit so every heap call in the test executable is counted. Each test
// snapshots the counter around one warmed-up 100k-iteration operation loop and
// asserts the delta is zero; the gtest assertions themselves live outside the
// measured region.

#include "cg/core_math/clock_mapper.hpp"
#include "cg/core_math/convert.hpp"
#include "cg/core_math/pose.hpp"
#include "cg/core_math/quat.hpp"
#include "cg/core_math/vec3.hpp"

#include <atomic>
#include <cstddef>
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

// Pins the accumulated result so the optimiser cannot discard the loops.
void Consume(double value) noexcept {
    static volatile double sink = 0.0;
    sink = value;
}

using cg::core_math::ClockMapper;
using cg::core_math::Compose;
using cg::core_math::HostTime;
using cg::core_math::Inverse;
using cg::core_math::Pose;
using cg::core_math::PoseFromSdk;
using cg::core_math::Quat;
using cg::core_math::Slerp;
using cg::core_math::Vec3;

constexpr int kWarmupIterations = 100;
constexpr int kMeasuredIterations = 100'000;

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

TEST(AllocationTest, QuatRotateAllocatesNothing) {
    const Quat rotation = Quat::FromAxisAngle(Vec3{0.0, 1.0, 0.0}, 0.7);
    const Vec3 point{1.0, 2.0, 3.0};
    double sum = 0.0;
    for (int i = 0; i < kWarmupIterations; ++i) {
        const Vec3 rotated = rotation.Rotate(point);
        sum += rotated.x + rotated.y + rotated.z;
    }

    const std::size_t before = AllocationCount();
    for (int i = 0; i < kMeasuredIterations; ++i) {
        const Vec3 rotated = rotation.Rotate(point);
        sum += rotated.x + rotated.y + rotated.z;
    }
    const std::size_t after = AllocationCount();
    Consume(sum);

    EXPECT_EQ(after - before, 0U) << "Quat::Rotate allocated " << (after - before) << " times over "
                                  << kMeasuredIterations << " iterations";
}

TEST(AllocationTest, QuatSlerpAllocatesNothing) {
    const Quat a = Quat::FromAxisAngle(Vec3{0.0, 1.0, 0.0}, 0.7);
    const Quat b = Quat::FromAxisAngle(Vec3{1.0, 0.0, 0.0}, 1.2);
    double sum = 0.0;
    for (int i = 0; i < kWarmupIterations; ++i) {
        const double t = static_cast<double>(i) / static_cast<double>(kWarmupIterations);
        const Quat interpolated = Slerp(a, b, t);
        sum += interpolated.w() + interpolated.x() + interpolated.y() + interpolated.z();
    }

    const std::size_t before = AllocationCount();
    for (int i = 0; i < kMeasuredIterations; ++i) {
        const double t = static_cast<double>(i) / static_cast<double>(kMeasuredIterations);
        const Quat interpolated = Slerp(a, b, t);
        sum += interpolated.w() + interpolated.x() + interpolated.y() + interpolated.z();
    }
    const std::size_t after = AllocationCount();
    Consume(sum);

    EXPECT_EQ(after - before, 0U) << "Slerp allocated " << (after - before) << " times over " << kMeasuredIterations
                                  << " iterations";
}

TEST(AllocationTest, PoseComposeAndInverseAllocateNothing) {
    const Pose parent{Vec3{1.0, 2.0, 3.0}, Quat::FromAxisAngle(Vec3{0.0, 1.0, 0.0}, 0.4)};
    const Pose child{Vec3{-0.5, 0.25, 1.5}, Quat::FromAxisAngle(Vec3{1.0, 0.0, 0.0}, -0.8)};
    double sum = 0.0;
    for (int i = 0; i < kWarmupIterations; ++i) {
        const Pose composed = Compose(parent, child);
        const Pose inverted = Inverse(composed);
        sum += composed.position.x + composed.position.y + composed.position.z + composed.rotation.w() +
               inverted.position.x + inverted.position.y + inverted.position.z + inverted.rotation.w();
    }

    const std::size_t before = AllocationCount();
    for (int i = 0; i < kMeasuredIterations; ++i) {
        const Pose composed = Compose(parent, child);
        const Pose inverted = Inverse(composed);
        sum += composed.position.x + composed.position.y + composed.position.z + composed.rotation.w() +
               inverted.position.x + inverted.position.y + inverted.position.z + inverted.rotation.w();
    }
    const std::size_t after = AllocationCount();
    Consume(sum);

    EXPECT_EQ(after - before, 0U) << "Compose/Inverse allocated " << (after - before) << " times over "
                                  << kMeasuredIterations << " iterations";
}

TEST(AllocationTest, ClockMapperAddSampleAndMapAllocateNothing) {
    ClockMapper mapper;
    constexpr double kSdkStart = 10.0;
    for (std::size_t i = 0; i < ClockMapper::kWindowSize; ++i) {
        const double sdk = kSdkStart + 0.01 * static_cast<double>(i);
        mapper.AddSample(sdk, static_cast<HostTime>((sdk + 0.0005) * 1e9));
    }

    double sum = 0.0;
    for (int i = 0; i < kWarmupIterations; ++i) {
        const double sdk = kSdkStart + 0.01 * static_cast<double>(i + 1);
        mapper.AddSample(sdk, static_cast<HostTime>((sdk + 0.0005) * 1e9));
        sum += static_cast<double>(mapper.Map(sdk));
    }

    const std::size_t before = AllocationCount();
    for (int i = 0; i < kMeasuredIterations; ++i) {
        const double sdk = kSdkStart + 0.01 * static_cast<double>(i + 1);
        mapper.AddSample(sdk, static_cast<HostTime>((sdk + 0.0005) * 1e9));
        sum += static_cast<double>(mapper.Map(sdk));
    }
    const std::size_t after = AllocationCount();
    Consume(sum);

    EXPECT_EQ(after - before, 0U) << "ClockMapper::AddSample/Map allocated " << (after - before) << " times over "
                                  << kMeasuredIterations << " iterations";
}

TEST(AllocationTest, PoseFromSdkAllocatesNothing) {
    const float sdk[7] = {0.1F, 0.2F, 0.3F, 0.0F, 0.0F, 0.0F, 1.0F};
    double sum = 0.0;
    for (int i = 0; i < kWarmupIterations; ++i) {
        const Pose pose = PoseFromSdk(sdk);
        sum += pose.position.x + pose.position.y + pose.position.z + pose.rotation.w();
    }

    const std::size_t before = AllocationCount();
    for (int i = 0; i < kMeasuredIterations; ++i) {
        const Pose pose = PoseFromSdk(sdk);
        sum += pose.position.x + pose.position.y + pose.position.z + pose.rotation.w();
    }
    const std::size_t after = AllocationCount();
    Consume(sum);

    EXPECT_EQ(after - before, 0U) << "PoseFromSdk allocated " << (after - before) << " times over "
                                  << kMeasuredIterations << " iterations";
}

} // namespace

// The newest-wins slot contract (S8 Task 2) plus the callback-budget property
// that keeps the SDK callback path allocation-free.
//
// Tear detection uses the FakeStereoSource's per-image property: every image
// is a contiguous byte ramp `(index + c) & 0xFF`, so any mixing of two frames
// breaks the ramp somewhere and fails the self-consistency check without the
// test needing the fake's private bias constants.

#include <atomic>
#include <cstddef>
#include <cstdint>
#include <cstdlib>
#include <new>
#include <thread>

#include "cg/capture/fake_stereo_source.hpp"
#include "cg/capture/newest_frame_slot.hpp"

#include <gtest/gtest.h>

#if defined(_MSC_VER)
#include <malloc.h>
#endif

namespace {

using cg::HostTime;
using cg::Result;
using cg::StereoFrame;
using cg::StereoImage;
using cg::capture::FakeStereoConfig;
using cg::capture::FakeStereoSource;
using cg::capture::FrameSlotGeometry;
using cg::capture::NewestFrameSlot;

/// True when `image` is a contiguous byte ramp; a torn copy from two frames is
/// never a ramp (the fake's biases differ per stream).
bool IsRamp(const StereoImage &image) {
    const std::uint8_t *bytes = image.left;
    const auto size = static_cast<std::size_t>(image.stride) * static_cast<std::size_t>(image.height);
    const std::uint8_t first = bytes[0];
    for (std::size_t i = 0; i < size; ++i) {
        const auto expected = static_cast<std::uint8_t>(first + i);
        if (bytes[i] != expected) {
            return false;
        }
    }
    return true;
}

bool FrameImagesAreRamps(const StereoFrame &frame) {
    return IsRamp(frame.f0) && IsRamp(frame.f0) && IsRamp(frame.f1) && frame.f0.left[0] != frame.f0.right[0] &&
           frame.f0.left[0] != frame.f1.left[0];
}

FrameSlotGeometry GeometryOf(const FakeStereoSource &source) {
    return FrameSlotGeometry{source.Width(), source.Height(), source.Stride()};
}

TEST(NewestFrameSlotContract, PublishThenTakeReturnsTheWholeFrame) {
    FakeStereoConfig config;
    config.width = 64;
    config.height = 8;
    config.stride = 64;
    FakeStereoSource source(config);
    NewestFrameSlot slot(GeometryOf(source));
    ASSERT_TRUE(source.Start(&slot).ok());

    source.EmitFrame();
    StereoFrame out{};
    ASSERT_TRUE(slot.TryTake(out));
    EXPECT_EQ(out.seq, 1U);
    EXPECT_EQ(out.time, 0);
    EXPECT_TRUE(FrameImagesAreRamps(out));
    EXPECT_EQ(slot.FramesPublished(), 1U);
    EXPECT_EQ(slot.FramesTaken(), 1U);
    EXPECT_EQ(slot.Overwritten(), 0U);
    source.Stop();
}

TEST(NewestFrameSlotContract, TakeWithoutAPublishIsFalse) {
    FakeStereoSource source;
    NewestFrameSlot slot(GeometryOf(source));
    ASSERT_TRUE(source.Start(&slot).ok());
    StereoFrame out{};
    EXPECT_FALSE(slot.TryTake(out));
    EXPECT_EQ(slot.FramesTaken(), 0U);
    source.Stop();
}

TEST(NewestFrameSlotContract, BurstKeepsOnlyTheNewestAndCountsOverwrites) {
    FakeStereoSource source;
    NewestFrameSlot slot(GeometryOf(source));
    ASSERT_TRUE(source.Start(&slot).ok());
    for (int i = 0; i < 5; ++i) {
        source.EmitFrame();
    }
    StereoFrame out{};
    ASSERT_TRUE(slot.TryTake(out));
    EXPECT_EQ(out.seq, 5U) << "newest wins";
    EXPECT_EQ(slot.FramesPublished(), 5U);
    EXPECT_EQ(slot.Overwritten(), 4U) << "four frames were dropped by the slot";
    EXPECT_FALSE(slot.TryTake(out)) << "the newest frame was already taken";
    source.Stop();
}

TEST(NewestFrameSlotContract, RepeatedPublishTakeIsNeverTorn) {
    FakeStereoSource source;
    NewestFrameSlot slot(GeometryOf(source));
    ASSERT_TRUE(source.Start(&slot).ok());
    std::uint64_t expected_seq = 1;
    for (int i = 0; i < 200; ++i) {
        source.EmitFrame();
        StereoFrame out{};
        ASSERT_TRUE(slot.TryTake(out));
        EXPECT_EQ(out.seq, expected_seq);
        EXPECT_TRUE(FrameImagesAreRamps(out)) << "frame " << out.seq << " is torn";
        ++expected_seq;
    }
    EXPECT_EQ(slot.TornRetries(), 0U) << "a single-threaded run never needs a retry";
    source.Stop();
}

TEST(NewestFrameSlotContract, GeometryMismatchIsRejectedAndCounted) {
    FakeStereoConfig config;
    config.width = 32;
    config.height = 4;
    config.stride = 32;
    FakeStereoSource source(config);
    NewestFrameSlot slot(FrameSlotGeometry{64, 8, 64});
    ASSERT_TRUE(source.Start(&slot).ok());
    source.EmitFrame();
    ASSERT_TRUE(source.Start(&slot).ok());
    source.EmitFrame();
    EXPECT_EQ(slot.FramesPublished(), 0U);
    EXPECT_EQ(slot.RejectedGeometry(), 2U);
    StereoFrame out{};
    EXPECT_FALSE(slot.TryTake(out));
    source.Stop();
}

TEST(NewestFrameSlotContract, ConcurrentProducerAndConsumerNeverTear) {
    FakeStereoConfig config;
    config.width = 96;
    config.height = 16;
    config.stride = 96;
    FakeStereoSource source(config);
    NewestFrameSlot slot(GeometryOf(source));
    ASSERT_TRUE(source.Start(&slot).ok());

    constexpr std::uint64_t kFrames = 20'000;
    std::uint64_t taken = 0;
    std::uint64_t last_seq = 0;
    std::atomic<bool> producer_done{false};
    const auto drain_one = [&] {
        StereoFrame out{};
        if (!slot.TryTake(out)) {
            return false;
        }
        EXPECT_TRUE(FrameImagesAreRamps(out)) << "torn frame at seq " << out.seq;
        EXPECT_GT(out.seq, last_seq) << "sequence must be strictly increasing";
        last_seq = out.seq;
        ++taken;
        return true;
    };
    std::thread producer([&source, &producer_done] {
        for (std::uint64_t i = 0; i < kFrames; ++i) {
            source.EmitFrame();
        }
        producer_done.store(true, std::memory_order_release);
    });
    while (!producer_done.load(std::memory_order_acquire)) {
        (void)drain_one();
    }
    producer.join();
    while (drain_one()) {
    }
    EXPECT_GE(taken, 1U);
    EXPECT_EQ(slot.FramesPublished(), kFrames);
    EXPECT_EQ(slot.FramesTaken(), taken);
    EXPECT_EQ(slot.Overwritten() + taken, kFrames) << "every published frame is taken or overwritten";
    source.Stop();
}

// --- Callback budget: Publish must not allocate after construction. --------

std::atomic<std::size_t> g_allocation_count{0};

void *Allocate(std::size_t size) {
    g_allocation_count.fetch_add(1, std::memory_order_relaxed);
    void *memory = std::malloc(size == 0 ? 1 : size);
    if (memory == nullptr) {
        throw std::bad_alloc();
    }
    return memory;
}

void *AllocateNoThrow(std::size_t size) noexcept {
    g_allocation_count.fetch_add(1, std::memory_order_relaxed);
    return std::malloc(size == 0 ? 1 : size);
}

#if defined(_MSC_VER)
void *AllocateAligned(std::size_t size, std::size_t alignment) {
    g_allocation_count.fetch_add(1, std::memory_order_relaxed);
    void *memory = _aligned_malloc(size == 0 ? 1 : size, alignment);
    if (memory == nullptr) {
        throw std::bad_alloc();
    }
    return memory;
}
void *AllocateAlignedNoThrow(std::size_t size, std::size_t alignment) noexcept {
    g_allocation_count.fetch_add(1, std::memory_order_relaxed);
    return _aligned_malloc(size == 0 ? 1 : size, alignment);
}
void FreeAligned(void *memory) noexcept { _aligned_free(memory); }
#endif

} // namespace

void *operator new(std::size_t size) { return Allocate(size); }
void *operator new[](std::size_t size) { return Allocate(size); }
void *operator new(std::size_t size, const std::nothrow_t &) noexcept { return AllocateNoThrow(size); }
void *operator new[](std::size_t size, const std::nothrow_t &) noexcept { return AllocateNoThrow(size); }
void operator delete(void *memory) noexcept { std::free(memory); }
void operator delete[](void *memory) noexcept { std::free(memory); }
void operator delete(void *memory, std::size_t) noexcept { std::free(memory); }
void operator delete[](void *memory, std::size_t) noexcept { std::free(memory); }
void operator delete(void *memory, const std::nothrow_t &) noexcept { std::free(memory); }
void operator delete[](void *memory, const std::nothrow_t &) noexcept { std::free(memory); }
#if defined(_MSC_VER)
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
void operator delete(void *memory, std::align_val_t) noexcept { FreeAligned(memory); }
void operator delete[](void *memory, std::align_val_t) noexcept { FreeAligned(memory); }
void operator delete(void *memory, std::size_t, std::align_val_t) noexcept { FreeAligned(memory); }
void operator delete[](void *memory, std::size_t, std::align_val_t) noexcept { FreeAligned(memory); }
#endif

namespace {

TEST(NewestFrameSlotContract, PublishAfterWarmUpDoesNotAllocate) {
    FakeStereoSource source;
    NewestFrameSlot slot(GeometryOf(source));
    ASSERT_TRUE(source.Start(&slot).ok());

    for (int i = 0; i < 8; ++i) {
        source.EmitFrame(); // warm-up: exactly here the slot must be ready
    }
    const std::size_t before = g_allocation_count.load(std::memory_order_relaxed);
    for (int i = 0; i < 1'000; ++i) {
        source.EmitFrame();
    }
    const std::size_t after = g_allocation_count.load(std::memory_order_relaxed);
    EXPECT_EQ(after, before) << "the callback path must not allocate";
    source.Stop();
}

} // namespace

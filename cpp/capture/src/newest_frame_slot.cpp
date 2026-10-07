#include "cg/capture/newest_frame_slot.hpp"

#include <algorithm>
#include <cstring>
#include <thread>
#include <utility>

namespace cg::capture {

namespace {

constexpr std::uint64_t kWriteFlag = 1; // version parity: odd while writing

[[nodiscard]] int EffectiveStride(const FrameSlotGeometry &geometry) noexcept {
    return geometry.stride > 0 ? geometry.stride : geometry.width;
}

} // namespace

NewestFrameSlot::NewestFrameSlot(FrameSlotGeometry geometry) : geometry_(geometry) {
    geometry_.stride = EffectiveStride(geometry_);
    image_bytes_ = static_cast<std::size_t>(std::max(geometry_.stride, 0)) *
                   static_cast<std::size_t>(std::max(geometry_.height, 0));
    for (Slot *slot : {&slots_.front(), &slots_.back(), &consumer_}) {
        slot->l0.resize(image_bytes_);
        slot->r0.resize(image_bytes_);
        slot->l1.resize(image_bytes_);
        slot->r1.resize(image_bytes_);
    }
}

void NewestFrameSlot::OnFrame(const StereoFrame &frame) noexcept {
    if (!GeometryMatches(frame)) {
        rejected_geometry_.fetch_add(1, std::memory_order_relaxed);
        return;
    }

    Slot &slot = slots_.at(write_);
    const std::uint64_t version = slot.version.load(std::memory_order_relaxed);
    slot.version.store(version + kWriteFlag, std::memory_order_relaxed);
    slot.time = frame.time;
    slot.seq = frame.seq;
    CopyImages(frame, slot, image_bytes_);
    slot.version.store(version + 2 * kWriteFlag, std::memory_order_release);

    active_.store(write_, std::memory_order_release);
    write_ ^= 1U;
    published_.fetch_add(1, std::memory_order_relaxed);
}

bool NewestFrameSlot::TryTake(StereoFrame &out) noexcept {
    for (;;) {
        // Re-read the active slot on every attempt: after a torn copy the
        // producer may have moved on to a newer frame in the other slot.
        const std::size_t index = active_.load(std::memory_order_acquire);
        const Slot &slot = slots_.at(index);
        const std::uint64_t before = slot.version.load(std::memory_order_acquire);
        if ((before & kWriteFlag) != 0) {
            std::this_thread::yield(); // the producer is mid-publish into this slot
            continue;
        }
        if (before == last_taken_version_.at(index)) {
            return false; // nothing settled since the last take from this slot
        }
        CopyImages(slot, consumer_, image_bytes_);
        const std::uint64_t after = slot.version.load(std::memory_order_acquire);
        if (after != before) {
            torn_retries_.fetch_add(1, std::memory_order_relaxed);
            continue;
        }
        last_taken_version_.at(index) = before;
        ++taken_;
        out.time = slot.time;
        out.seq = slot.seq;
        out.f0 = ViewFor(consumer_.l0, consumer_.r0, geometry_.width, geometry_.height, geometry_.stride);
        out.f1 = ViewFor(consumer_.l1, consumer_.r1, geometry_.width, geometry_.height, geometry_.stride);
        return true;
    }
}

std::uint64_t NewestFrameSlot::FramesPublished() const noexcept { return published_.load(std::memory_order_relaxed); }

std::uint64_t NewestFrameSlot::FramesTaken() const noexcept { return taken_; }

std::uint64_t NewestFrameSlot::Overwritten() const noexcept {
    const std::uint64_t published = published_.load(std::memory_order_relaxed);
    return published > taken_ ? published - taken_ : 0;
}

std::uint64_t NewestFrameSlot::TornRetries() const noexcept { return torn_retries_.load(std::memory_order_relaxed); }

std::uint64_t NewestFrameSlot::RejectedGeometry() const noexcept {
    return rejected_geometry_.load(std::memory_order_relaxed);
}

int NewestFrameSlot::Width() const noexcept { return geometry_.width; }

int NewestFrameSlot::Height() const noexcept { return geometry_.height; }

int NewestFrameSlot::Stride() const noexcept { return geometry_.stride; }

std::size_t NewestFrameSlot::ImageBytes() const noexcept { return image_bytes_; }

bool NewestFrameSlot::GeometryMatches(const StereoFrame &frame) const noexcept {
    return frame.f0.width == geometry_.width && frame.f0.height == geometry_.height &&
           frame.f0.stride == geometry_.stride && frame.f1.width == geometry_.width &&
           frame.f1.height == geometry_.height && frame.f1.stride == geometry_.stride;
}

StereoImage NewestFrameSlot::ViewFor(const std::vector<std::uint8_t> &left, const std::vector<std::uint8_t> &right,
                                     int width, int height, int stride) noexcept {
    return StereoImage{left.data(), right.data(), width, height, stride};
}

void NewestFrameSlot::CopyImages(const StereoFrame &frame, Slot &slot, std::size_t image_bytes) noexcept {
    std::memcpy(slot.l0.data(), frame.f0.left, image_bytes);
    std::memcpy(slot.r0.data(), frame.f0.right, image_bytes);
    std::memcpy(slot.l1.data(), frame.f1.left, image_bytes);
    std::memcpy(slot.r1.data(), frame.f1.right, image_bytes);
}

void NewestFrameSlot::CopyImages(const Slot &from, Slot &to, std::size_t image_bytes) noexcept {
    std::memcpy(to.l0.data(), from.l0.data(), image_bytes);
    std::memcpy(to.r0.data(), from.r0.data(), image_bytes);
    std::memcpy(to.l1.data(), from.l1.data(), image_bytes);
    std::memcpy(to.r1.data(), from.r1.data(), image_bytes);
}

} // namespace cg::capture

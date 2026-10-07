#include "cg/capture/newest_frame_slot.hpp"

#include <algorithm>
#include <atomic>
#include <cstdint>
#include <thread>

namespace cg::capture {

namespace {

constexpr std::uint64_t kWriteFlag = 1; // version parity: odd while writing
constexpr std::size_t kWordBytes = sizeof(std::uint64_t);

[[nodiscard]] int EffectiveStride(const FrameSlotGeometry &geometry) noexcept {
    return geometry.stride > 0 ? geometry.stride : geometry.width;
}

[[nodiscard]] std::size_t WordCount(std::size_t bytes) noexcept { return (bytes + kWordBytes - 1U) / kWordBytes; }

// NOLINTBEGIN(cppcoreguidelines-pro-type-reinterpret-cast, cppcoreguidelines-pro-type-const-cast,
// cppcoreguidelines-pro-bounds-pointer-arithmetic) — the payload is byte data stored in word
// vectors; the atomic word copy needs the reinterpretation, and the alignment probe
// bounds the pointer arithmetic to the caller's buffer.
/// Copies `bytes` bytes from `src` to `dst` through relaxed `std::atomic_ref`
/// accesses. The destination is one of our aligned word vectors; when the
/// source is 8-aligned the copy runs word-wise, otherwise byte-wise.
void AtomicCopy(std::uint64_t *dst, const std::uint8_t *src, std::size_t bytes) noexcept {
    const bool words_ok = (reinterpret_cast<std::uintptr_t>(src) % kWordBytes) == 0;
    if (words_ok) {
        const std::size_t words = bytes / kWordBytes;
        const auto *src_words = reinterpret_cast<const std::uint64_t *>(src);
        for (std::size_t i = 0; i < words; ++i) {
            const std::uint64_t value = std::atomic_ref<std::uint64_t>(*const_cast<std::uint64_t *>(src_words + i))
                                            .load(std::memory_order_relaxed);
            std::atomic_ref<std::uint64_t>(dst[i]).store(value, std::memory_order_relaxed);
        }
        auto *dst_bytes = reinterpret_cast<std::uint8_t *>(dst);
        for (std::size_t i = words * kWordBytes; i < bytes; ++i) {
            const std::uint8_t value =
                std::atomic_ref<std::uint8_t>(const_cast<std::uint8_t &>(src[i])).load(std::memory_order_relaxed);
            std::atomic_ref<std::uint8_t>(dst_bytes[i]).store(value, std::memory_order_relaxed);
        }
        return;
    }
    auto *dst_bytes = reinterpret_cast<std::uint8_t *>(dst);
    for (std::size_t i = 0; i < bytes; ++i) {
        const std::uint8_t value =
            std::atomic_ref<std::uint8_t>(const_cast<std::uint8_t &>(src[i])).load(std::memory_order_relaxed);
        std::atomic_ref<std::uint8_t>(dst_bytes[i]).store(value, std::memory_order_relaxed);
    }
}
// NOLINTEND(cppcoreguidelines-pro-type-reinterpret-cast, cppcoreguidelines-pro-type-const-cast,
// cppcoreguidelines-pro-bounds-pointer-arithmetic)

} // namespace

NewestFrameSlot::NewestFrameSlot(FrameSlotGeometry geometry) : geometry_(geometry) {
    geometry_.stride = EffectiveStride(geometry_);
    image_bytes_ = static_cast<std::size_t>(std::max(geometry_.stride, 0)) *
                   static_cast<std::size_t>(std::max(geometry_.height, 0));
    const std::size_t words = WordCount(image_bytes_);
    for (Slot *slot : {&slots_.front(), &slots_.back(), &consumer_}) {
        slot->l0.resize(words);
        slot->r0.resize(words);
        slot->l1.resize(words);
        slot->r1.resize(words);
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
    slot.time.store(frame.time, std::memory_order_relaxed);
    slot.seq.store(frame.seq, std::memory_order_relaxed);
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
        // The header is read inside the version bracket: reading it after the
        // final check would let a producer rewrite pair a newer seq with an
        // older version tag and return a duplicate frame.
        const HostTime time = slot.time.load(std::memory_order_relaxed);
        const std::uint64_t seq = slot.seq.load(std::memory_order_relaxed);
        const std::uint64_t after = slot.version.load(std::memory_order_acquire);
        if (after != before) {
            torn_retries_.fetch_add(1, std::memory_order_relaxed);
            continue;
        }
        last_taken_version_.at(index) = before;
        ++taken_;
        out.time = time;
        out.seq = seq;
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

StereoImage NewestFrameSlot::ViewFor(const std::vector<std::uint64_t> &left, const std::vector<std::uint64_t> &right,
                                     int width, int height, int stride) noexcept {
    // NOLINTBEGIN(cppcoreguidelines-pro-type-reinterpret-cast) — the payload is byte data in word storage.
    return StereoImage{reinterpret_cast<const std::uint8_t *>(left.data()),
                       reinterpret_cast<const std::uint8_t *>(right.data()), width, height, stride};
    // NOLINTEND(cppcoreguidelines-pro-type-reinterpret-cast)
}

void NewestFrameSlot::CopyImages(const StereoFrame &frame, Slot &slot, std::size_t image_bytes) noexcept {
    AtomicCopy(slot.l0.data(), frame.f0.left, image_bytes);
    AtomicCopy(slot.r0.data(), frame.f0.right, image_bytes);
    AtomicCopy(slot.l1.data(), frame.f1.left, image_bytes);
    AtomicCopy(slot.r1.data(), frame.f1.right, image_bytes);
}

void NewestFrameSlot::CopyImages(const Slot &from, Slot &to, std::size_t image_bytes) noexcept {
    // Both buffers are ours and aligned, so the same atomic word copy applies;
    // the source is viewed as bytes for the shared helper.
    // NOLINTNEXTLINE(cppcoreguidelines-pro-type-reinterpret-cast)
    AtomicCopy(to.l0.data(), reinterpret_cast<const std::uint8_t *>(from.l0.data()), image_bytes);
    // NOLINTNEXTLINE(cppcoreguidelines-pro-type-reinterpret-cast)
    AtomicCopy(to.r0.data(), reinterpret_cast<const std::uint8_t *>(from.r0.data()), image_bytes);
    // NOLINTNEXTLINE(cppcoreguidelines-pro-type-reinterpret-cast)
    AtomicCopy(to.l1.data(), reinterpret_cast<const std::uint8_t *>(from.l1.data()), image_bytes);
    // NOLINTNEXTLINE(cppcoreguidelines-pro-type-reinterpret-cast)
    AtomicCopy(to.r1.data(), reinterpret_cast<const std::uint8_t *>(from.r1.data()), image_bytes);
}

} // namespace cg::capture

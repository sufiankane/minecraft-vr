#pragma once

#include <array>
#include <atomic>
#include <cstddef>
#include <cstdint>
#include <vector>

#include "ports.hpp"

namespace cg::capture {

/// Producer slots the handoff alternates between (double buffering).
inline constexpr std::size_t kSlotCount = 2;

/// Fixed geometry the slot allocates for. The device geometry is constant per
/// session (U-03 decides the numbers), so a frame with any other layout is a
/// bug worth surfacing: `Publish` rejects it and counts it.
struct FrameSlotGeometry {
    int width = 0;
    int height = 0;
    /// Row stride in bytes; 0 means `width` (packed rows).
    int stride = 0;
};

/// Lock-free single-slot "newest frame wins" handoff between the SDK camera
/// callback (the single producer, dossier 3.3's budgeted callback) and one
/// consumer (the recorder writer thread).
///
/// Design: two producer-owned slots alternate. `Publish` copies into the
/// inactive slot under a per-slot version (odd while writing), then releases
/// the active index. A consumer that misses frames simply never reads the
/// skipped ones; the slot counts them as overwritten. `TryTake` seqlock-copies
/// the newest settled version into consumer-owned buffers and verifies the
/// version across the copy, so a producer overwrite during the copy is
/// retried, never torn; the returned views belong to the consumer buffers and
/// stay valid until the next `TryTake`.
///
/// The producer never allocates after construction and never touches consumer
/// buffers; the consumer never blocks the producer.
class NewestFrameSlot final : public IStereoFrameSink {
  public:
    explicit NewestFrameSlot(FrameSlotGeometry geometry);

    /// The SDK camera callback path. Copies the frame into the inactive slot
    /// and publishes it; O(frame bytes), no allocation, no blocking.
    void OnFrame(const StereoFrame &frame) noexcept override;

    /// Returns the newest frame that settled after the last successful take;
    /// false when no new frame arrived. The returned views stay valid until
    /// the next `TryTake`.
    bool TryTake(StereoFrame &out) noexcept;

    std::uint64_t FramesPublished() const noexcept;
    std::uint64_t FramesTaken() const noexcept;
    /// Frames published since the last successful take (the slot's drops).
    std::uint64_t Overwritten() const noexcept;
    /// Copy retries caused by a producer overwrite during a take.
    std::uint64_t TornRetries() const noexcept;
    /// Frames dropped because their geometry did not match the slot.
    std::uint64_t RejectedGeometry() const noexcept;

    int Width() const noexcept;
    int Height() const noexcept;
    int Stride() const noexcept;
    /// Bytes of one image buffer including stride padding.
    std::size_t ImageBytes() const noexcept;

  private:
    struct Slot {
        std::vector<std::uint8_t> l0;
        std::vector<std::uint8_t> r0;
        std::vector<std::uint8_t> l1;
        std::vector<std::uint8_t> r1;
        /// Frame header copied with the images (inside the version bracket).
        HostTime time = 0;
        std::uint64_t seq = 0;
        /// Even when settled, odd while the producer writes this slot.
        std::atomic<std::uint64_t> version{0};
    };

    [[nodiscard]] bool GeometryMatches(const StereoFrame &frame) const noexcept;
    [[nodiscard]] static StereoImage ViewFor(const std::vector<std::uint8_t> &left,
                                             const std::vector<std::uint8_t> &right, int width, int height,
                                             int stride) noexcept;
    static void CopyImages(const StereoFrame &frame, Slot &slot, std::size_t image_bytes) noexcept;
    static void CopyImages(const Slot &from, Slot &to, std::size_t image_bytes) noexcept;

    FrameSlotGeometry geometry_;
    std::size_t image_bytes_ = 0;
    std::array<Slot, kSlotCount> slots_{};
    Slot consumer_;
    std::atomic<std::size_t> active_{0};
    std::size_t write_ = 0;
    std::array<std::uint64_t, kSlotCount> last_taken_version_{};
    std::atomic<std::uint64_t> published_{0};
    std::uint64_t taken_ = 0;
    std::atomic<std::uint64_t> torn_retries_{0};
    std::atomic<std::uint64_t> rejected_geometry_{0};
};

} // namespace cg::capture

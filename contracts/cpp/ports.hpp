#pragma once

#include <cstdint>

#include "cg/core_math/pose.hpp"
#include "cg/core_math/time.hpp"
#include "result.hpp"

namespace cg {

using core_math::HostTime;
using core_math::Pose;

/// A signed duration in nanoseconds on the host timeline (additive contract
/// vocabulary; ADR-0009).
struct Duration {
    std::int64_t ns;
};

/// Tracking quality of a head sample (additive contract vocabulary; ADR-0009).
/// `Stable` is the zero value, matching `CG_TRACK_STABLE`.
enum class TrackState { Stable, Unstable, Lost };

// Dossier section 5.2, verbatim:
struct HeadSample {
    HostTime time;
    Pose pose;
    TrackState state;
    uint32_t seq;
};

class IHeadPoseSource {
  public:
    virtual ~IHeadPoseSource() = default;
    // Starts delivery; idempotent. Thread-safe.
    virtual Result<void> Start() = 0;
    virtual void Stop() noexcept = 0;
    // Wait-free. Returns the newest sample; never blocks; never allocates.
    virtual bool TryGetLatest(HeadSample &out, Duration predict) const noexcept = 0;
    // Recentres heading to the current orientation (yaw only).
    virtual Result<void> Recenter() = 0;
};

// Dossier section 5.3, verbatim:
struct StereoImage { // views are valid only during the callback
    const std::uint8_t *left;
    const std::uint8_t *right;
    int width;
    int height;
    int stride; // stride in bytes
};
struct StereoFrame {
    HostTime time;
    std::uint64_t seq;
    StereoImage f0; // frame 0 pair
    StereoImage f1; // frame 1 pair (semantics resolved by ADR from U-03)
};
class IStereoFrameSink {
  public:
    virtual ~IStereoFrameSink() = default;
    virtual void OnFrame(const StereoFrame &frame) noexcept = 0; // must return quickly
};
class IStereoFrameSource {
  public:
    virtual ~IStereoFrameSource() = default;
    virtual Result<void> Start(IStereoFrameSink *sink) = 0;
    virtual void Stop() noexcept = 0;
};

} // namespace cg

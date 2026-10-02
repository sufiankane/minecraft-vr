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

} // namespace cg

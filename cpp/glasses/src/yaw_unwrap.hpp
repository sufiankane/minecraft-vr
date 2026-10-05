#pragma once

namespace cg::glasses::detail {

/// Half a full turn in degrees: the fold threshold of `UnwrapYawDeltaDegrees`.
inline constexpr double kHalfTurnDegrees = 180.0;

/// A full turn in degrees: the amount folded away across the seam.
inline constexpr double kFullTurnDegrees = 360.0;

/// Folds a yaw difference into `(-180, 180]` degrees.
///
/// `YawDegrees` returns a wrapped angle in `[-180, 180]`, so a step that
/// crosses the seam reads as a jump of roughly 360 degrees. Every source that
/// derives a yaw rate from wrapped readings (`ReplayHeadPoseSource`,
/// `VitureHeadPoseSource`) routes the difference through this helper so a seam
/// crossing yields the short way round and the predicted pose does not snap.
[[nodiscard]] inline double UnwrapYawDeltaDegrees(double delta_deg) noexcept {
    if (delta_deg > kHalfTurnDegrees) {
        return delta_deg - kFullTurnDegrees;
    }
    if (delta_deg < -kHalfTurnDegrees) {
        return delta_deg + kFullTurnDegrees;
    }
    return delta_deg;
}

} // namespace cg::glasses::detail

#pragma once

namespace cg::glasses::detail {

/// Folds a yaw difference into `(-180, 180]` degrees.
///
/// `YawDegrees` returns a wrapped angle in `[-180, 180]`, so a step that
/// crosses the seam reads as a jump of roughly 360 degrees. Every source that
/// derives a yaw rate from wrapped readings (`ReplayHeadPoseSource`,
/// `VitureHeadPoseSource`) routes the difference through this helper so a seam
/// crossing yields the short way round and the predicted pose does not snap.
[[nodiscard]] inline double UnwrapYawDeltaDegrees(double delta_deg) noexcept {
    if (delta_deg > 180.0) {
        return delta_deg - 360.0;
    }
    if (delta_deg < -180.0) {
        return delta_deg + 360.0;
    }
    return delta_deg;
}

} // namespace cg::glasses::detail

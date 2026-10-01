#pragma once

#include "cg/core_math/pose.hpp"
#include "cg/core_math/quat.hpp"
#include "cg/core_math/vec3.hpp"

namespace cg::core_math {

/// Builds an internal `Pose` from a VITURE SDK pose.
///
/// `sdk` has layout `[px, py, pz, qw, qx, qy, qz]` in floats: the position is
/// widened to doubles and the quaternion is normalised through
/// `Quat::FromComponents` (degenerate input becomes identity). No allocation.
[[nodiscard]] Pose PoseFromSdk(const float sdk[7]) noexcept;

/// Maps an internal position to Unity space: `(x, y, -z)`. No allocation.
[[nodiscard]] Vec3 ToUnityPosition(const Vec3 &position) noexcept;

/// Maps an internal rotation to Unity space: `(w, -x, -y, z)`. No allocation.
[[nodiscard]] Quat ToUnityRotation(const Quat &rotation) noexcept;

/// Maps an internal pose to Unity space, applying both flips above.
/// No allocation.
[[nodiscard]] Pose ToUnityPose(const Pose &pose) noexcept;

// The Unity flips live here, once per language (ADR-0004): the S6 Unity adapter
// must call these functions and only narrow the result to `float`; it must not
// re-derive the flip.

} // namespace cg::core_math

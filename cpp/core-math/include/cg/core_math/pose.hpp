#pragma once

#include "cg/core_math/quat.hpp"
#include "cg/core_math/vec3.hpp"

namespace cg::core_math {

/// A rigid transform: a rotation followed by a translation.
///
/// `position` is the translation in metres and `rotation` is a unit
/// quaternion; the transform maps a point `v` to
/// `position + rotation.Rotate(v)`. Right-handed, Y up, forward -Z (ADR-0004).
/// A value type: every operation below is `noexcept` and allocates nothing.
struct Pose {
    /// Translation in metres.
    Vec3 position;

    /// Unit rotation.
    Quat rotation;
};

/// Composes `child` into `parent`'s frame.
///
/// The result's position is
/// `parent.position + parent.rotation.Rotate(child.position)` and its rotation
/// is `parent.rotation * child.rotation`. No allocation.
[[nodiscard]] Pose Compose(const Pose &parent, const Pose &child) noexcept;

/// Inverse transform of `pose`.
///
/// The rotation is the conjugate and the position is the negated inverse
/// rotation of the original position. No allocation.
[[nodiscard]] Pose Inverse(const Pose &pose) noexcept;

/// Maps `point` from the local frame of `pose` into its parent frame:
/// `pose.position + pose.rotation.Rotate(point)`. No allocation.
[[nodiscard]] Vec3 TransformPoint(const Pose &pose, const Vec3 &point) noexcept;

} // namespace cg::core_math

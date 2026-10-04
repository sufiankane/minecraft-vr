#pragma once

#include "cg/core_math/vec3.hpp"

namespace cg::core_math {

/// Default tolerance of `Quat::IsNormalized`: the squared-norm distance from
/// exactly 1 accepted by the contract tests.
inline constexpr double kDefaultNormalizeTolerance = 1e-9;

class Quat;

/// Returns `q` scaled to unit length.
///
/// Degenerate input (squared norm below `1e-24` or non-finite) yields the
/// identity quaternion. No allocation.
[[nodiscard]] Quat Normalize(const Quat &q) noexcept;

/// Normalised shortest-path spherical linear interpolation between `a` and `b`.
///
/// `t` is clamped by the caller to `[0, 1]` for interpolation; `t = 0` yields
/// `a` and `t = 1` yields `b` up to double rounding. A negative dot product
/// flips `b` so the shortest arc is taken, and a dot product above `0.9995`
/// switches to a normalised linear interpolation to avoid dividing by a
/// vanishing `sin(theta)`. The result is unit in every branch. No allocation.
[[nodiscard]] Quat Slerp(const Quat &a, const Quat &b, double t) noexcept;

/// A rotation stored as a unit quaternion `(w, x, y, z)` of doubles.
///
/// Right-handed, Y up, forward -Z (ADR-0004). Every instance is constructed
/// through `FromComponents` or `FromAxisAngle`, so a live `Quat` is always
/// unit and finite (degenerate input becomes `kIdentity`). All operations are
/// `noexcept` and allocate nothing; the Hamilton product is intentionally not
/// renormalised.
class Quat {
  public:
    /// The identity rotation `(1, 0, 0, 0)`.
    static const Quat kIdentity;

    /// Builds a unit quaternion from raw `(w, x, y, z)` components.
    ///
    /// If the squared norm is below `1e-24` or any component is non-finite,
    /// returns `kIdentity`. No allocation.
    [[nodiscard]] static Quat FromComponents(double w, double x, double y, double z) noexcept;

    /// Builds a unit quaternion rotating `radians` about `axis` (right-hand rule).
    ///
    /// The axis is normalised first; a zero or non-finite axis, or a non-finite
    /// `radians`, yields `kIdentity`. No allocation.
    [[nodiscard]] static Quat FromAxisAngle(Vec3 axis, double radians) noexcept;

    Quat() = delete;
    Quat(const Quat &) noexcept = default;
    Quat(Quat &&) noexcept = default;
    Quat &operator=(const Quat &) noexcept = default;
    Quat &operator=(Quat &&) noexcept = default;
    ~Quat() = default;

    /// Scalar part `w`. No allocation.
    [[nodiscard]] double w() const noexcept;

    /// Vector part `x`. No allocation.
    [[nodiscard]] double x() const noexcept;

    /// Vector part `y`. No allocation.
    [[nodiscard]] double y() const noexcept;

    /// Vector part `z`. No allocation.
    [[nodiscard]] double z() const noexcept;

    /// Conjugate `(w, -x, -y, -z)`. No allocation.
    [[nodiscard]] Quat Conjugate() const noexcept;

    /// Inverse rotation. Equals `Conjugate()` because the quaternion is unit.
    /// No allocation.
    [[nodiscard]] Quat Inverse() const noexcept;

    /// Dot product of the four components. No allocation.
    [[nodiscard]] double Dot(const Quat &other) const noexcept;

    /// Rotates `v` by this quaternion.
    ///
    /// Uses `t = 2 * Cross(q_xyz, v); result = v + w * t + Cross(q_xyz, t)`
    /// in exactly that floating-point order (shared with C#). No allocation.
    [[nodiscard]] Vec3 Rotate(const Vec3 &v) const noexcept;

    /// Hamilton product; the result is not renormalised. No allocation.
    [[nodiscard]] Quat operator*(const Quat &other) const noexcept;

    /// True when the squared norm is within `tolerance` (inclusive) of 1.
    /// No allocation.
    [[nodiscard]] bool IsNormalized(double tolerance = kDefaultNormalizeTolerance) const noexcept;

  private:
    friend Quat Normalize(const Quat &q) noexcept;
    friend Quat Slerp(const Quat &a, const Quat &b, double t) noexcept;

    Quat(double w, double x, double y, double z) noexcept;

    double w_;
    double x_;
    double y_;
    double z_;
};

} // namespace cg::core_math

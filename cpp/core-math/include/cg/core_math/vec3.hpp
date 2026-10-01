#pragma once

namespace cg::core_math {

/// A three-component vector of doubles in metres.
///
/// Right-handed, Y up, forward -Z, X right (ADR-0004). A value type: every
/// operation below is `noexcept` and allocates nothing.
struct Vec3 {
    double x;
    double y;
    double z;
};

/// Component-wise addition. No allocation.
[[nodiscard]] Vec3 operator+(const Vec3 &a, const Vec3 &b) noexcept;

/// Component-wise subtraction. No allocation.
[[nodiscard]] Vec3 operator-(const Vec3 &a, const Vec3 &b) noexcept;

/// Component-wise negation. No allocation.
[[nodiscard]] Vec3 operator-(const Vec3 &v) noexcept;

/// Multiplication of every component by `scalar`. No allocation.
[[nodiscard]] Vec3 operator*(const Vec3 &v, double scalar) noexcept;

/// Multiplication of every component by `scalar`. No allocation.
[[nodiscard]] Vec3 operator*(double scalar, const Vec3 &v) noexcept;

/// Division of every component by `scalar`.
///
/// Precondition: `scalar` is non-zero. No allocation.
[[nodiscard]] Vec3 operator/(const Vec3 &v, double scalar) noexcept;

/// Dot product `a.x * b.x + a.y * b.y + a.z * b.z`. No allocation.
[[nodiscard]] double Dot(const Vec3 &a, const Vec3 &b) noexcept;

/// Right-handed cross product `(a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x)`.
/// No allocation.
[[nodiscard]] Vec3 Cross(const Vec3 &a, const Vec3 &b) noexcept;

/// Euclidean length `sqrt(Dot(v, v))`. No allocation.
[[nodiscard]] double Length(const Vec3 &v) noexcept;

/// Unit vector in the direction of `v`.
///
/// The zero vector maps to the zero vector (no NaNs). No allocation.
[[nodiscard]] Vec3 Normalized(const Vec3 &v) noexcept;

/// True when every component of `a` is within `tolerance` (inclusive) of `b`.
/// No allocation.
[[nodiscard]] bool NearlyEquals(const Vec3 &a, const Vec3 &b, double tolerance) noexcept;

} // namespace cg::core_math

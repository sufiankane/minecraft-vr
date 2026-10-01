#include "cg/core_math/vec3.hpp"

#include <cmath>

namespace cg::core_math {

Vec3 operator+(const Vec3 &a, const Vec3 &b) noexcept { return Vec3{a.x + b.x, a.y + b.y, a.z + b.z}; }

Vec3 operator-(const Vec3 &a, const Vec3 &b) noexcept { return Vec3{a.x - b.x, a.y - b.y, a.z - b.z}; }

Vec3 operator-(const Vec3 &v) noexcept { return Vec3{-v.x, -v.y, -v.z}; }

Vec3 operator*(const Vec3 &v, double scalar) noexcept { return Vec3{v.x * scalar, v.y * scalar, v.z * scalar}; }

Vec3 operator*(double scalar, const Vec3 &v) noexcept { return v * scalar; }

Vec3 operator/(const Vec3 &v, double scalar) noexcept { return Vec3{v.x / scalar, v.y / scalar, v.z / scalar}; }

double Dot(const Vec3 &a, const Vec3 &b) noexcept { return (a.x * b.x) + (a.y * b.y) + (a.z * b.z); }

Vec3 Cross(const Vec3 &a, const Vec3 &b) noexcept {
    return Vec3{(a.y * b.z) - (a.z * b.y), (a.z * b.x) - (a.x * b.z), (a.x * b.y) - (a.y * b.x)};
}

double Length(const Vec3 &v) noexcept { return std::sqrt(Dot(v, v)); }

Vec3 Normalized(const Vec3 &v) noexcept {
    const double length = Length(v);
    if (length == 0.0) {
        return Vec3{0.0, 0.0, 0.0};
    }
    return Vec3{v.x / length, v.y / length, v.z / length};
}

bool NearlyEquals(const Vec3 &a, const Vec3 &b, double tolerance) noexcept {
    return std::abs(a.x - b.x) <= tolerance && std::abs(a.y - b.y) <= tolerance && std::abs(a.z - b.z) <= tolerance;
}

} // namespace cg::core_math

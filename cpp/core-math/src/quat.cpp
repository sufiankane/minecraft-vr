#include "cg/core_math/quat.hpp"

#include <cmath>

namespace cg::core_math {

namespace {

constexpr double kMinimumSquaredNorm = 1e-24;
constexpr double kLinearInterpolationDotThreshold = 0.9995;

} // namespace

const Quat Quat::kIdentity(1.0, 0.0, 0.0, 0.0);

Quat::Quat(double w, double x, double y, double z) noexcept : w_(w), x_(x), y_(y), z_(z) {}

Quat Quat::FromComponents(double w, double x, double y, double z) noexcept {
    const double squaredNorm = (w * w) + (x * x) + (y * y) + (z * z);
    if (!std::isfinite(squaredNorm) || squaredNorm < kMinimumSquaredNorm) {
        return kIdentity;
    }
    const double inverseLength = 1.0 / std::sqrt(squaredNorm);
    return Quat(w * inverseLength, x * inverseLength, y * inverseLength, z * inverseLength);
}

Quat Quat::FromAxisAngle(Vec3 axis, double radians) noexcept {
    const double squaredAxisLength = (axis.x * axis.x) + (axis.y * axis.y) + (axis.z * axis.z);
    if (!std::isfinite(squaredAxisLength) || squaredAxisLength < kMinimumSquaredNorm) {
        return kIdentity;
    }
    const double inverseLength = 1.0 / std::sqrt(squaredAxisLength);
    const double halfAngle = radians * 0.5;
    const double sine = std::sin(halfAngle);
    return Quat(std::cos(halfAngle), axis.x * inverseLength * sine, axis.y * inverseLength * sine,
                axis.z * inverseLength * sine);
}

double Quat::w() const noexcept { return w_; }

double Quat::x() const noexcept { return x_; }

double Quat::y() const noexcept { return y_; }

double Quat::z() const noexcept { return z_; }

Quat Quat::Conjugate() const noexcept { return Quat(w_, -x_, -y_, -z_); }

Quat Quat::Inverse() const noexcept { return Conjugate(); }

double Quat::Dot(const Quat &other) const noexcept {
    return (w_ * other.w_) + (x_ * other.x_) + (y_ * other.y_) + (z_ * other.z_);
}

Vec3 Quat::Rotate(const Vec3 &v) const noexcept {
    const Vec3 q(x_, y_, z_);
    const Vec3 t = 2.0 * Cross(q, v);
    return v + (w_ * t) + Cross(q, t);
}

Quat Quat::operator*(const Quat &other) const noexcept {
    return Quat((w_ * other.w_) - (x_ * other.x_) - (y_ * other.y_) - (z_ * other.z_),
                (w_ * other.x_) + (x_ * other.w_) + (y_ * other.z_) - (z_ * other.y_),
                (w_ * other.y_) - (x_ * other.z_) + (y_ * other.w_) + (z_ * other.x_),
                (w_ * other.z_) + (x_ * other.y_) - (y_ * other.x_) + (z_ * other.w_));
}

bool Quat::IsNormalized(double tolerance) const noexcept {
    const double squaredNorm = (w_ * w_) + (x_ * x_) + (y_ * y_) + (z_ * z_);
    return std::abs(squaredNorm - 1.0) <= tolerance;
}

Quat Normalize(const Quat &q) noexcept {
    const double squaredNorm = q.Dot(q);
    if (!std::isfinite(squaredNorm) || squaredNorm < kMinimumSquaredNorm) {
        return Quat::kIdentity;
    }
    const double inverseLength = 1.0 / std::sqrt(squaredNorm);
    return Quat(q.w_ * inverseLength, q.x_ * inverseLength, q.y_ * inverseLength, q.z_ * inverseLength);
}

Quat Slerp(const Quat &a, const Quat &b, double t) noexcept {
    double dot = a.Dot(b);
    double bw = b.w_;
    double bx = b.x_;
    double by = b.y_;
    double bz = b.z_;
    if (dot < 0.0) {
        dot = -dot;
        bw = -bw;
        bx = -bx;
        by = -by;
        bz = -bz;
    }
    if (dot > kLinearInterpolationDotThreshold) {
        const double oneMinusT = 1.0 - t;
        return Normalize(Quat((oneMinusT * a.w_) + (t * bw), (oneMinusT * a.x_) + (t * bx),
                              (oneMinusT * a.y_) + (t * by), (oneMinusT * a.z_) + (t * bz)));
    }
    const double theta = std::acos(dot);
    const double sineTheta = std::sin(theta);
    const double s1 = std::sin((1.0 - t) * theta) / sineTheta;
    const double s2 = std::sin(t * theta) / sineTheta;
    return Normalize(
        Quat((s1 * a.w_) + (s2 * bw), (s1 * a.x_) + (s2 * bx), (s1 * a.y_) + (s2 * by), (s1 * a.z_) + (s2 * bz)));
}

} // namespace cg::core_math

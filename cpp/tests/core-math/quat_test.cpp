#include "cg/core_math/quat.hpp"
#include "cg/core_math/vec3.hpp"

#include <cmath>
#include <limits>

#include <gtest/gtest.h>

namespace {

using cg::core_math::Quat;
using cg::core_math::Slerp;
using cg::core_math::Vec3;

// cos(pi/4), the component of a 90 degree rotation about a principal axis.
constexpr double kHalfSqrt2 = 0.7071067811865476;
constexpr double kPi = 3.14159265358979323846;

void ExpectQuatNear(const Quat &q, double w, double x, double y, double z, double tolerance) {
    EXPECT_NEAR(q.w(), w, tolerance);
    EXPECT_NEAR(q.x(), x, tolerance);
    EXPECT_NEAR(q.y(), y, tolerance);
    EXPECT_NEAR(q.z(), z, tolerance);
}

void ExpectVecNear(const Vec3 &v, double x, double y, double z, double tolerance) {
    EXPECT_NEAR(v.x, x, tolerance);
    EXPECT_NEAR(v.y, y, tolerance);
    EXPECT_NEAR(v.z, z, tolerance);
}

TEST(Quat, FromComponentsNormalizesAxisAligned) {
    const Quat q = Quat::FromComponents(2.0, 0.0, 0.0, 0.0);
    EXPECT_DOUBLE_EQ(q.w(), 1.0);
    EXPECT_DOUBLE_EQ(q.x(), 0.0);
    EXPECT_DOUBLE_EQ(q.y(), 0.0);
    EXPECT_DOUBLE_EQ(q.z(), 0.0);
}

TEST(Quat, FromComponentsNormalizesDiagonal) {
    const Quat q = Quat::FromComponents(1.0, 1.0, 1.0, 1.0);
    EXPECT_DOUBLE_EQ(q.w(), 0.5);
    EXPECT_DOUBLE_EQ(q.x(), 0.5);
    EXPECT_DOUBLE_EQ(q.y(), 0.5);
    EXPECT_DOUBLE_EQ(q.z(), 0.5);
}

TEST(Quat, FromComponentsDegenerateIsIdentity) {
    const Quat q = Quat::FromComponents(0.0, 0.0, 0.0, 0.0);
    EXPECT_DOUBLE_EQ(q.w(), 1.0);
    EXPECT_DOUBLE_EQ(q.x(), 0.0);
    EXPECT_DOUBLE_EQ(q.y(), 0.0);
    EXPECT_DOUBLE_EQ(q.z(), 0.0);
}

TEST(Quat, FromComponentsNaNIsIdentity) {
    const double nan = std::numeric_limits<double>::quiet_NaN();
    const Quat q = Quat::FromComponents(nan, 0.0, 0.0, 0.0);
    EXPECT_DOUBLE_EQ(q.w(), 1.0);
    EXPECT_DOUBLE_EQ(q.x(), 0.0);
    EXPECT_DOUBLE_EQ(q.y(), 0.0);
    EXPECT_DOUBLE_EQ(q.z(), 0.0);
}

TEST(Quat, FromComponentsInfinityIsIdentity) {
    const double infinity = std::numeric_limits<double>::infinity();
    const Quat q = Quat::FromComponents(0.0, 0.0, infinity, 0.0);
    EXPECT_DOUBLE_EQ(q.w(), 1.0);
    EXPECT_DOUBLE_EQ(q.x(), 0.0);
    EXPECT_DOUBLE_EQ(q.y(), 0.0);
    EXPECT_DOUBLE_EQ(q.z(), 0.0);
}

TEST(Quat, FromAxisAngleYaw90RotatesForwardToNegativeX) {
    const Quat q = Quat::FromAxisAngle(Vec3{0.0, 1.0, 0.0}, kPi / 2.0);
    ExpectQuatNear(q, kHalfSqrt2, 0.0, kHalfSqrt2, 0.0, 1e-15);

    const Vec3 rotated = q.Rotate(Vec3{0.0, 0.0, -1.0});
    ExpectVecNear(rotated, -1.0, 0.0, 0.0, 1e-15);
}

TEST(Quat, FromAxisAngleZeroAxisIsIdentity) {
    const Quat q = Quat::FromAxisAngle(Vec3{0.0, 0.0, 0.0}, 1.0);
    EXPECT_DOUBLE_EQ(q.w(), 1.0);
    EXPECT_DOUBLE_EQ(q.x(), 0.0);
    EXPECT_DOUBLE_EQ(q.y(), 0.0);
    EXPECT_DOUBLE_EQ(q.z(), 0.0);
}

TEST(Quat, IdentityRotationLeavesVectorUnchanged) {
    const Vec3 v{0.25, -1.5, 3.0};
    const Vec3 rotated = Quat::kIdentity.Rotate(v);
    ExpectVecNear(rotated, v.x, v.y, v.z, 0.0);
}

TEST(Quat, Roll180FlipsX) {
    const Quat q = Quat::FromAxisAngle(Vec3{0.0, 0.0, 1.0}, kPi);
    const Vec3 rotated = q.Rotate(Vec3{1.0, 0.0, 0.0});
    ExpectVecNear(rotated, -1.0, 0.0, 0.0, 1e-15);
}

TEST(Quat, HamiltonProductKnownCase) {
    const Quat a = Quat::FromAxisAngle(Vec3{0.0, 1.0, 0.0}, kPi / 2.0);
    const Quat b = Quat::FromAxisAngle(Vec3{1.0, 0.0, 0.0}, kPi / 2.0);
    const Quat product = a * b;
    ExpectQuatNear(product, 0.5, 0.5, 0.5, -0.5, 1e-15);
}

TEST(Quat, InverseOfYaw90IsConjugate) {
    const Quat q = Quat::FromAxisAngle(Vec3{0.0, 1.0, 0.0}, kPi / 2.0);
    ExpectQuatNear(q.Inverse(), kHalfSqrt2, 0.0, -kHalfSqrt2, 0.0, 1e-15);
}

TEST(Quat, ProductWithConjugateIsIdentity) {
    const Quat q = Quat::FromAxisAngle(Vec3{1.0, 2.0, 3.0}, 0.7);
    ExpectQuatNear(q * q.Conjugate(), 1.0, 0.0, 0.0, 0.0, 1e-15);
}

TEST(Quat, SlerpMidpointIs22Point5Degrees) {
    const Quat mid = Slerp(Quat::kIdentity, Quat::FromAxisAngle(Vec3{0.0, 1.0, 0.0}, kPi / 2.0), 0.5);
    ExpectQuatNear(mid, 0.9238795325112867, 0.0, 0.3826834323650898, 0.0, 1e-12);
}

TEST(Quat, SlerpEndpointsAreExact) {
    const Quat a = Quat::kIdentity;
    const Quat b = Quat::FromAxisAngle(Vec3{0.0, 1.0, 0.0}, kPi / 2.0);
    ExpectQuatNear(Slerp(a, b, 0.0), a.w(), a.x(), a.y(), a.z(), 1e-15);
    ExpectQuatNear(Slerp(a, b, 1.0), b.w(), b.x(), b.y(), b.z(), 1e-15);
}

TEST(Quat, SlerpNearParallelUsesLinearInterpolation) {
    const Quat a = Quat::kIdentity;
    const Quat b = Quat::FromAxisAngle(Vec3{0.0, 1.0, 0.0}, 1e-5);
    EXPECT_GT(a.Dot(b), 0.9995);

    const Quat mid = Slerp(a, b, 0.5);
    ExpectQuatNear(mid, 0.999999999996875, 0.0, 2.4999999999973958e-6, 0.0, 1e-12);
    EXPECT_TRUE(mid.IsNormalized(1e-9));
}

TEST(Quat, SlerpOppositeQuaternionsTakesShortestPath) {
    const Quat a = Quat::kIdentity;
    const Quat b = Quat::FromComponents(-1.0, 0.0, 0.0, 0.0);

    const Quat mid = Slerp(a, b, 0.5);
    ExpectQuatNear(mid, 1.0, 0.0, 0.0, 0.0, 1e-15);
    EXPECT_TRUE(mid.IsNormalized(1e-9));

    ExpectQuatNear(Slerp(a, b, 1.0), 1.0, 0.0, 0.0, 0.0, 1e-15);
}

TEST(Quat, IsNormalizedReportsUnitQuaternions) {
    EXPECT_TRUE(Quat::kIdentity.IsNormalized(1e-9));
    EXPECT_TRUE(Quat::FromComponents(1.0, 1.0, 1.0, 1.0).IsNormalized(1e-9));
    EXPECT_TRUE(Quat::FromAxisAngle(Vec3{1.0, 2.0, 3.0}, 0.7).IsNormalized(1e-9));
}

} // namespace

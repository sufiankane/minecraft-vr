#include "cg/core_math/pose.hpp"
#include "cg/core_math/quat.hpp"
#include "cg/core_math/vec3.hpp"

#include <gtest/gtest.h>

namespace {

using cg::core_math::Compose;
using cg::core_math::Inverse;
using cg::core_math::Pose;
using cg::core_math::Quat;
using cg::core_math::TransformPoint;
using cg::core_math::Vec3;

// cos(pi/4), the component of a 90 degree rotation about a principal axis.
constexpr double kHalfSqrt2 = 0.7071067811865476;
constexpr double kPi = 3.14159265358979323846;

void ExpectVecNear(const Vec3 &v, double x, double y, double z, double tolerance) {
    EXPECT_NEAR(v.x, x, tolerance);
    EXPECT_NEAR(v.y, y, tolerance);
    EXPECT_NEAR(v.z, z, tolerance);
}

void ExpectQuatNear(const Quat &q, double w, double x, double y, double z, double tolerance) {
    EXPECT_NEAR(q.w(), w, tolerance);
    EXPECT_NEAR(q.x(), x, tolerance);
    EXPECT_NEAR(q.y(), y, tolerance);
    EXPECT_NEAR(q.z(), z, tolerance);
}

TEST(Pose, ComposeWithInverseIsIdentityWithinTolerance) {
    const Pose pose{Vec3{0.3, -1.2, 2.5}, Quat::FromAxisAngle(Vec3{1.0, 2.0, 3.0}, 0.7)};

    const Pose after = Compose(pose, Inverse(pose));
    ExpectVecNear(after.position, 0.0, 0.0, 0.0, 1e-12);
    ExpectQuatNear(after.rotation, 1.0, 0.0, 0.0, 0.0, 1e-12);

    const Pose before = Compose(Inverse(pose), pose);
    ExpectVecNear(before.position, 0.0, 0.0, 0.0, 1e-12);
    ExpectQuatNear(before.rotation, 1.0, 0.0, 0.0, 0.0, 1e-12);
}

TEST(Pose, InverseYaw90AtX1) {
    const Pose pose{Vec3{1.0, 0.0, 0.0}, Quat::FromAxisAngle(Vec3{0.0, 1.0, 0.0}, kPi / 2.0)};

    const Pose inverse = Inverse(pose);
    ExpectVecNear(inverse.position, 0.0, 0.0, -1.0, 1e-15);
    ExpectQuatNear(inverse.rotation, kHalfSqrt2, 0.0, -kHalfSqrt2, 0.0, 1e-15);
}

TEST(Pose, TransformPointRotatesThenTranslates) {
    const Pose pose{Vec3{1.0, 2.0, 3.0}, Quat::FromAxisAngle(Vec3{0.0, 1.0, 0.0}, kPi / 2.0)};

    const Vec3 transformed = TransformPoint(pose, Vec3{0.0, 0.0, -1.0});
    ExpectVecNear(transformed, 0.0, 2.0, 3.0, 1e-12);
}

TEST(Pose, TransformPointRoundTripsThroughInverse) {
    const Pose pose{Vec3{0.3, -1.2, 2.5}, Quat::FromAxisAngle(Vec3{1.0, 2.0, 3.0}, 0.7)};
    const Vec3 point{-4.0, 0.5, 2.0};

    const Vec3 roundTrip = TransformPoint(Inverse(pose), TransformPoint(pose, point));
    ExpectVecNear(roundTrip, point.x, point.y, point.z, 1e-12);
}

TEST(Pose, ComposeTranslatesChildThroughParentRotation) {
    const Pose parent{Vec3{1.0, 2.0, 3.0}, Quat::FromAxisAngle(Vec3{0.0, 1.0, 0.0}, kPi / 2.0)};
    const Pose child{Vec3{0.0, 0.0, -1.0}, Quat::kIdentity};

    const Pose composed = Compose(parent, child);
    ExpectVecNear(composed.position, 0.0, 2.0, 3.0, 1e-12);
    ExpectQuatNear(composed.rotation, kHalfSqrt2, 0.0, kHalfSqrt2, 0.0, 1e-12);
}

TEST(Pose, ComposeMultipliesParentAndChildRotations) {
    const Pose parent{Vec3{0.0, 0.0, 0.0}, Quat::FromAxisAngle(Vec3{0.0, 1.0, 0.0}, kPi / 2.0)};
    const Pose child{Vec3{0.0, 0.0, 0.0}, Quat::FromAxisAngle(Vec3{1.0, 0.0, 0.0}, kPi / 2.0)};

    const Pose composed = Compose(parent, child);
    ExpectVecNear(composed.position, 0.0, 0.0, 0.0, 0.0);
    ExpectQuatNear(composed.rotation, 0.5, 0.5, 0.5, -0.5, 1e-15);
}

} // namespace

#include "cg/core_math/convert.hpp"
#include "cg/core_math/pose.hpp"
#include "cg/core_math/quat.hpp"
#include "cg/core_math/vec3.hpp"

#include <gtest/gtest.h>

namespace {

using cg::core_math::Pose;
using cg::core_math::PoseFromSdk;
using cg::core_math::Quat;
using cg::core_math::ToUnityPose;
using cg::core_math::ToUnityPosition;
using cg::core_math::ToUnityRotation;
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

TEST(Convert, PoseFromSdkReadsLayoutAndNormalizesRotation) {
    const float sdk[7] = {0.1F, 0.2F, 0.3F, 0.0F, 0.0F, 0.0F, 2.0F};

    const Pose pose = PoseFromSdk(sdk);
    ExpectVecNear(pose.position, 0.1, 0.2, 0.3, 1e-6);
    EXPECT_DOUBLE_EQ(pose.rotation.w(), 0.0);
    EXPECT_DOUBLE_EQ(pose.rotation.x(), 0.0);
    EXPECT_DOUBLE_EQ(pose.rotation.y(), 0.0);
    EXPECT_DOUBLE_EQ(pose.rotation.z(), 1.0);
}

TEST(Convert, PoseFromSdkNormalizesUnitScaleRotation) {
    const float sdk[7] = {0.0F, 0.0F, 0.0F, 1.0F, 1.0F, 1.0F, 1.0F};

    const Pose pose = PoseFromSdk(sdk);
    ExpectVecNear(pose.position, 0.0, 0.0, 0.0, 0.0);
    EXPECT_DOUBLE_EQ(pose.rotation.w(), 0.5);
    EXPECT_DOUBLE_EQ(pose.rotation.x(), 0.5);
    EXPECT_DOUBLE_EQ(pose.rotation.y(), 0.5);
    EXPECT_DOUBLE_EQ(pose.rotation.z(), 0.5);
}

TEST(Convert, PoseFromSdkDegenerateRotationIsIdentity) {
    const float sdk[7] = {0.0F, 0.0F, 0.0F, 0.0F, 0.0F, 0.0F, 0.0F};

    const Pose pose = PoseFromSdk(sdk);
    ExpectVecNear(pose.position, 0.0, 0.0, 0.0, 0.0);
    EXPECT_DOUBLE_EQ(pose.rotation.w(), 1.0);
    EXPECT_DOUBLE_EQ(pose.rotation.x(), 0.0);
    EXPECT_DOUBLE_EQ(pose.rotation.y(), 0.0);
    EXPECT_DOUBLE_EQ(pose.rotation.z(), 0.0);
}

TEST(Convert, ToUnityPositionFlipsZ) {
    const Vec3 unity = ToUnityPosition(Vec3{1.0, 2.0, -3.0});
    EXPECT_DOUBLE_EQ(unity.x, 1.0);
    EXPECT_DOUBLE_EQ(unity.y, 2.0);
    EXPECT_DOUBLE_EQ(unity.z, 3.0);
}

TEST(Convert, ToUnityRotationIdentityIsIdentity) {
    const Quat unity = ToUnityRotation(Quat::kIdentity);
    EXPECT_DOUBLE_EQ(unity.w(), 1.0);
    EXPECT_DOUBLE_EQ(unity.x(), 0.0);
    EXPECT_DOUBLE_EQ(unity.y(), 0.0);
    EXPECT_DOUBLE_EQ(unity.z(), 0.0);
}

TEST(Convert, ToUnityRotationYaw90FlipsVectorPart) {
    const Quat yaw90 = Quat::FromAxisAngle(Vec3{0.0, 1.0, 0.0}, kPi / 2.0);

    const Quat unity = ToUnityRotation(yaw90);
    ExpectQuatNear(unity, kHalfSqrt2, 0.0, -kHalfSqrt2, 0.0, 1e-15);
}

TEST(Convert, UnityYawMapsConvertedForwardToExpectedUnityForward) {
    const Quat internalYaw = Quat::FromAxisAngle(Vec3{0.0, 1.0, 0.0}, kPi / 2.0);
    const Vec3 expected = ToUnityPosition(internalYaw.Rotate(Vec3{0.0, 0.0, -1.0}));

    const Quat unityYaw = ToUnityRotation(internalYaw);
    const Vec3 actual = unityYaw.Rotate(ToUnityPosition(Vec3{0.0, 0.0, -1.0}));

    ExpectVecNear(actual, expected.x, expected.y, expected.z, 1e-12);
    ExpectVecNear(actual, -1.0, 0.0, 0.0, 1e-12);
}

TEST(Convert, ToUnityPoseConvertsPositionAndRotation) {
    const Pose pose{Vec3{1.0, 2.0, -3.0}, Quat::FromAxisAngle(Vec3{0.0, 1.0, 0.0}, kPi / 2.0)};

    const Pose unity = ToUnityPose(pose);
    ExpectVecNear(unity.position, 1.0, 2.0, 3.0, 1e-15);
    ExpectQuatNear(unity.rotation, kHalfSqrt2, 0.0, -kHalfSqrt2, 0.0, 1e-15);
}

} // namespace

#include "cg/core_math/vec3.hpp"

#include <gtest/gtest.h>

namespace {

using cg::core_math::Cross;
using cg::core_math::Dot;
using cg::core_math::Length;
using cg::core_math::NearlyEquals;
using cg::core_math::Normalized;
using cg::core_math::Vec3;

TEST(Vec3, DotHandValue) { EXPECT_DOUBLE_EQ(Dot(Vec3{1.0, 2.0, 3.0}, Vec3{4.0, 5.0, 6.0}), 32.0); }

TEST(Vec3, CrossHandValue) {
    const Vec3 result = Cross(Vec3{1.0, 2.0, 3.0}, Vec3{4.0, 5.0, 6.0});
    EXPECT_DOUBLE_EQ(result.x, -3.0);
    EXPECT_DOUBLE_EQ(result.y, 6.0);
    EXPECT_DOUBLE_EQ(result.z, -3.0);
}

TEST(Vec3, CrossBasisVectors) {
    const Vec3 result = Cross(Vec3{1.0, 0.0, 0.0}, Vec3{0.0, 1.0, 0.0});
    EXPECT_DOUBLE_EQ(result.x, 0.0);
    EXPECT_DOUBLE_EQ(result.y, 0.0);
    EXPECT_DOUBLE_EQ(result.z, 1.0);
}

TEST(Vec3, ArithmeticOperators) {
    const Vec3 a{1.0, 2.0, 3.0};
    const Vec3 b{4.0, 5.0, 6.0};

    const Vec3 sum = a + b;
    EXPECT_DOUBLE_EQ(sum.x, 5.0);
    EXPECT_DOUBLE_EQ(sum.y, 7.0);
    EXPECT_DOUBLE_EQ(sum.z, 9.0);

    const Vec3 difference = b - a;
    EXPECT_DOUBLE_EQ(difference.x, 3.0);
    EXPECT_DOUBLE_EQ(difference.y, 3.0);
    EXPECT_DOUBLE_EQ(difference.z, 3.0);

    const Vec3 negated = -a;
    EXPECT_DOUBLE_EQ(negated.x, -1.0);
    EXPECT_DOUBLE_EQ(negated.y, -2.0);
    EXPECT_DOUBLE_EQ(negated.z, -3.0);

    const Vec3 rightScaled = a * 2.0;
    EXPECT_DOUBLE_EQ(rightScaled.x, 2.0);
    EXPECT_DOUBLE_EQ(rightScaled.y, 4.0);
    EXPECT_DOUBLE_EQ(rightScaled.z, 6.0);

    const Vec3 leftScaled = 2.0 * a;
    EXPECT_DOUBLE_EQ(leftScaled.x, 2.0);
    EXPECT_DOUBLE_EQ(leftScaled.y, 4.0);
    EXPECT_DOUBLE_EQ(leftScaled.z, 6.0);

    const Vec3 halved = b / 2.0;
    EXPECT_DOUBLE_EQ(halved.x, 2.0);
    EXPECT_DOUBLE_EQ(halved.y, 2.5);
    EXPECT_DOUBLE_EQ(halved.z, 3.0);
}

TEST(Vec3, LengthHandValue) { EXPECT_DOUBLE_EQ(Length(Vec3{3.0, 4.0, 12.0}), 13.0); }

TEST(Vec3, NormalizedHasUnitLength) {
    const Vec3 normalised = Normalized(Vec3{1.0, 2.0, 3.0});
    EXPECT_NEAR(Length(normalised), 1.0, 1e-12);
}

TEST(Vec3, NormalizedZeroStaysZero) {
    const Vec3 normalised = Normalized(Vec3{0.0, 0.0, 0.0});
    EXPECT_DOUBLE_EQ(normalised.x, 0.0);
    EXPECT_DOUBLE_EQ(normalised.y, 0.0);
    EXPECT_DOUBLE_EQ(normalised.z, 0.0);
}

TEST(Vec3, NearlyEqualsToleranceBoundaries) {
    EXPECT_TRUE(NearlyEquals(Vec3{1.0, 2.0, 3.0}, Vec3{1.0, 2.0, 3.0}, 0.0));
    EXPECT_TRUE(NearlyEquals(Vec3{0.0, 0.0, 0.0}, Vec3{1e-3, 0.0, 0.0}, 1e-3));
    EXPECT_FALSE(NearlyEquals(Vec3{0.0, 0.0, 0.0}, Vec3{1.0000001e-3, 0.0, 0.0}, 1e-3));
    EXPECT_TRUE(NearlyEquals(Vec3{0.0, 0.0, 0.0}, Vec3{0.0, 0.0, 1e-3}, 1e-3));
    EXPECT_FALSE(NearlyEquals(Vec3{0.0, 0.0, 0.0}, Vec3{0.0, 1e-3, 0.0}, 1e-12));
}

} // namespace

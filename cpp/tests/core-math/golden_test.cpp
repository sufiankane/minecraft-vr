#include "cg/core_math/clock_mapper.hpp"
#include "cg/core_math/convert.hpp"
#include "cg/core_math/pose.hpp"
#include "cg/core_math/quat.hpp"
#include "cg/core_math/time.hpp"
#include "cg/core_math/vec3.hpp"

#include <array>
#include <cstddef>
#include <cstdint>
#include <fstream>
#include <map>
#include <set>
#include <string>

#include <gtest/gtest.h>
#include <nlohmann/json.hpp>

#ifndef CG_GOLDEN_FIXTURE
#error "CG_GOLDEN_FIXTURE must be defined as the path to contracts/golden/transforms.json"
#endif

namespace {

using cg::core_math::ClockMapper;
using cg::core_math::Compose;
using cg::core_math::Cross;
using cg::core_math::Dot;
using cg::core_math::HostTime;
using cg::core_math::Inverse;
using cg::core_math::Length;
using cg::core_math::Normalize;
using cg::core_math::Normalized;
using cg::core_math::Pose;
using cg::core_math::PoseFromSdk;
using cg::core_math::Quat;
using cg::core_math::Slerp;
using cg::core_math::ToUnityPose;
using cg::core_math::ToUnityPosition;
using cg::core_math::ToUnityRotation;
using cg::core_math::TransformPoint;
using cg::core_math::Vec3;

using Json = nlohmann::json;

/// Reads a fixture `[x, y, z]` array.
Vec3 ToVec3(const Json &value) {
    return Vec3{value.at(0).get<double>(), value.at(1).get<double>(), value.at(2).get<double>()};
}

/// Reads a fixture `[w, x, y, z]` array through the public normalising constructor.
Quat ToQuat(const Json &value) {
    return Quat::FromComponents(value.at(0).get<double>(), value.at(1).get<double>(), value.at(2).get<double>(),
                                value.at(3).get<double>());
}

/// Reads a fixture `{"p": [...], "q": [...]}` pose.
Pose ToPose(const Json &value) { return Pose{ToVec3(value.at("p")), ToQuat(value.at("q"))}; }

/// Compares a scalar against a fixture number, absolutely, and reports one comparison.
int ExpectScalar(double actual, const Json &expected, double tolerance) {
    EXPECT_NEAR(actual, expected.get<double>(), tolerance);
    return 1;
}

/// Compares a vector element-wise against a fixture value, absolutely, and reports three comparisons.
int ExpectVec3(const Vec3 &actual, const Json &expected, double tolerance) {
    if (!expected.is_array() || expected.size() != 3U) {
        ADD_FAILURE() << "expected a 3-component vector";
        return 0;
    }
    EXPECT_NEAR(actual.x, expected.at(0).get<double>(), tolerance);
    EXPECT_NEAR(actual.y, expected.at(1).get<double>(), tolerance);
    EXPECT_NEAR(actual.z, expected.at(2).get<double>(), tolerance);
    return 3;
}

/// Compares a quaternion element-wise against a fixture `[w, x, y, z]` value, absolutely, and reports four comparisons.
int ExpectQuat(const Quat &actual, const Json &expected, double tolerance) {
    if (!expected.is_array() || expected.size() != 4U) {
        ADD_FAILURE() << "expected a 4-component quaternion";
        return 0;
    }
    EXPECT_NEAR(actual.w(), expected.at(0).get<double>(), tolerance);
    EXPECT_NEAR(actual.x(), expected.at(1).get<double>(), tolerance);
    EXPECT_NEAR(actual.y(), expected.at(2).get<double>(), tolerance);
    EXPECT_NEAR(actual.z(), expected.at(3).get<double>(), tolerance);
    return 4;
}

/// Compares a pose against a fixture `{"p": [...], "q": [...]}` value, absolutely, and reports its comparisons.
int ExpectPose(const Pose &actual, const Json &expected, double tolerance) {
    return ExpectVec3(actual.position, expected.at("p"), tolerance) +
           ExpectQuat(actual.rotation, expected.at("q"), tolerance);
}

int RunVec3Dot(const Json &input, const Json &expected, double tolerance) {
    return ExpectScalar(Dot(ToVec3(input.at("a")), ToVec3(input.at("b"))), expected, tolerance);
}

int RunVec3Cross(const Json &input, const Json &expected, double tolerance) {
    return ExpectVec3(Cross(ToVec3(input.at("a")), ToVec3(input.at("b"))), expected, tolerance);
}

int RunVec3Length(const Json &input, const Json &expected, double tolerance) {
    return ExpectScalar(Length(ToVec3(input.at("a"))), expected, tolerance);
}

int RunVec3Normalize(const Json &input, const Json &expected, double tolerance) {
    return ExpectVec3(Normalized(ToVec3(input.at("a"))), expected, tolerance);
}

int RunQuatNormalize(const Json &input, const Json &expected, double tolerance) {
    return ExpectQuat(Normalize(ToQuat(input.at("q"))), expected, tolerance);
}

int RunQuatRotate(const Json &input, const Json &expected, double tolerance) {
    return ExpectVec3(ToQuat(input.at("q")).Rotate(ToVec3(input.at("v"))), expected, tolerance);
}

int RunQuatMultiply(const Json &input, const Json &expected, double tolerance) {
    return ExpectQuat(ToQuat(input.at("a")) * ToQuat(input.at("b")), expected, tolerance);
}

int RunQuatInverse(const Json &input, const Json &expected, double tolerance) {
    return ExpectQuat(ToQuat(input.at("q")).Inverse(), expected, tolerance);
}

int RunQuatSlerp(const Json &input, const Json &expected, double tolerance) {
    return ExpectQuat(Slerp(ToQuat(input.at("a")), ToQuat(input.at("b")), input.at("t").get<double>()), expected,
                      tolerance);
}

int RunPoseCompose(const Json &input, const Json &expected, double tolerance) {
    return ExpectPose(Compose(ToPose(input.at("a")), ToPose(input.at("b"))), expected, tolerance);
}

int RunPoseInverse(const Json &input, const Json &expected, double tolerance) {
    return ExpectPose(Inverse(ToPose(input.at("a"))), expected, tolerance);
}

int RunPoseTransformPoint(const Json &input, const Json &expected, double tolerance) {
    return ExpectVec3(TransformPoint(ToPose(input.at("a")), ToVec3(input.at("v"))), expected, tolerance);
}

int RunSdkPoseToInternal(const Json &input, const Json &expected, double tolerance) {
    const Json &sdk = input.at("sdk");
    if (!sdk.is_array() || sdk.size() != 7U) {
        ADD_FAILURE() << "expected a 7-component SDK pose";
        return 0;
    }
    std::array<float, 7> values{};
    for (std::size_t i = 0; i < values.size(); ++i) {
        values.at(i) = static_cast<float>(sdk.at(i).get<double>());
    }
    return ExpectPose(PoseFromSdk(values.data()), expected, tolerance);
}

int RunUnityPosition(const Json &input, const Json &expected, double tolerance) {
    return ExpectVec3(ToUnityPosition(ToVec3(input.at("p"))), expected, tolerance);
}

int RunUnityRotation(const Json &input, const Json &expected, double tolerance) {
    return ExpectQuat(ToUnityRotation(ToQuat(input.at("q"))), expected, tolerance);
}

int RunUnityPose(const Json &input, const Json &expected, double tolerance) {
    return ExpectPose(ToUnityPose(ToPose(input.at("a"))), expected, tolerance);
}

/// `clock_map` is the one op with exact integer expectations: the samples and
/// the `Map` result are integer nanoseconds, compared without tolerance.
int RunClockMap(const Json &input, const Json &expected, double /*tolerance*/) {
    const Json &samples = input.at("samples");
    if (!samples.is_array()) {
        ADD_FAILURE() << "clock_map samples must be an array";
        return 0;
    }

    ClockMapper mapper;
    for (const Json &sample : samples) {
        if (!sample.is_array() || sample.size() != 2U) {
            ADD_FAILURE() << "each clock_map sample must be [sdk_seconds, host_ns]";
            return 0;
        }
        mapper.AddSample(sample.at(0).get<double>(), sample.at(1).get<HostTime>());
    }

    EXPECT_EQ(mapper.Map(input.at("query_sdk").get<double>()), expected.get<HostTime>());
    return 1;
}

/// Runs one fixture case and returns the number of comparisons it performed
/// (zero signals a stub or an unimplemented branch).
using CaseRunner = int (*)(const Json &input, const Json &expected, double tolerance);

/// Dispatch table: one entry per op the driver implements. The keys of this
/// table are compared against the ops observed in the fixture, so an op in the
/// fixture without a branch fails as an unknown op and a branch without a case
/// fails as unreachable.
const std::map<std::string, CaseRunner> &Dispatcher() {
    static const std::map<std::string, CaseRunner> kDispatch{
        {"vec3_dot", &RunVec3Dot},
        {"vec3_cross", &RunVec3Cross},
        {"vec3_length", &RunVec3Length},
        {"vec3_normalize", &RunVec3Normalize},
        {"quat_normalize", &RunQuatNormalize},
        {"quat_rotate", &RunQuatRotate},
        {"quat_multiply", &RunQuatMultiply},
        {"quat_inverse", &RunQuatInverse},
        {"quat_slerp", &RunQuatSlerp},
        {"pose_compose", &RunPoseCompose},
        {"pose_inverse", &RunPoseInverse},
        {"pose_transform_point", &RunPoseTransformPoint},
        {"sdk_pose_to_internal", &RunSdkPoseToInternal},
        {"unity_position", &RunUnityPosition},
        {"unity_rotation", &RunUnityRotation},
        {"unity_pose", &RunUnityPose},
        {"clock_map", &RunClockMap},
    };
    return kDispatch;
}

std::set<std::string> DispatcherOps() {
    std::set<std::string> ops;
    for (const auto &entry : Dispatcher()) {
        ops.insert(entry.first);
    }
    return ops;
}

TEST(GoldenFixture, AllCasesMatchFixture) {
    std::ifstream stream(CG_GOLDEN_FIXTURE);
    ASSERT_TRUE(stream.is_open()) << "cannot open golden fixture '" << CG_GOLDEN_FIXTURE << "'";

    const Json document = Json::parse(stream, nullptr, false);
    ASSERT_FALSE(document.is_discarded()) << "cannot parse golden fixture '" << CG_GOLDEN_FIXTURE << "'";

    ASSERT_TRUE(document.contains("schema")) << "golden fixture has no schema";
    EXPECT_EQ(document.at("schema").get<int>(), 1) << "golden fixture schema must be 1";

    ASSERT_TRUE(document.contains("tolerance")) << "golden fixture has no tolerance";
    ASSERT_TRUE(document.at("tolerance").is_number()) << "golden fixture tolerance must be a number";
    const double tolerance = document.at("tolerance").get<double>();
    EXPECT_GT(tolerance, 0.0) << "golden fixture tolerance must be positive";

    ASSERT_TRUE(document.contains("cases")) << "golden fixture has no cases";
    const Json &cases = document.at("cases");
    ASSERT_TRUE(cases.is_array()) << "golden fixture cases must be an array";
    EXPECT_FALSE(cases.empty()) << "golden fixture has no cases";

    const std::map<std::string, CaseRunner> &dispatcher = Dispatcher();
    std::set<std::string> fixture_ops;
    std::set<std::string> visited_ops;

    for (const Json &entry : cases) {
        const std::string name = entry.at("name").get<std::string>();
        const std::string op = entry.at("op").get<std::string>();
        SCOPED_TRACE(::testing::Message() << "case " << name << " (op " << op << ")");
        fixture_ops.insert(op);

        const auto runner = dispatcher.find(op);
        if (runner == dispatcher.end()) {
            ADD_FAILURE() << "unknown op '" << op << "'";
            continue;
        }

        visited_ops.insert(op);
        if (runner->second(entry.at("input"), entry.at("expected"), tolerance) == 0) {
            ADD_FAILURE() << "dispatch for op '" << op << "' performed no comparisons";
        }
    }

    EXPECT_EQ(visited_ops, fixture_ops) << "every op defined in the fixture must be dispatched";
    EXPECT_EQ(DispatcherOps(), fixture_ops) << "every dispatch branch must be reachable from the fixture";
}

/// CXX-16: independent recomputation of golden values, decoupled from the
/// JSON fixture. Every expected constant below is hand-derived and written
/// literally with its derivation in the comment; nothing is read from
/// `transforms.json`, so a corrupted fixture (or one regenerated from the
/// implementation, which would be tautological) cannot make this pass. The
/// fixture driver above covers the whole op surface; this test re-derives the
/// arithmetic for the same cases from first principles. A Python derivation
/// script was deliberately not added: the constants below are small enough to
/// verify by hand, and a generator would have to re-implement the maths it is
/// meant to cross-check.
TEST(GoldenFixture, HandDerivedConstantsMatchIndependently) {
    // sqrt(2)/2 = sin/cos of 45 degrees, to 32 significant digits.
    constexpr double kSqrt2Over2 = 0.70710678118654752440084436210485;
    // cos(22.5 deg) and sin(22.5 deg) (half of the 45-degree half-angle used
    // by a halfway slerp between identity and a 90-degree rotation).
    constexpr double kCos22_5 = 0.92387953251128675612818318939679;
    constexpr double kSin22_5 = 0.38268343236508977172845998403040;

    const Quat yaw90 = Quat::FromComponents(kSqrt2Over2, 0.0, kSqrt2Over2, 0.0);
    const Quat pitch90 = Quat::FromComponents(kSqrt2Over2, kSqrt2Over2, 0.0, 0.0);

    // Hamilton product (w1*w2 - v1.v2, w1*v2 + w2*v1 + v1 x v2) for
    // v1 = (0,s,0), v2 = (s,0,0), s^2 = 1/2:
    //   w = s^2 = 1/2, x = s^2 = 1/2, y = s^2 = 1/2, z = -s^2 = -1/2.
    const Quat product = yaw90 * pitch90;
    EXPECT_NEAR(product.w(), 0.5, 1e-15);
    EXPECT_NEAR(product.x(), 0.5, 1e-15);
    EXPECT_NEAR(product.y(), 0.5, 1e-15);
    EXPECT_NEAR(product.z(), -0.5, 1e-15);

    // A +90-degree pitch about +X carries +Y to +Z; a +90-degree yaw about +Y
    // carries -Z to -X (right-handed, y up).
    const Vec3 pitched_up = pitch90.Rotate(Vec3{0.0, 1.0, 0.0});
    EXPECT_NEAR(pitched_up.x, 0.0, 1e-15);
    EXPECT_NEAR(pitched_up.y, 0.0, 1e-15);
    EXPECT_NEAR(pitched_up.z, 1.0, 1e-15);
    const Vec3 yawed_forward = yaw90.Rotate(Vec3{0.0, 0.0, -1.0});
    EXPECT_NEAR(yawed_forward.x, -1.0, 1e-15);
    EXPECT_NEAR(yawed_forward.y, 0.0, 1e-15);
    EXPECT_NEAR(yawed_forward.z, 0.0, 1e-15);

    // A halfway slerp to a 90-degree rotation is a 45-degree rotation:
    // (cos 22.5 deg, 0, sin 22.5 deg, 0).
    const Quat midpoint = Slerp(Quat::kIdentity, yaw90, 0.5);
    EXPECT_NEAR(midpoint.w(), kCos22_5, 1e-15);
    EXPECT_NEAR(midpoint.x(), 0.0, 1e-15);
    EXPECT_NEAR(midpoint.y(), kSin22_5, 1e-15);
    EXPECT_NEAR(midpoint.z(), 0.0, 1e-15);

    // pose_compose: child origin (0,0,-1) under a yaw-90 parent at (1,2,3)
    // rotates to (-1,0,0), so the child lands at (1-1, 2, 3) = (0, 2, 3).
    const Pose parent{Pose{Vec3{1.0, 2.0, 3.0}, yaw90}};
    const Pose child{Pose{Vec3{0.0, 0.0, -1.0}, Quat::kIdentity}};
    const Pose composed = Compose(parent, child);
    EXPECT_NEAR(composed.position.x, 0.0, 1e-15);
    EXPECT_NEAR(composed.position.y, 2.0, 1e-15);
    EXPECT_NEAR(composed.position.z, 3.0, 1e-15);

    // clock_map: every offset host_ns - sdk_seconds*1e9 is an exact integer.
    // Odd case offsets: {500000, 400000, 600000} -> median 500000, so
    // 4.0 s maps to 4'000'000'000 + 500'000. Even case offsets:
    // {400000, 500000, 600000, 400000} -> sorted {400000, 400000, 500000,
    // 600000} -> median (400000+500000)/2 = 450000, so 5.0 s maps to
    // 5'000'000'000 + 450'000.
    ClockMapper odd;
    odd.AddSample(1.0, 1'000'500'000);
    odd.AddSample(2.0, 2'000'400'000);
    odd.AddSample(3.0, 3'000'600'000);
    EXPECT_EQ(odd.Map(4.0), 4'000'500'000);
    ClockMapper even;
    even.AddSample(1.0, 1'000'400'000);
    even.AddSample(2.0, 2'000'500'000);
    even.AddSample(3.0, 3'000'600'000);
    even.AddSample(4.0, 4'000'400'000);
    EXPECT_EQ(even.Map(5.0), 5'000'450'000);
}

} // namespace

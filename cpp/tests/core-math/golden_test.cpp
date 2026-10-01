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

} // namespace

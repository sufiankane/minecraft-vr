using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using NUnit.Framework;

namespace Cubeglass.CoreMath.Tests
{
    /// <summary>
    /// Drives the C# core maths from the shared golden fixture
    /// <c>contracts/golden/transforms.json</c> (ADR-0004), mirroring the C++
    /// runner in <c>cpp/tests/core-math/golden_test.cpp</c> case for case.
    /// </summary>
    [TestFixture]
    public sealed class GoldenFixtureTests
    {
        [Test]
        public void AllCasesMatchFixture()
        {
            string fixturePath = TestPaths.GoldenFixturePath();
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(fixturePath));

            JsonElement root = document.RootElement;
            Assert.That(root.GetProperty("schema").GetInt32(), Is.EqualTo(1), "golden fixture schema must be 1");

            double tolerance = root.GetProperty("tolerance").GetDouble();
            Assert.That(tolerance, Is.GreaterThan(0.0), "golden fixture tolerance must be positive");

            JsonElement cases = root.GetProperty("cases");
            Assert.That(cases.ValueKind, Is.EqualTo(JsonValueKind.Array), "golden fixture cases must be an array");
            Assert.That(cases.GetArrayLength(), Is.GreaterThan(0), "golden fixture has no cases");

            HashSet<string> fixtureOps = new HashSet<string>(StringComparer.Ordinal);
            HashSet<string> visitedOps = new HashSet<string>(StringComparer.Ordinal);

            foreach (JsonElement entry in cases.EnumerateArray())
            {
                string name = entry.GetProperty("name").GetString() ?? string.Empty;
                string op = entry.GetProperty("op").GetString() ?? string.Empty;
                string context = $"case {name} (op {op})";
                fixtureOps.Add(op);

                if (Dispatch.TryGetValue(op, out CaseRunner? runner))
                {
                    visitedOps.Add(op);
                    if (runner(entry.GetProperty("input"), entry.GetProperty("expected"), tolerance, context) == 0)
                    {
                        Assert.Fail($"{context}: dispatch performed no comparisons");
                    }
                }
                else
                {
                    Assert.Fail($"{context}: unknown op '{op}'");
                }
            }

            Assert.That(visitedOps, Is.EquivalentTo(fixtureOps), "every op defined in the fixture must be dispatched");
            Assert.That(Dispatch.Keys, Is.EquivalentTo(fixtureOps), "every dispatch branch must be reachable from the fixture");
        }

        /// <summary>
        /// Runs one fixture case and returns the number of comparisons it
        /// performed; zero signals a stub or an unfinished branch and fails the
        /// case.
        /// </summary>
        private delegate int CaseRunner(JsonElement input, JsonElement expected, double tolerance, string context);

        /// <summary>
        /// Dispatch table: one entry per op the driver implements. Its keys are
        /// compared against the ops observed in the fixture, so an op in the
        /// fixture without a branch fails as an unknown op and a branch without a
        /// case fails as unreachable.
        /// </summary>
        private static readonly Dictionary<string, CaseRunner> Dispatch =
            new Dictionary<string, CaseRunner>(StringComparer.Ordinal)
            {
                { "vec3_dot", RunVec3Dot },
                { "vec3_cross", RunVec3Cross },
                { "vec3_length", RunVec3Length },
                { "vec3_normalize", RunVec3Normalize },
                { "quat_normalize", RunQuatNormalize },
                { "quat_rotate", RunQuatRotate },
                { "quat_multiply", RunQuatMultiply },
                { "quat_inverse", RunQuatInverse },
                { "quat_slerp", RunQuatSlerp },
                { "pose_compose", RunPoseCompose },
                { "pose_inverse", RunPoseInverse },
                { "pose_transform_point", RunPoseTransformPoint },
                { "sdk_pose_to_internal", RunSdkPoseToInternal },
                { "unity_position", RunUnityPosition },
                { "unity_rotation", RunUnityRotation },
                { "unity_pose", RunUnityPose },
                { "clock_map", RunClockMap },
            };

        /// <summary>Reads a fixture <c>[x, y, z]</c> array.</summary>
        private static Vec3 ToVec3(JsonElement value)
        {
            return new Vec3(value[0].GetDouble(), value[1].GetDouble(), value[2].GetDouble());
        }

        /// <summary>Reads a fixture <c>[w, x, y, z]</c> array through the public normalising constructor.</summary>
        private static Quat ToQuat(JsonElement value)
        {
            return Quat.FromComponents(
                value[0].GetDouble(), value[1].GetDouble(), value[2].GetDouble(), value[3].GetDouble());
        }

        /// <summary>Reads a fixture <c>{"p": [...], "q": [...]}</c> pose.</summary>
        private static Pose ToPose(JsonElement value)
        {
            return new Pose(ToVec3(value.GetProperty("p")), ToQuat(value.GetProperty("q")));
        }

        /// <summary>Compares a scalar against a fixture number, absolutely, and reports one comparison.</summary>
        private static int ExpectScalar(double actual, JsonElement expected, double tolerance, string context)
        {
            Assert.That(actual, Is.EqualTo(expected.GetDouble()).Within(tolerance), context);
            return 1;
        }

        /// <summary>Compares a vector element-wise against a fixture value, absolutely, and reports three comparisons.</summary>
        private static int ExpectVec3(Vec3 actual, JsonElement expected, double tolerance, string context)
        {
            RequireArray(expected, 3, "a 3-component vector", context);
            Assert.That(actual.X, Is.EqualTo(expected[0].GetDouble()).Within(tolerance), $"{context} X");
            Assert.That(actual.Y, Is.EqualTo(expected[1].GetDouble()).Within(tolerance), $"{context} Y");
            Assert.That(actual.Z, Is.EqualTo(expected[2].GetDouble()).Within(tolerance), $"{context} Z");
            return 3;
        }

        /// <summary>
        /// Compares a quaternion element-wise against a fixture
        /// <c>[w, x, y, z]</c> value, absolutely, and reports four comparisons.
        /// </summary>
        private static int ExpectQuat(Quat actual, JsonElement expected, double tolerance, string context)
        {
            RequireArray(expected, 4, "a 4-component quaternion", context);
            Assert.That(actual.W, Is.EqualTo(expected[0].GetDouble()).Within(tolerance), $"{context} W");
            Assert.That(actual.X, Is.EqualTo(expected[1].GetDouble()).Within(tolerance), $"{context} X");
            Assert.That(actual.Y, Is.EqualTo(expected[2].GetDouble()).Within(tolerance), $"{context} Y");
            Assert.That(actual.Z, Is.EqualTo(expected[3].GetDouble()).Within(tolerance), $"{context} Z");
            return 4;
        }

        /// <summary>
        /// Compares a pose against a fixture <c>{"p": [...], "q": [...]}</c>
        /// value, absolutely, and reports its comparisons.
        /// </summary>
        private static int ExpectPose(Pose actual, JsonElement expected, double tolerance, string context)
        {
            return ExpectVec3(actual.Position, expected.GetProperty("p"), tolerance, context)
                + ExpectQuat(actual.Rotation, expected.GetProperty("q"), tolerance, context);
        }

        /// <summary>Fails the case when a fixture value does not have the required array shape.</summary>
        private static void RequireArray(JsonElement value, int length, string description, string context)
        {
            if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != length)
            {
                Assert.Fail($"{context}: expected {description}");
            }
        }

        private static int RunVec3Dot(JsonElement input, JsonElement expected, double tolerance, string context)
        {
            return ExpectScalar(Vec3.Dot(ToVec3(input.GetProperty("a")), ToVec3(input.GetProperty("b"))), expected, tolerance, context);
        }

        private static int RunVec3Cross(JsonElement input, JsonElement expected, double tolerance, string context)
        {
            return ExpectVec3(Vec3.Cross(ToVec3(input.GetProperty("a")), ToVec3(input.GetProperty("b"))), expected, tolerance, context);
        }

        private static int RunVec3Length(JsonElement input, JsonElement expected, double tolerance, string context)
        {
            return ExpectScalar(Vec3.Length(ToVec3(input.GetProperty("a"))), expected, tolerance, context);
        }

        private static int RunVec3Normalize(JsonElement input, JsonElement expected, double tolerance, string context)
        {
            return ExpectVec3(Vec3.Normalized(ToVec3(input.GetProperty("a"))), expected, tolerance, context);
        }

        private static int RunQuatNormalize(JsonElement input, JsonElement expected, double tolerance, string context)
        {
            return ExpectQuat(Quat.Normalize(ToQuat(input.GetProperty("q"))), expected, tolerance, context);
        }

        private static int RunQuatRotate(JsonElement input, JsonElement expected, double tolerance, string context)
        {
            return ExpectVec3(ToQuat(input.GetProperty("q")).Rotate(ToVec3(input.GetProperty("v"))), expected, tolerance, context);
        }

        private static int RunQuatMultiply(JsonElement input, JsonElement expected, double tolerance, string context)
        {
            return ExpectQuat(ToQuat(input.GetProperty("a")) * ToQuat(input.GetProperty("b")), expected, tolerance, context);
        }

        private static int RunQuatInverse(JsonElement input, JsonElement expected, double tolerance, string context)
        {
            return ExpectQuat(ToQuat(input.GetProperty("q")).Inverse(), expected, tolerance, context);
        }

        private static int RunQuatSlerp(JsonElement input, JsonElement expected, double tolerance, string context)
        {
            return ExpectQuat(
                Quat.Slerp(ToQuat(input.GetProperty("a")), ToQuat(input.GetProperty("b")), input.GetProperty("t").GetDouble()),
                expected,
                tolerance,
                context);
        }

        private static int RunPoseCompose(JsonElement input, JsonElement expected, double tolerance, string context)
        {
            return ExpectPose(Pose.Compose(ToPose(input.GetProperty("a")), ToPose(input.GetProperty("b"))), expected, tolerance, context);
        }

        private static int RunPoseInverse(JsonElement input, JsonElement expected, double tolerance, string context)
        {
            return ExpectPose(ToPose(input.GetProperty("a")).Inverse(), expected, tolerance, context);
        }

        private static int RunPoseTransformPoint(JsonElement input, JsonElement expected, double tolerance, string context)
        {
            return ExpectVec3(ToPose(input.GetProperty("a")).TransformPoint(ToVec3(input.GetProperty("v"))), expected, tolerance, context);
        }

        private static int RunSdkPoseToInternal(JsonElement input, JsonElement expected, double tolerance, string context)
        {
            JsonElement sdk = input.GetProperty("sdk");
            RequireArray(sdk, 7, "a 7-component SDK pose", context);
            float[] values = new float[7];
            for (int i = 0; i < values.Length; ++i)
            {
                values[i] = (float)sdk[i].GetDouble();
            }

            return ExpectPose(Pose.FromSdk(values), expected, tolerance, context);
        }

        private static int RunUnityPosition(JsonElement input, JsonElement expected, double tolerance, string context)
        {
            return ExpectVec3(UnityConvert.ToUnity(ToVec3(input.GetProperty("p"))), expected, tolerance, context);
        }

        private static int RunUnityRotation(JsonElement input, JsonElement expected, double tolerance, string context)
        {
            return ExpectQuat(UnityConvert.ToUnity(ToQuat(input.GetProperty("q"))), expected, tolerance, context);
        }

        private static int RunUnityPose(JsonElement input, JsonElement expected, double tolerance, string context)
        {
            return ExpectPose(UnityConvert.ToUnity(ToPose(input.GetProperty("a"))), expected, tolerance, context);
        }

        /// <summary>
        /// <c>clock_map</c> is the one op with exact integer expectations: the
        /// samples and the <c>Map</c> result are integer nanoseconds, compared
        /// without tolerance.
        /// </summary>
        private static int RunClockMap(JsonElement input, JsonElement expected, double tolerance, string context)
        {
            JsonElement samples = input.GetProperty("samples");
            Assert.That(samples.ValueKind, Is.EqualTo(JsonValueKind.Array), $"{context}: clock_map samples must be an array");

            ClockMapper mapper = new ClockMapper();
            foreach (JsonElement sample in samples.EnumerateArray())
            {
                RequireArray(sample, 2, "each clock_map sample must be [sdk_seconds, host_ns]", context);
                mapper.AddSample(sample[0].GetDouble(), sample[1].GetInt64());
            }

            Assert.That(
                mapper.Map(input.GetProperty("query_sdk").GetDouble()),
                Is.EqualTo(expected.GetInt64()),
                $"{context} expected host nanoseconds exactly");
            return 1;
        }
    }
}

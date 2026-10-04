using System;
using NUnit.Framework;

namespace Cubeglass.CoreMath.Tests
{
    [TestFixture]
    public sealed class ConvertTests
    {
        // cos(pi/4), the component of a 90 degree rotation about a principal axis.
        private const double HalfSqrt2 = 0.7071067811865476;
        private const double Pi = 3.14159265358979323846;

        private static void AssertVecNear(Vec3 v, double x, double y, double z, double tolerance)
        {
            Assert.That(v.X, Is.EqualTo(x).Within(tolerance), "X");
            Assert.That(v.Y, Is.EqualTo(y).Within(tolerance), "Y");
            Assert.That(v.Z, Is.EqualTo(z).Within(tolerance), "Z");
        }

        private static void AssertQuatNear(Quat q, double w, double x, double y, double z, double tolerance)
        {
            Assert.That(q.W, Is.EqualTo(w).Within(tolerance), "W");
            Assert.That(q.X, Is.EqualTo(x).Within(tolerance), "X");
            Assert.That(q.Y, Is.EqualTo(y).Within(tolerance), "Y");
            Assert.That(q.Z, Is.EqualTo(z).Within(tolerance), "Z");
        }

        [Test]
        public void PoseFromSdkReadsLayoutAndNormalizesRotation()
        {
            float[] sdk = { 0.1F, 0.2F, 0.3F, 0.0F, 0.0F, 0.0F, 2.0F };

            Pose pose = Pose.FromSdk(sdk);
            AssertVecNear(pose.Position, 0.1, 0.2, 0.3, 1e-6);
            Assert.That(pose.Rotation.W, Is.EqualTo(0.0));
            Assert.That(pose.Rotation.X, Is.EqualTo(0.0));
            Assert.That(pose.Rotation.Y, Is.EqualTo(0.0));
            Assert.That(pose.Rotation.Z, Is.EqualTo(1.0));
        }

        [Test]
        public void PoseFromSdkNormalizesUnitScaleRotation()
        {
            float[] sdk = { 0.0F, 0.0F, 0.0F, 1.0F, 1.0F, 1.0F, 1.0F };

            Pose pose = Pose.FromSdk(sdk);
            AssertVecNear(pose.Position, 0.0, 0.0, 0.0, 0.0);
            Assert.That(pose.Rotation.W, Is.EqualTo(0.5));
            Assert.That(pose.Rotation.X, Is.EqualTo(0.5));
            Assert.That(pose.Rotation.Y, Is.EqualTo(0.5));
            Assert.That(pose.Rotation.Z, Is.EqualTo(0.5));
        }

        [Test]
        public void PoseFromSdkDegenerateRotationIsIdentity()
        {
            float[] sdk = { 0.0F, 0.0F, 0.0F, 0.0F, 0.0F, 0.0F, 0.0F };

            Pose pose = Pose.FromSdk(sdk);
            AssertVecNear(pose.Position, 0.0, 0.0, 0.0, 0.0);
            Assert.That(pose.Rotation.W, Is.EqualTo(1.0));
            Assert.That(pose.Rotation.X, Is.EqualTo(0.0));
            Assert.That(pose.Rotation.Y, Is.EqualTo(0.0));
            Assert.That(pose.Rotation.Z, Is.EqualTo(0.0));
        }

        [Test]
        public void PoseFromSdkThrowsForWrongLength()
        {
            Assert.That(() => Pose.FromSdk(new float[6]), Throws.TypeOf<ArgumentException>());
            Assert.That(() => Pose.FromSdk(new float[8]), Throws.TypeOf<ArgumentException>());
            Assert.That(() => Pose.FromSdk(ReadOnlySpan<float>.Empty), Throws.TypeOf<ArgumentException>());
        }

        [Test]
        public void ToUnityPositionFlipsZ()
        {
            Vec3 unity = UnityConvert.ToUnity(new Vec3(1.0, 2.0, -3.0));
            Assert.That(unity.X, Is.EqualTo(1.0));
            Assert.That(unity.Y, Is.EqualTo(2.0));
            Assert.That(unity.Z, Is.EqualTo(3.0));
        }

        [Test]
        public void ToUnityRotationIdentityIsIdentity()
        {
            Quat unity = UnityConvert.ToUnity(Quat.Identity);
            Assert.That(unity.W, Is.EqualTo(1.0));
            Assert.That(unity.X, Is.EqualTo(0.0));
            Assert.That(unity.Y, Is.EqualTo(0.0));
            Assert.That(unity.Z, Is.EqualTo(0.0));
        }

        [Test]
        public void ToUnityRotationYaw90FlipsVectorPart()
        {
            Quat yaw90 = Quat.FromAxisAngle(new Vec3(0.0, 1.0, 0.0), Pi / 2.0);

            Quat unity = UnityConvert.ToUnity(yaw90);
            AssertQuatNear(unity, HalfSqrt2, 0.0, -HalfSqrt2, 0.0, 1e-15);
        }

        [Test]
        public void UnityYawMapsConvertedForwardToExpectedUnityForward()
        {
            Quat internalYaw = Quat.FromAxisAngle(new Vec3(0.0, 1.0, 0.0), Pi / 2.0);
            Vec3 expected = UnityConvert.ToUnity(internalYaw.Rotate(new Vec3(0.0, 0.0, -1.0)));

            Quat unityYaw = UnityConvert.ToUnity(internalYaw);
            Vec3 actual = unityYaw.Rotate(UnityConvert.ToUnity(new Vec3(0.0, 0.0, -1.0)));

            AssertVecNear(actual, expected.X, expected.Y, expected.Z, 1e-12);
            AssertVecNear(actual, -1.0, 0.0, 0.0, 1e-12);
        }

        [Test]
        public void ToUnityPoseConvertsPositionAndRotation()
        {
            Pose pose = new Pose(new Vec3(1.0, 2.0, -3.0), Quat.FromAxisAngle(new Vec3(0.0, 1.0, 0.0), Pi / 2.0));

            Pose unity = UnityConvert.ToUnity(pose);
            AssertVecNear(unity.Position, 1.0, 2.0, 3.0, 1e-15);
            AssertQuatNear(unity.Rotation, HalfSqrt2, 0.0, -HalfSqrt2, 0.0, 1e-15);
        }

        [Test]
        public void NanosecondsPerSecondIsOneBillion()
        {
            Assert.That(HostTime.NanosecondsPerSecond, Is.EqualTo(1_000_000_000L));
        }

        [Test]
        public void ToSecondsHandValue()
        {
            Assert.That(HostTime.ToSeconds(1_500_000_000L), Is.EqualTo(1.5));
        }

        [Test]
        public void ToNanosecondsHandValue()
        {
            Assert.That(HostTime.ToNanoseconds(1.25), Is.EqualTo(1_250_000_000L));
        }

        [Test]
        public void ToNanosecondsRoundsToNearestNanosecond()
        {
            Assert.That(HostTime.ToNanoseconds(0.25), Is.EqualTo(250_000_000L));
            Assert.That(HostTime.ToNanoseconds(-0.25), Is.EqualTo(-250_000_000L));
            Assert.That(HostTime.ToNanoseconds(1.0000000004), Is.EqualTo(1_000_000_000L));
            Assert.That(HostTime.ToNanoseconds(1.0000000006), Is.EqualTo(1_000_000_001L));
        }

        [Test]
        public void ToNanosecondsRoundsHalfwayAwayFromZero()
        {
            Assert.That(HostTime.ToNanoseconds(0.5e-9), Is.EqualTo(1L));
            Assert.That(HostTime.ToNanoseconds(-0.5e-9), Is.EqualTo(-1L));
        }

        [Test]
        public void ToNanosecondsSaturatesAtTheHostTimeRange()
        {
            // Mirrors the C++ time.hpp contract (M-11): every finite double and
            // every infinity is total, independent of the runtime's float-to-long
            // cast behaviour (Mono/IL2CPP do not saturate like CoreCLR).
            Assert.That(HostTime.ToNanoseconds(double.NaN), Is.Zero, "NaN maps to zero");
            Assert.That(HostTime.ToNanoseconds(double.PositiveInfinity), Is.EqualTo(long.MaxValue));
            Assert.That(HostTime.ToNanoseconds(double.NegativeInfinity), Is.EqualTo(long.MinValue));
            Assert.That(HostTime.ToNanoseconds(1e300), Is.EqualTo(long.MaxValue));
            Assert.That(HostTime.ToNanoseconds(-1e300), Is.EqualTo(long.MinValue));
            Assert.That(
                HostTime.ToNanoseconds(HostTime.ToSeconds(long.MaxValue)),
                Is.EqualTo(long.MaxValue),
                "the largest representable stamp stays saturated, not wrapped");
            Assert.That(
                HostTime.ToNanoseconds(HostTime.ToSeconds(long.MinValue)),
                Is.EqualTo(long.MinValue),
                "the smallest representable stamp stays saturated, not wrapped");
            Assert.That(HostTime.ToNanoseconds(9.2), Is.EqualTo(9_200_000_000L), "in-range values are untouched");
        }

        [Test]
        public void ToSecondsDividesByNanosecondsPerSecond()
        {
            // For this sample, division by 1e9 and multiplication by 1e-9 round differently:
            // the test pins the C++ expression (division) rather than the multiplication form.
            const long sample = 6495625611625438L;
            double viaDivision = (double)sample / 1_000_000_000.0;
            double viaMultiplication = (double)sample * 1e-9;

            Assert.That(viaMultiplication, Is.Not.EqualTo(viaDivision));
            Assert.That(HostTime.ToSeconds(sample), Is.EqualTo(viaDivision));
        }

        [Test]
        public void RoundTripStaysWithinOneNanosecond()
        {
            long[] samples =
            {
                0L,
                1L,
                -1L,
                999L,
                -999L,
                1_000_000L,
                -1_500_000L,
                1_500_000L,
                999_999_999L,
                1_234_567_890L,
                123_456_789L,
                -98_765_432L,
                4_000_500_000L,
                -2_500_000_000L,
            };

            foreach (long sample in samples)
            {
                Assert.That(
                    Math.Abs(HostTime.ToNanoseconds(HostTime.ToSeconds(sample)) - sample),
                    Is.LessThanOrEqualTo(1L),
                    $"sample: {sample}");
            }
        }
    }
}

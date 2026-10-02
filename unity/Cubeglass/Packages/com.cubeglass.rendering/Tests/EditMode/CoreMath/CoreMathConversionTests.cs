using Cubeglass.CoreMath;
using Cubeglass.Unity.Bridge;
using NUnit.Framework;
using UnityEngine;
using CorePose = Cubeglass.CoreMath.Pose;

namespace Cubeglass.Unity.Rendering.Tests
{
    /// <summary>
    /// The S1 golden conversion cases asserted directly against
    /// <see cref="UnityConvert"/> (identity, yaw 90° → <c>(w, −x, −y, z)</c>,
    /// position z-flip, both together), plus the rig applying exactly the same
    /// converted values. The test assembly references the managed
    /// <c>Cubeglass.CoreMath.dll</c> plugin explicitly, so the conversion source
    /// is pinned here and not only through the rig's float expectations.
    /// </summary>
    public class CoreMathConversionTests
    {
        private const float Tolerance = 1e-6f;
        private const double HalfSqrt2 = 0.7071067811865476;

        private static Quat InternalYaw90
        {
            get { return Quat.FromAxisAngle(new Vec3(0.0, 1.0, 0.0), System.Math.PI / 2.0); }
        }

        private static void AssertQuat(Quat quaternion, double w, double x, double y, double z, string context)
        {
            Assert.AreEqual(w, quaternion.W, 1e-12, context + ".W");
            Assert.AreEqual(x, quaternion.X, 1e-12, context + ".X");
            Assert.AreEqual(y, quaternion.Y, 1e-12, context + ".Y");
            Assert.AreEqual(z, quaternion.Z, 1e-12, context + ".Z");
        }

        private static BridgeHeadSample GoldenYaw90Sample()
        {
            Quat rotation = InternalYaw90;
            return new BridgeHeadSample
            {
                HostTime = 123456789L,
                Pose = new BridgePose
                {
                    Position = new BridgeVec3 { X = 1f, Y = 2f, Z = -3f },
                    Rotation = new BridgeQuat
                    {
                        W = (float)rotation.W,
                        X = (float)rotation.X,
                        Y = (float)rotation.Y,
                        Z = (float)rotation.Z,
                    },
                },
                State = TrackState.Stable,
                Sequence = 7,
            };
        }

        private sealed class FixedPoseProvider : IPoseProvider
        {
            public BridgeHeadSample Sample;
            public int Calls;

            public bool TryGetLatest(out BridgeHeadSample sample)
            {
                Calls++;
                sample = Sample;
                return true;
            }
        }

        [Test]
        public void IdentityRotationIsUnchanged()
        {
            AssertQuat(UnityConvert.ToUnity(Quat.Identity), 1.0, 0.0, 0.0, 0.0, "unity(identity)");
        }

        [Test]
        public void Yaw90FlipsTheVectorPart()
        {
            Quat unity = UnityConvert.ToUnity(InternalYaw90);

            AssertQuat(unity, HalfSqrt2, 0.0, -HalfSqrt2, 0.0, "unity(yaw90)");
        }

        [Test]
        public void PositionFlipsZ()
        {
            Vec3 unity = UnityConvert.ToUnity(new Vec3(1.0, 2.0, -3.0));

            Assert.AreEqual(1.0, unity.X, 1e-12, "x unchanged");
            Assert.AreEqual(2.0, unity.Y, 1e-12, "y unchanged");
            Assert.AreEqual(3.0, unity.Z, 1e-12, "z negated");
        }

        [Test]
        public void PoseFlipsPositionAndRotationTogether()
        {
            CorePose unity = UnityConvert.ToUnity(new CorePose(new Vec3(1.0, 2.0, -3.0), InternalYaw90));

            Assert.AreEqual(1.0, unity.Position.X, 1e-12, "position.x");
            Assert.AreEqual(2.0, unity.Position.Y, 1e-12, "position.y");
            Assert.AreEqual(3.0, unity.Position.Z, 1e-12, "position.z");
            AssertQuat(unity.Rotation, HalfSqrt2, 0.0, -HalfSqrt2, 0.0, "rotation");
        }

        [Test]
        public void RigApplicationMatchesTheConvertedGoldenPose()
        {
            var root = new GameObject("CoreMathRig");
            try
            {
                var rig = root.AddComponent<StereoRig>();
                var latch = root.AddComponent<LateLatchPose>();
                rig.ApplyEyeLayout();
                latch.Rig = rig;

                var provider = new FixedPoseProvider { Sample = GoldenYaw90Sample() };
                latch.Provider = provider;

                latch.TickOnce();

                CorePose unityPose = UnityConvert.ToUnity(new CorePose(new Vec3(1.0, 2.0, -3.0), InternalYaw90));

                Assert.AreEqual(1, provider.Calls, "one provider read");
                Assert.AreEqual((float)unityPose.Position.X, rig.transform.localPosition.x, Tolerance, "position.x");
                Assert.AreEqual((float)unityPose.Position.Y, rig.transform.localPosition.y, Tolerance, "position.y");
                Assert.AreEqual((float)unityPose.Position.Z, rig.transform.localPosition.z, Tolerance, "position.z");

                Assert.AreEqual((float)unityPose.Rotation.X, rig.transform.localRotation.x, Tolerance, "rotation.x");
                Assert.AreEqual((float)unityPose.Rotation.Y, rig.transform.localRotation.y, Tolerance, "rotation.y");
                Assert.AreEqual((float)unityPose.Rotation.Z, rig.transform.localRotation.z, Tolerance, "rotation.z");
                Assert.AreEqual((float)unityPose.Rotation.W, rig.transform.localRotation.w, Tolerance, "rotation.w");

                Assert.Less(
                    Quaternion.Angle(rig.LeftCamera.transform.rotation, rig.transform.rotation),
                    Tolerance,
                    "left eye inherits the converted pose");
                Assert.Less(
                    Quaternion.Angle(rig.RightCamera.transform.rotation, rig.transform.rotation),
                    Tolerance,
                    "right eye inherits the converted pose");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }
    }
}

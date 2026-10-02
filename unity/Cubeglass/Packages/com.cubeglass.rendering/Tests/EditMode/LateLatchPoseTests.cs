using System;
using System.Reflection;
using Cubeglass.Unity.Bridge;
using NUnit.Framework;
using UnityEngine;

namespace Cubeglass.Unity.Rendering.Tests
{
    /// <summary>
    /// Late-latch behaviour: the newest internal sample is converted once via
    /// <c>Cubeglass.CoreMath.UnityConvert</c>, applied to the rig, shared by both
    /// eyes, kept when no sample arrives, and read at most once per tick.
    /// </summary>
    public class LateLatchPoseTests
    {
        private const float Tolerance = 1e-4f;

        private sealed class ScriptedPoseProvider : IPoseProvider
        {
            public BridgeHeadSample Sample;
            public bool HasSample = true;
            public int Calls;

            public bool TryGetLatest(out BridgeHeadSample sample)
            {
                Calls++;
                sample = Sample;
                return HasSample;
            }
        }

        private GameObject root;
        private StereoRig rig;
        private LateLatchPose latch;
        private ScriptedPoseProvider provider;

        [SetUp]
        public void CreateRig()
        {
            root = new GameObject("LateLatchRoot");
            rig = root.AddComponent<StereoRig>();
            latch = root.AddComponent<LateLatchPose>();
            rig.ApplyEyeLayout();
            latch.Rig = rig;

            provider = new ScriptedPoseProvider();
            latch.Provider = provider;
        }

        [TearDown]
        public void DestroyRig()
        {
            UnityEngine.Object.DestroyImmediate(root);
            root = null;
            rig = null;
            latch = null;
            provider = null;
        }

        private static BridgeHeadSample YawSample(float degrees)
        {
            double half = degrees * Math.PI / 360.0;
            return new BridgeHeadSample
            {
                HostTime = 42L,
                Pose = new BridgePose
                {
                    Position = new BridgeVec3 { X = 0.25f, Y = 1.6f, Z = -0.5f },
                    Rotation = new BridgeQuat
                    {
                        W = (float)Math.Cos(half),
                        X = 0f,
                        Y = (float)Math.Sin(half),
                        Z = 0f,
                    },
                },
                State = TrackState.Stable,
                Sequence = 3,
            };
        }

        private static BridgeHeadSample PitchSample(float degrees)
        {
            double half = degrees * Math.PI / 360.0;
            return new BridgeHeadSample
            {
                HostTime = 42L,
                Pose = new BridgePose
                {
                    Position = new BridgeVec3 { X = 0f, Y = 0f, Z = 0f },
                    Rotation = new BridgeQuat
                    {
                        W = (float)Math.Cos(half),
                        X = (float)Math.Sin(half),
                        Y = 0f,
                        Z = 0f,
                    },
                },
                State = TrackState.Stable,
                Sequence = 4,
            };
        }

        [Test]
        public void GoldenInternalYaw90MapsToUnityMinus90()
        {
            provider.Sample = YawSample(90f);

            latch.TickOnce();

            Assert.AreEqual(1, provider.Calls, "one provider read");
            Assert.Less(
                Quaternion.Angle(latch.transform.localRotation, Quaternion.Euler(0f, -90f, 0f)),
                Tolerance,
                "converted quaternion");
            Assert.Less(
                Mathf.Abs(Mathf.DeltaAngle(latch.transform.eulerAngles.y, -90f)),
                Tolerance,
                "Unity yaw is -90 degrees");
            Assert.AreEqual(PoseTrackingState.Stable, latch.TrackingState, "state");
        }

        [Test]
        public void PitchFollowsTheUnityConvertSignConvention()
        {
            provider.Sample = PitchSample(30f);

            latch.TickOnce();

            Assert.Less(
                Quaternion.Angle(latch.transform.localRotation, Quaternion.Euler(-30f, 0f, 0f)),
                Tolerance,
                "pitch is the single UnityConvert flip, no second sign change");
        }

        [Test]
        public void PositionIsConvertedWithTheSameSingleSource()
        {
            provider.Sample = YawSample(0f);

            latch.TickOnce();

            Assert.AreEqual(0.25f, latch.transform.localPosition.x, Tolerance, "x unchanged");
            Assert.AreEqual(1.6f, latch.transform.localPosition.y, Tolerance, "y unchanged");
            Assert.AreEqual(0.5f, latch.transform.localPosition.z, Tolerance, "z negated");
        }

        [Test]
        public void OneReadPerTickFeedsBothEyes()
        {
            provider.Sample = YawSample(45f);

            latch.TickOnce();

            Assert.AreEqual(1, provider.Calls, "exactly one provider read per tick");
            Assert.Less(
                Quaternion.Angle(rig.LeftCamera.transform.rotation, rig.RightCamera.transform.rotation),
                1e-6f,
                "both eyes share the sample");
            Assert.Less(
                Quaternion.Angle(rig.LeftCamera.transform.rotation, latch.transform.rotation),
                1e-6f,
                "eyes inherit the rig pose");
        }

        [Test]
        public void NoSampleKeepsTheLastPoseAndState()
        {
            provider.Sample = YawSample(90f);
            latch.TickOnce();

            Quaternion pose = latch.transform.localRotation;
            Vector3 position = latch.transform.localPosition;
            Assert.AreEqual(PoseTrackingState.Stable, latch.TrackingState, "sampled state");

            provider.HasSample = false;
            latch.TickOnce();

            Assert.AreEqual(2, provider.Calls, "the failing read still happened");
            Assert.Less(Quaternion.Angle(pose, latch.transform.localRotation), Tolerance, "rotation kept");
            Assert.Less(Vector3.Distance(position, latch.transform.localPosition), Tolerance, "position kept");
            Assert.AreEqual(PoseTrackingState.Stable, latch.TrackingState, "state unchanged");
        }

        [Test]
        public void NeverSampledLatchStaysNotReady()
        {
            provider.HasSample = false;

            latch.TickOnce();

            Assert.AreEqual(PoseTrackingState.NotReady, latch.TrackingState, "state");
            Assert.Less(Vector3.Distance(Vector3.zero, latch.transform.localPosition), Tolerance, "position untouched");
            Assert.Less(Quaternion.Angle(Quaternion.identity, latch.transform.localRotation), Tolerance, "rotation untouched");
        }

        [Test]
        public void LostSampleIsStillLatchedAndReported()
        {
            BridgeHeadSample sample = YawSample(90f);
            sample.State = TrackState.Lost;
            provider.Sample = sample;

            latch.TickOnce();

            Assert.AreEqual(PoseTrackingState.Lost, latch.TrackingState, "state");
            Assert.Less(
                Quaternion.Angle(latch.transform.localRotation, Quaternion.Euler(0f, -90f, 0f)),
                Tolerance,
                "a stale head still latches");
        }

        [Test]
        public void SubscriptionIsIdempotent()
        {
            latch.Unsubscribe();
            Assert.IsFalse(latch.IsSubscribed, "unsubscribed");

            latch.Subscribe();
            latch.Subscribe();
            Assert.IsTrue(latch.IsSubscribed, "subscribed once");

            latch.Unsubscribe();
            latch.Unsubscribe();
            Assert.IsFalse(latch.IsSubscribed, "unsubscribed once");

            latch.Subscribe();
        }

        [Test]
        public void PreCullReadsOncePerFrameAcrossBothEyes()
        {
            provider.Sample = YawSample(20f);
            MethodInfo handler = typeof(LateLatchPose).GetMethod(
                "HandlePreCull", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(handler, "HandlePreCull hook exists");
            var arguments = new object[1];

            arguments[0] = rig.LeftCamera;
            handler.Invoke(latch, arguments);
            arguments[0] = rig.RightCamera;
            handler.Invoke(latch, arguments);

            Assert.AreEqual(1, provider.Calls, "the second eye must not read again in the same frame");
        }

        [Test]
        public void TickOnceAllocatesNothingAfterWarmUp()
        {
            provider.Sample = YawSample(15f);
            for (int i = 0; i < 64; i++)
            {
                latch.TickOnce();
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++)
            {
                latch.TickOnce();
            }

            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.AreEqual(0L, allocated, "TickOnce allocated bytes after warm-up");
        }
    }
}

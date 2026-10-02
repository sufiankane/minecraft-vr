using System;
using System.Collections;
using System.Collections.Generic;
using Cubeglass.Unity.Input;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Cubeglass.Unity.Rendering.PlayTests
{
    /// <summary>
    /// Real-frame coverage for the late-latch path: the scripted synthetic
    /// provider sweeps yaw, the rig tracks it, both eye cameras render the same
    /// sample per frame, the <c>Camera.onPreCull</c> ordering log proves the
    /// pose is applied before rendering, and the disabled overlay path
    /// allocates nothing across 300 frames.
    /// </summary>
    /// <remarks>
    /// The CLI PlayMode lane runs without a Game View, so no camera renders on
    /// its own and <c>Camera.onPreCull</c> would never fire. Each iteration
    /// therefore calls <c>Camera.Render()</c> for both eyes explicitly, which
    /// drives the same pre-cull hook a normal frame uses before yielding to the
    /// next frame.
    /// </remarks>
    public class RigPlayModeTests
    {
        private const float Tolerance = 1e-4f;

        private GameObject root;
        private StereoRig rig;
        private LateLatchPose latch;
        private SyntheticPoseProvider provider;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            root = new GameObject("PlayModeRig");
            rig = root.AddComponent<StereoRig>();
            latch = root.AddComponent<LateLatchPose>();
            rig.ApplyEyeLayout();
            latch.Rig = rig;

            var providerObject = new GameObject("SyntheticPoseProvider");
            providerObject.transform.SetParent(root.transform, false);
            provider = providerObject.AddComponent<SyntheticPoseProvider>();
            latch.Provider = provider;

            yield return null;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            UnityEngine.Object.Destroy(root);
            yield return null;

            root = null;
            rig = null;
            latch = null;
            provider = null;
        }

        [UnityTest]
        public IEnumerator RigYawTracksTheScriptedSweepOneSamplePerFrame()
        {
            provider.SetPose(0f, 0f, 0f, Vector3.zero);
            provider.SetRates(90f, 0f, 0f);
            provider.SetSampleRateHz(90f);

            int firstFrame = Time.frameCount;
            int firstCalls = provider.CallCount;
            int firstSamples = provider.SampleCount;

            for (int i = 0; i < 30; i++)
            {
                // The CLI PlayMode lane runs with no Game View, so no camera
                // renders on its own. Render both eyes explicitly: this drives
                // the exact Camera.onPreCull path a real frame would.
                rig.LeftCamera.Render();
                rig.RightCamera.Render();
                yield return null;

                Assert.Less(
                    Quaternion.Angle(rig.LeftCamera.transform.rotation, rig.RightCamera.transform.rotation),
                    Tolerance,
                    "both eye cameras share the latched pose");
            }

            int frames = Time.frameCount - firstFrame;
            int calls = provider.CallCount - firstCalls;
            int samples = provider.SampleCount - firstSamples;

            Assert.GreaterOrEqual(frames, 1, "frames elapsed");
            Assert.AreEqual(30, frames, "one test iteration per frame");
            Assert.AreEqual(frames, calls, "exactly one provider read per rendered frame");
            Assert.AreEqual(frames, samples, "exactly one scripted sample per rendered frame");

            // Internal yaw is converted by UnityConvert's single flip: +yaw
            // internal becomes -yaw in Unity. The last emitted sample is the one
            // currently applied (the next read only happens in the next pre-cull).
            float expectedUnityYaw = -provider.CurrentYawDegrees;
            Assert.Greater(
                Mathf.Abs(Mathf.DeltaAngle(expectedUnityYaw, 0f)),
                10f,
                "the scripted yaw actually swept");
            Assert.Less(
                Mathf.Abs(Mathf.DeltaAngle(latch.transform.eulerAngles.y, expectedUnityYaw)),
                0.5f,
                "rig yaw tracks the scripted value");
            Assert.Less(
                Quaternion.Angle(rig.LeftCamera.transform.rotation, rig.transform.rotation),
                Tolerance,
                "left eye inherits the sweep");
            Assert.Less(
                Quaternion.Angle(rig.RightCamera.transform.rotation, rig.transform.rotation),
                Tolerance,
                "right eye inherits the sweep");
        }

        [UnityTest]
        public IEnumerator PreCullOrderingLogShowsPoseAppliedBeforeRendering()
        {
            provider.SetPose(35f, 0f, 0f, Vector3.zero);
            provider.SetRates(0f, 0f, 0f);

            // Subscribed after LateLatchPose.OnEnable, so the probe's pre-cull
            // entries run after the latch handler in the same frame callback:
            // seeing HasSample here proves the pose was applied first.
            var probe = new FrameOrderProbe(latch, rig.LeftCamera, rig.RightCamera);
            probe.Subscribe();
            try
            {
                for (int i = 0; i < 10; i++)
                {
                    rig.LeftCamera.Render();
                    rig.RightCamera.Render();
                    yield return null;
                }
            }
            finally
            {
                probe.Unsubscribe();
            }

            Assert.GreaterOrEqual(probe.Entries.Count, 20, "two eye pre-culls per frame");
            Assert.IsTrue(probe.AllPoseApplied, "every pre-cull entry logged after the pose was applied");
            Assert.AreEqual(0, probe.MissingPoseCount, "no entry logged before the latch ran");
            CollectionAssert.DoesNotContain(probe.OrderLog, "preCull:poseMissing", "ordering log");
            Assert.AreEqual(
                "preCull:poseApplied",
                probe.OrderLog[probe.OrderLog.Count - 1],
                "last frame callback ordering");

            int frames = 0;
            int index = 0;
            while (index < probe.Entries.Count)
            {
                int frame = probe.Entries[index].Frame;
                int count = 1;
                while (index + count < probe.Entries.Count &&
                       probe.Entries[index + count].Frame == frame)
                {
                    count++;
                }

                Assert.AreEqual(2, count, "both eye cameras must pre-cull in each frame");
                Assert.AreEqual(
                    probe.Entries[index].Sequence,
                    probe.Entries[index + 1].Sequence,
                    "one latched sample shared by both eyes");
                frames++;
                index += count;
            }

            Assert.GreaterOrEqual(frames, 5, "frames observed");
            Assert.Greater(
                probe.Entries[probe.Entries.Count - 1].Sequence,
                probe.Entries[0].Sequence,
                "new samples were latched across frames");
        }

        [UnityTest]
        public IEnumerator ManualTickAndRenderInTheSameFrameReadOnce()
        {
            provider.SetPose(15f, 0f, 0f, Vector3.zero);
            provider.SetRates(0f, 0f, 0f);

            int firstCalls = provider.CallCount;
            latch.TickOnce();
            rig.LeftCamera.Render();
            rig.RightCamera.Render();

            Assert.AreEqual(
                1,
                provider.CallCount - firstCalls,
                "a manual tick plus the pre-cull render in one frame must read once");

            yield return null;

            int secondCalls = provider.CallCount;
            rig.LeftCamera.Render();
            rig.RightCamera.Render();

            Assert.AreEqual(
                1,
                provider.CallCount - secondCalls,
                "the next frame reads the provider again");
        }

        [UnityTest]
        public IEnumerator DisabledOverlayAllocatesNothingAcross300Frames()
        {
            var overlayObject = new GameObject("DebugOverlay");
            overlayObject.transform.SetParent(root.transform, false);
            var overlay = overlayObject.AddComponent<DebugOverlay>();
            overlay.enabled = false;

            provider.SetPose(0f, 0f, 0f, Vector3.zero);
            provider.SetRates(30f, 0f, 0f);

            for (int i = 0; i < 64; i++)
            {
                rig.LeftCamera.Render();
                rig.RightCamera.Render();
                yield return null;
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            int callsBefore = provider.CallCount;
            for (int i = 0; i < 300; i++)
            {
                rig.LeftCamera.Render();
                rig.RightCamera.Render();
                yield return null;
            }

            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            int callsDuringMeasurement = provider.CallCount - callsBefore;

            Assert.AreEqual(0L, allocated, "per-frame allocations with the overlay disabled");
            Assert.GreaterOrEqual(
                callsDuringMeasurement,
                300,
                "the late-latch path ran at least once per measured frame");
        }

        private readonly struct PreCullEntry
        {
            public readonly int Frame;
            public readonly bool PoseApplied;
            public readonly uint Sequence;

            public PreCullEntry(int frame, bool poseApplied, uint sequence)
            {
                Frame = frame;
                PoseApplied = poseApplied;
                Sequence = sequence;
            }
        }

        private sealed class FrameOrderProbe
        {
            private readonly LateLatchPose latch;
            private readonly Camera left;
            private readonly Camera right;
            private readonly List<PreCullEntry> entries = new List<PreCullEntry>();
            private readonly List<string> orderLog = new List<string>();

            public FrameOrderProbe(LateLatchPose latch, Camera left, Camera right)
            {
                this.latch = latch;
                this.left = left;
                this.right = right;
            }

            public List<PreCullEntry> Entries
            {
                get { return entries; }
            }

            public List<string> OrderLog
            {
                get { return orderLog; }
            }

            public bool AllPoseApplied { get; private set; } = true;

            public int MissingPoseCount { get; private set; }

            public void Subscribe()
            {
                Camera.onPreCull += HandlePreCull;
            }

            public void Unsubscribe()
            {
                Camera.onPreCull -= HandlePreCull;
            }

            private void HandlePreCull(Camera camera)
            {
                if (camera != left && camera != right)
                {
                    return;
                }

                bool applied = latch != null && latch.HasSample;
                uint sequence = applied ? latch.LastSample.Sequence : 0u;
                entries.Add(new PreCullEntry(Time.frameCount, applied, sequence));
                orderLog.Add(applied ? "preCull:poseApplied" : "preCull:poseMissing");

                if (!applied)
                {
                    AllPoseApplied = false;
                    MissingPoseCount++;
                }
            }
        }
    }
}

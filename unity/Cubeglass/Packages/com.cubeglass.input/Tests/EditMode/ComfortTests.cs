using NUnit.Framework;
using UnityEngine;

namespace Cubeglass.Unity.Input.Tests
{
    /// <summary>
    /// Pins the comfort and HUD maths (S7 Task 3): the snap-turn increment is
    /// exact and yaw-only, the vignette curve is the recorded 0.5/2.0 m/s
    /// ramp to 0.4, and the WorldUi layout helpers (anchor, slots, projection)
    /// are pure.
    /// </summary>
    public class ComfortTests
    {
        private const float Tolerance = 1e-5f;

        [Test]
        public void SnapTurnAppliesExactlyAndOnlyToYaw()
        {
            var rig = new GameObject("SnapRig");
            try
            {
                SnapTurn snap = rig.AddComponent<SnapTurn>();
                Assert.AreEqual(45f, snap.IncrementDegrees, Tolerance, "default increment");

                snap.Apply(45f);
                Assert.AreEqual(45f, Mathf.DeltaAngle(0f, rig.transform.eulerAngles.y), 1e-3f, "one increment");
                Assert.AreEqual(45f, snap.AccumulatedDegrees, Tolerance);

                snap.Apply(45f);
                Assert.AreEqual(90f, Mathf.DeltaAngle(0f, rig.transform.eulerAngles.y), 1e-3f, "two increments");

                snap.ApplyIncrement(-1f);
                Assert.AreEqual(45f, Mathf.DeltaAngle(0f, rig.transform.eulerAngles.y), 1e-3f, "the negative increment");

                snap.Apply(0f);
                Assert.AreEqual(45f, Mathf.DeltaAngle(0f, rig.transform.eulerAngles.y), 1e-3f, "zero is a no-op");

                snap.SnapEnabled = false;
                snap.Apply(45f);
                Assert.AreEqual(45f, Mathf.DeltaAngle(0f, rig.transform.eulerAngles.y), 1e-3f, "disabled is a no-op");
            }
            finally
            {
                Object.DestroyImmediate(rig);
            }
        }

        [Test]
        public void SnapTurnKeepsPitchUnchanged()
        {
            var rig = new GameObject("SnapRig");
            try
            {
                rig.transform.localRotation = Quaternion.Euler(20f, 0f, 0f);
                SnapTurn snap = rig.AddComponent<SnapTurn>();
                snap.Apply(45f);

                Vector3 euler = rig.transform.eulerAngles;
                Assert.AreEqual(45f, Mathf.DeltaAngle(0f, euler.y), 1e-3f);
                Assert.AreEqual(20f, Mathf.DeltaAngle(0f, euler.x), 1e-3f, "pitch survives the yaw snap");
            }
            finally
            {
                Object.DestroyImmediate(rig);
            }
        }

        [Test]
        public void VignetteCurveMatchesTheRecordedRamp()
        {
            Assert.AreEqual(0f, MotionVignette.OpacityForSpeed(0f, 0.4f, 0.5f, 2f), Tolerance, "at rest");
            Assert.AreEqual(0f, MotionVignette.OpacityForSpeed(0.5f, 0.4f, 0.5f, 2f), Tolerance, "at the low edge");
            Assert.AreEqual(0f, MotionVignette.OpacityForSpeed(-1f, 0.4f, 0.5f, 2f), Tolerance, "negative speed");
            Assert.AreEqual(0f, MotionVignette.OpacityForSpeed(float.NaN, 0.4f, 0.5f, 2f), Tolerance, "NaN speed");
            Assert.AreEqual(0.2f, MotionVignette.OpacityForSpeed(1.25f, 0.4f, 0.5f, 2f), Tolerance, "midpoint");
            Assert.AreEqual(0.4f, MotionVignette.OpacityForSpeed(2f, 0.4f, 0.5f, 2f), Tolerance, "at the high edge");
            Assert.AreEqual(0.4f, MotionVignette.OpacityForSpeed(9f, 0.4f, 0.5f, 2f), Tolerance, "clamped above");
            Assert.AreEqual(0.4f, MotionVignette.OpacityForSpeed(5f, 0.4f, 2f, 2f), Tolerance, "degenerate ramp is a step");
        }

        [Test]
        public void VignetteOpacityFollowsAssignedSpeed()
        {
            var overlay = new GameObject("Vignette");
            try
            {
                MotionVignette vignette = overlay.AddComponent<MotionVignette>();
                Assert.AreEqual(0f, vignette.Opacity, Tolerance, "starts at rest");

                vignette.Speed = 1.25f;
                Assert.AreEqual(0.2f, vignette.Opacity, Tolerance);

                vignette.Speed = 4.5f;
                Assert.AreEqual(0.4f, vignette.Opacity, Tolerance);

                vignette.Speed = 0.25f;
                Assert.AreEqual(0f, vignette.Opacity, Tolerance);
            }
            finally
            {
                Object.DestroyImmediate(overlay);
            }
        }

        [Test]
        public void HotbarAnchorSitsInFrontOfTheRig()
        {
            AssertVector(new Vector3(0f, 0f, 1.5f), WorldUi.HotbarAnchor(Vector3.zero, Quaternion.identity, 1.5f));
            AssertVector(
                new Vector3(1.5f, 0f, 0f),
                WorldUi.HotbarAnchor(Vector3.zero, Quaternion.Euler(0f, 90f, 0f), 1.5f));
            AssertVector(
                new Vector3(3f, 2f, 4.5f),
                WorldUi.HotbarAnchor(new Vector3(3f, 2f, 3f), Quaternion.identity, 1.5f));
        }

        [Test]
        public void SlotRectsTileTheStrip()
        {
            var strip = new Rect(10f, 20f, 900f, 100f);

            Rect first = WorldUi.SlotRect(strip, 0, 9);
            Assert.AreEqual(10f, first.x, Tolerance);
            Assert.AreEqual(20f, first.y, Tolerance);
            Assert.AreEqual(100f, first.width, Tolerance);
            Assert.AreEqual(100f, first.height, Tolerance);

            Rect last = WorldUi.SlotRect(strip, 8, 9);
            Assert.AreEqual(810f, last.x, Tolerance);
            Assert.AreEqual(100f, last.width, Tolerance);

            Assert.AreEqual(Rect.zero, WorldUi.SlotRect(strip, 9, 9), "out of range above");
            Assert.AreEqual(Rect.zero, WorldUi.SlotRect(strip, -1, 9), "out of range below");
            Assert.AreEqual(Rect.zero, WorldUi.SlotRect(strip, 0, 0), "no slots");
        }

        [Test]
        public void ProjectedPixelsPerMeterHalvesWithDistance()
        {
            float near = WorldUi.ProjectedPixelsPerMeter(60f, 1080f, 1f);
            Assert.AreEqual(1080f / (2f * Mathf.Tan(30f * Mathf.Deg2Rad)), near, 1e-2f);

            float far = WorldUi.ProjectedPixelsPerMeter(60f, 1080f, 2f);
            Assert.AreEqual(near * 0.5f, far, 1e-3f, "inverse distance");

            Assert.AreEqual(0f, WorldUi.ProjectedPixelsPerMeter(60f, 1080f, 0f), Tolerance, "zero distance");
            Assert.AreEqual(0f, WorldUi.ProjectedPixelsPerMeter(60f, 1080f, -1f), Tolerance, "negative distance");
            Assert.AreEqual(0f, WorldUi.ProjectedPixelsPerMeter(0f, 1080f, 1f), Tolerance, "degenerate FOV");
        }

        [Test]
        public void HotbarAnchorIsWorldLockedUntilReset()
        {
            var rig = new GameObject("AnchorRig");
            var hud = new GameObject("WorldUi");
            try
            {
                WorldUi ui = hud.AddComponent<WorldUi>();
                ui.AnchorSource = rig.transform;
                ui.Refresh();
                Vector3 first = ui.AnchorWorldPosition;
                Assert.AreEqual(1.5f, first.z, Tolerance, "captured 1.5 m ahead");

                rig.transform.position = new Vector3(10f, 0f, 0f);
                ui.Refresh();
                AssertVector(first, ui.AnchorWorldPosition, "the world-locked anchor does not follow the rig");

                ui.ResetAnchor();
                ui.Refresh();
                AssertVector(new Vector3(10f, 0f, 1.5f), ui.AnchorWorldPosition, "an explicit reset re-captures");
            }
            finally
            {
                Object.DestroyImmediate(hud);
                Object.DestroyImmediate(rig);
            }
        }

        [Test]
        public void SelectedSlotWrapsTheBridgeHotbarIndex()
        {
            var hud = new GameObject("WorldUi");
            try
            {
                WorldUi ui = hud.AddComponent<WorldUi>();
                ui.Refresh();
                Assert.AreEqual(0, ui.SelectedSlot, "no bridge defaults to slot zero");
            }
            finally
            {
                Object.DestroyImmediate(hud);
            }
        }

        private static void AssertVector(Vector3 expected, Vector3 actual, string message = null)
        {
            Assert.AreEqual(expected.x, actual.x, 1e-4f, "{0} (x)", message);
            Assert.AreEqual(expected.y, actual.y, 1e-4f, "{0} (y)", message);
            Assert.AreEqual(expected.z, actual.z, 1e-4f, "{0} (z)", message);
        }
    }
}

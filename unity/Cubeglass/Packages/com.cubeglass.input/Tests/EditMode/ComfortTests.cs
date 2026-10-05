using Cubeglass.Unity.Rendering;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools.Constraints;
using Is = UnityEngine.TestTools.Constraints.Is;

namespace Cubeglass.Unity.Input.Tests
{
    /// <summary>
    /// Pins the comfort and HUD maths (S7 Task 3, reworked in Task 4a): the
    /// snap-turn increment is exact, signed and accumulator-backed, the
    /// vignette curve is the recorded 0.5/2.0 m/s ramp to 0.4, and the WorldUi
    /// layout helpers (re-anchoring, slots, projection) are pure.
    /// </summary>
    public class ComfortTests
    {
        private const float Tolerance = 1e-5f;

        [Test]
        public void SnapTurnReturnsTheSignedIncrementAndAccumulates()
        {
            var rig = new GameObject("SnapRig");
            try
            {
                SnapTurn snap = rig.AddComponent<SnapTurn>();
                Assert.AreEqual(45f, snap.IncrementDegrees, Tolerance, "default increment");

                Assert.AreEqual(
                    45f,
                    snap.ApplyIncrement(1f),
                    Tolerance,
                    "one positive increment is Unity +Y (turn right) degrees");
                Assert.AreEqual(45f, snap.AccumulatedDegrees, Tolerance);

                Assert.AreEqual(45f, snap.ApplyIncrement(1f), Tolerance, "two increments");
                Assert.AreEqual(-45f, snap.ApplyIncrement(-1f), Tolerance, "the negative increment");
                Assert.AreEqual(0f, snap.ApplyIncrement(0f), Tolerance, "zero is a no-op");
                Assert.AreEqual(0f, snap.ApplyIncrement(float.NaN), Tolerance, "NaN is a no-op");
                Assert.AreEqual(0f, snap.ApplyIncrement(float.PositiveInfinity), Tolerance, "infinity is a no-op");

                snap.SnapEnabled = false;
                Assert.AreEqual(0f, snap.ApplyIncrement(1f), Tolerance, "disabled is a no-op");
                Assert.AreEqual(45f, snap.AccumulatedDegrees, Tolerance, "disabled calls do not accumulate");

                snap.ResetAccumulated();
                Assert.AreEqual(0f, snap.AccumulatedDegrees, Tolerance);
            }
            finally
            {
                Object.DestroyImmediate(rig);
            }
        }

        [Test]
        public void SnapTurnLeavesTheTransformUntouchedSoTheBridgeOwnsTheHeading()
        {
            var rig = new GameObject("SnapRig");
            try
            {
                rig.transform.localRotation = Quaternion.Euler(20f, 0f, 0f);
                SnapTurn snap = rig.AddComponent<SnapTurn>();
                snap.ApplyIncrement(1f);

                Assert.Less(
                    Quaternion.Angle(Quaternion.Euler(20f, 0f, 0f), rig.transform.localRotation),
                    Tolerance,
                    "the component reports degrees; GameplayBridge applies them to PlayerState");
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
        public void HotbarAnchorSitsAtEyeHeightInFrontOfTheRig()
        {
            const float eye = 0.9f;
            const float drop = 0.2f;
            AssertVector(
                new Vector3(0f, eye - drop, 1.5f),
                WorldUi.HotbarAnchor(Vector3.zero, Quaternion.identity, eye, 1.5f, drop));
            AssertVector(
                new Vector3(1.5f, eye - drop, 0f),
                WorldUi.HotbarAnchor(Vector3.zero, Quaternion.Euler(0f, 90f, 0f), eye, 1.5f, drop));
            AssertVector(
                new Vector3(3f, 2f + eye - drop, 4.5f),
                WorldUi.HotbarAnchor(new Vector3(3f, 2f, 3f), Quaternion.identity, eye, 1.5f, drop));
        }

        [Test]
        public void HotbarAnchorSitsInsideTheDefaultPerEyeFov()
        {
            float aspect = StereoRig.PerEyeViewportAspect(
                StereoRig.NominalScreenWidth, StereoRig.NominalScreenHeight);
            float halfVerticalFov = StereoRig.VerticalFovForHorizontal(
                StereoRigConfig.DefaultFovDegrees, aspect) * 0.5f;

            float centreAngle = WorldUi.AnchorAngleBelowEyeDegrees(
                WorldUi.DefaultAnchorDropMeters, WorldUi.DefaultAnchorDistanceMeters);
            float bottomAngle = WorldUi.AnchorAngleBelowEyeDegrees(
                WorldUi.DefaultAnchorDropMeters + (WorldUi.DefaultHotbarHeightMeters * 0.5f),
                WorldUi.DefaultAnchorDistanceMeters);

            Assert.Greater(centreAngle, 0f, "the world-locked anchor must sit below the eye axis");
            Assert.Less(
                bottomAngle,
                halfVerticalFov,
                "the bottom edge of the strip must stay inside the per-eye frustum at the default FOV "
                    + "(bottom {0:F1} deg vs half-FOV {1:F1} deg)",
                bottomAngle,
                halfVerticalFov);
            Assert.Less(
                centreAngle,
                halfVerticalFov,
                "the anchor centre must be inside the per-eye frustum");
            Assert.AreEqual(90f, WorldUi.AnchorAngleBelowEyeDegrees(0.2f, 0f), Tolerance, "degenerate distance");
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
        public void HotbarAnchorReanchorsOnTheBodyEveryRefresh()
        {
            var rig = new GameObject("AnchorRig");
            var hud = new GameObject("WorldUi");
            try
            {
                WorldUi ui = hud.AddComponent<WorldUi>();
                ui.AnchorSource = rig.transform;
                ui.Refresh();
                AssertVector(
                    new Vector3(0f, PlayerRoot.EyeHeightMeters - WorldUi.DefaultAnchorDropMeters, 1.5f),
                    ui.AnchorWorldPosition,
                    "anchored at eye height 1.5 m in front of the body");

                rig.transform.position = new Vector3(10f, 0f, 0f);
                ui.Refresh();
                AssertVector(
                    new Vector3(10f, PlayerRoot.EyeHeightMeters - WorldUi.DefaultAnchorDropMeters, 1.5f),
                    ui.AnchorWorldPosition,
                    "the anchor follows the body after the pose application");

                rig.transform.localRotation = Quaternion.Euler(0f, 90f, 0f);
                ui.Refresh();
                AssertVector(
                    new Vector3(11.5f, PlayerRoot.EyeHeightMeters - WorldUi.DefaultAnchorDropMeters, 0f),
                    ui.AnchorWorldPosition,
                    "the anchor follows the body heading, not the head-relative rotation");
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

        [Test]
        public void VisibilityRequiresBothEyes()
        {
            var rig = new GameObject("VisRig");
            try
            {
                Camera left = CreateEye(rig.transform, new Vector3(-0.032f, 0f, 0f));
                Camera right = CreateEye(rig.transform, new Vector3(0.032f, 0f, 0f));
                Vector3 inFront = new Vector3(0f, 0f, 1.5f);

                Assert.IsTrue(WorldUi.IsVisibleFrom(left, inFront), "left eye sees the anchor");
                Assert.IsTrue(WorldUi.IsVisibleFrom(right, inFront), "right eye sees the anchor");
                Assert.IsFalse(WorldUi.IsVisibleFrom(left, new Vector3(0f, 0f, -1.5f)), "behind the left eye");
                Assert.IsFalse(WorldUi.IsVisibleFrom(right, new Vector3(0f, 0f, -1.5f)), "behind the right eye");

                // A point only one eye can see must not count as visible: the
                // HUD is the AND of both eye viewports (TD-012).
                right.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
                Assert.IsTrue(WorldUi.IsVisibleFrom(left, inFront), "left still sees it");
                Assert.IsFalse(WorldUi.IsVisibleFrom(right, inFront), "the turned right eye does not");
            }
            finally
            {
                Object.DestroyImmediate(rig);
            }
        }

        [Test]
        public void SteadyStateRefreshIsAllocationFree()
        {
            var rigObject = new GameObject("AllocRig");
            var hudObject = new GameObject("AllocHud");
            try
            {
                StereoRig stereo = rigObject.AddComponent<StereoRig>();
                stereo.ApplyEyeLayout(3840, 1080);
                WorldUi ui = hudObject.AddComponent<WorldUi>();
                ui.AnchorSource = rigObject.transform;
                ui.GazeCamera = stereo.LeftCamera;
                ui.Visible = true;
                ui.Refresh();
                ui.Refresh();

                Assert.That(
                    () => ui.Refresh(),
                    Is.Not.AllocatingGCMemory(),
                    "the steady-state HUD refresh must not allocate (TD-012)");
            }
            finally
            {
                Object.DestroyImmediate(hudObject);
                Object.DestroyImmediate(rigObject);
            }
        }

        private static Camera CreateEye(Transform parent, Vector3 localPosition)
        {
            var eye = new GameObject("Eye");
            eye.transform.SetParent(parent, false);
            eye.transform.localPosition = localPosition;
            return eye.AddComponent<Camera>();
        }

        private static void AssertVector(Vector3 expected, Vector3 actual, string message = null)
        {
            Assert.AreEqual(expected.x, actual.x, 1e-4f, "{0} (x)", message);
            Assert.AreEqual(expected.y, actual.y, 1e-4f, "{0} (y)", message);
            Assert.AreEqual(expected.z, actual.z, 1e-4f, "{0} (z)", message);
        }
    }
}

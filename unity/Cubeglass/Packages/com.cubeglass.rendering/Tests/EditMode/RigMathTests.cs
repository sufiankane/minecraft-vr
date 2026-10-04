using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace Cubeglass.Unity.Rendering.Tests
{
    /// <summary>
    /// Rig layout math: half-width viewports tile the target exactly at any
    /// configured resolution, the eye offsets are ±IPD/2 along the rig's right
    /// axis, and the per-eye camera parameters come from the config.
    /// </summary>
    public class StereoRigTests
    {
        private const float Tolerance = 1e-6f;

        private GameObject root;
        private StereoRig rig;

        [SetUp]
        public void CreateRig()
        {
            root = new GameObject("StereoRigRoot");
            rig = root.AddComponent<StereoRig>();
            rig.ApplyEyeLayout();
        }

        [TearDown]
        public void DestroyRig()
        {
            Object.DestroyImmediate(root);
            root = null;
            rig = null;
        }

        [TestCase(1920, 1080)]
        [TestCase(2560, 1440)]
        [TestCase(3840, 2160)]
        public void ViewportsTileExactly(int width, int height)
        {
            Rect left = rig.LeftCamera.rect;
            Rect right = rig.RightCamera.rect;

            Assert.AreEqual(0f, left.x, Tolerance, "left.x");
            Assert.AreEqual(0f, left.y, Tolerance, "left.y");
            Assert.AreEqual(0.5f, left.width, Tolerance, "left.width");
            Assert.AreEqual(1f, left.height, Tolerance, "left.height");
            Assert.AreEqual(0.5f, right.x, Tolerance, "right.x");
            Assert.AreEqual(0f, right.y, Tolerance, "right.y");
            Assert.AreEqual(0.5f, right.width, Tolerance, "right.width");
            Assert.AreEqual(1f, right.height, Tolerance, "right.height");

            // In target pixels the viewports must meet exactly (left right edge
            // == right left edge, equal widths) and cover the frame.
            float leftRightPx = (left.x + left.width) * width;
            float rightLeftPx = right.x * width;
            float leftWidthPx = left.width * width;
            float rightWidthPx = right.width * width;
            float rightRightPx = (right.x + right.width) * width;

            Assert.AreEqual(leftRightPx, rightLeftPx, 1e-3f, "left/right seam");
            Assert.AreEqual(leftWidthPx, rightWidthPx, 1e-3f, "equal eye widths");
            Assert.AreEqual(0f, left.x * width, 1e-3f, "left edge");
            Assert.AreEqual(width, rightRightPx, 1e-3f, "right edge");
            Assert.AreEqual(height, (left.y + left.height) * height, 1e-3f, "left full height");
            Assert.AreEqual(height, (right.y + right.height) * height, 1e-3f, "right full height");
        }

        [Test]
        public void DefaultIpdOffsetsEyesByHalfIpd()
        {
            Assert.AreEqual(0.064f, rig.Config.IpdMeters, Tolerance, "default IPD");

            Assert.AreEqual(-0.032f, rig.LeftCamera.transform.localPosition.x, Tolerance, "left offset");
            Assert.AreEqual(0.032f, rig.RightCamera.transform.localPosition.x, Tolerance, "right offset");
            Assert.AreEqual(0f, rig.LeftCamera.transform.localPosition.y, Tolerance, "left y");
            Assert.AreEqual(0f, rig.LeftCamera.transform.localPosition.z, Tolerance, "left z");
            Assert.AreEqual(0f, rig.RightCamera.transform.localPosition.y, Tolerance, "right y");
            Assert.AreEqual(0f, rig.RightCamera.transform.localPosition.z, Tolerance, "right z");
        }

        [Test]
        public void ChangingIpdUpdatesBothEyeOffsets()
        {
            rig.Config.IpdMeters = 0.08f;
            rig.ApplyEyeLayout();

            Assert.AreEqual(-0.04f, rig.LeftCamera.transform.localPosition.x, Tolerance, "left offset");
            Assert.AreEqual(0.04f, rig.RightCamera.transform.localPosition.x, Tolerance, "right offset");
        }

        [Test]
        public void ProgrammaticOutOfRangeIpdAndFovClampTheCameras()
        {
            rig.Config.IpdMeters = 0.5f;
            rig.Config.FovDegrees = 200f;
            rig.ApplyEyeLayout(StereoRig.NominalScreenWidth, StereoRig.NominalScreenHeight);

            Assert.AreEqual(StereoRigConfig.MaxIpdMeters, rig.Config.IpdMeters, Tolerance, "ipd clamped on set");
            Assert.AreEqual(StereoRigConfig.MaxFovDegrees, rig.Config.FovDegrees, Tolerance, "fov clamped on set");
            Assert.AreEqual(-0.045f, rig.LeftCamera.transform.localPosition.x, Tolerance, "left offset from clamped ipd");
            Assert.AreEqual(0.045f, rig.RightCamera.transform.localPosition.x, Tolerance, "right offset from clamped ipd");
            Assert.AreEqual(
                ConvertedVerticalFov(StereoRigConfig.MaxFovDegrees),
                rig.LeftCamera.fieldOfView,
                1e-4f,
                "left fov from clamped horizontal fov");
            Assert.AreEqual(
                ConvertedVerticalFov(StereoRigConfig.MaxFovDegrees),
                rig.RightCamera.fieldOfView,
                1e-4f,
                "right fov from clamped horizontal fov");

            rig.Config.IpdMeters = 0.001f;
            rig.Config.FovDegrees = 5f;
            rig.ApplyEyeLayout(StereoRig.NominalScreenWidth, StereoRig.NominalScreenHeight);

            Assert.AreEqual(StereoRigConfig.MinIpdMeters, rig.Config.IpdMeters, Tolerance, "ipd lower clamp");
            Assert.AreEqual(StereoRigConfig.MinFovDegrees, rig.Config.FovDegrees, Tolerance, "fov lower clamp");
            Assert.AreEqual(-0.01f, rig.LeftCamera.transform.localPosition.x, Tolerance, "lower-clamped left offset");
            Assert.AreEqual(0.01f, rig.RightCamera.transform.localPosition.x, Tolerance, "lower-clamped right offset");
            Assert.AreEqual(
                ConvertedVerticalFov(StereoRigConfig.MinFovDegrees),
                rig.LeftCamera.fieldOfView,
                1e-4f,
                "lower-clamped fov");
        }

        [Test]
        public void EyeOffsetsRideTheRigRightAxis()
        {
            rig.Config.IpdMeters = 0.08f;
            rig.transform.localPosition = new Vector3(1f, 2f, 3f);
            rig.transform.localRotation = Quaternion.Euler(0f, 37f, 12f);
            rig.ApplyEyeLayout();

            Vector3 leftOffset = rig.LeftCamera.transform.position - rig.transform.position;
            Vector3 rightOffset = rig.RightCamera.transform.position - rig.transform.position;

            Assert.Less((rig.transform.right * -0.04f - leftOffset).magnitude, 1e-5f, "left offset along right");
            Assert.Less((rig.transform.right * 0.04f - rightOffset).magnitude, 1e-5f, "right offset along right");
        }

        [Test]
        public void CamerasMatchConfigAndShareDepthAndClearFlags()
        {
            rig.Config.FovDegrees = 62.5f;
            rig.Config.Near = 0.1f;
            rig.Config.Far = 250f;
            rig.ApplyEyeLayout(StereoRig.NominalScreenWidth, StereoRig.NominalScreenHeight);

            foreach (Camera camera in new[] { rig.LeftCamera, rig.RightCamera })
            {
                Assert.AreEqual(ConvertedVerticalFov(62.5f), camera.fieldOfView, 1e-4f, "converted fov");
                Assert.AreEqual(0.1f, camera.nearClipPlane, Tolerance, "near");
                Assert.AreEqual(250f, camera.farClipPlane, Tolerance, "far");
                Assert.IsTrue(camera.enabled, "camera enabled");
            }

            Assert.AreEqual(rig.LeftCamera.depth, rig.RightCamera.depth, "equal depth");
            Assert.AreEqual(rig.LeftCamera.clearFlags, rig.RightCamera.clearFlags, "equal clear flags");
        }

        /// <summary>
        /// I-3: a corrupted serialized config (a scene asset can hold NaN/inf
        /// because Unity deserialization bypasses the setters) must never reach
        /// a camera's transform or projection parameters.
        /// </summary>
        [Test]
        public void NonFiniteSerializedConfigNeverReachesTheCameras()
        {
            StereoRigConfig config = rig.Config;
            foreach (float poison in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
            {
                SetConfigField(config, "ipdMeters", poison);
                SetConfigField(config, "fovDegrees", poison);
                SetConfigField(config, "near", poison);
                SetConfigField(config, "far", poison);

                rig.ApplyEyeLayout(StereoRig.NominalScreenWidth, StereoRig.NominalScreenHeight);

                foreach (Camera camera in new[] { rig.LeftCamera, rig.RightCamera })
                {
                    Assert.IsTrue(float.IsFinite(camera.fieldOfView), "fov finite for poison " + poison);
                    Assert.IsTrue(float.IsFinite(camera.nearClipPlane), "near finite for poison " + poison);
                    Assert.IsTrue(float.IsFinite(camera.farClipPlane), "far finite for poison " + poison);
                    Assert.Greater(camera.nearClipPlane, 0f, "near positive");
                    Assert.Greater(camera.farClipPlane, camera.nearClipPlane, "far beyond near");
                    Assert.IsTrue(
                        float.IsFinite(camera.transform.localPosition.x),
                        "eye offset finite for poison " + poison);
                }
            }

            Assert.AreEqual(-0.032f, rig.LeftCamera.transform.localPosition.x, Tolerance, "default IPD fallback");
            Assert.AreEqual(0.032f, rig.RightCamera.transform.localPosition.x, Tolerance, "default IPD fallback");
            Assert.AreEqual(
                ConvertedVerticalFov(StereoRigConfig.DefaultFovDegrees),
                rig.LeftCamera.fieldOfView,
                1e-4f,
                "default FOV fallback");
            Assert.AreEqual(StereoRigConfig.DefaultNear, rig.LeftCamera.nearClipPlane, Tolerance, "default near fallback");
            Assert.AreEqual(StereoRigConfig.DefaultFar, rig.LeftCamera.farClipPlane, Tolerance, "default far fallback");
        }

        /// <summary>Far must stay beyond near even when both were written finite.</summary>
        [Test]
        public void InvertedSerializedNearFarFallsBackAboveNear()
        {
            SetConfigField(rig.Config, "near", 100f);
            SetConfigField(rig.Config, "far", 50f);

            rig.ApplyEyeLayout(StereoRig.NominalScreenWidth, StereoRig.NominalScreenHeight);

            Assert.AreEqual(100f, rig.LeftCamera.nearClipPlane, Tolerance, "the valid near is kept");
            Assert.Greater(rig.LeftCamera.farClipPlane, 100f, "far falls back beyond near");
            Assert.IsTrue(float.IsFinite(rig.LeftCamera.farClipPlane));
        }

        private static void SetConfigField(StereoRigConfig config, string field, object value)
        {
            FieldInfo info = typeof(StereoRigConfig).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(info, "StereoRigConfig." + field + " must exist");
            info.SetValue(config, value);
        }

        [Test]
        public void ApplyEyeLayoutIsIdempotent()
        {
            rig.Config.IpdMeters = 0.07f;
            rig.Config.FovDegrees = 55f;
            rig.ApplyEyeLayout(StereoRig.NominalScreenWidth, StereoRig.NominalScreenHeight);

            Camera left = rig.LeftCamera;
            Camera right = rig.RightCamera;
            Rect leftRect = left.rect;
            Rect rightRect = right.rect;

            rig.ApplyEyeLayout(StereoRig.NominalScreenWidth, StereoRig.NominalScreenHeight);

            Assert.AreSame(left, rig.LeftCamera, "left camera reused");
            Assert.AreSame(right, rig.RightCamera, "right camera reused");
            Assert.AreEqual(leftRect, left.rect, "left rect stable");
            Assert.AreEqual(rightRect, right.rect, "right rect stable");
            Assert.AreEqual(ConvertedVerticalFov(55f), left.fieldOfView, 1e-4f, "fov stable");
        }

        [Test]
        public void HorizontalFovConvertsToVerticalAtThePerEyeViewportAspect()
        {
            // 3840x1080 side by side = 1920x1080 per eye (16:9): 45 degrees
            // horizontal becomes 26.2313 degrees vertical.
            float wide = StereoRig.VerticalFovForHorizontal(
                45f, StereoRig.PerEyeViewportAspect(3840, 1080));
            Assert.AreEqual(26.2313f, wide, 0.05f, "1920x1080 per eye");

            // 1920x1080 total = 960x1080 per eye: 45 degrees horizontal
            // becomes 49.9701 degrees vertical.
            float half = StereoRig.VerticalFovForHorizontal(
                45f, StereoRig.PerEyeViewportAspect(1920, 1080));
            Assert.AreEqual(49.9701f, half, 0.05f, "960x1080 per eye");

            // The conversion inverts Unity's horizontal FOV relation.
            float roundTrip = 2f * Mathf.Atan(
                Mathf.Tan(half * 0.5f * Mathf.Deg2Rad) * StereoRig.PerEyeViewportAspect(1920, 1080))
                * Mathf.Rad2Deg;
            Assert.AreEqual(45f, roundTrip, 1e-3f, "round trip");
        }

        [Test]
        public void ApplyEyeLayoutAppliesTheConvertedVerticalFov()
        {
            rig.Config.FovDegrees = 45f;
            rig.ApplyEyeLayout(3840, 1080);

            float expected = StereoRig.VerticalFovForHorizontal(
                45f, StereoRig.PerEyeViewportAspect(3840, 1080));
            Assert.AreEqual(26.2313f, expected, 0.05f, "pinned per-eye 1920x1080 conversion");
            Assert.AreEqual(expected, rig.LeftCamera.fieldOfView, 1e-4f, "left eye vertical fov");
            Assert.AreEqual(expected, rig.RightCamera.fieldOfView, 1e-4f, "right eye vertical fov");
            Assert.AreNotEqual(45f, rig.LeftCamera.fieldOfView, "raw horizontal assignment must fail");
        }

        [Test]
        public void ApplyEyeLayoutWithUnknownScreenUsesTheNominalTarget()
        {
            rig.ApplyEyeLayout(0, 0);

            Assert.AreEqual(ConvertedVerticalFov(45f), rig.LeftCamera.fieldOfView, 1e-4f, "nominal target fallback");
        }

        private static float ConvertedVerticalFov(float horizontalFovDegrees)
        {
            return StereoRig.VerticalFovForHorizontal(
                horizontalFovDegrees,
                StereoRig.PerEyeViewportAspect(StereoRig.NominalScreenWidth, StereoRig.NominalScreenHeight));
        }

        [Test]
        public void CamerasAreChildrenOfTheRig()
        {
            Assert.AreEqual(rig.transform, rig.LeftCamera.transform.parent, "left camera parent");
            Assert.AreEqual(rig.transform, rig.RightCamera.transform.parent, "right camera parent");
        }
    }

    /// <summary>Defaults, the JSON subset load and the clamp rules.</summary>
    public class StereoRigConfigTests
    {
        private const float Tolerance = 1e-6f;

        [Test]
        public void DefaultsMatchAdr0010()
        {
            var config = new StereoRigConfig();

            Assert.AreEqual(0.064f, config.IpdMeters, Tolerance, "ipd");
            Assert.AreEqual(45f, config.FovDegrees, Tolerance, "fov");
            Assert.AreEqual(0.05f, config.Near, Tolerance, "near");
            Assert.AreEqual(500f, config.Far, Tolerance, "far");
            Assert.IsTrue(config.BorderlessFullscreen, "borderless");
            Assert.AreEqual(90, config.TargetRefresh, "refresh");
        }

        [Test]
        public void JsonOverridesPresentFieldsAndKeepsTheRestDefault()
        {
            var config = new StereoRigConfig();

            config.LoadFromJson(
                "{\"ipdMeters\":0.07,\"fovDegrees\":60,\"borderlessFullscreen\":false,\"targetRefresh\":72}");

            Assert.AreEqual(0.07f, config.IpdMeters, Tolerance, "ipd");
            Assert.AreEqual(60f, config.FovDegrees, Tolerance, "fov");
            Assert.IsFalse(config.BorderlessFullscreen, "borderless");
            Assert.AreEqual(72, config.TargetRefresh, "refresh");
            Assert.AreEqual(0.05f, config.Near, Tolerance, "near default kept");
            Assert.AreEqual(500f, config.Far, Tolerance, "far default kept");
        }

        [Test]
        public void EmptyJsonObjectKeepsEveryDefault()
        {
            var config = new StereoRigConfig();
            config.LoadFromJson("{}");

            Assert.AreEqual(0.064f, config.IpdMeters, Tolerance, "ipd");
            Assert.AreEqual(45f, config.FovDegrees, Tolerance, "fov");
            Assert.AreEqual(0.05f, config.Near, Tolerance, "near");
            Assert.AreEqual(500f, config.Far, Tolerance, "far");
            Assert.IsTrue(config.BorderlessFullscreen, "borderless");
            Assert.AreEqual(90, config.TargetRefresh, "refresh");
        }

        [Test]
        public void IpdOutsideRangeClamps()
        {
            var config = new StereoRigConfig();

            config.LoadFromJson("{\"ipdMeters\":0.5}");
            Assert.AreEqual(0.09f, config.IpdMeters, Tolerance, "upper clamp");

            config.LoadFromJson("{\"ipdMeters\":0.001}");
            Assert.AreEqual(0.02f, config.IpdMeters, Tolerance, "lower clamp");

            config.LoadFromJson("{\"ipdMeters\":0.075}");
            Assert.AreEqual(0.075f, config.IpdMeters, Tolerance, "in range untouched");
        }

        [Test]
        public void FovOutsideRangeClamps()
        {
            var config = new StereoRigConfig();

            config.LoadFromJson("{\"fovDegrees\":5}");
            Assert.AreEqual(10f, config.FovDegrees, Tolerance, "lower clamp");

            config.LoadFromJson("{\"fovDegrees\":200}");
            Assert.AreEqual(120f, config.FovDegrees, Tolerance, "upper clamp");

            config.LoadFromJson("{\"fovDegrees\":90}");
            Assert.AreEqual(90f, config.FovDegrees, Tolerance, "in range untouched");
        }

        [Test]
        public void NullOrEmptyJsonRestoresDefaults()
        {
            var config = new StereoRigConfig();

            config.LoadFromJson("{\"ipdMeters\":0.08}");
            config.LoadFromJson(null);
            Assert.AreEqual(0.064f, config.IpdMeters, Tolerance, "null restores defaults");

            config.LoadFromJson("{\"ipdMeters\":0.08}");
            config.LoadFromJson("   ");
            Assert.AreEqual(0.064f, config.IpdMeters, Tolerance, "blank restores defaults");
        }

        /// <summary>I-3: every numeric setter rejects non-finite input.</summary>
        [Test]
        public void NonFiniteSettersFallBackToDefaults()
        {
            var config = new StereoRigConfig();

            config.IpdMeters = float.NaN;
            Assert.AreEqual(StereoRigConfig.DefaultIpdMeters, config.IpdMeters, Tolerance, "NaN ipd");
            config.IpdMeters = float.PositiveInfinity;
            Assert.AreEqual(StereoRigConfig.DefaultIpdMeters, config.IpdMeters, Tolerance, "+inf ipd");
            config.FovDegrees = float.NegativeInfinity;
            Assert.AreEqual(StereoRigConfig.DefaultFovDegrees, config.FovDegrees, Tolerance, "-inf fov");
            config.Near = float.NaN;
            Assert.AreEqual(StereoRigConfig.DefaultNear, config.Near, Tolerance, "NaN near");
            config.Near = -1f;
            Assert.AreEqual(StereoRigConfig.DefaultNear, config.Near, Tolerance, "negative near");
            config.Far = float.PositiveInfinity;
            Assert.AreEqual(StereoRigConfig.DefaultFar, config.Far, Tolerance, "+inf far");
            config.Far = 0f;
            Assert.AreEqual(StereoRigConfig.DefaultFar, config.Far, Tolerance, "zero far");

            Assert.IsTrue(float.IsFinite(config.IpdMeters), "stored ipd finite");
            Assert.IsTrue(float.IsFinite(config.FovDegrees), "stored fov finite");
            Assert.IsTrue(float.IsFinite(config.Near), "stored near finite");
            Assert.IsTrue(float.IsFinite(config.Far), "stored far finite");
        }

        /// <summary>
        /// I-3/M-10: values that bypass the setters (JSON overwrite and
        /// serialized scene data) are rejected by <see cref="StereoRigConfig.Validate"/>,
        /// and refresh is clamped into the display-sane range.
        /// </summary>
        [Test]
        public void ValidateRejectsNonFiniteSerializedFieldsAndClampsRefresh()
        {
            var config = new StereoRigConfig();
            SetConfigField(config, "ipdMeters", float.NaN);
            SetConfigField(config, "fovDegrees", float.PositiveInfinity);
            SetConfigField(config, "near", float.NaN);
            SetConfigField(config, "far", -5f);
            SetConfigField(config, "targetRefresh", 0);

            Assert.IsTrue(config.Validate(), "every poisoned field must be reported as changed");
            Assert.AreEqual(StereoRigConfig.DefaultIpdMeters, config.IpdMeters, Tolerance, "ipd fallback");
            Assert.AreEqual(StereoRigConfig.DefaultFovDegrees, config.FovDegrees, Tolerance, "fov fallback");
            Assert.AreEqual(StereoRigConfig.DefaultNear, config.Near, Tolerance, "near fallback");
            Assert.AreEqual(500f, config.Far, Tolerance, "far fallback above near");
            Assert.AreEqual(
                StereoRigConfig.MinTargetRefresh,
                config.TargetRefresh,
                "a 1 Hz request must be clamped up (review M-10)");
            Assert.IsFalse(config.Validate(), "the second validation pass is clean");
        }

        [Test]
        public void LoadFromJsonClampsRefreshAndKeepsNearFarOrdered()
        {
            var config = new StereoRigConfig();
            config.LoadFromJson("{\"targetRefresh\":10000}");
            Assert.AreEqual(StereoRigConfig.MaxTargetRefresh, config.TargetRefresh, "upper refresh clamp");

            config.LoadFromJson("{\"near\":10,\"far\":5}");
            Assert.AreEqual(10f, config.Near, Tolerance, "valid near kept");
            Assert.Greater(config.Far, config.Near, "far is repaired above near");
            Assert.IsTrue(float.IsFinite(config.Far), "far stays finite");
        }

        private static void SetConfigField(StereoRigConfig config, string field, object value)
        {
            FieldInfo info = typeof(StereoRigConfig).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(info, "StereoRigConfig." + field + " must exist");
            info.SetValue(config, value);
        }
    }
}

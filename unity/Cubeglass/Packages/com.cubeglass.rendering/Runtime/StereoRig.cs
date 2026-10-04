using UnityEngine;

namespace Cubeglass.Unity.Rendering
{
    /// <summary>
    /// Owns the two side-by-side stereo cameras: one camera per eye with
    /// half-width viewports that tile the target exactly, eye offsets of
    /// ±IPD/2 along the rig's right axis and the per-eye camera parameters
    /// from <see cref="Config"/>.
    /// </summary>
    /// <remarks>
    /// The left camera renders <c>rect = (0, 0, 0.5, 1)</c> and the right
    /// camera <c>rect = (0.5, 0, 0.5, 1)</c>. <see cref="StereoRigConfig.FovDegrees"/>
    /// is a per-eye **horizontal** field of view (ADR-0010), while Unity's
    /// <see cref="Camera.fieldOfView"/> is vertical, so <see cref="ApplyEyeLayout"/>
    /// converts it at the per-eye viewport aspect. <see cref="ApplyEyeLayout()"/>
    /// is idempotent and allocation-free once the cameras exist, so it is safe
    /// to call from <see cref="Awake"/> and from the late-latch path.
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class StereoRig : MonoBehaviour
    {
        /// <summary>Nominal full side-by-side target width (1920×1080 per eye).</summary>
        public const int NominalScreenWidth = 3840;

        /// <summary>Nominal full side-by-side target height (1920×1080 per eye).</summary>
        public const int NominalScreenHeight = 1080;

        private const string LeftEyeName = "LeftEye";
        private const string RightEyeName = "RightEye";

        [SerializeField] private StereoRigConfig config = new StereoRigConfig();
        [SerializeField] private Camera leftCamera;
        [SerializeField] private Camera rightCamera;

        /// <summary>The rig's config instance; mutate then <see cref="ApplyEyeLayout"/>.</summary>
        public StereoRigConfig Config
        {
            get { return config; }
        }

        /// <summary>The left-eye camera (viewport <c>(0, 0, 0.5, 1)</c>).</summary>
        public Camera LeftCamera
        {
            get { return leftCamera; }
        }

        /// <summary>The right-eye camera (viewport <c>(0.5, 0, 0.5, 1)</c>).</summary>
        public Camera RightCamera
        {
            get { return rightCamera; }
        }

        private void Awake()
        {
            // A deserialized scene can carry out-of-range or non-finite values
            // (Unity writes serialized fields directly); repair them once
            // before the first layout so the committed defaults are restored
            // and the warning is logged at boot (reviews I-3, R-1).
            Config.Validate();
            ApplyEyeLayout();
        }

        private void OnValidate()
        {
            if (config == null)
            {
                config = new StereoRigConfig();
            }
        }

        /// <summary>
        /// Per-eye viewport aspect of a full side-by-side target: half the
        /// screen width over the screen height,
        /// <c>(screenWidth * 0.5) / screenHeight</c>. Returns 0 when either
        /// dimension is not positive (unknown/headless target).
        /// </summary>
        public static float PerEyeViewportAspect(int screenWidth, int screenHeight)
        {
            if (screenWidth <= 0 || screenHeight <= 0)
            {
                return 0f;
            }

            return (screenWidth * 0.5f) / screenHeight;
        }

        /// <summary>
        /// Converts a per-eye <paramref name="horizontalFovDegrees"/> into the
        /// vertical <see cref="Camera.fieldOfView"/> that shows it in a viewport
        /// of <paramref name="viewportAspect"/>:
        /// <c>vertical = 2 * atan(tan(horizontal / 2) / aspect)</c>. A
        /// non-positive aspect has no defined conversion, so the horizontal
        /// value is returned unchanged.
        /// </summary>
        public static float VerticalFovForHorizontal(float horizontalFovDegrees, float viewportAspect)
        {
            if (viewportAspect <= 0f)
            {
                return horizontalFovDegrees;
            }

            float halfRadians = horizontalFovDegrees * 0.5f * Mathf.Deg2Rad;
            return 2f * Mathf.Atan(Mathf.Tan(halfRadians) / viewportAspect) * Mathf.Rad2Deg;
        }

        /// <summary>
        /// Applies the layout for the current screen, falling back to the
        /// nominal 3840×1080 side-by-side target when the screen size is not
        /// available (headless/batch runs).
        /// </summary>
        public void ApplyEyeLayout()
        {
            ApplyEyeLayout(Screen.width, Screen.height);
        }

        /// <summary>
        /// Creates the eye cameras when missing and applies the viewport, eye
        /// offsets, FOV, clip planes, depth and clear flags from
        /// <see cref="Config"/>. <paramref name="screenWidth"/> and
        /// <paramref name="screenHeight"/> are the full side-by-side target in
        /// pixels; each eye's viewport is half the width, so the per-eye aspect
        /// is <c>(screenWidth * 0.5) / screenHeight</c>. The configured
        /// horizontal FOV is converted to Unity's vertical
        /// <see cref="Camera.fieldOfView"/> at that aspect; non-positive
        /// dimensions fall back to the nominal 3840×1080 target. Repeated calls
        /// converge on the same layout and do not allocate once both cameras
        /// exist.
        /// </summary>
        public void ApplyEyeLayout(int screenWidth, int screenHeight)
        {
            if (config == null)
            {
                config = new StereoRigConfig();
            }

            EnsureCameras();

            // Guard at the point of use too: Unity deserialization writes the
            // serialized fields directly, bypassing the clamping setters, so a
            // scene asset can still carry out-of-range values. Non-finite
            // inputs are rejected before any comparison (I-3): a NaN never
            // satisfies < or >, and a non-finite camera parameter poisons the
            // projection matrix of both eyes.
            float ipd = float.IsFinite(config.IpdMeters)
                ? Mathf.Clamp(config.IpdMeters, StereoRigConfig.MinIpdMeters, StereoRigConfig.MaxIpdMeters)
                : StereoRigConfig.DefaultIpdMeters;
            float horizontalFov = float.IsFinite(config.FovDegrees)
                ? Mathf.Clamp(config.FovDegrees, StereoRigConfig.MinFovDegrees, StereoRigConfig.MaxFovDegrees)
                : StereoRigConfig.DefaultFovDegrees;
            float nearClip = float.IsFinite(config.Near) && config.Near > 0f
                ? config.Near
                : StereoRigConfig.DefaultNear;
            float farClip;
            if (float.IsFinite(config.Far) && config.Far > nearClip)
            {
                farClip = config.Far;
            }
            else
            {
                // Repair the pair without overflowing: doubling a huge finite
                // near would otherwise produce +infinity (review R-5).
                float repairedFar = nearClip * 2f;
                if (!float.IsFinite(repairedFar) || !(repairedFar > nearClip))
                {
                    nearClip = StereoRigConfig.DefaultNear;
                    farClip = StereoRigConfig.DefaultFar;
                }
                else
                {
                    farClip = Mathf.Max(StereoRigConfig.DefaultFar, repairedFar);
                }
            }
            float aspect = PerEyeViewportAspect(screenWidth, screenHeight);
            if (aspect <= 0f)
            {
                aspect = PerEyeViewportAspect(NominalScreenWidth, NominalScreenHeight);
            }

            float verticalFov = VerticalFovForHorizontal(horizontalFov, aspect);
            float halfIpd = ipd * 0.5f;
            leftCamera.transform.localPosition = new Vector3(-halfIpd, 0f, 0f);
            rightCamera.transform.localPosition = new Vector3(halfIpd, 0f, 0f);

            leftCamera.rect = new Rect(0f, 0f, 0.5f, 1f);
            rightCamera.rect = new Rect(0.5f, 0f, 0.5f, 1f);

            leftCamera.fieldOfView = verticalFov;
            rightCamera.fieldOfView = verticalFov;

            leftCamera.nearClipPlane = nearClip;
            rightCamera.nearClipPlane = nearClip;
            leftCamera.farClipPlane = farClip;
            rightCamera.farClipPlane = farClip;

            leftCamera.depth = 0f;
            rightCamera.depth = 0f;

            leftCamera.clearFlags = CameraClearFlags.Skybox;
            rightCamera.clearFlags = CameraClearFlags.Skybox;

            leftCamera.enabled = true;
            rightCamera.enabled = true;
        }

        private void EnsureCameras()
        {
            if (leftCamera == null)
            {
                leftCamera = CreateEye(LeftEyeName);
            }

            if (rightCamera == null)
            {
                rightCamera = CreateEye(RightEyeName);
            }
        }

        private Camera CreateEye(string eyeName)
        {
            var eye = new GameObject(eyeName);
            eye.transform.SetParent(transform, false);
            return eye.AddComponent<Camera>();
        }
    }
}

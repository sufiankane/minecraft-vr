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
    /// camera <c>rect = (0.5, 0, 0.5, 1)</c>. <see cref="ApplyEyeLayout"/> is
    /// idempotent and allocation-free once the cameras exist, so it is safe to
    /// call from <see cref="Awake"/> and from the late-latch path.
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class StereoRig : MonoBehaviour
    {
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
        /// Creates the eye cameras when missing and applies the viewport, eye
        /// offsets, FOV, clip planes, depth and clear flags from
        /// <see cref="Config"/>. Repeated calls converge on the same layout and
        /// do not allocate once both cameras exist.
        /// </summary>
        public void ApplyEyeLayout()
        {
            if (config == null)
            {
                config = new StereoRigConfig();
            }

            EnsureCameras();

            // Clamp at the point of use too: Unity deserialization writes the
            // serialized fields directly, bypassing the clamping setters, so a
            // scene asset can still carry out-of-range values.
            float ipd = Mathf.Clamp(config.IpdMeters, StereoRigConfig.MinIpdMeters, StereoRigConfig.MaxIpdMeters);
            float fov = Mathf.Clamp(config.FovDegrees, StereoRigConfig.MinFovDegrees, StereoRigConfig.MaxFovDegrees);
            float halfIpd = ipd * 0.5f;
            leftCamera.transform.localPosition = new Vector3(-halfIpd, 0f, 0f);
            rightCamera.transform.localPosition = new Vector3(halfIpd, 0f, 0f);

            leftCamera.rect = new Rect(0f, 0f, 0.5f, 1f);
            rightCamera.rect = new Rect(0.5f, 0f, 0.5f, 1f);

            leftCamera.fieldOfView = fov;
            rightCamera.fieldOfView = fov;

            leftCamera.nearClipPlane = config.Near;
            rightCamera.nearClipPlane = config.Near;
            leftCamera.farClipPlane = config.Far;
            rightCamera.farClipPlane = config.Far;

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

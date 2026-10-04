using Cubeglass.Gameplay;
using Cubeglass.Unity.Rendering;
using UnityEngine;

namespace Cubeglass.Unity.Input
{
    /// <summary>
    /// The in-world HUD (S7 Task 3): a gaze-centred reticle and a hotbar strip
    /// anchored at the rig's eye height and
    /// <see cref="DefaultAnchorDistanceMeters"/> in front of the player body,
    /// with the selected slot highlighted from
    /// <see cref="PlayerState.HotbarIndex"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Update order.</b> <see cref="Refresh"/> runs every <c>LateUpdate</c>,
    /// after every <c>Update</c> in the frame, so it re-anchors on the player
    /// pose that <see cref="GameplayBridge"/> wrote this tick (bridge
    /// <c>Update</c> → HUD <c>LateUpdate</c> → <c>OnGUI</c>). It recomputes
    /// the anchor from the anchor source's current position and body yaw every
    /// frame — body-relative, not head-locked: the strip follows walking and
    /// snap turns but not the head-relative rotation applied by the late latch.
    /// It reuses cached <see cref="GUIContent"/> buffers and allocates nothing
    /// on steady frames; <c>OnGUI</c> only draws on
    /// <see cref="EventType.Repaint"/>.
    /// </para>
    /// <para>
    /// <b>Placement.</b> The anchor is <see cref="AnchorEyeHeight"/> above the
    /// anchor source's origin (the <see cref="PlayerRoot"/> feet), then
    /// <see cref="DefaultAnchorDistanceMeters"/> along the body forward and
    /// <see cref="AnchorDropMeters"/> straight down (world-locked, so it does
    /// not track head pitch). At the ADR-0010 defaults the drop is ≈7.6° below
    /// the eye axis and the strip's bottom edge ≈9.8°, inside the per-eye
    /// vertical half-FOV of ≈13.1° for the 45° horizontal FOV at 16:9 per eye
    /// (pinned by the EditMode FOV placement test). The old feet-anchored
    /// composition put the strip ≈31° below the eye and off-screen.
    /// </para>
    /// <para>
    /// <b>Monocular SBS (S7 known, deferred M2+).</b> The HUD (reticle and
    /// hotbar) is one screen-space IMGUI pass projected through the left eye
    /// camera only, so in the 3840×1080 side-by-side target it is composited
    /// into the left half and the right eye sees no HUD; there is no stereo
    /// depth. The reticle is screen-centred, so its apparent direction is the
    /// same for both eyes. Per-eye HUD geometry is deferred; see ADR-0011 and
    /// the s7-gate deferred minors.
    /// </para>
    /// <para>
    /// The layout maths (<see cref="HotbarAnchor"/>, <see cref="SlotRect"/>,
    /// <see cref="ProjectedPixelsPerMeter"/>) are static and pure so they are
    /// pinned in EditMode tests.
    /// </para>
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class WorldUi : MonoBehaviour
    {
        /// <summary>Default world-locked anchor distance in metres.</summary>
        public const float DefaultAnchorDistanceMeters = 1.5f;

        /// <summary>
        /// Default world-locked downward offset of the strip below the eye
        /// axis, in metres (≈7.6° at the default distance).
        /// </summary>
        public const float DefaultAnchorDropMeters = 0.2f;

        /// <summary>Default hotbar strip width in metres at the anchor.</summary>
        public const float DefaultHotbarWidthMeters = 0.9f;

        /// <summary>Default hotbar strip height in metres at the anchor.</summary>
        public const float DefaultHotbarHeightMeters = 0.12f;

        private const float ReticleDistanceMeters = 2f;

        [SerializeField] private Camera gazeCamera;
        [SerializeField] private Transform anchorSource;
        [SerializeField] private GameplayBridge bridge;
        [SerializeField] private bool visible = true;
        [SerializeField] private float anchorDistance = DefaultAnchorDistanceMeters;
        [SerializeField] private float anchorEyeHeight = PlayerRoot.EyeHeightMeters;
        [SerializeField] private float anchorDropMeters = DefaultAnchorDropMeters;
        [SerializeField] private float hotbarWidthMeters = DefaultHotbarWidthMeters;
        [SerializeField] private float hotbarHeightMeters = DefaultHotbarHeightMeters;
        [SerializeField] private float reticleSizePixels = 24f;

        private GUIContent reticleContent;
        private GUIContent[] slotContents;
        private Texture2D quad;
        private bool hasAnchor;
        private Vector3 anchorWorldPosition;
        private bool hotbarProjected;
        private Rect hotbarScreenRect;
        private int selectedSlot;

        /// <summary>Camera the HUD projects through; falls back to the bridge/rig.</summary>
        public Camera GazeCamera
        {
            get { return gazeCamera; }
            set { gazeCamera = value; }
        }

        /// <summary>
        /// The body transform the anchor is recomputed from; defaults to the
        /// bridge's <see cref="Cubeglass.Unity.Rendering.PlayerRoot"/> (or rig).
        /// </summary>
        public Transform AnchorSource
        {
            get { return anchorSource; }
            set { anchorSource = value; }
        }

        /// <summary>
        /// Eye height above the anchor source's origin, in metres; defaults to
        /// <see cref="PlayerRoot.EyeHeightMeters"/> because the source is the
        /// body (feet). Set 0 when the source is already at eye level.
        /// </summary>
        public float AnchorEyeHeight
        {
            get { return anchorEyeHeight; }
            set { anchorEyeHeight = value; }
        }

        /// <summary>
        /// World-locked downward offset of the strip below the eye axis, in
        /// metres; defaults to <see cref="DefaultAnchorDropMeters"/>.
        /// </summary>
        public float AnchorDropMeters
        {
            get { return anchorDropMeters; }
            set { anchorDropMeters = value; }
        }

        /// <summary>The bridge supplying the selected hotbar slot.</summary>
        public GameplayBridge Bridge
        {
            get { return bridge; }
            set { bridge = value; }
        }

        /// <summary>Whether the reticle and hotbar draw.</summary>
        public bool Visible
        {
            get { return visible; }
            set { visible = value; }
        }

        /// <summary>The cached body-relative anchor in Unity world space.</summary>
        public Vector3 AnchorWorldPosition
        {
            get { return anchorWorldPosition; }
        }

        /// <summary>Whether the hotbar strip projected on the last refresh.</summary>
        public bool HotbarProjected
        {
            get { return hotbarProjected; }
        }

        /// <summary>The cached hotbar screen rectangle (IMGUI, top-left origin).</summary>
        public Rect HotbarScreenRect
        {
            get { return hotbarScreenRect; }
        }

        /// <summary>The cached selected slot, wrapped into the hotbar range.</summary>
        public int SelectedSlot
        {
            get { return selectedSlot; }
        }

        private void LateUpdate()
        {
            Refresh();
        }

        /// <summary>
        /// Re-anchors on the current body pose and recomputes the selection and
        /// projected hotbar rectangle. Runs after the gameplay Update, so the
        /// anchor always reflects this frame's applied player root. Allocation-free
        /// after the first call.
        /// </summary>
        public void Refresh()
        {
            Transform source = ResolveAnchorSource(out float eyeHeight);
            hasAnchor = source != null;
            if (hasAnchor)
            {
                anchorWorldPosition = HotbarAnchor(
                    source.position, source.rotation, eyeHeight, anchorDistance, anchorDropMeters);
            }

            GameplayBridge sourceBridge = bridge;
            selectedSlot = sourceBridge != null && sourceBridge.Player != null
                ? Hotbar.WrapIndex(sourceBridge.Player.HotbarIndex)
                : 0;

            hotbarProjected = false;
            if (!visible || !hasAnchor)
            {
                return;
            }

            hotbarProjected = TryProjectStrip(
                ResolveCamera(),
                anchorWorldPosition,
                hotbarWidthMeters,
                hotbarHeightMeters,
                out hotbarScreenRect);
        }

        /// <summary>
        /// The body-relative anchor: <paramref name="eyeHeightMeters"/> above
        /// the source origin (the eye), <paramref name="distanceMeters"/> along
        /// the source's body forward, and <paramref name="dropMeters"/> straight
        /// down in world space (not head-relative).
        /// </summary>
        public static Vector3 HotbarAnchor(
            Vector3 sourcePosition,
            Quaternion sourceRotation,
            float eyeHeightMeters,
            float distanceMeters,
            float dropMeters)
        {
            Vector3 eye = sourcePosition + (Vector3.up * eyeHeightMeters);
            return eye + (sourceRotation * Vector3.forward * distanceMeters) + (Vector3.down * dropMeters);
        }

        /// <summary>
        /// The angle below the eye forward axis of a world-locked point
        /// <paramref name="dropMeters"/> below the eye at
        /// <paramref name="distanceMeters"/> in front, in degrees. Negative when
        /// the point is above the axis; a non-positive distance is degenerate
        /// and reported as 90 degrees.
        /// </summary>
        public static float AnchorAngleBelowEyeDegrees(float dropMeters, float distanceMeters)
        {
            if (!(distanceMeters > 0f))
            {
                return 90f;
            }

            return Mathf.Atan2(dropMeters, distanceMeters) * Mathf.Rad2Deg;
        }

        /// <summary>
        /// The <paramref name="slotIndex"/>-th equal-width column of
        /// <paramref name="strip"/>; out-of-range indices return
        /// <see cref="Rect.zero"/>.
        /// </summary>
        public static Rect SlotRect(Rect strip, int slotIndex, int slotCount)
        {
            if (slotCount <= 0 || slotIndex < 0 || slotIndex >= slotCount)
            {
                return Rect.zero;
            }

            float width = strip.width / slotCount;
            return new Rect(strip.x + (slotIndex * width), strip.y, width, strip.height);
        }

        /// <summary>
        /// Pixels per metre at <paramref name="distance"/> for a camera with
        /// the given vertical field of view and viewport height:
        /// <c>height / (2 * tan(fov / 2) * distance)</c>. Zero for a
        /// non-positive distance or a degenerate FOV.
        /// </summary>
        public static float ProjectedPixelsPerMeter(float verticalFovDegrees, float viewportHeightPixels, float distance)
        {
            if (!(distance > 0f) || float.IsNaN(distance) || float.IsInfinity(distance))
            {
                return 0f;
            }

            float tangent = Mathf.Tan(verticalFovDegrees * 0.5f * Mathf.Deg2Rad);
            if (!(tangent > 0f))
            {
                return 0f;
            }

            return viewportHeightPixels / (2f * tangent * distance);
        }

        /// <summary>
        /// Projects a world point to the IMGUI (top-left origin) rectangle of
        /// a strip <paramref name="widthMeters"/> by
        /// <paramref name="heightMeters"/> centred on it. False when the point
        /// is behind the camera or the projection degenerates.
        /// </summary>
        public static bool TryProjectStrip(
            Camera camera,
            Vector3 worldPoint,
            float widthMeters,
            float heightMeters,
            out Rect rect)
        {
            rect = Rect.zero;
            if (camera == null)
            {
                return false;
            }

            Vector3 screen = camera.WorldToScreenPoint(worldPoint);
            if (!(screen.z > 0f) || float.IsNaN(screen.z))
            {
                return false;
            }

            float pixelsPerMeter = ProjectedPixelsPerMeter(camera.fieldOfView, camera.pixelHeight, screen.z);
            if (!(pixelsPerMeter > 0f))
            {
                return false;
            }

            float width = widthMeters * pixelsPerMeter;
            float height = heightMeters * pixelsPerMeter;
            rect = new Rect(
                screen.x - (width * 0.5f),
                (Screen.height - screen.y) - (height * 0.5f),
                width,
                height);
            return true;
        }

        private void OnGUI()
        {
            if (!visible)
            {
                return;
            }

            if (Event.current != null && Event.current.type != EventType.Repaint)
            {
                return;
            }

            EnsureBuffers();
            DrawReticle();
            DrawHotbar();
        }

        private void DrawReticle()
        {
            Camera camera = ResolveCamera();
            if (camera == null)
            {
                return;
            }

            Vector3 point = camera.transform.position + (camera.transform.forward * ReticleDistanceMeters);
            Vector3 screen = camera.WorldToScreenPoint(point);
            if (!(screen.z > 0f) || float.IsNaN(screen.z))
            {
                return;
            }

            float half = reticleSizePixels * 0.5f;
            var rect = new Rect(
                screen.x - half,
                (Screen.height - screen.y) - half,
                reticleSizePixels,
                reticleSizePixels);
            GUI.Label(rect, reticleContent);
        }

        private void DrawHotbar()
        {
            if (!hotbarProjected)
            {
                return;
            }

            Color previous = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.55f);
            GUI.DrawTexture(hotbarScreenRect, quad);

            int count = Hotbar.SlotCount;
            for (int slot = 0; slot < count; slot++)
            {
                Rect slotRect = SlotRect(hotbarScreenRect, slot, count);
                slotRect.x += 1f;
                slotRect.y += 1f;
                slotRect.width -= 2f;
                slotRect.height -= 2f;
                GUI.color = slot == selectedSlot
                    ? new Color(1f, 0.85f, 0.2f, 0.85f)
                    : new Color(0.15f, 0.15f, 0.15f, 0.75f);
                GUI.DrawTexture(slotRect, quad);
                GUI.Label(slotRect, slotContents[slot]);
            }

            GUI.color = previous;
        }

        private Camera ResolveCamera()
        {
            if (gazeCamera != null)
            {
                return gazeCamera;
            }

            GameplayBridge sourceBridge = bridge;
            if (sourceBridge != null)
            {
                if (sourceBridge.GazeCamera != null)
                {
                    return sourceBridge.GazeCamera;
                }

                if (sourceBridge.Rig != null)
                {
                    return sourceBridge.Rig.LeftCamera;
                }
            }

            return anchorSource != null ? anchorSource.GetComponent<Camera>() : null;
        }

        private Transform ResolveAnchorSource(out float eyeHeightMeters)
        {
            if (anchorSource != null)
            {
                eyeHeightMeters = anchorEyeHeight;
                return anchorSource;
            }

            GameplayBridge sourceBridge = bridge;
            if (sourceBridge == null)
            {
                eyeHeightMeters = 0f;
                return null;
            }

            if (sourceBridge.PlayerRoot != null)
            {
                eyeHeightMeters = anchorEyeHeight;
                return sourceBridge.PlayerRoot.transform;
            }

            // The rig and bridge fallbacks are already at eye level (or have no
            // documented eye offset), so no extra height is added.
            eyeHeightMeters = 0f;
            return sourceBridge.Rig != null ? sourceBridge.Rig.transform : sourceBridge.transform;
        }

        private void EnsureBuffers()
        {
            if (reticleContent == null)
            {
                reticleContent = new GUIContent("+");
            }

            if (quad == null)
            {
                quad = new Texture2D(1, 1, TextureFormat.RGBA32, false);
                quad.name = "CubeglassWorldUiQuad";
                quad.hideFlags = HideFlags.HideAndDontSave;
                quad.SetPixel(0, 0, Color.white);
                quad.Apply(false, true);
            }

            if (slotContents == null)
            {
                int count = Hotbar.SlotCount;
                slotContents = new GUIContent[count];
                for (int slot = 0; slot < count; slot++)
                {
                    slotContents[slot] = new GUIContent((slot + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
            }
        }

        private void OnDestroy()
        {
            if (quad != null)
            {
                Destroy(quad);
                quad = null;
            }
        }
    }
}

using Cubeglass.Gameplay;
using UnityEngine;

namespace Cubeglass.Unity.Input
{
    /// <summary>
    /// The in-world HUD (S7 Task 3): a gaze-centred reticle and a hotbar strip
    /// world-locked <c>1.5 m</c> in front of the rig, with the selected slot
    /// highlighted from <see cref="PlayerState.HotbarIndex"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Refresh"/> runs every <c>Update</c> and recomputes the
    /// cache used by <c>OnGUI</c>: the world-locked anchor (captured once from
    /// the rig pose at start, not head-locked), the selected slot and the
    /// projected screen rectangle. It reuses cached <see cref="GUIContent"/>
    /// buffers and allocates nothing on steady frames; <c>OnGUI</c> only draws
    /// on <see cref="EventType.Repaint"/>.
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
        [SerializeField] private float hotbarWidthMeters = DefaultHotbarWidthMeters;
        [SerializeField] private float hotbarHeightMeters = DefaultHotbarHeightMeters;
        [SerializeField] private float reticleSizePixels = 24f;

        private GUIContent reticleContent;
        private GUIContent[] slotContents;
        private Texture2D quad;
        private bool anchorSet;
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

        /// <summary>The transform the world-locked anchor is captured from.</summary>
        public Transform AnchorSource
        {
            get { return anchorSource; }
            set { anchorSource = value; }
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

        /// <summary>The cached world-locked anchor in Unity world space.</summary>
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

        private void Update()
        {
            Refresh();
        }

        /// <summary>
        /// Recomputes the cached anchor (once), selection and projected hotbar
        /// rectangle. Allocation-free after the first call.
        /// </summary>
        public void Refresh()
        {
            Transform source = ResolveAnchorSource();
            if (!anchorSet && source != null)
            {
                anchorWorldPosition = HotbarAnchor(source.position, source.rotation, anchorDistance);
                anchorSet = true;
            }

            GameplayBridge sourceBridge = bridge;
            selectedSlot = sourceBridge != null && sourceBridge.Player != null
                ? Hotbar.WrapIndex(sourceBridge.Player.HotbarIndex)
                : 0;

            hotbarProjected = false;
            if (!visible || !anchorSet)
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

        /// <summary>Captures the anchor again from the current rig pose.</summary>
        public void ResetAnchor()
        {
            anchorSet = false;
        }

        /// <summary>The world-locked anchor: rig position plus its Unity forward.</summary>
        public static Vector3 HotbarAnchor(Vector3 rigPosition, Quaternion rigRotation, float distance)
        {
            return rigPosition + (rigRotation * Vector3.forward * distance);
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

        private Transform ResolveAnchorSource()
        {
            if (anchorSource != null)
            {
                return anchorSource;
            }

            GameplayBridge sourceBridge = bridge;
            if (sourceBridge == null)
            {
                return null;
            }

            if (sourceBridge.Rig != null)
            {
                return sourceBridge.Rig.transform;
            }

            return sourceBridge.transform;
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

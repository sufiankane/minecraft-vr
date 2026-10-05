using System;
using Cubeglass.Gameplay;
using Cubeglass.Unity.Rendering;
using UnityEngine;

namespace Cubeglass.Unity.Input
{
    /// <summary>
    /// The in-world HUD (S7 Task 3, world-space since TD-012): a gaze-centred
    /// reticle quad and a hotbar strip of quads anchored at the rig's eye
    /// height and <see cref="DefaultAnchorDistanceMeters"/> in front of the
    /// player body, with the selected slot highlighted from
    /// <see cref="PlayerState.HotbarIndex"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Stereo (TD-012).</b> Reticle and hotbar are real scene geometry with
    /// <see cref="MeshRenderer"/>s, so both eye cameras of the side-by-side rig
    /// render them (each at its own eye offset), unlike the old single-pass
    /// IMGUI HUD which only composited into the left viewport. The quads share
    /// one generated mesh and one unlit colour material; colours are applied
    /// through cached <see cref="MaterialPropertyBlock"/>s, so steady frames
    /// reuse every object and allocation-free after the first refresh.
    /// </para>
    /// <para>
    /// <b>Update order.</b> <see cref="Refresh"/> runs every <c>LateUpdate</c>,
    /// after every <c>Update</c> in the frame, so it re-anchors on the player
    /// pose that <see cref="GameplayBridge"/> wrote this tick (bridge
    /// <c>Update</c> → HUD <c>LateUpdate</c>). It recomputes the anchor from
    /// the anchor source's current position and body yaw every frame —
    /// body-relative, not head-locked: the strip follows walking and snap turns
    /// but not the head-relative rotation applied by the late latch.
    /// </para>
    /// <para>
    /// <b>Placement.</b> The anchor is <see cref="AnchorEyeHeight"/> above the
    /// anchor source's origin (the <see cref="PlayerRoot"/> feet), then
    /// <see cref="DefaultAnchorDistanceMeters"/> along the body forward and
    /// <see cref="AnchorDropMeters"/> straight down (world-locked, so it does
    /// not track head pitch). At the ADR-0010 defaults the drop is ≈7.6° below
    /// the eye axis and the strip's bottom edge ≈9.8°, inside the per-eye
    /// vertical half-FOV of ≈13.1° for the 45° horizontal FOV at 16:9 per eye
    /// (pinned by the EditMode FOV placement test). The strip faces the gaze
    /// camera so it stays readable without head-locking.
    /// </para>
    /// <para>
    /// The layout maths (<see cref="HotbarAnchor"/>, <see cref="SlotRect"/>,
    /// <see cref="ProjectedPixelsPerMeter"/>) are static and pure so they are
    /// pinned in EditMode tests. <see cref="HotbarVisible"/> and
    /// <see cref="ReticleVisible"/> report whether both eye cameras see the
    /// geometry on the last refresh.
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

        /// <summary>Default reticle quad edge length in metres at the gaze point.</summary>
        public const float DefaultReticleSizeMeters = 0.02f;

        /// <summary>Distance along the gaze the reticle quad is placed, in metres.</summary>
        public const float ReticleDistanceMeters = 2f;

        private const float SlotGapMeters = 0.01f;
        private const string ReticleName = "CubeglassHudReticle";
        private const string HotbarName = "CubeglassHudHotbar";

        private static readonly Color BackgroundColor = new Color(0f, 0f, 0f, 0.55f);
        private static readonly Color SlotColor = new Color(0.15f, 0.15f, 0.15f, 0.75f);
        private static readonly Color SelectedSlotColor = new Color(1f, 0.85f, 0.2f, 0.85f);

        [SerializeField] private Camera gazeCamera;
        [SerializeField] private Transform anchorSource;
        [SerializeField] private GameplayBridge bridge;
        [SerializeField] private bool visible = true;
        [SerializeField] private float anchorDistance = DefaultAnchorDistanceMeters;
        [SerializeField] private float anchorEyeHeight = PlayerRoot.EyeHeightMeters;
        [SerializeField] private float anchorDropMeters = DefaultAnchorDropMeters;
        [SerializeField] private float hotbarWidthMeters = DefaultHotbarWidthMeters;
        [SerializeField] private float hotbarHeightMeters = DefaultHotbarHeightMeters;
        [SerializeField] private float reticleSizeMeters = DefaultReticleSizeMeters;

        private UnityEngine.Mesh quadMesh;
        private Material material;
        private MaterialPropertyBlock[] slotProperties;
        private Color[] slotColours;
        private GameObject reticleObject;
        private GameObject hotbarObject;
        private MeshRenderer reticleRenderer;
        private MeshRenderer hotbarBackground;
        private MeshRenderer[] hotbarSlots;
        private bool hasAnchor;
        private Vector3 anchorWorldPosition;
        private bool hotbarVisible;
        private bool reticleVisible;
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

        /// <summary>World-locked anchor distance in metres along body forward.</summary>
        public float AnchorDistance
        {
            get { return anchorDistance; }
            set { anchorDistance = value; }
        }

        /// <summary>The bridge supplying the selected hotbar slot.</summary>
        public GameplayBridge Bridge
        {
            get { return bridge; }
            set { bridge = value; }
        }

        /// <summary>Whether the reticle and hotbar are active.</summary>
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

        /// <summary>The reticle quad object; created on the first refresh.</summary>
        public GameObject ReticleObject
        {
            get { return reticleObject; }
        }

        /// <summary>The hotbar strip object (background + slot children); created on the first refresh.</summary>
        public GameObject HotbarObject
        {
            get { return hotbarObject; }
        }

        /// <summary>The reticle's renderer.</summary>
        public MeshRenderer ReticleRenderer
        {
            get { return reticleRenderer; }
        }

        /// <summary>The hotbar background renderer.</summary>
        public MeshRenderer HotbarBackground
        {
            get { return hotbarBackground; }
        }

        /// <summary>The number of hotbar slot renderers (equals <see cref="Hotbar.SlotCount"/>).</summary>
        public int HotbarSlotCount
        {
            get { return hotbarSlots != null ? hotbarSlots.Length : 0; }
        }

        /// <summary>The shared quad mesh reused by every HUD renderer; null before the first refresh.</summary>
        public UnityEngine.Mesh QuadMesh
        {
            get { return quadMesh; }
        }

        /// <summary>The shared unlit HUD material; null before the first refresh.</summary>
        public Material HudMaterial
        {
            get { return material; }
        }

        /// <summary>
        /// Whether both eye cameras saw the reticle on the last
        /// <see cref="Refresh"/> (TD-012 stereo visibility).
        /// </summary>
        public bool ReticleVisible
        {
            get { return reticleVisible; }
        }

        /// <summary>
        /// Whether both eye cameras saw the hotbar anchor on the last
        /// <see cref="Refresh"/> (TD-012 stereo visibility).
        /// </summary>
        public bool HotbarVisible
        {
            get { return hotbarVisible; }
        }

        /// <summary>The cached selected slot, wrapped into the hotbar range.</summary>
        public int SelectedSlot
        {
            get { return selectedSlot; }
        }

        /// <summary>The renderer of <paramref name="slotIndex"/>, or null when out of range.</summary>
        public MeshRenderer SlotRenderer(int slotIndex)
        {
            if (hotbarSlots == null || slotIndex < 0 || slotIndex >= hotbarSlots.Length)
            {
                return null;
            }

            return hotbarSlots[slotIndex];
        }

        /// <summary>Whether <paramref name="slotIndex"/> was the selected slot on the last refresh.</summary>
        public bool IsSlotSelected(int slotIndex)
        {
            return slotIndex == selectedSlot;
        }

        private void LateUpdate()
        {
            Refresh();
        }

        /// <summary>
        /// Re-anchors on the current body pose, updates the selection and the
        /// world-space visuals, and records whether both eye cameras see them.
        /// Runs after the gameplay Update, so the anchor always reflects this
        /// frame's applied player root. Allocation-free after the first call.
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

            hotbarVisible = false;
            reticleVisible = false;
            ResolveEyeCameras(out Camera left, out Camera right);
            Camera gaze = left != null ? left : right;
            if (!visible || !hasAnchor || gaze == null || left == null || right == null)
            {
                SetVisualsActive(false);
                return;
            }

            EnsureVisuals();
            if (hotbarObject == null || reticleObject == null)
            {
                SetVisualsActive(false);
                return;
            }

            SetVisualsActive(true);
            UpdateSelectionColours();

            Quaternion gazeRotation = gaze.transform.rotation;
            hotbarObject.transform.SetPositionAndRotation(anchorWorldPosition, gazeRotation);
            Vector3 reticlePosition = gaze.transform.position + (gaze.transform.forward * ReticleDistanceMeters);
            reticleObject.transform.SetPositionAndRotation(reticlePosition, gazeRotation);

            hotbarVisible = IsVisibleFrom(left, anchorWorldPosition) && IsVisibleFrom(right, anchorWorldPosition);
            reticleVisible = IsVisibleFrom(left, reticlePosition) && IsVisibleFrom(right, reticlePosition);
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
        /// Whether <paramref name="camera"/> sees <paramref name="worldPoint"/>:
        /// in front of the near plane and inside its own viewport rectangle
        /// (a half-width eye rect reports its own [0, 1] range).
        /// </summary>
        public static bool IsVisibleFrom(Camera camera, Vector3 worldPoint)
        {
            if (camera == null)
            {
                return false;
            }

            Vector3 viewport = camera.WorldToViewportPoint(worldPoint);
            return viewport.z > 0f
                && !float.IsNaN(viewport.x) && !float.IsNaN(viewport.y)
                && viewport.x >= 0f && viewport.x <= 1f
                && viewport.y >= 0f && viewport.y <= 1f;
        }

        /// <summary>
        /// The generated unit quad (centred at the origin, normal -Z) shared by
        /// every HUD renderer; reused, never rebuilt after the first call.
        /// </summary>
        public static UnityEngine.Mesh CreateQuadMesh(string name)
        {
            var mesh = new UnityEngine.Mesh();
            mesh.name = name;
            mesh.hideFlags = HideFlags.HideAndDontSave;
            mesh.vertices = new[]
            {
                new Vector3(-0.5f, -0.5f, 0f),
                new Vector3(0.5f, -0.5f, 0f),
                new Vector3(0.5f, 0.5f, 0f),
                new Vector3(-0.5f, 0.5f, 0f),
            };
            mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            mesh.normals = new[] { Vector3.back, Vector3.back, Vector3.back, Vector3.back };
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>
        /// The unlit HUD shader: <c>Sprites/Default</c> (always included, unlit,
        /// alpha blend) with fallbacks for stripped player builds. Null only
        /// when every candidate is missing.
        /// </summary>
        public static Shader FindHudShader()
        {
            return Shader.Find("Sprites/Default")
                ?? Shader.Find("Unlit/Color")
                ?? Shader.Find("Unlit/Texture");
        }

        private void EnsureVisuals()
        {
            if (quadMesh == null)
            {
                quadMesh = CreateQuadMesh("CubeglassWorldUiQuad");
            }

            if (material == null)
            {
                Shader shader = FindHudShader();
                if (shader == null)
                {
                    Debug.LogError("WorldUi: no unlit HUD shader available; the HUD is hidden.", this);
                    SetVisualsActive(false);
                    return;
                }

                material = new Material(shader);
                material.name = "CubeglassWorldUiMaterial";
                material.hideFlags = HideFlags.HideAndDontSave;
            }

            if (reticleObject == null)
            {
                reticleObject = new GameObject(ReticleName);
                reticleObject.hideFlags = HideFlags.HideAndDontSave;
                reticleRenderer = AddQuad(reticleObject, reticleSizeMeters, reticleSizeMeters, BackgroundColor);
            }

            if (hotbarObject == null)
            {
                BuildHotbar();
            }
        }

        private void BuildHotbar()
        {
            int count = Hotbar.SlotCount;
            hotbarObject = new GameObject(HotbarName);
            hotbarObject.hideFlags = HideFlags.HideAndDontSave;

            var backgroundObject = new GameObject("Background");
            backgroundObject.hideFlags = HideFlags.HideAndDontSave;
            backgroundObject.transform.SetParent(hotbarObject.transform, false);
            hotbarBackground = AddQuad(backgroundObject, hotbarWidthMeters, hotbarHeightMeters, BackgroundColor);

            hotbarSlots = new MeshRenderer[count];
            slotProperties = new MaterialPropertyBlock[count];
            slotColours = new Color[count];
            var strip = new Rect(
                -hotbarWidthMeters * 0.5f,
                -hotbarHeightMeters * 0.5f,
                hotbarWidthMeters,
                hotbarHeightMeters);
            for (int slot = 0; slot < count; slot++)
            {
                Rect rect = SlotRect(strip, slot, count);
                var slotObject = new GameObject("Slot" + (slot + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
                slotObject.hideFlags = HideFlags.HideAndDontSave;
                slotObject.transform.SetParent(hotbarObject.transform, false);
                slotObject.transform.localPosition = new Vector3(rect.center.x, rect.center.y, 0f);
                slotObject.transform.localScale = new Vector3(
                    Mathf.Max(1e-4f, rect.width - SlotGapMeters),
                    Mathf.Max(1e-4f, rect.height - SlotGapMeters),
                    1f);
                slotObject.AddComponent<MeshFilter>().sharedMesh = quadMesh;
                hotbarSlots[slot] = slotObject.AddComponent<MeshRenderer>();
                hotbarSlots[slot].sharedMaterial = material;
                slotProperties[slot] = new MaterialPropertyBlock();
                hotbarSlots[slot].SetPropertyBlock(slotProperties[slot]);
            }

            UpdateSelectionColours();
        }

        private MeshRenderer AddQuad(GameObject owner, float width, float height, Color colour)
        {
            owner.transform.localScale = new Vector3(width, height, 1f);
            owner.AddComponent<MeshFilter>().sharedMesh = quadMesh;
            var renderer = owner.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            var properties = new MaterialPropertyBlock();
            properties.SetColor(ColorId, colour);
            renderer.SetPropertyBlock(properties);
            return renderer;
        }

        private static readonly int ColorId = Shader.PropertyToID("_Color");

        private void UpdateSelectionColours()
        {
            if (hotbarSlots == null)
            {
                return;
            }

            for (int slot = 0; slot < hotbarSlots.Length; slot++)
            {
                Color target = slot == selectedSlot ? SelectedSlotColor : SlotColor;
                if (slotColours[slot] != target)
                {
                    slotColours[slot] = target;
                    slotProperties[slot].SetColor(ColorId, target);
                    hotbarSlots[slot].SetPropertyBlock(slotProperties[slot]);
                }
            }
        }

        private void SetVisualsActive(bool active)
        {
            if (reticleObject != null && reticleObject.activeSelf != active)
            {
                reticleObject.SetActive(active);
            }

            if (hotbarObject != null && hotbarObject.activeSelf != active)
            {
                hotbarObject.SetActive(active);
            }
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

        private void ResolveEyeCameras(out Camera left, out Camera right)
        {
            Camera gaze = ResolveCamera();
            StereoRig rig = bridge != null ? bridge.Rig : null;
            if (rig == null && gaze != null)
            {
                rig = gaze.GetComponentInParent<StereoRig>();
            }

            if (rig != null && rig.LeftCamera != null && rig.RightCamera != null)
            {
                left = rig.LeftCamera;
                right = rig.RightCamera;
                return;
            }

            // No stereo rig (single-camera preview or a bare test): the one
            // camera stands in for both eyes so the visibility contract still
            // reports honestly instead of always-false.
            left = gaze;
            right = gaze;
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

        private void OnDestroy()
        {
            DestroyAsset(quadMesh);
            quadMesh = null;

            DestroyAsset(material);
            material = null;

            if (reticleObject != null)
            {
                DestroyAsset(reticleObject);
                reticleObject = null;
                reticleRenderer = null;
            }

            if (hotbarObject != null)
            {
                DestroyAsset(hotbarObject);
                hotbarObject = null;
                hotbarBackground = null;
                hotbarSlots = null;
                slotProperties = null;
                slotColours = null;
            }
        }

        private static void DestroyAsset(UnityEngine.Object target)
        {
            if (target == null)
            {
                return;
            }

            if (Application.isPlaying)
            {
                Destroy(target);
            }
            else
            {
                DestroyImmediate(target);
            }
        }
    }
}

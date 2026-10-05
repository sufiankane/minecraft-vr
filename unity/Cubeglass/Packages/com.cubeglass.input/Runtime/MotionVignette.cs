using Cubeglass.Unity.Rendering;
using UnityEngine;

namespace Cubeglass.Unity.Input
{
    /// <summary>
    /// Comfort vignette shown while the player moves (S7 Task 3): a soft-edge
    /// black overlay whose opacity is a pure function of planar speed
    /// (0 at or below <c>0.5 m/s</c>, 0.4 at or above <c>2 m/s</c>, linear in
    /// between). The overlay is only drawn when the opacity is positive.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="GameplayBridge"/> writes <see cref="Speed"/> from the
    /// player's planar velocity every tick. The gradient texture is generated
    /// once and reused, so steady frames do not allocate.
    /// </para>
    /// <para>
    /// <b>Per-eye rects (TD-026).</b> In the side-by-side target the overlay is
    /// drawn once per eye camera over that camera's own pixel rectangle instead
    /// of one full-screen rectangle spanning both eyes, so each eye gets the
    /// correct vignette. With no stereo rig the full screen is drawn once
    /// (single-camera preview). <see cref="GuiRectForCamera"/> and
    /// <see cref="GuiEyeRects"/> are pure and pinned in EditMode tests at the
    /// nominal 3840×1080 target.
    /// </para>
    /// </remarks>
    [DisallowMultipleComponent]
    public class MotionVignette : MonoBehaviour
    {
        /// <summary>Default maximum opacity at full speed.</summary>
        public const float DefaultMaxOpacity = 0.4f;

        /// <summary>Default speed below which the vignette is invisible, in m/s.</summary>
        public const float DefaultMinSpeed = 0.5f;

        /// <summary>Default speed at which the vignette reaches full opacity, in m/s.</summary>
        public const float DefaultFullSpeed = 2f;

        private const int TextureSize = 32;

        [SerializeField] private bool vignetteEnabled = true;
        [SerializeField] private float maxOpacity = DefaultMaxOpacity;
        [SerializeField] private float minSpeed = DefaultMinSpeed;
        [SerializeField] private float fullSpeed = DefaultFullSpeed;
        [SerializeField] private StereoRig rig;

        private Texture2D gradient;
        private StereoRig resolvedRig;
        private bool rigResolved;

        /// <summary>
        /// The stereo rig whose eye rectangles are used; resolved from the
        /// scene on first use when not assigned.
        /// </summary>
        public StereoRig Rig
        {
            get { return rig; }
            set
            {
                rig = value;
                resolvedRig = value;
                rigResolved = true;
            }
        }

        /// <summary>The most recently forwarded planar speed in metres per second.</summary>
        public float Speed { get; set; }

        /// <summary>The forwarded opacity in [0, <see cref="MaxOpacity"/>].</summary>
        public float Opacity
        {
            get { return OpacityForSpeed(Speed, maxOpacity, minSpeed, fullSpeed); }
        }

        /// <summary>Whether the overlay may draw at all.</summary>
        public bool VignetteEnabled
        {
            get { return vignetteEnabled; }
            set { vignetteEnabled = value; }
        }

        /// <summary>The opacity at full speed (0.4 by default).</summary>
        public float MaxOpacity
        {
            get { return maxOpacity; }
            set { maxOpacity = value; }
        }

        /// <summary>
        /// The pure speed-to-opacity curve: zero for a non-positive or NaN
        /// speed, zero at or below <paramref name="minSpeed"/>, full
        /// <paramref name="maxOpacity"/> at or above
        /// <paramref name="fullSpeed"/>, and linear between.
        /// </summary>
        public static float OpacityForSpeed(float speed, float maxOpacity, float minSpeed, float fullSpeed)
        {
            if (!(speed > minSpeed) || float.IsNaN(maxOpacity))
            {
                return 0f;
            }

            if (!(fullSpeed > minSpeed))
            {
                return maxOpacity;
            }

            if (speed >= fullSpeed)
            {
                return maxOpacity;
            }

            return maxOpacity * ((speed - minSpeed) / (fullSpeed - minSpeed));
        }

        private void OnGUI()
        {
            if (!vignetteEnabled)
            {
                return;
            }

            float opacity = Opacity;
            if (!(opacity > 0f))
            {
                return;
            }

            if (Event.current != null && Event.current.type != EventType.Repaint)
            {
                return;
            }

            EnsureGradient();
            StereoRig stereo = ResolveRig();
            if (stereo != null && stereo.LeftCamera != null && stereo.RightCamera != null)
            {
                int width = Screen.width;
                int height = Screen.height;
                PaintTexture(GuiEyeRect(stereo.LeftCamera, width, height), opacity);
                PaintTexture(GuiEyeRect(stereo.RightCamera, width, height), opacity);
            }
            else
            {
                PaintTexture(new Rect(0f, 0f, Screen.width, Screen.height), opacity);
            }
        }

        /// <summary>
        /// Paints one vignette rectangle with the gradient at
        /// <paramref name="opacity"/>. Virtual so the headless IMGUI paint tests
        /// can substitute a recording surface (TD-020): <c>GUI.DrawTexture</c>
        /// refuses to run outside a real OnGUI callback, which a batch-mode test
        /// cannot provide.
        /// </summary>
        protected virtual void PaintTexture(Rect rect, float opacity)
        {
            Color previous = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, opacity);
            GUI.DrawTexture(rect, gradient, ScaleMode.StretchToFill);
            GUI.color = previous;
        }

        /// <summary>
        /// The IMGUI (top-left origin) rectangle of <paramref name="camera"/>'s
        /// viewport for a screen of the given size. IMGUI's Y axis starts at the
        /// top while <see cref="Camera.rect"/>/pixel rects start at the bottom,
        /// so the Y is flipped and the height subtracted.
        /// </summary>
        public static Rect GuiEyeRect(Camera camera, int screenWidth, int screenHeight)
        {
            if (camera == null)
            {
                return new Rect(0f, 0f, screenWidth, screenHeight);
            }

            return GuiRectFromViewport(camera.rect, screenWidth, screenHeight);
        }

        /// <summary>
        /// Converts a camera viewport rectangle (bottom-left origin, fractions
        /// of the screen) into the IMGUI rectangle for a screen of the given
        /// size: <c>(x, (1 - y - height), width, height) * screen</c>.
        /// </summary>
        public static Rect GuiRectFromViewport(Rect viewport, int screenWidth, int screenHeight)
        {
            float width = viewport.width * screenWidth;
            float height = viewport.height * screenHeight;
            return new Rect(
                viewport.x * screenWidth,
                screenHeight - ((viewport.y * screenHeight) + height),
                width,
                height);
        }

        /// <summary>
        /// The per-eye IMGUI rectangles for a left and right camera: two
        /// non-overlapping halves of a 3840×1080 side-by-side target under the
        /// committed rig layout, in left-then-right order.
        /// </summary>
        public static Rect[] GuiEyeRects(Camera left, Camera right, int screenWidth, int screenHeight)
        {
            return new[]
            {
                GuiEyeRect(left, screenWidth, screenHeight),
                GuiEyeRect(right, screenWidth, screenHeight),
            };
        }

        private StereoRig ResolveRig()
        {
            if (!rigResolved)
            {
                rigResolved = true;
                resolvedRig = rig != null
                    ? rig
                    : FindFirstObjectByType<StereoRig>(FindObjectsInactive.Include);
            }

            return resolvedRig;
        }

        private void OnDestroy()
        {
            if (gradient != null)
            {
                Destroy(gradient);
                gradient = null;
            }
        }

        private void EnsureGradient()
        {
            if (gradient != null)
            {
                return;
            }

            var texture = new Texture2D(TextureSize, TextureSize, TextureFormat.RGBA32, false);
            texture.name = "CubeglassMotionVignette";
            texture.hideFlags = HideFlags.HideAndDontSave;
            var pixels = new Color32[TextureSize * TextureSize];
            float half = TextureSize * 0.5f;
            float band = half * 0.8f;
            for (int y = 0; y < TextureSize; y++)
            {
                for (int x = 0; x < TextureSize; x++)
                {
                    float edge = Mathf.Min(Mathf.Min(x, TextureSize - 1 - x), Mathf.Min(y, TextureSize - 1 - y));
                    float falloff = Mathf.Clamp01(edge / band);
                    float alpha = 1f - falloff;
                    alpha *= alpha;
                    pixels[(y * TextureSize) + x] = new Color32(0, 0, 0, (byte)(alpha * 255f));
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            gradient = texture;
        }
    }
}

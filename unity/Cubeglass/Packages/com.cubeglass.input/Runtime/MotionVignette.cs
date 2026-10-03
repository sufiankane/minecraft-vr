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
    /// <see cref="GameplayBridge"/> writes <see cref="Speed"/> from the
    /// player's planar velocity every tick. The gradient texture is generated
    /// once and reused, so steady frames do not allocate.
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class MotionVignette : MonoBehaviour
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

        private Texture2D gradient;

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
            Color previous = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, opacity);
            GUI.DrawTexture(
                new Rect(0f, 0f, Screen.width, Screen.height),
                gradient,
                ScaleMode.StretchToFill);
            GUI.color = previous;
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

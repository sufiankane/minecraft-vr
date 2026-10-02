using UnityEngine;

namespace Cubeglass.Unity.Input
{
    /// <summary>
    /// Drives the calibration scene's <see cref="SyntheticPoseProvider"/> from
    /// the legacy Unity input mapping, so the scene is steerable on a machine
    /// with no live pose source: the move axes (WASD / arrows) turn the head,
    /// Q/E snap-turn, and R recentres to the internal forward pose.
    /// </summary>
    /// <remarks>
    /// Angles are integrated here and written to the provider's base pose each
    /// frame, so releasing the keys holds the current view instead of snapping
    /// back to the script's base. Signs follow the S1 conversion: internal yaw
    /// maps to Unity <c>-yaw</c> (positive internal yaw looks left) and internal
    /// positive pitch looks up, so <c>D</c> (move x +1) decreases internal yaw
    /// and <c>W</c> (move y +1) increases internal pitch.
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class SyntheticPoseDrive : MonoBehaviour
    {
        [SerializeField] private UnityInputProvider input;
        [SerializeField] private SyntheticPoseProvider provider;
        [SerializeField] private float yawDegreesPerSecond = 45f;
        [SerializeField] private float pitchDegreesPerSecond = 30f;
        [SerializeField] private float maxPitchDegrees = 80f;

        private float yaw;
        private float pitch;

        /// <summary>The input mapping polled each frame; defaults to the sibling.</summary>
        public UnityInputProvider Input
        {
            get { return input; }
            set { input = value; }
        }

        /// <summary>The provider driven each frame; defaults to the sibling.</summary>
        public SyntheticPoseProvider Provider
        {
            get { return provider; }
            set { provider = value; }
        }

        /// <summary>Accumulated internal yaw in degrees.</summary>
        public float YawDegrees
        {
            get { return yaw; }
        }

        /// <summary>Accumulated internal pitch in degrees.</summary>
        public float PitchDegrees
        {
            get { return pitch; }
        }

        private void Awake()
        {
            if (input == null)
            {
                input = GetComponent<UnityInputProvider>();
            }

            if (provider == null)
            {
                provider = GetComponent<SyntheticPoseProvider>();
            }
        }

        private void Update()
        {
            if (input == null || provider == null)
            {
                return;
            }

            UnityMoveInput frame = input.Latest;
            if (frame.RecenterPressed)
            {
                yaw = 0f;
                pitch = 0f;
                provider.Recenter();
                return;
            }

            float deltaTime = Time.unscaledDeltaTime;
            yaw = WrapDegrees(yaw - (frame.Move.x * yawDegreesPerSecond * deltaTime) - frame.TurnSnap);
            pitch = Mathf.Clamp(
                pitch + (frame.Move.y * pitchDegreesPerSecond * deltaTime),
                -maxPitchDegrees,
                maxPitchDegrees);

            provider.SetOrientation(yaw, pitch, 0f);
            provider.SetRates(0f, 0f, 0f);
        }

        private static float WrapDegrees(float degrees)
        {
            return Mathf.Repeat(degrees + 180f, 360f) - 180f;
        }
    }
}

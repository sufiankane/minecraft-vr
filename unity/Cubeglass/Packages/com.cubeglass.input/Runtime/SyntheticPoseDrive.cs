using Cubeglass.Gameplay;
using UnityEngine;

namespace Cubeglass.Unity.Input
{
    /// <summary>
    /// Drives the calibration scene's <see cref="SyntheticPoseProvider"/> from
    /// the legacy Unity input mapping, so the scene is steerable on a machine
    /// with no live pose source: the move axes (WASD / arrows) turn the head,
    /// F / Shift+F snap-turn like the game scene's
    /// <see cref="ISnapInputSource"/> path, and R recentres to the internal
    /// forward pose.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Angles are integrated here and written to the provider's base pose each
    /// frame, so releasing the keys holds the current view instead of snapping
    /// back to the script's base. Signs follow the S1 conversion: internal yaw
    /// maps to Unity <c>-yaw</c> (positive internal yaw looks left) and internal
    /// positive pitch looks up, so <c>D</c> (move x +1) decreases internal yaw
    /// and <c>W</c> (move y +1) increases internal pitch.
    /// </para>
    /// <para>
    /// <b>Units.</b> <see cref="InputFrame.TurnSnap"/> is the S4 degrees-per-second
    /// rate, so it is multiplied by the frame delta exactly like the pitch/move
    /// rates; without the <c>* dt</c> the steering was ~60–90× too sensitive and
    /// framerate-dependent. The discrete snap edge is consumed once per frame
    /// through <see cref="ISnapInputSource"/> (as <see cref="GameplayBridge"/>
    /// does) and applied as a fixed <see cref="SnapIncrementDegrees"/> step, so
    /// hold-repeat cannot double-apply it.
    /// </para>
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class SyntheticPoseDrive : MonoBehaviour
    {
        [SerializeField] private MonoBehaviour input;
        [SerializeField] private SyntheticPoseProvider provider;
        [SerializeField] private float yawDegreesPerSecond = 45f;
        [SerializeField] private float pitchDegreesPerSecond = 30f;
        [SerializeField] private float maxPitchDegrees = 80f;
        [SerializeField] private float snapIncrementDegrees = 45f;

        private float yaw;
        private float pitch;

        /// <summary>
        /// The input source sampled each frame; must implement
        /// <see cref="IInputProvider"/>, and implements
        /// <see cref="ISnapInputSource"/> when snap turn is wanted. Defaults to
        /// the sibling <see cref="UnityInputProvider"/>.
        /// </summary>
        public MonoBehaviour Input
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

        /// <summary>The fixed snap-turn step applied per consumed edge, in internal degrees.</summary>
        public float SnapIncrementDegrees
        {
            get { return snapIncrementDegrees; }
            set { snapIncrementDegrees = value; }
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
            AdvanceFromSource(Time.unscaledDeltaTime);
        }

        /// <summary>
        /// Samples <see cref="Input"/> (which refreshes the device poll),
        /// consumes its one-shot snap edge through
        /// <see cref="ISnapInputSource.ConsumeSnapDirection"/> and advances the
        /// pose. Public so EditMode tests can drive a fake source without a
        /// device.
        /// </summary>
        public void AdvanceFromSource(float deltaTime)
        {
            if (!(input is IInputProvider source) || provider == null)
            {
                return;
            }

            InputFrame frame = source.Sample(Time.timeAsDouble);
            int snapDirection = input is ISnapInputSource snapSource
                ? snapSource.ConsumeSnapDirection()
                : 0;
            Advance(frame, snapDirection, deltaTime);
        }

        /// <summary>
        /// Integrates one frame: move axes and <see cref="InputFrame.TurnSnap"/>
        /// are degrees-per-second rates scaled by <paramref name="deltaTime"/>,
        /// while <paramref name="snapDirection"/> (-1 left / +1 right) is a
        /// discrete step. A recentre frame zeroes the accumulated orientation
        /// and recentres the provider.
        /// </summary>
        public void Advance(InputFrame frame, int snapDirection, float deltaTime)
        {
            if (provider == null)
            {
                return;
            }

            if (frame.RecenterPressed)
            {
                yaw = 0f;
                pitch = 0f;
                provider.Recenter();
                return;
            }

            float dt = SanitizeDelta(deltaTime);
            int snap = snapDirection > 0 ? 1 : (snapDirection < 0 ? -1 : 0);
            yaw = WrapDegrees(
                yaw
                    - (frame.Move.X * yawDegreesPerSecond * dt)
                    - (frame.TurnSnap * dt)
                    - (snap * snapIncrementDegrees));
            pitch = Mathf.Clamp(
                pitch + (frame.Move.Y * pitchDegreesPerSecond * dt),
                -maxPitchDegrees,
                maxPitchDegrees);

            provider.SetOrientation(yaw, pitch, 0f);
            provider.SetRates(0f, 0f, 0f);
        }

        private static float SanitizeDelta(float deltaTime)
        {
            if (float.IsNaN(deltaTime) || float.IsInfinity(deltaTime) || deltaTime < 0f)
            {
                return 0f;
            }

            return deltaTime;
        }

        private static float WrapDegrees(float degrees)
        {
            return Mathf.Repeat(degrees + 180f, 360f) - 180f;
        }
    }
}

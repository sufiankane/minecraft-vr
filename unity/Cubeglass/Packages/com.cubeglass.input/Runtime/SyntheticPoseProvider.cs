using System;
using Cubeglass.Unity.Bridge;
using Cubeglass.Unity.Rendering;
using UnityEngine;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace Cubeglass.Unity.Input
{
    /// <summary>
    /// Deterministic scripted <see cref="IPoseProvider"/> used by the PlayMode
    /// tests and the calibration scene: every <see cref="TryGetLatest"/> call
    /// emits the next sample of a scripted trajectory, so the sequence is a pure
    /// function of the call index (no wall-clock dependency, no allocation).
    /// </summary>
    /// <remarks>
    /// The script is a base pose (yaw/pitch/roll degrees and a position in the
    /// internal, bridge-space frame) plus a constant per-axis angular rate in
    /// degrees per second. Sample <c>i</c> is taken at
    /// <c>t = i / <see cref="SetSampleRateHz"/></c> seconds after the last
    /// <see cref="ResetSamples"/> and advanced by <c>rate * t</c>; yaw is
    /// wrapped to <c>[-180, 180)</c>. <c>HostTime</c> is a monotonic clock in
    /// nanoseconds rebased on reset, so the overlay's pose rate and age stay
    /// meaningful without the native bridge. The emitted pose stays in internal
    /// coordinates; conversion is still the single
    /// <c>Cubeglass.CoreMath.UnityConvert</c> call inside
    /// <see cref="LateLatchPose"/>.
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class SyntheticPoseProvider : MonoBehaviour, IPoseProvider
    {
        /// <summary>Default deterministic sample rate in Hz.</summary>
        public const float DefaultSampleRateHz = 90f;

        private const double Pi = Math.PI;
        private const double NanosecondsPerSecond = 1_000_000_000.0;

        [Header("Scripted internal pose (degrees / metres)")]
        [SerializeField] private float yawDegrees;
        [SerializeField] private float pitchDegrees;
        [SerializeField] private float rollDegrees;
        [SerializeField] private Vector3 positionMeters;

        [Header("Scripted rates (degrees per second)")]
        [SerializeField] private float yawRateDegreesPerSecond;
        [SerializeField] private float pitchRateDegreesPerSecond;
        [SerializeField] private float rollRateDegreesPerSecond;

        [Header("Sampling")]
        [SerializeField] private float sampleRateHz = DefaultSampleRateHz;
        [SerializeField] private TrackState trackingState = TrackState.Stable;

        private int sampleIndex;
        private long hostTimeBaseNs;

        /// <summary>Total <see cref="TryGetLatest"/> calls since the last reset.</summary>
        public int CallCount { get; private set; }

        /// <summary>Samples emitted since the last reset (the next call's index).</summary>
        public int SampleCount
        {
            get { return sampleIndex; }
        }

        /// <summary>The most recently emitted sample.</summary>
        public BridgeHeadSample CurrentSample { get; private set; }

        /// <summary>Internal yaw of the most recently emitted sample, wrapped to [-180, 180).</summary>
        public float CurrentYawDegrees { get; private set; }

        /// <summary>Internal pitch of the most recently emitted sample, in degrees.</summary>
        public float CurrentPitchDegrees { get; private set; }

        /// <summary>Internal roll of the most recently emitted sample, in degrees.</summary>
        public float CurrentRollDegrees { get; private set; }

        /// <summary>The tracking state stamped on every emitted sample.</summary>
        public TrackState TrackingState
        {
            get { return trackingState; }
        }

        /// <summary>The deterministic step between samples in seconds.</summary>
        public float StepSeconds
        {
            get { return 1f / sampleRateHz; }
        }

        private void OnEnable()
        {
            ResetSamples();
        }

        /// <summary>
        /// Sets the static script in internal coordinates: yaw/pitch/roll in
        /// degrees and a position in metres.
        /// </summary>
        public void SetPose(float yaw, float pitch, float roll, Vector3 position)
        {
            yawDegrees = yaw;
            pitchDegrees = pitch;
            rollDegrees = roll;
            positionMeters = position;
        }

        /// <summary>Sets the base internal yaw in degrees (keeps pitch/roll/position).</summary>
        public void SetYaw(float degrees)
        {
            yawDegrees = degrees;
        }

        /// <summary>Sets the constant per-axis angular rate in degrees per second.</summary>
        public void SetRates(float yawPerSecond, float pitchPerSecond, float rollPerSecond)
        {
            yawRateDegreesPerSecond = yawPerSecond;
            pitchRateDegreesPerSecond = pitchPerSecond;
            rollRateDegreesPerSecond = rollPerSecond;
        }

        /// <summary>Sets the tracking state stamped on every emitted sample.</summary>
        public void SetTrackingState(TrackState state)
        {
            trackingState = state;
        }

        /// <summary>Sets the deterministic sample rate; clamped to at least 1 Hz.</summary>
        public void SetSampleRateHz(float hertz)
        {
            sampleRateHz = Mathf.Max(1f, hertz);
        }

        /// <summary>
        /// Restarts the deterministic sequence at index 0 and rebases
        /// <c>HostTime</c> on the monotonic clock. Called from <c>OnEnable</c>.
        /// </summary>
        public void ResetSamples()
        {
            sampleIndex = 0;
            CallCount = 0;
            hostTimeBaseNs = NowNanoseconds();
        }

        /// <summary>
        /// Emits the next scripted sample. Always returns true (like the native
        /// bridge for a stale sample): the scripted <see cref="TrackState"/> is
        /// carried in the sample, not signalled by the return value.
        /// </summary>
        public bool TryGetLatest(out BridgeHeadSample sample)
        {
            double seconds = sampleIndex / (double)sampleRateHz;
            CurrentYawDegrees = WrapDegrees(yawDegrees + (float)(yawRateDegreesPerSecond * seconds));
            CurrentPitchDegrees = pitchDegrees + (float)(pitchRateDegreesPerSecond * seconds);
            CurrentRollDegrees = rollDegrees + (float)(rollRateDegreesPerSecond * seconds);

            CurrentSample = new BridgeHeadSample
            {
                HostTime = hostTimeBaseNs + (long)(seconds * NanosecondsPerSecond),
                Pose = new BridgePose
                {
                    Position = new BridgeVec3
                    {
                        X = positionMeters.x,
                        Y = positionMeters.y,
                        Z = positionMeters.z,
                    },
                    Rotation = InternalEuler(CurrentYawDegrees, CurrentPitchDegrees, CurrentRollDegrees),
                },
                State = trackingState,
                Sequence = (uint)(sampleIndex + 1),
            };

            sampleIndex++;
            CallCount++;
            sample = CurrentSample;
            return true;
        }

        private static float WrapDegrees(float degrees)
        {
            return Mathf.Repeat(degrees + 180f, 360f) - 180f;
        }

        /// <summary>
        /// Internal-frame quaternion for yaw (about +Y), then pitch (about +X),
        /// then roll (about +Z), in degrees.
        /// </summary>
        private static BridgeQuat InternalEuler(float yaw, float pitch, float roll)
        {
            double halfYaw = yaw * (Pi / 360.0);
            double halfPitch = pitch * (Pi / 360.0);
            double halfRoll = roll * (Pi / 360.0);

            double cy = Math.Cos(halfYaw);
            double sy = Math.Sin(halfYaw);
            double cp = Math.Cos(halfPitch);
            double sp = Math.Sin(halfPitch);
            double cr = Math.Cos(halfRoll);
            double sr = Math.Sin(halfRoll);

            // q = qYaw * qPitch * qRoll, expanded component-wise.
            return new BridgeQuat
            {
                W = (float)((cy * cp * cr) + (sy * sp * sr)),
                X = (float)((cy * sp * cr) + (sy * cp * sr)),
                Y = (float)((sy * cp * cr) - (cy * sp * sr)),
                Z = (float)((cy * cp * sr) - (sy * sp * cr)),
            };
        }

        private static long NowNanoseconds()
        {
            return (long)(Stopwatch.GetTimestamp() * (NanosecondsPerSecond / Stopwatch.Frequency));
        }
    }
}

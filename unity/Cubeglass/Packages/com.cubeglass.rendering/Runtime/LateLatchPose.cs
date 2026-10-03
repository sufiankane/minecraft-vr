using Cubeglass.CoreMath;
using Cubeglass.Unity.Bridge;
using UnityEngine;
using CorePose = Cubeglass.CoreMath.Pose;

namespace Cubeglass.Unity.Rendering
{
    /// <summary>Tracking quality reported by the late latch.</summary>
    public enum PoseTrackingState
    {
        /// <summary>No sample has ever been applied.</summary>
        NotReady = 0,

        /// <summary>The latest sample is stable.</summary>
        Stable = 1,

        /// <summary>The latest sample is usable but degraded.</summary>
        Unstable = 2,

        /// <summary>The latest sample is stale (250 ms heartbeat rule).</summary>
        Lost = 3,
    }

    /// <summary>
    /// Late-latch pose application: reads the newest head sample once per
    /// rendered frame from an <see cref="IPoseProvider"/> (the bridge adapter or
    /// the scripted input provider), reduces it to the head-relative rotation
    /// (recentred yaw/pitch/roll, no absolute position), converts that through
    /// the single conversion source <see cref="Cubeglass.CoreMath.UnityConvert"/>,
    /// and applies it to the rig transform so both eye cameras render the same
    /// sample.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Hierarchy (S7 Task 4a, ADR-0011): this component is the head level, a
    /// child of <see cref="PlayerRoot"/>; it writes only
    /// <c>target.localRotation</c>. The root owns the world position and the
    /// body yaw, and the root places the head at the fixed eye height, so a
    /// sample's absolute position is deliberately ignored and the shoot ray
    /// cannot be dragged around by head translation.
    /// </para>
    /// <para>
    /// Rotation is relative to the recentre baseline. Initially the baseline is
    /// identity, so the first sample is applied as-is; <see cref="Recentre"/>
    /// clears the head offset so the view returns to the player's forward.
    /// When the provider implements <see cref="IRecenterablePoseProvider"/> the
    /// source is recentred too and the baseline resets to identity; otherwise
    /// the current sample rotation becomes the baseline (a local offset reset)
    /// and the emitted pose is immediately identity.
    /// </para>
    /// <para>
    /// The hook is <c>Camera.onPreCull</c>, subscribed once per enable and
    /// removed on disable/destroy. The callback deduplicates by
    /// <c>Time.frameCount</c>, so the left and right camera callbacks of one
    /// frame produce exactly one provider read and the same pose for both eyes.
    /// When the provider has no sample, the last pose and
    /// <see cref="TrackingState"/> are kept unchanged. <see cref="TickOnce"/>
    /// is the manual entry point used by tests.
    /// </para>
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class LateLatchPose : MonoBehaviour
    {
        [SerializeField] private StereoRig rig;

        private IPoseProvider provider;
        private bool subscribed;
        private bool hasSample;
        private int lastTickFrame = int.MinValue;
        private BridgeHeadSample lastSample;
        private Quat recentreBaseline = Quat.Identity;
        private Quat lastRotation = Quat.Identity;

        /// <summary>Tracking quality of the latest applied sample.</summary>
        public PoseTrackingState TrackingState { get; private set; } = PoseTrackingState.NotReady;

        /// <summary>True after at least one sample has been applied.</summary>
        public bool HasSample
        {
            get { return hasSample; }
        }

        /// <summary>
        /// The most recently applied sample; a copy of it, valid when
        /// <see cref="HasSample"/> is true (the debug overlay reads its
        /// <c>HostTime</c> for the pose rate/age without consuming a new sample).
        /// </summary>
        public BridgeHeadSample LastSample
        {
            get { return lastSample; }
        }

        /// <summary>True while the <c>Camera.onPreCull</c> hook is subscribed.</summary>
        public bool IsSubscribed
        {
            get { return subscribed; }
        }

        /// <summary>The rig whose transform receives the pose; defaults to the sibling rig.</summary>
        public StereoRig Rig
        {
            get { return rig; }
            set { rig = value; }
        }

        /// <summary>The sample source; null keeps the last pose (bridge absent).</summary>
        public IPoseProvider Provider
        {
            get { return provider; }
            set { provider = value; }
        }

        private void Awake()
        {
            if (rig == null)
            {
                rig = GetComponent<StereoRig>();
            }
        }

        private void OnEnable()
        {
            Subscribe();
        }

        private void OnDisable()
        {
            Unsubscribe();
        }

        private void OnDestroy()
        {
            Unsubscribe();
        }

        /// <summary>Subscribes <c>Camera.onPreCull</c>; idempotent.</summary>
        public void Subscribe()
        {
            if (subscribed)
            {
                return;
            }

            Camera.onPreCull += HandlePreCull;
            subscribed = true;
        }

        /// <summary>Removes the <c>Camera.onPreCull</c> hook; idempotent.</summary>
        public void Unsubscribe()
        {
            if (!subscribed)
            {
                return;
            }

            Camera.onPreCull -= HandlePreCull;
            subscribed = false;
        }

        /// <summary>
        /// Reads one sample and applies it. Called once per frame from the
        /// pre-cull hook and directly by tests. Stamps the current frame, so a
        /// manual tick and a render in the same frame still read the provider
        /// exactly once.
        /// </summary>
        public void TickOnce()
        {
            lastTickFrame = Time.frameCount;

            if (provider == null || !provider.TryGetLatest(out BridgeHeadSample sample))
            {
                return;
            }

            hasSample = true;
            lastSample = sample;
            TrackingState = MapState(sample.State);
            ApplySample(in sample);
        }

        /// <summary>
        /// Clears the head offset (S7 Task 4a, ADR-0011): the rig returns to
        /// the player's forward immediately and the next sample is measured
        /// relative to the recentred baseline. When the provider implements
        /// <see cref="IRecenterablePoseProvider"/> its origin is recentred as
        /// well (the source recentre path); otherwise the current sample
        /// rotation becomes the local baseline (the offset reset path).
        /// </summary>
        public void Recentre()
        {
            if (provider is IRecenterablePoseProvider recenterable)
            {
                recenterable.Recentre();
                recentreBaseline = Quat.Identity;
            }
            else if (hasSample)
            {
                recentreBaseline = lastRotation;
            }
            else
            {
                recentreBaseline = Quat.Identity;
            }

            ApplyRotation(Quat.Identity);
        }

        private void HandlePreCull(Camera camera)
        {
            if (rig != null)
            {
                if (camera != rig.LeftCamera && camera != rig.RightCamera)
                {
                    return;
                }
            }
            else if (camera.cameraType != CameraType.Game)
            {
                return;
            }

            int frame = Time.frameCount;
            if (frame == lastTickFrame)
            {
                return;
            }

            lastTickFrame = frame;
            TickOnce();
        }

        private void ApplySample(in BridgeHeadSample sample)
        {
            Quat sampleRotation = Quat.FromComponents(
                sample.Pose.Rotation.W,
                sample.Pose.Rotation.X,
                sample.Pose.Rotation.Y,
                sample.Pose.Rotation.Z);
            lastRotation = sampleRotation;

            // Relative to the recentre baseline, in the internal frame, then
            // converted once. A sample position is intentionally not applied.
            Quat relative = recentreBaseline.Inverse() * sampleRotation;
            ApplyRotation(relative);

            if (rig != null)
            {
                rig.ApplyEyeLayout();
            }
        }

        private void ApplyRotation(Quat internalRotation)
        {
            CorePose unityPose = UnityConvert.ToUnity(new CorePose(default, internalRotation));
            Transform target = rig != null ? rig.transform : transform;
            target.localRotation = new Quaternion(
                (float)unityPose.Rotation.X,
                (float)unityPose.Rotation.Y,
                (float)unityPose.Rotation.Z,
                (float)unityPose.Rotation.W);
        }

        private static PoseTrackingState MapState(TrackState state)
        {
            switch (state)
            {
                case TrackState.Stable:
                    return PoseTrackingState.Stable;
                case TrackState.Unstable:
                    return PoseTrackingState.Unstable;
                case TrackState.Lost:
                    return PoseTrackingState.Lost;
                default:
                    return PoseTrackingState.NotReady;
            }
        }
    }
}

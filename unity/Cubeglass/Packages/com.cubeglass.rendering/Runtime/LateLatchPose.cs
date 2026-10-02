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
    /// the scripted input provider), converts it through the single conversion
    /// source <see cref="Cubeglass.CoreMath.UnityConvert"/>, and applies it to
    /// the rig transform so both eye cameras render the same sample.
    /// </summary>
    /// <remarks>
    /// The hook is <c>Camera.onPreCull</c>, subscribed once per enable and
    /// removed on disable/destroy. The callback deduplicates by
    /// <c>Time.frameCount</c>, so the left and right camera callbacks of one
    /// frame produce exactly one provider read and the same pose for both eyes.
    /// When the provider has no sample, the last pose and
    /// <see cref="TrackingState"/> are kept unchanged. <see cref="TickOnce"/>
    /// is the manual entry point used by tests.
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class LateLatchPose : MonoBehaviour
    {
        [SerializeField] private StereoRig rig;

        private IPoseProvider provider;
        private bool subscribed;
        private bool hasSample;
        private int lastTickFrame = int.MinValue;

        /// <summary>Tracking quality of the latest applied sample.</summary>
        public PoseTrackingState TrackingState { get; private set; } = PoseTrackingState.NotReady;

        /// <summary>True after at least one sample has been applied.</summary>
        public bool HasSample
        {
            get { return hasSample; }
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
        /// pre-cull hook and directly by tests.
        /// </summary>
        public void TickOnce()
        {
            if (provider == null || !provider.TryGetLatest(out BridgeHeadSample sample))
            {
                return;
            }

            hasSample = true;
            TrackingState = MapState(sample.State);
            ApplySample(in sample);
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
            var internalPose = new CorePose(
                new Vec3(sample.Pose.Position.X, sample.Pose.Position.Y, sample.Pose.Position.Z),
                Quat.FromComponents(
                    sample.Pose.Rotation.W,
                    sample.Pose.Rotation.X,
                    sample.Pose.Rotation.Y,
                    sample.Pose.Rotation.Z));
            CorePose unityPose = UnityConvert.ToUnity(internalPose);

            Transform target = rig != null ? rig.transform : transform;
            target.localPosition = new Vector3(
                (float)unityPose.Position.X,
                (float)unityPose.Position.Y,
                (float)unityPose.Position.Z);
            target.localRotation = new Quaternion(
                (float)unityPose.Rotation.X,
                (float)unityPose.Rotation.Y,
                (float)unityPose.Rotation.Z,
                (float)unityPose.Rotation.W);

            if (rig != null)
            {
                rig.ApplyEyeLayout();
            }
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

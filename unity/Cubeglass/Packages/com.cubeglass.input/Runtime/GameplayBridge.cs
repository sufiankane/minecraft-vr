using System;
using Cubeglass.CoreMath;
using Cubeglass.Gameplay;
using Cubeglass.Unity.Bridge;
using Cubeglass.Unity.Rendering;
using Cubeglass.Voxel;
using UnityEngine;

namespace Cubeglass.Unity.Input
{
    /// <summary>
    /// Wires the S4 gameplay module into the live Unity scene (S7 Task 3):
    /// every <see cref="Tick"/> builds an <see cref="InputFrame"/> from the
    /// input provider plus gaze targeting and pose-derived tracking quality,
    /// advances the <see cref="PlayerState"/> with
    /// <see cref="PlayerController.Step"/>, and runs
    /// <see cref="IInteractionService.Update"/> with a real
    /// <see cref="DdaRaycaster"/> against the streamed <see cref="World"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Update order (one tick).</b>
    /// <list type="number">
    /// <item><description><b>Input</b>: sample the provider, add the gaze
    /// pointer when the provider supplies none, add tracking quality and route
    /// a snap-turn edge to <see cref="SnapTurn"/> (clearing
    /// <see cref="InputFrame.TurnSnap"/> so the discrete increment is not also
    /// scaled by <c>dt</c> in the controller).</description></item>
    /// <item><description><b>Controller</b>: sync the player yaw from the rig
    /// (gaze-relative movement) and step gravity/collision against the
    /// world.</description></item>
    /// <item><description><b>Interaction</b>: target (pointer first, view-ray
    /// fallback), hold-to-break, edge-place, hotbar and recentre; world edits
    /// reach <see cref="World.Apply"/> through the service.</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// The bridge also forwards editing stats (<see cref="HotbarIndex"/>,
    /// <see cref="BreakProgress"/>, <see cref="TrackingState"/>) and the
    /// planar speed for the overlay/vignette, and raises
    /// <see cref="SaveRequested"/> once an edit lands so S7 Task 4 can persist
    /// the world.
    /// </para>
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class GameplayBridge : MonoBehaviour
    {
        [SerializeField] private StreamingRuntime streaming;
        [SerializeField] private UnityInputProvider inputProvider;
        [SerializeField] private LateLatchPose lateLatch;
        [SerializeField] private StereoRig rig;
        [SerializeField] private Camera gazeCamera;
        [SerializeField] private SnapTurn snapTurn;
        [SerializeField] private MotionVignette motionVignette;
        [SerializeField] private bool autoUpdate = true;

        private World world;
        private InteractionService interaction;
        private PlayerState player;
        private IInputProvider inputSource;
        private IPoseProvider poseSource;
        private bool initialized;

        /// <summary>Raised by tests/scene builders when the world is not ready yet.</summary>
        public event Action Initialized;

        /// <summary>The streamed world (set through the runtime's view manager).</summary>
        public World World
        {
            get { return world; }
        }

        /// <summary>The interaction service driving targeting, breaks and places.</summary>
        public IInteractionService InteractionService
        {
            get { return interaction; }
        }

        /// <summary>The player state advanced each tick.</summary>
        public PlayerState Player
        {
            get { return player; }
        }

        /// <summary>True once the world and services are resolved.</summary>
        public bool IsInitialized
        {
            get { return initialized; }
        }

        /// <summary>
        /// The provider sampled each tick; defaults to the serialized
        /// <see cref="UnityInputProvider"/>.
        /// </summary>
        public IInputProvider InputSource
        {
            get { return inputSource != null ? inputSource : inputProvider; }
            set { inputSource = value; }
        }

        /// <summary>
        /// Tracking-quality source used only when no <see cref="LateLatchPose"/>
        /// is wired (tests inject a scripted provider here).
        /// </summary>
        public IPoseProvider PoseSource
        {
            get { return poseSource; }
            set { poseSource = value; }
        }

        /// <summary>The streaming runtime owning the world.</summary>
        public StreamingRuntime Streaming
        {
            get { return streaming; }
            set { streaming = value; }
        }

        /// <summary>The stereo rig used for gaze targeting and yaw sync.</summary>
        public StereoRig Rig
        {
            get { return rig; }
            set { rig = value; }
        }

        /// <summary>Single-camera gaze fallback when no rig is wired.</summary>
        public Camera GazeCamera
        {
            get { return gazeCamera; }
            set { gazeCamera = value; }
        }

        /// <summary>The comfort snap-turn component receiving edge increments.</summary>
        public SnapTurn SnapTurn
        {
            get { return snapTurn; }
            set { snapTurn = value; }
        }

        /// <summary>The comfort vignette receiving the planar speed.</summary>
        public MotionVignette MotionVignette
        {
            get { return motionVignette; }
            set { motionVignette = value; }
        }

        /// <summary>Whether <see cref="Update"/> ticks automatically.</summary>
        public bool AutoUpdate
        {
            get { return autoUpdate; }
            set { autoUpdate = value; }
        }

        /// <summary>The late latch supplying tracking quality in the scene.</summary>
        public LateLatchPose LateLatch
        {
            get { return lateLatch; }
            set { lateLatch = value; }
        }

        /// <summary>The selected hotbar slot after the latest tick.</summary>
        public int HotbarIndex
        {
            get { return player != null ? player.HotbarIndex : 0; }
        }

        /// <summary>Break completion in [0, 1]; zero when no break is in progress.</summary>
        public float BreakProgress { get; private set; }

        /// <summary>The tracking state forwarded to the overlay.</summary>
        public PoseTrackingState TrackingState { get; private set; }

        /// <summary>The tracking quality consumed by the interaction service.</summary>
        public TrackingQuality Tracking { get; private set; }

        /// <summary>True while the latest pose is past the tracking-loss window.</summary>
        public bool TrackingLost
        {
            get { return interaction != null && interaction.TrackingLost; }
        }

        /// <summary>The planar speed forwarded to the vignette, in m/s.</summary>
        public float PlanarSpeed { get; private set; }

        /// <summary>The last snap-turn increment routed to <see cref="SnapTurn"/>.</summary>
        public float LastSnapDegrees { get; private set; }

        /// <summary>Number of world edits applied since scene start.</summary>
        public int EditsApplied { get; private set; }

        /// <summary>Set when an edit lands; S7 Task 4 consumes and clears it.</summary>
        public bool SaveRequested { get; set; }

        private void Update()
        {
            if (autoUpdate)
            {
                Tick(Time.deltaTime);
            }
        }

        /// <summary>
        /// Resolves the world, player and services; returns false when the
        /// streaming runtime has not built its view manager yet.
        /// </summary>
        public bool EnsureInitialized()
        {
            if (initialized)
            {
                return true;
            }

            if (streaming == null || streaming.Views == null)
            {
                return false;
            }

            world = streaming.Views.World;
            if (world == null)
            {
                return false;
            }

            if (player == null)
            {
                player = new PlayerState();
            }

            if (interaction == null)
            {
                interaction = new InteractionService(new DdaRaycaster(), SliceBlockRegistry.Default);
            }

            initialized = true;
            Action handler = Initialized;
            if (handler != null)
            {
                handler();
            }

            return true;
        }

        /// <summary>
        /// Runs one deterministic tick in the documented order: input (with
        /// gaze pointer, quality and snap routing), controller, interaction.
        /// A no-op until <see cref="EnsureInitialized"/> succeeds.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="dt"/> is negative, NaN or infinite.
        /// </exception>
        public void Tick(double dt)
        {
            if (!EnsureInitialized())
            {
                return;
            }

            if (!(dt >= 0.0) || double.IsInfinity(dt))
            {
                throw new ArgumentOutOfRangeException(nameof(dt), dt, "dt must be finite and non-negative.");
            }

            double time = Time.timeAsDouble;
            InputFrame sampled = SampleInput(time);
            Vector2f move = sampled.Move;
            float turn = sampled.TurnSnap;
            bool recenter = sampled.RecenterPressed;
            PointerRay? pointer = sampled.Pointer;
            ButtonState primary = sampled.Primary;
            ButtonState secondary = sampled.Secondary;
            int hotbarDelta = sampled.HotbarDelta;

            if (!pointer.HasValue && TryBuildGaze(out PointerRay gaze))
            {
                pointer = gaze;
            }

            TrackingQuality quality = ResolveQuality();
            Tracking = quality;

            if (turn != 0f)
            {
                if (snapTurn != null)
                {
                    snapTurn.Apply(turn);
                }

                LastSnapDegrees = turn;
                turn = 0f;
            }
            else
            {
                LastSnapDegrees = 0f;
            }

            var frame = new InputFrame(move, turn, recenter, pointer, primary, secondary, hotbarDelta, quality);

            SyncPlayerYawFromRig();
            PlayerController.Step(player, frame, world, dt);

            InteractionResult result = interaction.Update(frame, world, player, dt);
            BreakProgress = result.BreakInProgress ? result.BreakProgress : 0f;
            if (result.Edited)
            {
                EditsApplied++;
                SaveRequested = true;
            }

            PlanarSpeed = (float)Math.Sqrt((player.Velocity.X * player.Velocity.X) + (player.Velocity.Z * player.Velocity.Z));
            if (motionVignette != null)
            {
                motionVignette.Speed = PlanarSpeed;
            }
        }

        private InputFrame SampleInput(double time)
        {
            IInputProvider provider = InputSource;
            return provider != null ? provider.Sample(time) : InputFrame.Neutral;
        }

        private bool TryBuildGaze(out PointerRay ray)
        {
            ray = default;
            if (rig != null && GazeTargeting.TryBuildPointerRay(rig, out ray))
            {
                return true;
            }

            return gazeCamera != null && GazeTargeting.TryBuildPointerRay(gazeCamera, null, out ray);
        }

        private TrackingQuality ResolveQuality()
        {
            if (lateLatch != null)
            {
                TrackingState = lateLatch.TrackingState;
                return MapPoseTrackingState(lateLatch.TrackingState);
            }

            if (poseSource != null && poseSource.TryGetLatest(out BridgeHeadSample sample))
            {
                TrackingState = MapTrackState(sample.State);
                return MapPoseTrackingState(TrackingState);
            }

            TrackingState = PoseTrackingState.Stable;
            return TrackingQuality.Good;
        }

        /// <summary>
        /// The bridge-derived quality when only a pose state is available
        /// (late latch path).
        /// </summary>
        public static PoseTrackingState MapTrackState(TrackState state)
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

        /// <summary>
        /// Maps the rendering tracking state onto the gameplay quality:
        /// stable is good, unstable is degraded, lost and not-ready are none.
        /// </summary>
        public static TrackingQuality MapPoseTrackingState(PoseTrackingState state)
        {
            switch (state)
            {
                case PoseTrackingState.Stable:
                    return TrackingQuality.Good;
                case PoseTrackingState.Unstable:
                    return TrackingQuality.Degraded;
                default:
                    return TrackingQuality.None;
            }
        }

        private void SyncPlayerYawFromRig()
        {
            if (rig == null)
            {
                return;
            }

            // UnityConvert flips the yaw sign (ADR-0004): internal yaw =
            // -Unity yaw, so the movement basis follows where the head looks.
            float unityYaw = rig.transform.eulerAngles.y;
            player.YawRadians = WrapRadians(-unityYaw * Mathf.Deg2Rad);
        }

        private static float WrapRadians(float radians)
        {
            return Mathf.Repeat(radians + Mathf.PI, Mathf.PI * 2f) - Mathf.PI;
        }
    }
}

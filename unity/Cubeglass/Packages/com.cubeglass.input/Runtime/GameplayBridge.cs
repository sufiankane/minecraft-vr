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
    /// pointer when the provider supplies none, add tracking quality, and
    /// consume the additive snap-turn edge (<see cref="ISnapInputSource"/>);
    /// the frame's <see cref="InputFrame.TurnSnap"/> stays in degrees per
    /// second and is integrated by the controller.</description></item>
    /// <item><description><b>Heading</b>: apply the snap increment to
    /// <see cref="PlayerState.YawRadians"/> so the movement basis follows the
    /// turn and the late latch cannot overwrite it (ADR-0011).</description></item>
    /// <item><description><b>Controller</b>: step gravity/collision against the
    /// world; yaw is integrated from the frame's degrees-per-second
    /// rate.</description></item>
    /// <item><description><b>Interaction</b>: target (pointer first, view-ray
    /// fallback), hold-to-break, edge-place, hotbar and recentre; world edits
    /// reach <see cref="World.Apply"/> through the service.</description></item>
    /// <item><description><b>Pose</b>: on the recentre frame clear the head
    /// offset through the late latch, then write <see cref="PlayerRoot"/> from
    /// the player state. The rig follows the player; yaw is never read back
    /// from the rig.</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>Live world.</b> <see cref="ChunkViewManager"/> replaces its world
    /// instance when it compacts (see
    /// <see cref="ChunkViewManager.WorldCompactions"/>), so <see cref="Tick"/>
    /// re-resolves <c>streaming.Views.World</c> by reference before every use
    /// (<see cref="RefreshWorld"/>); a cached reference would send edits to the
    /// detached world, whose <c>ChunkChanged</c> no longer reaches the
    /// manager's remesh subscription. The compare is allocation-free and a
    /// no-op on the steady path.
    /// </para>
    /// <para>
    /// The bridge also forwards editing stats (<see cref="HotbarIndex"/>,
    /// <see cref="BreakProgress"/>, <see cref="TrackingState"/>) and the
    /// planar speed for the overlay/vignette, and raises
    /// <see cref="SaveRequested"/> once an edit lands so S7 Task 4 can persist
    /// the world. Persistence itself listens to <see cref="EditApplied"/>
    /// (exact edited cells) and to the optional <see cref="SaveBatches"/> sink;
    /// the interaction service edits through a thin <c>IWorld</c> observer that
    /// records applied commands, so the pure Gameplay module needs no change.
    /// </para>
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class GameplayBridge : MonoBehaviour
    {
        [SerializeField] private StreamingRuntime streaming;
        [SerializeField] private UnityInputProvider inputProvider;
        [SerializeField] private LateLatchPose lateLatch;
        [SerializeField] private StereoRig rig;
        [SerializeField] private PlayerRoot playerRoot;
        [SerializeField] private Camera gazeCamera;
        [SerializeField] private SnapTurn snapTurn;
        [SerializeField] private MotionVignette motionVignette;
        [SerializeField] private SaveBatches saveBatches;
        [SerializeField] private bool autoUpdate = true;

        private readonly EditObserver editObserver = new EditObserver();

        private World world;
        private InteractionService interaction;
        private PlayerState player;
        private IInputProvider inputSource;
        private IPoseProvider poseSource;
        private bool initialized;

        /// <summary>Raised by tests/scene builders when the world is not ready yet.</summary>
        public event Action Initialized;

        /// <summary>
        /// Raised once per world edit with the edited world cell and its new
        /// block (S7 Task 4b), after the edit landed; persistence listens here
        /// to build chunk deltas.
        /// </summary>
        public event Action<Int3, BlockId> EditApplied;

        /// <summary>
        /// The streamed world; re-resolved from the runtime's view manager
        /// every tick so a manager compaction cannot leave the bridge editing
        /// a detached instance (see <see cref="RefreshWorld"/>).
        /// </summary>
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

        /// <summary>The stereo rig used for gaze targeting.</summary>
        public StereoRig Rig
        {
            get { return rig; }
            set { rig = value; }
        }

        /// <summary>
        /// The player root driven from <see cref="PlayerState"/> each tick
        /// (position and yaw, converted once); the rig is its head child.
        /// </summary>
        public PlayerRoot PlayerRoot
        {
            get { return playerRoot; }
            set { playerRoot = value; }
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

        /// <summary>
        /// Optional batched persistence sink (S7 Task 4b). Every applied edit
        /// is forwarded to <see cref="SaveBatches.TrackEdit"/> in addition to
        /// the <see cref="EditApplied"/> event.
        /// </summary>
        public SaveBatches SaveBatches
        {
            get { return saveBatches; }
            set { saveBatches = value; }
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

        /// <summary>
        /// The last snap-turn increment applied to the heading, in Unity-yaw
        /// degrees (positive is turn right); zero when no edge landed.
        /// </summary>
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

            editObserver.Inner = world;

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
        /// Picks up a world instance replaced by a
        /// <see cref="ChunkViewManager"/> compaction (S7 Task 4a fix round).
        /// Cheap reference compare; keeps the previous world when the runtime
        /// or its manager is not available.
        /// </summary>
        private void RefreshWorld()
        {
            if (streaming == null || streaming.Views == null)
            {
                return;
            }

            World live = streaming.Views.World;
            if (live != null && !ReferenceEquals(live, world))
            {
                world = live;
            }

            editObserver.Inner = world;
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

            RefreshWorld();
            if (world == null)
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

            // Snap turn is an additive provider edge (S7 Task 4a): apply the
            // discrete increment to the heading before Step so movement follows
            // the turn. TurnSnap itself stays a degrees-per-second rate.
            IInputProvider active = InputSource;
            bool snapPressed = active is ISnapInputSource snapSource && snapSource.ConsumeSnapPressed();
            float snapDegrees = snapPressed && snapTurn != null ? snapTurn.ApplyIncrement(1f) : 0f;
            LastSnapDegrees = snapDegrees;
            if (snapDegrees != 0f)
            {
                // Positive snap degrees are Unity +Y (turn right); internal yaw
                // is the negative of Unity yaw (ADR-0004).
                player.YawRadians = WrapRadians(player.YawRadians - (snapDegrees * Mathf.Deg2Rad));
            }

            var frame = new InputFrame(move, turn, recenter, pointer, primary, secondary, hotbarDelta, quality);

            PlayerController.Step(player, frame, world, dt);

            // The interaction service edits through the observer, which
            // records every applied command so persistence sees exact cells
            // without changing the pure Gameplay module.
            editObserver.Reset();
            InteractionResult result = interaction.Update(frame, editObserver, player, dt);
            BreakProgress = result.BreakInProgress ? result.BreakProgress : 0f;
            if (result.Edited)
            {
                EditsApplied++;
                SaveRequested = true;
            }

            DispatchAppliedEdits();

            if (interaction.Recentered && lateLatch != null)
            {
                lateLatch.Recentre();
            }

            ApplyPlayerPose();

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

        private void ApplyPlayerPose()
        {
            if (playerRoot != null)
            {
                playerRoot.SetPlayerPose(player.Position, player.YawRadians);
            }
        }

        private void DispatchAppliedEdits()
        {
            int count = editObserver.RecordedCount;
            for (int i = 0; i < count; i++)
            {
                Int3 cell;
                BlockId block;
                editObserver.GetApplied(i, out cell, out block);
                Action<Int3, BlockId> handler = EditApplied;
                if (handler != null)
                {
                    handler(cell, block);
                }

                if (saveBatches != null)
                {
                    saveBatches.TrackEdit(cell, block);
                }
            }
        }

        private static float WrapRadians(float radians)
        {
            return Mathf.Repeat(radians + Mathf.PI, Mathf.PI * 2f) - Mathf.PI;
        }

        /// <summary>
        /// Forwards <see cref="IWorld"/> reads to the live world and records
        /// every applied <see cref="EditCommand"/> (cell and new block) so the
        /// bridge can raise <see cref="EditApplied"/> without a pure-module
        /// change. One edit per interaction call is the norm; the buffer
        /// covers a break completion and a placement edge in the same tick.
        /// </summary>
        private sealed class EditObserver : IWorld
        {
            private readonly Int3[] cells = new Int3[2];
            private readonly BlockId[] blocks = new BlockId[2];

            public World Inner;

            public int AppliedCount { get; private set; }

            public int RecordedCount
            {
                get { return AppliedCount < cells.Length ? AppliedCount : cells.Length; }
            }

#pragma warning disable CS0067 // The observer forwards reads and Apply only; ChunkChanged stays on the inner world.
            public event Action<ChunkCoord> ChunkChanged;
#pragma warning restore CS0067

            public void Reset()
            {
                AppliedCount = 0;
            }

            public BlockId Get(Int3 cell)
            {
                return Inner.Get(cell);
            }

            public bool IsLoaded(Int3 cell)
            {
                return Inner.IsLoaded(cell);
            }

            public EditResult Apply(in EditCommand cmd)
            {
                EditResult result = Inner.Apply(cmd);
                if (result == EditResult.Applied)
                {
                    if (AppliedCount < cells.Length)
                    {
                        cells[AppliedCount] = cmd.Cell;
                        blocks[AppliedCount] = cmd.New;
                    }

                    AppliedCount++;
                }

                return result;
            }

            public void GetApplied(int index, out Int3 cell, out BlockId block)
            {
                cell = cells[index];
                block = blocks[index];
            }
        }
    }
}

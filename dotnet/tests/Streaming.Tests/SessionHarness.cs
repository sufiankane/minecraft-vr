using System;
using System.Collections.Generic;
using Cubeglass.CoreMath;
using Cubeglass.Gameplay;
using Cubeglass.Mesh;
using Cubeglass.Voxel;

namespace Cubeglass.Streaming.Tests
{
    /// <summary>
    /// The observable outcome of a scripted <see cref="SessionHarness"/> run.
    /// </summary>
    /// <param name="WorldHash">
    /// FNV-1a 64 over every loaded cell in canonical chunk order.
    /// </param>
    /// <param name="ChunksLoaded">Number of load actions the session performed.</param>
    /// <param name="QuadsEmitted">Number of quads the pooled mesher produced.</param>
    /// <param name="EditsApplied">Number of break/place edits the service applied.</param>
    public readonly record struct SessionResult(
        ulong WorldHash,
        int ChunksLoaded,
        int QuadsEmitted,
        int EditsApplied);

    /// <summary>
    /// The deterministic five-minute S7 integration session (plan Task 1b).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The harness drives the pure S7 pipeline end to end: a scripted waypoint
    /// walk (~50 m, <see cref="PlayerController"/> turns and gravity), 40
    /// break/place edits at fixed canonical step indices through
    /// <see cref="InteractionService"/> (20 columns, break then place the same
    /// column with a world-fixed pointer ray), two recentres, chunk loading via
    /// <see cref="ChunkStreamingScheduler"/> into <see cref="World"/> with
    /// <see cref="TerrainGenerator"/>, and meshing of every dirty chunk with a
    /// pooled <see cref="GreedyMesher"/> whose <see cref="MeshData"/> is
    /// released exactly once on upload or unload.
    /// </para>
    /// <para>
    /// The terrain seed and route are pinned so the player can actually walk
    /// the route: the controller has no jump, so every leg is monotone
    /// non-increasing in terrain height with one cell of lateral clearance.
    /// All edit columns lie outside the walk corridor and inside the start
    /// view box, so their chunks are loaded long before the first edit at
    /// canonical step 2700 (45 s). The walk itself waits until step 1500
    /// (25 s), by which time both 60 Hz and 30 Hz runs have fully drained the
    /// start box, so the loaded union is frame-rate independent. Break holds
    /// last 0.8 s: grass (0.6 s) completes, the 0.2 s left on the dirt below
    /// never does. The timeline is defined in canonical 60 Hz step indices and
    /// dispatched by <c>round(step * dt / CanonicalDt)</c>, so a dt of
    /// <c>1/30</c> dispatches the same events at the same simulated times in
    /// half as many steps.
    /// </para>
    /// <para>
    /// The world hash is test-only FNV-1a 64 (offset basis
    /// <c>14695981039346656037</c>, prime <c>1099511628211</c>) over the
    /// resident chunks sorted by ascending <c>(X, Y, Z)</c>; within a chunk,
    /// cells run <c>x</c>, then <c>y</c>, then <c>z</c> ascending, and each
    /// cell folds its local coordinates and block id. Unloaded chunks are
    /// excluded; a session on the pinned timeline never unloads.
    /// </para>
    /// </remarks>
    public sealed class SessionHarness : IStreamingTarget
    {
        /// <summary>The canonical fixed timestep, 1/60 s.</summary>
        public const double CanonicalDt = 1.0 / 60.0;

        /// <summary>Steps in the canonical five-minute session (300 s at 60 Hz).</summary>
        public const int CanonicalSteps = 18_000;

        /// <summary>The seed of the pinned terrain and edit columns.</summary>
        public const long TerrainSeed = 4L;

        /// <summary>Canonical steps a break button is held (0.8 s).</summary>
        public const int BreakHoldSteps = 48;

        /// <summary>Canonical steps from a break start to the place edge (1.6 s).</summary>
        public const int PlaceDelaySteps = 96;

        /// <summary>Canonical steps between edit columns (10 s).</summary>
        public const int BreakSpacingSteps = 600;

        /// <summary>Canonical step of the first break (45 s, after the walk).</summary>
        public const int FirstBreakStep = 2_700;

        /// <summary>Canonical step the walk starts (25 s), after the initial drain.</summary>
        public const int WalkStartStep = 1_500;

        /// <summary>Number of edited columns (one break and one place each).</summary>
        public const int EditColumnCount = 20;

        /// <summary>Total scripted edits: two per column.</summary>
        public const int ExpectedEditCount = EditColumnCount * 2;

        /// <summary>The two recentre steps (90 s and 210 s).</summary>
        private const int FirstRecentreStep = 5_400;

        private const int SecondRecentreStep = 12_600;
        private const double ArrivalRadius = 0.5;
        private const double MaxTurnDegreesPerSecond = 120.0;
        private const double DegreesToRadians = Math.PI / 180.0;
        private const double AlignToleranceRadians = 1e-3;
        private const double StartX = 22.0;
        private const double StartZ = 44.0;
        private const double StartHeightOffset = 1.0;
        private const double PointerEyeOffset = 2.5;
        private const ulong FnvOffsetBasis = 14695981039346656037UL;
        private const ulong FnvPrime = 1099511628211UL;

        private static readonly (double X, double Z)[] WalkWaypoints =
        {
            (22.0, 44.0),
            (38.0, 44.0),
            (38.0, 54.0),
            (50.0, 54.0),
            (50.0, 66.0),
        };

        private static readonly (int X, int Z)[] EditColumns =
        {
            (14, 36), (20, 36), (26, 36), (32, 36), (38, 36), (44, 36), (50, 36), (56, 36),
            (14, 74), (20, 74), (26, 74), (32, 74), (38, 74), (44, 74), (50, 74), (56, 74),
            (64, 44), (64, 50), (64, 56), (64, 62),
        };

        private static readonly Comparison<TimelineEvent> EventOrder = CompareEvents;

        private readonly ChunkStreamingScheduler _scheduler;
        private readonly TerrainGenerator _generator = new TerrainGenerator();
        private readonly World _world;
        private readonly PlayerState _player;
        private readonly InteractionService _interaction;
        private readonly MeshBufferPool _pool = new MeshBufferPool();
        private readonly GreedyMesher _mesher;
        private readonly BlockRegistry _blocks = BlockRegistry.Default;
        private readonly Dictionary<ChunkCoord, MeshData> _pendingMesh = new Dictionary<ChunkCoord, MeshData>();
        private readonly List<ChunkCoord> _dirty = new List<ChunkCoord>();
        private readonly TimelineEvent[] _events;
        private readonly int[] _columnHeights = new int[EditColumnCount];

        private long _stepIndex;
        private long _canonicalStep;
        private int _eventIndex;
        private int _waypointIndex;
        private int _activeColumn = -1;
        private bool _primaryHeld;
        private bool _placePending;
        private bool _recenterPending;
        private int _chunksLoaded;
        private int _unloads;
        private int _quadsEmitted;
        private int _editsApplied;

        /// <summary>
        /// Creates a harness with a fresh world, player, scheduler and mesher.
        /// </summary>
        /// <param name="config">The streaming budgets and radii for this session.</param>
        /// <param name="seed">The scheduler seed; the terrain seed is fixed.</param>
        /// <exception cref="ArgumentNullException"><paramref name="config"/> is null.</exception>
        public SessionHarness(StreamingConfig config, long seed)
        {
            ArgumentNullException.ThrowIfNull(config);

            _scheduler = new ChunkStreamingScheduler(config, seed);
            _mesher = new GreedyMesher(new AtlasLayout(16, 16), _pool);
            _world = new World(_generator);
            _world.ChunkChanged += MarkDirty;
            _player = new PlayerState
            {
                Position = new Vec3(
                    StartX,
                    TerrainGenerator.HeightAt((int)StartX, (int)StartZ, TerrainSeed) + StartHeightOffset,
                    StartZ),
            };
            _interaction = new InteractionService(new DdaRaycaster(), _blocks);

            for (int i = 0; i < EditColumns.Length; i++)
            {
                _columnHeights[i] = TerrainGenerator.HeightAt(EditColumns[i].X, EditColumns[i].Z, TerrainSeed);
            }

            _events = BuildTimeline();
        }

        /// <summary>The player's current feet-centre position.</summary>
        public Vec3 PlayerPosition => _player.Position;

        /// <summary>True when the scripted route reached its final waypoint.</summary>
        public bool WalkCompleted => _waypointIndex >= WalkWaypoints.Length;

        /// <summary>Number of unload actions the session performed (zero on the pinned route).</summary>
        public int Unloads => _unloads;

        /// <summary>Number of terrain chunks generated into the world.</summary>
        public int ChunksLoaded => _chunksLoaded;

        /// <summary>Number of quads the pooled mesher emitted.</summary>
        public int QuadsEmitted => _quadsEmitted;

        /// <summary>Number of applied break/place edits.</summary>
        public int EditsApplied => _editsApplied;

        /// <summary>
        /// Runs <paramref name="steps"/> frames of <paramref name="dt"/> seconds
        /// and returns the session result. State accumulates across calls.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="steps"/> is negative, or <paramref name="dt"/> is
        /// non-positive, NaN or infinite.
        /// </exception>
        public SessionResult Run(int steps, double dt)
        {
            if (steps < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(steps), steps, "steps must be non-negative.");
            }

            ValidateDt(dt);
            for (int i = 0; i < steps; i++)
            {
                Step(dt);
            }

            return CreateResult();
        }

        /// <summary>Advances the session exactly one frame.</summary>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="dt"/> is non-positive, NaN or infinite.
        /// </exception>
        public void Step(double dt)
        {
            ValidateDt(dt);
            _canonicalStep = ToCanonicalStep(dt);
            ApplyTimeline(_canonicalStep);

            _scheduler.Update(this, _player.Position);

            InputFrame frame = ComposeFrame(dt);
            PlayerController.Step(_player, in frame, _world, dt);
            InteractionResult result = _interaction.Update(in frame, _world, _player, dt);
            if (result.Edited)
            {
                _editsApplied++;
            }

            ProcessDirty();

            if (_placePending && !_primaryHeld)
            {
                _activeColumn = -1;
            }

            _placePending = false;
            _recenterPending = false;
            _stepIndex++;
        }

        /// <summary>Loads the chunk and marks it for meshing.</summary>
        /// <inheritdoc />
        public void OnLoad(ChunkCoord chunk)
        {
            if (_world.TryGetChunk(chunk) is null)
            {
                _world.LoadChunk(_generator.Generate(chunk, TerrainSeed));
            }

            _chunksLoaded++;
            MarkDirty(chunk);
        }

        /// <summary>Releases the chunk's pending mesh, if any.</summary>
        /// <inheritdoc />
        public void OnUnload(ChunkCoord chunk)
        {
            _unloads++;
            if (_pendingMesh.TryGetValue(chunk, out MeshData? mesh))
            {
                _pendingMesh.Remove(chunk);
                mesh.Release();
            }

            _dirty.Remove(chunk);
        }

        /// <summary>Releases the uploaded mesh; false when none is pending.</summary>
        /// <inheritdoc />
        public bool OnUpload(ChunkCoord chunk)
        {
            if (!_pendingMesh.TryGetValue(chunk, out MeshData? mesh))
            {
                return false;
            }

            _pendingMesh.Remove(chunk);
            mesh.Release();
            return true;
        }

        /// <summary>
        /// Hashes every resident cell in canonical <c>(chunk, x, y, z)</c>
        /// order with test-only FNV-1a 64.
        /// </summary>
        public ulong ComputeWorldHash()
        {
            var chunks = new List<ChunkCoord>(_scheduler.LoadedCount);
            foreach (ChunkCoord chunk in _scheduler.LoadedChunks)
            {
                chunks.Add(chunk);
            }

            chunks.Sort(CompareChunks);
            ulong hash = FnvOffsetBasis;
            for (int i = 0; i < chunks.Count; i++)
            {
                ChunkCoord coord = chunks[i];
                hash = Mix(hash, (ulong)(uint)coord.X);
                hash = Mix(hash, (ulong)(uint)coord.Y);
                hash = Mix(hash, (ulong)(uint)coord.Z);
                Chunk chunk = _world.GetChunk(coord)
                    ?? throw new InvalidOperationException("A resident chunk is missing from the world.");
                for (int x = 0; x < ChunkMath.ChunkSize; x++)
                {
                    for (int y = 0; y < ChunkMath.ChunkSize; y++)
                    {
                        for (int z = 0; z < ChunkMath.ChunkSize; z++)
                        {
                            hash = Mix(hash, (ulong)(uint)x);
                            hash = Mix(hash, (ulong)(uint)y);
                            hash = Mix(hash, (ulong)(uint)z);
                            hash = Mix(hash, chunk.Get(new Int3(x, y, z)).Value);
                        }
                    }
                }
            }

            return hash;
        }

        /// <summary>Builds the current <see cref="SessionResult"/>.</summary>
        public SessionResult CreateResult()
        {
            return new SessionResult(ComputeWorldHash(), _chunksLoaded, _quadsEmitted, _editsApplied);
        }

        private void MarkDirty(ChunkCoord chunk)
        {
            if (!_dirty.Contains(chunk))
            {
                _dirty.Add(chunk);
            }
        }

        private void ProcessDirty()
        {
            while (_dirty.Count > 0)
            {
                int last = _dirty.Count - 1;
                ChunkCoord coord = _dirty[last];
                _dirty.RemoveAt(last);

                Chunk? chunk = _world.TryGetChunk(coord);
                if (chunk is null)
                {
                    continue;
                }

                if (_pendingMesh.TryGetValue(coord, out MeshData? stale))
                {
                    _pendingMesh.Remove(coord);
                    stale.Release();
                }

                MeshData data = _mesher.Build(chunk.Snapshot(), _world.CreateNeighbourSnapshot(coord), _blocks);
                _quadsEmitted += data.IndexCount / 6;
                _pendingMesh.Add(coord, data);
                _scheduler.NotifyMeshReady(coord);
            }
        }

        private void ApplyTimeline(long canonicalStep)
        {
            while (_eventIndex < _events.Length && _events[_eventIndex].Step <= canonicalStep)
            {
                TimelineEvent timelineEvent = _events[_eventIndex];
                switch (timelineEvent.Kind)
                {
                    case TimelineEventKind.BreakStart:
                        _primaryHeld = true;
                        _activeColumn = timelineEvent.Column;
                        break;
                    case TimelineEventKind.BreakEnd:
                        _primaryHeld = false;
                        _activeColumn = -1;
                        break;
                    case TimelineEventKind.Place:
                        _placePending = true;
                        _activeColumn = timelineEvent.Column;
                        break;
                    default:
                        _recenterPending = true;
                        break;
                }

                _eventIndex++;
            }
        }

        private InputFrame ComposeFrame(double dt)
        {
            Vector2f move = default;
            float turn = 0f;
            if (_waypointIndex < WalkWaypoints.Length)
            {
                Steer(dt, out turn, out move);
            }

            PointerRay? pointer = null;
            if (_activeColumn >= 0)
            {
                (int x, int z) = EditColumns[_activeColumn];
                pointer = new PointerRay(
                    new Vec3(x + 0.5, _columnHeights[_activeColumn] + PointerEyeOffset, z + 0.5),
                    new Vec3(0.0, -1.0, 0.0));
            }

            return new InputFrame(
                move,
                turn,
                _recenterPending,
                pointer,
                _primaryHeld ? ButtonState.Held : ButtonState.Up,
                _placePending ? ButtonState.Pressed : ButtonState.Up,
                0,
                TrackingQuality.Good);
        }

        private void Steer(double dt, out float turnSnap, out Vector2f move)
        {
            if (_canonicalStep < WalkStartStep)
            {
                turnSnap = 0f;
                move = default;
                return;
            }

            (double X, double Z) waypoint = WalkWaypoints[_waypointIndex];
            double dx = waypoint.X - _player.Position.X;
            double dz = waypoint.Z - _player.Position.Z;
            double distanceSquared = (dx * dx) + (dz * dz);

            while (distanceSquared <= ArrivalRadius * ArrivalRadius)
            {
                _waypointIndex++;
                if (_waypointIndex >= WalkWaypoints.Length)
                {
                    turnSnap = 0f;
                    move = default;
                    return;
                }

                waypoint = WalkWaypoints[_waypointIndex];
                dx = waypoint.X - _player.Position.X;
                dz = waypoint.Z - _player.Position.Z;
                distanceSquared = (dx * dx) + (dz * dz);
            }

            double desiredYaw = Math.Atan2(-dx, -dz);
            double error = Math.IEEERemainder(desiredYaw - _player.YawRadians, 2.0 * Math.PI);
            double maxTurn = MaxTurnDegreesPerSecond * DegreesToRadians * dt;
            if (Math.Abs(error) <= maxTurn)
            {
                turnSnap = (float)(error / (DegreesToRadians * dt));
                move = Math.Abs(error) <= AlignToleranceRadians ? new Vector2f(0f, 1f) : default;
            }
            else
            {
                turnSnap = (float)(error > 0.0 ? MaxTurnDegreesPerSecond : -MaxTurnDegreesPerSecond);
                move = default;
            }
        }

        private static void ValidateDt(double dt)
        {
            if (!(dt > 0.0) || double.IsInfinity(dt))
            {
                throw new ArgumentOutOfRangeException(nameof(dt), dt, "dt must be finite and positive.");
            }
        }

        private long ToCanonicalStep(double dt)
        {
            return (long)Math.Round(_stepIndex * (dt / CanonicalDt), MidpointRounding.AwayFromZero);
        }

        private static TimelineEvent[] BuildTimeline()
        {
            var events = new List<TimelineEvent>((EditColumnCount * 3) + 2);
            for (int i = 0; i < EditColumnCount; i++)
            {
                int start = FirstBreakStep + (i * BreakSpacingSteps);
                events.Add(new TimelineEvent(start, TimelineEventKind.BreakStart, i));
                events.Add(new TimelineEvent(start + BreakHoldSteps, TimelineEventKind.BreakEnd, i));
                events.Add(new TimelineEvent(start + PlaceDelaySteps, TimelineEventKind.Place, i));
            }

            events.Add(new TimelineEvent(FirstRecentreStep, TimelineEventKind.Recentre, -1));
            events.Add(new TimelineEvent(SecondRecentreStep, TimelineEventKind.Recentre, -1));
            events.Sort(EventOrder);
            return events.ToArray();
        }

        private static int CompareEvents(TimelineEvent a, TimelineEvent b)
        {
            return a.Step.CompareTo(b.Step);
        }

        private static int CompareChunks(ChunkCoord a, ChunkCoord b)
        {
            int result = a.X.CompareTo(b.X);
            if (result != 0)
            {
                return result;
            }

            result = a.Y.CompareTo(b.Y);
            return result != 0 ? result : a.Z.CompareTo(b.Z);
        }

        private static ulong Mix(ulong hash, ulong value)
        {
            return (hash ^ value) * FnvPrime;
        }

        private enum TimelineEventKind
        {
            BreakStart,
            BreakEnd,
            Place,
            Recentre,
        }

        private readonly struct TimelineEvent
        {
            internal TimelineEvent(int step, TimelineEventKind kind, int column)
            {
                Step = step;
                Kind = kind;
                Column = column;
            }

            internal int Step { get; }

            internal TimelineEventKind Kind { get; }

            internal int Column { get; }
        }
    }
}

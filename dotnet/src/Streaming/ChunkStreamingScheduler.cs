using System;
using System.Collections.Generic;
using Cubeglass.CoreMath;
using Cubeglass.Voxel;

namespace Cubeglass.Streaming
{
    /// <summary>Identifies the kind of streaming work a <see cref="StreamingAction"/> carries.</summary>
    public enum StreamingActionKind
    {
        Load,
        Unload,
        Upload,
    }

    /// <summary>One ordered unit of streaming work.</summary>
    public readonly record struct StreamingAction(StreamingActionKind Kind, ChunkCoord Chunk);

    /// <summary>
    /// Deterministic, allocation-free per-frame scheduler for chunk streaming.
    /// </summary>
    /// <remarks>
    /// Each update emits up to <see cref="StreamingConfig.MaxLoadsPerFrame"/>
    /// Loads, <see cref="StreamingConfig.MaxUnloadsPerFrame"/> Unloads and
    /// <see cref="StreamingConfig.MaxMeshUploadsPerFrame"/> Uploads for the
    /// chunks around the player's chunk, in three groups in that order. Every
    /// group is ordered by squared chunk distance from the player's chunk and
    /// then by ascending <c>(X, Y, Z)</c>.
    /// <para>
    /// The desired set is the box of <see cref="StreamingConfig.ViewDistanceChunks"/>
    /// horizontal chunks and <see cref="StreamingConfig.VerticalRadiusChunks"/>
    /// vertical chunks around the player's chunk. A resident chunk unloads only
    /// once it leaves that box expanded by
    /// <see cref="StreamingConfig.UnloadHysteresis"/> on every axis, so jitter at
    /// the boundary cannot cause load/unload churn.
    /// </para>
    /// <para>
    /// Load and Unload actions apply to the resident set as soon as the
    /// scheduler emits them; the <c>Notify</c> methods exist so a host can
    /// report load, mesh and unload events it performed outside the action list
    /// (the calls are idempotent). <see cref="Update(Vec3)"/> returns a reused
    /// list that must be consumed before the next update.
    /// </para>
    /// <para>
    /// Extreme inputs are contained rather than trusted: a non-finite position
    /// component maps to chunk 0 on that axis, a finite position outside the
    /// representable cell space clamps to the edge chunk
    /// (<see cref="MinChunkCoordinate"/> or <see cref="MaxChunkCoordinate"/>),
    /// and the desired box is clipped to that same range with 64-bit
    /// arithmetic, so no radius addition can wrap. Priority order compares
    /// squared chunk distance in <see cref="double"/> space, so two chunks at
    /// opposite extremes cannot overflow into a wrong "nearer" chunk. The
    /// config itself is already validated by
    /// <see cref="StreamingConfig"/>, which bounds every radius and budget.
    /// </para>
    /// </remarks>
    public sealed class ChunkStreamingScheduler
    {
        /// <summary>
        /// Smallest chunk coordinate whose cells are representable:
        /// <c>int.MinValue / 16</c>.
        /// </summary>
        internal const int MinChunkCoordinate = int.MinValue / ChunkMath.ChunkSize;

        /// <summary>
        /// Largest chunk coordinate whose cells are representable:
        /// <c>int.MaxValue / 16</c>.
        /// </summary>
        internal const int MaxChunkCoordinate = int.MaxValue / ChunkMath.ChunkSize;

        private readonly StreamingConfig _config;
        private readonly HashSet<ChunkCoord> _desired = new HashSet<ChunkCoord>();
        private readonly HashSet<ChunkCoord> _loaded = new HashSet<ChunkCoord>();
        private readonly HashSet<ChunkCoord> _pendingMesh = new HashSet<ChunkCoord>();
        private readonly List<ChunkCoord> _candidates = new List<ChunkCoord>();
        private readonly List<StreamingAction> _actions = new List<StreamingAction>();
        private readonly Comparison<ChunkCoord> _priority;
        private ChunkCoord _playerChunk;

        /// <summary>
        /// Creates a scheduler for <paramref name="config"/>. The seed is
        /// reserved for future schedule jitter; the current order depends only
        /// on the config and the position timeline.
        /// </summary>
        public ChunkStreamingScheduler(StreamingConfig config, long seed)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            Seed = seed;
            _priority = ComparePriority;
        }

        /// <summary>The seed supplied at construction.</summary>
        public long Seed { get; }

        /// <summary>The chunks currently considered resident.</summary>
        public IReadOnlyCollection<ChunkCoord> LoadedChunks => _loaded;

        /// <summary>Number of resident chunks.</summary>
        public int LoadedCount => _loaded.Count;

        /// <summary>Number of chunks in the current desired set.</summary>
        public int DesiredCount => _desired.Count;

        /// <summary>Number of resident chunks whose mesh is ready and not yet uploaded.</summary>
        public int PendingMeshCount => _pendingMesh.Count;

        /// <summary>True when <paramref name="chunk"/> is resident.</summary>
        public bool IsLoaded(ChunkCoord chunk)
        {
            return _loaded.Contains(chunk);
        }

        /// <summary>True when <paramref name="chunk"/> has a mesh waiting to upload.</summary>
        public bool IsPendingMesh(ChunkCoord chunk)
        {
            return _pendingMesh.Contains(chunk);
        }

        /// <summary>Reports that a chunk became resident outside the action list.</summary>
        public void NotifyLoaded(ChunkCoord chunk)
        {
            _loaded.Add(chunk);
        }

        /// <summary>Reports that a resident chunk's mesh is ready to upload.</summary>
        public void NotifyMeshReady(ChunkCoord chunk)
        {
            _pendingMesh.Add(chunk);
        }

        /// <summary>Reports that a chunk left the resident set outside the action list.</summary>
        public void NotifyUnloaded(ChunkCoord chunk)
        {
            _loaded.Remove(chunk);
            _pendingMesh.Remove(chunk);
        }

        /// <summary>
        /// Advances the schedule one frame and returns the ordered actions,
        /// capped by the per-frame budgets.
        /// </summary>
        public IReadOnlyList<StreamingAction> Update(Vec3 playerPosition)
        {
            _playerChunk = ToChunk(playerPosition);
            RecomputeDesired();
            _actions.Clear();

            SelectNearest(_desired, _config.MaxLoadsPerFrame, CandidateKind.Load);
            for (int i = 0; i < _candidates.Count; i++)
            {
                ChunkCoord chunk = _candidates[i];
                _actions.Add(new StreamingAction(StreamingActionKind.Load, chunk));
                _loaded.Add(chunk);
            }

            SelectNearest(_loaded, _config.MaxUnloadsPerFrame, CandidateKind.Unload);
            for (int i = 0; i < _candidates.Count; i++)
            {
                ChunkCoord chunk = _candidates[i];
                _actions.Add(new StreamingAction(StreamingActionKind.Unload, chunk));
                _loaded.Remove(chunk);
                _pendingMesh.Remove(chunk);
            }

            SelectNearest(_pendingMesh, _config.MaxMeshUploadsPerFrame, CandidateKind.Upload);
            for (int i = 0; i < _candidates.Count; i++)
            {
                ChunkCoord chunk = _candidates[i];
                _actions.Add(new StreamingAction(StreamingActionKind.Upload, chunk));
                _pendingMesh.Remove(chunk);
            }

            return _actions;
        }

        /// <summary>
        /// Advances the schedule and raises each action on
        /// <paramref name="target"/> in the same deterministic order.
        /// </summary>
        /// <remarks>
        /// When <see cref="IStreamingTarget.OnUpload"/> returns false the
        /// rejected upload is not part of the returned list, no further uploads
        /// are attempted that frame, and every attempt that was skipped stays
        /// pending for the next update.
        /// </remarks>
        public IReadOnlyList<StreamingAction> Update(IStreamingTarget target, Vec3 playerPosition)
        {
            if (target is null)
            {
                throw new ArgumentNullException(nameof(target));
            }

            Update(playerPosition);
            for (int i = 0; i < _actions.Count; i++)
            {
                StreamingAction action = _actions[i];
                switch (action.Kind)
                {
                    case StreamingActionKind.Load:
                        target.OnLoad(action.Chunk);
                        break;
                    case StreamingActionKind.Unload:
                        target.OnUnload(action.Chunk);
                        break;
                    default:
                        if (!target.OnUpload(action.Chunk))
                        {
                            for (int j = i; j < _actions.Count; j++)
                            {
                                _pendingMesh.Add(_actions[j].Chunk);
                            }

                            _actions.RemoveRange(i, _actions.Count - i);
                            return _actions;
                        }

                        break;
                }
            }

            return _actions;
        }

        private void RecomputeDesired()
        {
            _desired.Clear();
            int radius = _config.ViewDistanceChunks;
            int vertical = (int)Math.Floor(_config.VerticalRadiusChunks);

            for (int dx = -radius; dx <= radius; dx++)
            {
                long x = (long)_playerChunk.X + dx;
                if (x < MinChunkCoordinate || x > MaxChunkCoordinate)
                {
                    continue;
                }

                for (int dy = -vertical; dy <= vertical; dy++)
                {
                    long y = (long)_playerChunk.Y + dy;
                    if (y < MinChunkCoordinate || y > MaxChunkCoordinate)
                    {
                        continue;
                    }

                    for (int dz = -radius; dz <= radius; dz++)
                    {
                        long z = (long)_playerChunk.Z + dz;
                        if (z < MinChunkCoordinate || z > MaxChunkCoordinate)
                        {
                            continue;
                        }

                        _desired.Add(new ChunkCoord((int)x, (int)y, (int)z));
                    }
                }
            }
        }

        private void SelectNearest(HashSet<ChunkCoord> source, int budget, CandidateKind kind)
        {
            _candidates.Clear();
            if (budget <= 0)
            {
                return;
            }

            foreach (ChunkCoord chunk in source)
            {
                if (IsCandidate(chunk, kind))
                {
                    _candidates.Add(chunk);
                }
            }

            if (_candidates.Count == 0)
            {
                return;
            }

            _candidates.Sort(_priority);
            if (_candidates.Count > budget)
            {
                _candidates.RemoveRange(budget, _candidates.Count - budget);
            }
        }

        private bool IsCandidate(ChunkCoord chunk, CandidateKind kind)
        {
            switch (kind)
            {
                case CandidateKind.Load:
                    return !_loaded.Contains(chunk);
                case CandidateKind.Unload:
                    return !_desired.Contains(chunk) && BeyondRetention(chunk);
                default:
                    return _loaded.Contains(chunk);
            }
        }

        private bool BeyondRetention(ChunkCoord chunk)
        {
            long horizontal = (long)_config.ViewDistanceChunks + _config.UnloadHysteresis;
            long vertical = (long)(int)Math.Floor(_config.VerticalRadiusChunks) + _config.UnloadHysteresis;
            long dx = (long)chunk.X - _playerChunk.X;
            long dy = (long)chunk.Y - _playerChunk.Y;
            long dz = (long)chunk.Z - _playerChunk.Z;

            return dx > horizontal || dx < -horizontal
                || dy > vertical || dy < -vertical
                || dz > horizontal || dz < -horizontal;
        }

        private double SquaredDistance(ChunkCoord chunk)
        {
            double dx = (double)chunk.X - _playerChunk.X;
            double dy = (double)chunk.Y - _playerChunk.Y;
            double dz = (double)chunk.Z - _playerChunk.Z;
            return (dx * dx) + (dy * dy) + (dz * dz);
        }

        private int ComparePriority(ChunkCoord a, ChunkCoord b)
        {
            double distanceA = SquaredDistance(a);
            double distanceB = SquaredDistance(b);
            if (distanceA != distanceB)
            {
                return distanceA < distanceB ? -1 : 1;
            }

            if (a.X != b.X)
            {
                return a.X < b.X ? -1 : 1;
            }

            if (a.Y != b.Y)
            {
                return a.Y < b.Y ? -1 : 1;
            }

            if (a.Z != b.Z)
            {
                return a.Z < b.Z ? -1 : 1;
            }

            return 0;
        }

        private static ChunkCoord ToChunk(Vec3 position)
        {
            return new ChunkCoord(
                ChunkComponent(position.X),
                ChunkComponent(position.Y),
                ChunkComponent(position.Z));
        }

        /// <summary>
        /// Maps one world coordinate to its chunk: NaN maps to 0, and a finite
        /// value outside the representable cell space clamps to the edge
        /// chunk, so the cast can never wrap and the result always names a
        /// chunk whose cells exist.
        /// </summary>
        private static int ChunkComponent(double world)
        {
            if (double.IsNaN(world))
            {
                return 0;
            }

            double chunk = Math.Floor(world / ChunkMath.ChunkSize);
            if (chunk <= MinChunkCoordinate)
            {
                return MinChunkCoordinate;
            }

            if (chunk >= MaxChunkCoordinate)
            {
                return MaxChunkCoordinate;
            }

            return (int)chunk;
        }

        private enum CandidateKind
        {
            Load,
            Unload,
            Upload,
        }
    }
}

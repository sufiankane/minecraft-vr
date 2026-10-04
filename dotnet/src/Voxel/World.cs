using System;
using System.Collections.Generic;

namespace Cubeglass.Voxel
{
    /// <summary>
    /// The loaded-chunk world model with <see cref="Apply"/> as the only
    /// mutation path (dossier section 5.9).
    /// </summary>
    /// <remarks>
    /// Chunks are keyed by the struct <see cref="ChunkCoord"/>, so
    /// <see cref="Get"/>, <see cref="IsLoaded"/> and <see cref="Apply"/> are
    /// dictionary lookups plus floor maths with no allocation. Unloaded cells
    /// read as <see cref="BlockId.Air"/>; edits to them are rejected. An
    /// optional <see cref="IWorldGenerator"/> and <see cref="IWorldStore"/> are
    /// retained for later streaming work.
    /// </remarks>
    public sealed class World : IWorld
    {
        private readonly Dictionary<ChunkCoord, Chunk> _chunks = new Dictionary<ChunkCoord, Chunk>();

        private Action<ChunkCoord>? _chunkChanged;

        public World(IWorldGenerator? generator = null, IWorldStore? store = null)
        {
            Generator = generator;
            Store = store;
        }

        public event Action<ChunkCoord> ChunkChanged
        {
            add { _chunkChanged += value; }
            remove { _chunkChanged -= value; }
        }

        /// <summary>The generator retained for later streaming; may be null.</summary>
        public IWorldGenerator? Generator { get; }

        /// <summary>The store retained for later streaming; may be null.</summary>
        public IWorldStore? Store { get; }

        public BlockId Get(Int3 cell)
        {
            if (_chunks.TryGetValue(ChunkMath.ToChunk(cell), out Chunk? chunk))
            {
                return chunk.Get(ChunkMath.ToLocal(cell));
            }

            return BlockId.Air;
        }

        public bool IsLoaded(Int3 cell)
        {
            return _chunks.ContainsKey(ChunkMath.ToChunk(cell));
        }

        public EditResult Apply(in EditCommand cmd)
        {
            if (!_chunks.TryGetValue(ChunkMath.ToChunk(cmd.Cell), out Chunk? chunk))
            {
                return EditResult.Rejected;
            }

            Int3 local = ChunkMath.ToLocal(cmd.Cell);
            if (chunk.Get(local) != cmd.Expected)
            {
                return EditResult.Rejected;
            }

            chunk.Set(local, cmd.New);
            _chunkChanged?.Invoke(chunk.Coord);
            return EditResult.Applied;
        }

        /// <summary>
        /// Builds a read-only view of the loaded chunks around
        /// <paramref name="coord"/> for border meshing (ADR-0007). Chunks that
        /// are not loaded are absent from the view and read as
        /// <see cref="BlockId.Air"/>. A neighbour coordinate outside the
        /// <see cref="int"/> range does not exist and is skipped, so the
        /// lookup can never wrap onto a chunk at the opposite end of the
        /// coordinate space.
        /// </summary>
        public NeighbourSnapshot CreateNeighbourSnapshot(ChunkCoord coord)
        {
            var neighbours = new ChunkSnapshot?[NeighbourSnapshot.DirectionCount];

            for (int dz = -1; dz <= 1; dz++)
            {
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0 && dz == 0)
                        {
                            continue;
                        }

                        long x = (long)coord.X + dx;
                        long y = (long)coord.Y + dy;
                        long z = (long)coord.Z + dz;
                        if (x < int.MinValue || x > int.MaxValue
                            || y < int.MinValue || y > int.MaxValue
                            || z < int.MinValue || z > int.MaxValue)
                        {
                            continue;
                        }

                        var neighbourCoord = new ChunkCoord((int)x, (int)y, (int)z);
                        if (_chunks.TryGetValue(neighbourCoord, out Chunk? chunk))
                        {
                            var direction = new Int3(dx, dy, dz);
                            neighbours[NeighbourSnapshot.Index(direction)] = chunk.Snapshot();
                        }
                    }
                }
            }

            return new NeighbourSnapshot(coord, neighbours);
        }

        /// <summary>
        /// Loads <paramref name="chunk"/>, replacing any chunk with the same
        /// coordinate, for tests and S7 streaming.
        /// </summary>
        public void LoadChunk(Chunk chunk)
        {
            if (chunk is null)
            {
                throw new ArgumentNullException(nameof(chunk));
            }

            _chunks[chunk.Coord] = chunk;
        }

        /// <summary>
        /// Returns the loaded chunk at <paramref name="coord"/>, or null when it
        /// is not loaded.
        /// </summary>
        public Chunk? TryGetChunk(ChunkCoord coord)
        {
            return _chunks.TryGetValue(coord, out Chunk? chunk) ? chunk : null;
        }

        /// <summary>
        /// Alias of <see cref="TryGetChunk"/> kept for callers written against
        /// the task brief's name.
        /// </summary>
        public Chunk? GetChunk(ChunkCoord coord)
        {
            return TryGetChunk(coord);
        }
    }
}

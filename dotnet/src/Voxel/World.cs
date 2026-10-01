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

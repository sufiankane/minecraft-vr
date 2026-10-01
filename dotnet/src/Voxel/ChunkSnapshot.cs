using System;

namespace Cubeglass.Voxel
{
    /// <summary>
    /// An immutable copy of a chunk for worker-thread meshing (dossier
    /// section 5.10). Created by <see cref="Chunk.Snapshot"/>; later writes to
    /// the source chunk are not observed.
    /// </summary>
    public sealed class ChunkSnapshot
    {
        private readonly BlockId[] _blocks;

        internal ChunkSnapshot(ChunkCoord coord, BlockId[] blocks)
        {
            Coord = coord;
            _blocks = new BlockId[blocks.Length];
            Array.Copy(blocks, _blocks, blocks.Length);
        }

        /// <summary>The chunk coordinate this snapshot was taken from.</summary>
        public ChunkCoord Coord { get; }

        /// <summary>
        /// Returns the block at a chunk-local cell.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Any component of <paramref name="local"/> lies outside
        /// <c>[0, ChunkMath.ChunkSize)</c>.
        /// </exception>
        public BlockId Get(Int3 local)
        {
            Chunk.CheckLocal(local);
            return _blocks[Chunk.Index(local)];
        }
    }
}

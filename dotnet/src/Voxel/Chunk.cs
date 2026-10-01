using System;

namespace Cubeglass.Voxel
{
    /// <summary>
    /// A 16x16x16 block of the world: the storage unit for edits and meshing.
    /// </summary>
    /// <remarks>
    /// Cells live in one flat <see cref="BlockId"/> array rather than a
    /// multidimensional array so indexing stays a single computed offset, the
    /// layout is explicit and <see cref="Snapshot"/> is one copy. The linear
    /// index is <c>x + 16*y + 256*z</c> (Z-major local order), matching the save
    /// format in ADR-0006. Construction allocates the array once;
    /// <see cref="Get"/> and <see cref="Set"/> allocate nothing.
    /// </remarks>
    public sealed class Chunk
    {
        internal const int CellCount = ChunkMath.ChunkSize * ChunkMath.ChunkSize * ChunkMath.ChunkSize;

        private const int LayerSize = ChunkMath.ChunkSize * ChunkMath.ChunkSize;

        private readonly BlockId[] _blocks = new BlockId[CellCount];

        public Chunk(ChunkCoord coord)
        {
            Coord = coord;
        }

        /// <summary>The chunk coordinate this chunk was created for.</summary>
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
            CheckLocal(local);
            return _blocks[Index(local)];
        }

        /// <summary>Returns an independent copy of the chunk for worker use.</summary>
        public ChunkSnapshot Snapshot()
        {
            return new ChunkSnapshot(Coord, _blocks);
        }

        internal static int Index(Int3 local)
        {
            return local.X + (ChunkMath.ChunkSize * local.Y) + (LayerSize * local.Z);
        }

        internal static void CheckLocal(Int3 local)
        {
            if (local.X < 0 || local.X >= ChunkMath.ChunkSize
                || local.Y < 0 || local.Y >= ChunkMath.ChunkSize
                || local.Z < 0 || local.Z >= ChunkMath.ChunkSize)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(local),
                    local,
                    "Local cell components must lie in [0, ChunkSize).");
            }
        }

        internal void Set(Int3 local, BlockId block)
        {
            CheckLocal(local);
            _blocks[Index(local)] = block;
        }
    }
}

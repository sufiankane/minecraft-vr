using System;

namespace Cubeglass.Voxel
{
#pragma warning disable CA1716 // Get is frozen by the S3 brief, matching IBlockRegistry.Get.
    /// <summary>
    /// An immutable read-only view of the 26 chunks around a centre chunk
    /// (Chebyshev distance 1 is the neighbour scope pinned by ADR-0007).
    /// </summary>
    /// <remarks>
    /// Every direction component lies in <c>{-1, 0, 1}</c> and the zero
    /// direction is invalid: the centre chunk is not part of the view. A
    /// missing neighbour is unloaded and reads as <see cref="BlockId.Air"/>.
    /// Instances are created by <see cref="World.CreateNeighbourSnapshot"/> or
    /// by the internal constructor in tests. The view is a copy: later edits
    /// to the source chunks are not observed, so meshing can run on a worker
    /// thread.
    /// </remarks>
    public sealed class NeighbourSnapshot
    {
        internal const int DirectionCount = 27;

        private readonly ChunkCoord _centerChunk;
        private readonly ChunkSnapshot?[] _neighbours;

        internal NeighbourSnapshot(ChunkCoord centerChunk, ChunkSnapshot?[] neighbours)
        {
            _centerChunk = centerChunk;
            _neighbours = new ChunkSnapshot?[DirectionCount];
            Array.Copy(neighbours, _neighbours, DirectionCount);
        }

        /// <summary>An empty view: all 26 neighbours are absent.</summary>
        public static NeighbourSnapshot Empty { get; } =
            new NeighbourSnapshot(default, new ChunkSnapshot?[DirectionCount]);

        /// <summary>
        /// Returns the snapshot of the neighbour in <paramref name="direction"/>,
        /// or null when that chunk is not loaded.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Any component of <paramref name="direction"/> lies outside
        /// <c>{-1, 0, 1}</c>, or the direction is zero.
        /// </exception>
        public ChunkSnapshot? Get(Int3 direction)
        {
            return _neighbours[Index(direction)];
        }

        /// <summary>
        /// Returns the block at <paramref name="worldCell"/>, resolved through
        /// the neighbour chunk that owns it. Cells inside the centre chunk (or
        /// outside the 26-chunk neighbourhood) and cells in unloaded neighbours
        /// read as <see cref="BlockId.Air"/>.
        /// </summary>
        public BlockId GetCell(Int3 worldCell)
        {
            ChunkCoord chunk = ChunkMath.ToChunk(worldCell);
            var direction = new Int3(
                chunk.X - _centerChunk.X,
                chunk.Y - _centerChunk.Y,
                chunk.Z - _centerChunk.Z);

            if (direction.X < -1 || direction.X > 1
                || direction.Y < -1 || direction.Y > 1
                || direction.Z < -1 || direction.Z > 1
                || direction == Int3.Zero)
            {
                return BlockId.Air;
            }

            ChunkSnapshot? neighbour = _neighbours[IndexUnchecked(direction)];
            if (neighbour is null)
            {
                return BlockId.Air;
            }

            return neighbour.Get(ChunkMath.ToLocal(worldCell));
        }

        /// <summary>
        /// Returns the array slot of <paramref name="direction"/>.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Any component lies outside <c>{-1, 0, 1}</c>, or the direction is
        /// zero.
        /// </exception>
        internal static int Index(Int3 direction)
        {
            if (direction.X < -1 || direction.X > 1
                || direction.Y < -1 || direction.Y > 1
                || direction.Z < -1 || direction.Z > 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(direction),
                    direction,
                    "A neighbour direction must have components in {-1, 0, 1}.");
            }

            if (direction == Int3.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(direction),
                    direction,
                    "The zero direction is not a neighbour.");
            }

            return IndexUnchecked(direction);
        }

        private static int IndexUnchecked(Int3 direction)
        {
            return ((direction.X + 1) * 9) + ((direction.Y + 1) * 3) + (direction.Z + 1);
        }
    }
#pragma warning restore CA1716
}

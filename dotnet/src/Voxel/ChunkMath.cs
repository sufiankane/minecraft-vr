namespace Cubeglass.Voxel
{
    /// <summary>
    /// Floor-based conversions between world cells, chunk coordinates and
    /// chunk-local cells (ADR-0006).
    /// </summary>
    /// <remarks>
    /// Every conversion is total, allocation-free and defined for the full
    /// <see cref="int"/> range, including negative coordinates. Floor division
    /// and floor modulo are used rather than C# truncation, so
    /// <c>ToWorld(ToChunk(c), ToLocal(c)) == c</c> holds for every cell and the
    /// round trip is exact at the <see cref="int"/> extremes. <see cref="ToWorld"/>
    /// requires the documented local-range precondition; results outside it are
    /// unchecked.
    /// </remarks>
    public static class ChunkMath
    {
        /// <summary>Edge length of a cubic chunk: 16 cells per axis.</summary>
        public const int ChunkSize = 16;

        /// <summary>
        /// Returns the chunk that owns <paramref name="cell"/> using floor
        /// division, so <c>(-1, -1, -1)</c> belongs to chunk
        /// <c>(-1, -1, -1)</c>.
        /// </summary>
        public static ChunkCoord ToChunk(Int3 cell)
        {
            return new ChunkCoord(FloorDiv(cell.X), FloorDiv(cell.Y), FloorDiv(cell.Z));
        }

        /// <summary>
        /// Returns the chunk-local cell of <paramref name="cell"/> using floor
        /// modulo. The result is always within <c>[0, ChunkSize)</c> per axis.
        /// </summary>
        public static Int3 ToLocal(Int3 cell)
        {
            return new Int3(FloorMod(cell.X), FloorMod(cell.Y), FloorMod(cell.Z));
        }

        /// <summary>
        /// Returns the world cell for a chunk-local cell.
        /// Precondition: every component of <paramref name="local"/> lies in
        /// <c>[0, ChunkSize)</c> (use <see cref="ToLocal"/> to obtain one).
        /// </summary>
        public static Int3 ToWorld(ChunkCoord chunk, Int3 local)
        {
            return new Int3(
                (chunk.X * ChunkSize) + local.X,
                (chunk.Y * ChunkSize) + local.Y,
                (chunk.Z * ChunkSize) + local.Z);
        }

        private static int FloorDiv(int value)
        {
            int quotient = value / ChunkSize;
            if (value % ChunkSize != 0 && value < 0)
            {
                quotient--;
            }

            return quotient;
        }

        private static int FloorMod(int value)
        {
            int remainder = value % ChunkSize;
            if (remainder < 0)
            {
                remainder += ChunkSize;
            }

            return remainder;
        }
    }
}

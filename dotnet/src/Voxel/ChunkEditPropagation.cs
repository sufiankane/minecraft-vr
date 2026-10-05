using System;
using System.Collections.Generic;

namespace Cubeglass.Voxel
{
    /// <summary>
    /// The dirty-propagation rule for a single cell edit (ADR-0007).
    /// </summary>
    /// <remarks>
    /// An edit can change face visibility for every opaque neighbour in the 26
    /// directions and, from S3 WI3, the ambient occlusion of every cell at
    /// Chebyshev distance 1 (a vertex corner samples the eight cells around
    /// it). Both sets lie inside <c>cell + [-1, 1]³</c>, so every chunk
    /// containing a cell of that box must be remeshed. Per axis the box spans
    /// at most two chunks and chunk membership is constant between its two
    /// boundary values, so the eight corner cells already produce every
    /// affected chunk: 1, 2, 4 or 8 of them, sorted by <c>(X, Y, Z)</c> for a
    /// deterministic dirty order. A corner whose coordinate falls outside the
    /// <see cref="int"/> cell range is skipped: there is no cell there, and
    /// wrapping would otherwise name an unrelated chunk at the opposite end of
    /// the coordinate space.
    /// </remarks>
    public static class ChunkEditPropagation
    {
        private const int MaxAffectedChunks = 8;

        /// <summary>
        /// Returns every chunk that must be remeshed after the cell at
        /// <paramref name="cell"/> changes.
        /// </summary>
        /// <remarks>
        /// Convenience wrapper over <see cref="FillAffectedChunks"/> that
        /// allocates a result array of exactly the returned count. Hot paths
        /// should call <see cref="FillAffectedChunks"/> with a caller-owned
        /// buffer instead.
        /// </remarks>
        public static IReadOnlyList<ChunkCoord> GetAffectedChunks(Int3 cell)
        {
            Span<ChunkCoord> buffer = stackalloc ChunkCoord[MaxAffectedChunks];
            int count = FillAffectedChunks(cell, buffer);

            var result = new ChunkCoord[count];
            for (int i = 0; i < count; i++)
            {
                result[i] = buffer[i];
            }

            return result;
        }

        /// <summary>
        /// Writes every chunk that must be remeshed after the cell at
        /// <paramref name="cell"/> changes into the first
        /// <paramref name="destination"/> slots, sorted by <c>(X, Y, Z)</c>,
        /// and returns how many were written.
        /// </summary>
        /// <remarks>
        /// Allocation-free: the caller owns <paramref name="destination"/> and
        /// the method writes only the first <c>N</c> slots. At most
        /// <see cref="MaxAffectedChunks"/> (8) chunks can be affected, so the
        /// buffer must hold at least that many; a smaller buffer is rejected
        /// up front rather than partially filled.
        /// </remarks>
        /// <exception cref="ArgumentException">
        /// <paramref name="destination"/> is shorter than
        /// <see cref="MaxAffectedChunks"/>.
        /// </exception>
        public static int FillAffectedChunks(Int3 cell, Span<ChunkCoord> destination)
        {
            if (destination.Length < MaxAffectedChunks)
            {
                throw new ArgumentException(
                    $"destination must provide room for {MaxAffectedChunks} chunks.",
                    nameof(destination));
            }

            int count = 0;

            for (int dz = -1; dz <= 1; dz += 2)
            {
                for (int dy = -1; dy <= 1; dy += 2)
                {
                    for (int dx = -1; dx <= 1; dx += 2)
                    {
                        long x = (long)cell.X + dx;
                        long y = (long)cell.Y + dy;
                        long z = (long)cell.Z + dz;
                        if (x < int.MinValue || x > int.MaxValue
                            || y < int.MinValue || y > int.MaxValue
                            || z < int.MinValue || z > int.MaxValue)
                        {
                            continue;
                        }

                        ChunkCoord chunk = ChunkMath.ToChunk(new Int3((int)x, (int)y, (int)z));

                        bool seen = false;
                        for (int i = 0; i < count; i++)
                        {
                            if (destination[i] == chunk)
                            {
                                seen = true;
                                break;
                            }
                        }

                        if (!seen)
                        {
                            destination[count++] = chunk;
                        }
                    }
                }
            }

            SortByXyz(destination, count);
            return count;
        }

        private static void SortByXyz(Span<ChunkCoord> chunks, int count)
        {
            for (int i = 1; i < count; i++)
            {
                ChunkCoord value = chunks[i];
                int j = i - 1;
                while (j >= 0 && Compare(chunks[j], value) > 0)
                {
                    chunks[j + 1] = chunks[j];
                    j--;
                }

                chunks[j + 1] = value;
            }
        }

        private static int Compare(ChunkCoord a, ChunkCoord b)
        {
            int result = a.X.CompareTo(b.X);
            if (result != 0)
            {
                return result;
            }

            result = a.Y.CompareTo(b.Y);
            return result != 0 ? result : a.Z.CompareTo(b.Z);
        }
    }
}

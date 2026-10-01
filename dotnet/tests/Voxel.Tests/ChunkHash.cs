using System;

namespace Cubeglass.Voxel.Tests
{
    /// <summary>
    /// Test-only FNV-1a 64 hash over a chunk's block ids in Z-major local
    /// order (the flat-array order used by <see cref="Chunk"/>: X fastest,
    /// then Y, then Z).
    /// </summary>
    /// <remarks>
    /// Each id is hashed as its two little-endian bytes with the standard
    /// FNV-1a 64 offset basis and prime. The golden terrain test pins a
    /// committed constant produced by this helper.
    /// </remarks>
    internal static class ChunkHash
    {
        private const ulong OffsetBasis = 14695981039346656037UL;
        private const ulong Prime = 1099511628211UL;

        internal static ulong Hash(Chunk chunk)
        {
            ArgumentNullException.ThrowIfNull(chunk);

            ulong hash = OffsetBasis;
            for (int z = 0; z < ChunkMath.ChunkSize; z++)
            {
                for (int y = 0; y < ChunkMath.ChunkSize; y++)
                {
                    for (int x = 0; x < ChunkMath.ChunkSize; x++)
                    {
                        ushort value = chunk.Get(new Int3(x, y, z)).Value;
                        hash = (hash ^ (byte)(value & 0xFF)) * Prime;
                        hash = (hash ^ (byte)(value >> 8)) * Prime;
                    }
                }
            }

            return hash;
        }
    }
}

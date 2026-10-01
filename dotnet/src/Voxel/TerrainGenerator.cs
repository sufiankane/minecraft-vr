using System;

namespace Cubeglass.Voxel
{
    /// <summary>
    /// Deterministic layered terrain built from seeded 2D value noise
    /// (dossier S2).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Heights are sampled from <see cref="Noise.Fbm2D"/> once per 16 world
    /// cells (one noise lattice cell per chunk side) and shaped into
    /// <c>[BaseHeight - Amplitude, BaseHeight + Amplitude]</c> — that is,
    /// <c>[6, 14]</c> for the default constants. Each column is stone below
    /// <c>height - 3</c>, dirt for <c>height - 3</c> through
    /// <c>height - 1</c>, grass at <c>height</c> and air above. World Y below
    /// zero is always stone.
    /// </para>
    /// <para>
    /// Generation is a pure function of <c>(seed, ChunkCoord)</c>: there is no
    /// clock, no <c>System.Random</c> and no mutable state, so equal inputs
    /// produce equal chunks on every call and every instance. Chunk-local Y
    /// maps to world Y as <c>chunk.Y * 16 + localY</c>.
    /// </para>
    /// </remarks>
    public sealed class TerrainGenerator : IWorldGenerator
    {
        /// <summary>World Y of the noise-free average column top.</summary>
        public const int BaseHeight = 10;

        /// <summary>Maximum deviation of a column top from <see cref="BaseHeight"/>.</summary>
        public const int Amplitude = 4;

        /// <summary>Number of dirt layers directly below the grass layer.</summary>
        public const int DirtDepth = 3;

        private const int Octaves = 4;
        private const double NoiseCellsPerSample = 16.0;

        private static readonly BlockId Stone = new BlockId(1);
        private static readonly BlockId Dirt = new BlockId(2);
        private static readonly BlockId Grass = new BlockId(3);

        public Chunk Generate(ChunkCoord c, long seed)
        {
            var chunk = new Chunk(c);
            int originX = c.X * ChunkMath.ChunkSize;
            int originY = c.Y * ChunkMath.ChunkSize;
            int originZ = c.Z * ChunkMath.ChunkSize;

            for (int z = 0; z < ChunkMath.ChunkSize; z++)
            {
                for (int x = 0; x < ChunkMath.ChunkSize; x++)
                {
                    int height = HeightAt(originX + x, originZ + z, seed);
                    for (int y = 0; y < ChunkMath.ChunkSize; y++)
                    {
                        BlockId block = BlockFor(originY + y, height);
                        if (block != BlockId.Air)
                        {
                            chunk.Set(new Int3(x, y, z), block);
                        }
                    }
                }
            }

            return chunk;
        }

        /// <summary>
        /// The world Y of the grass layer for the column at
        /// <paramref name="worldX"/>, <paramref name="worldZ"/> and
        /// <paramref name="seed"/>.
        /// </summary>
        public static int HeightAt(int worldX, int worldZ, long seed)
        {
            double noise = Noise.Fbm2D(
                worldX / NoiseCellsPerSample,
                worldZ / NoiseCellsPerSample,
                seed,
                Octaves);
            double shaped = (noise * 2.0) - 1.0;
            return BaseHeight + (int)Math.Floor((shaped * Amplitude) + 0.5);
        }

        private static BlockId BlockFor(int worldY, int height)
        {
            if (worldY < 0)
            {
                return Stone;
            }

            if (worldY > height)
            {
                return BlockId.Air;
            }

            if (worldY == height)
            {
                return Grass;
            }

            if (worldY >= height - DirtDepth)
            {
                return Dirt;
            }

            return Stone;
        }
    }
}

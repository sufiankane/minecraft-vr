using NUnit.Framework;

namespace Cubeglass.Voxel.Tests
{
    [TestFixture]
    public sealed class TerrainGeneratorTests
    {
        private const long Seed = 42L;

        // Produced once by running ChunkHash.Hash(TerrainGenerator.Generate((0,0,0), 42))
        // and committed as the golden value; the reviewer recomputes it.
        private const ulong GoldenHash = 0x1EF678D6ADDA1CECUL;

        private static readonly BlockId Stone = new BlockId(1);
        private static readonly BlockId Dirt = new BlockId(2);
        private static readonly BlockId Grass = new BlockId(3);

        [Test]
        public void GenerateIsDeterministicAcrossCallsAndInstances()
        {
            var generator = new TerrainGenerator();
            var fresh = new TerrainGenerator();
            var coord = new ChunkCoord(3, -2, 5);

            ulong first = ChunkHash.Hash(generator.Generate(coord, Seed));
            ulong second = ChunkHash.Hash(generator.Generate(coord, Seed));
            ulong third = ChunkHash.Hash(fresh.Generate(coord, Seed));

            Assert.That(second, Is.EqualTo(first));
            Assert.That(third, Is.EqualTo(first));
        }

        [Test]
        public void DifferentSeedsProduceDifferentChunks()
        {
            var generator = new TerrainGenerator();
            Chunk atFortyTwo = generator.Generate(new ChunkCoord(0, 0, 0), 42L);
            Chunk atFortyThree = generator.Generate(new ChunkCoord(0, 0, 0), 43L);

            Assert.That(ChunkHash.Hash(atFortyThree), Is.Not.EqualTo(ChunkHash.Hash(atFortyTwo)));
        }

        [Test]
        public void GoldenHashOfTheOriginChunkForSeed42()
        {
            Chunk chunk = new TerrainGenerator().Generate(new ChunkCoord(0, 0, 0), Seed);

            Assert.That(ChunkHash.Hash(chunk), Is.EqualTo(GoldenHash));
        }

        [Test]
        public void EachColumnIsGrassOverDirtOverStoneUnderAir()
        {
            Chunk chunk = new TerrainGenerator().Generate(new ChunkCoord(0, 0, 0), Seed);

            for (int z = 0; z < ChunkMath.ChunkSize; z++)
            {
                for (int x = 0; x < ChunkMath.ChunkSize; x++)
                {
                    int height = TerrainGenerator.HeightAt(x, z, Seed);
                    Assert.That(height, Is.InRange(4, ChunkMath.ChunkSize - 2), $"height at ({x},{z})");
                    Assert.That(chunk.Get(new Int3(x, height, z)), Is.EqualTo(Grass), $"grass at ({x},{z})");
                    Assert.That(chunk.Get(new Int3(x, height - 1, z)), Is.EqualTo(Dirt));
                    Assert.That(chunk.Get(new Int3(x, height - 2, z)), Is.EqualTo(Dirt));
                    Assert.That(chunk.Get(new Int3(x, height - 3, z)), Is.EqualTo(Dirt));
                    Assert.That(chunk.Get(new Int3(x, height - 4, z)), Is.EqualTo(Stone));
                    Assert.That(chunk.Get(new Int3(x, height + 1, z)), Is.EqualTo(BlockId.Air));
                }
            }
        }

        [Test]
        public void WorldYBelowZeroIsAlwaysStone()
        {
            Chunk chunk = new TerrainGenerator().Generate(new ChunkCoord(0, -1, 0), Seed);

            for (int z = 0; z < ChunkMath.ChunkSize; z++)
            {
                for (int y = 0; y < ChunkMath.ChunkSize; y++)
                {
                    for (int x = 0; x < ChunkMath.ChunkSize; x++)
                    {
                        Assert.That(chunk.Get(new Int3(x, y, z)), Is.EqualTo(Stone), $"cell ({x},{y},{z})");
                    }
                }
            }
        }

        [Test]
        public void LocalYIsOffsetByTheChunkYCoordinate()
        {
            var generator = new TerrainGenerator();

            // World Y in [-16, -1]; the lowest possible height is 6.
            Chunk below = generator.Generate(new ChunkCoord(0, -1, 0), Seed);
            Assert.That(below.Get(new Int3(0, 15, 0)), Is.EqualTo(Stone), "world Y -1");
            Assert.That(below.Get(new Int3(0, 0, 0)), Is.EqualTo(Stone), "world Y -16");

            // World Y in [16, 31]; the highest possible height is 14.
            Chunk above = generator.Generate(new ChunkCoord(0, 1, 0), Seed);
            Assert.That(above.Get(new Int3(0, 0, 0)), Is.EqualTo(BlockId.Air), "world Y 16");
            Assert.That(above.Get(new Int3(15, 15, 15)), Is.EqualTo(BlockId.Air), "world Y 31");
        }

        [Test]
        public void GenerateOnlyFillsDefinedBlocksInsideItsLocalBounds()
        {
            var generator = new TerrainGenerator();
            var coords = new[]
            {
                new ChunkCoord(0, 0, 0),
                new ChunkCoord(1, -1, 2),
                new ChunkCoord(-3, 0, 4),
                new ChunkCoord(0, -2, 0),
                new ChunkCoord(5, 1, -7),
            };

            foreach (ChunkCoord coord in coords)
            {
                Chunk chunk = generator.Generate(coord, Seed);
                Assert.That(chunk.Coord, Is.EqualTo(coord));

                for (int z = 0; z < ChunkMath.ChunkSize; z++)
                {
                    for (int y = 0; y < ChunkMath.ChunkSize; y++)
                    {
                        for (int x = 0; x < ChunkMath.ChunkSize; x++)
                        {
                            BlockId block = chunk.Get(new Int3(x, y, z));
                            Assert.That(
                                block == BlockId.Air || block == Stone || block == Dirt || block == Grass,
                                Is.True,
                                $"chunk {coord} cell ({x},{y},{z}) = {block.Value}");
                        }
                    }
                }
            }
        }
    }
}

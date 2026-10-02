using System.Collections.Generic;
using Cubeglass.Voxel;
using NUnit.Framework;

namespace Cubeglass.Mesh.Tests
{
    /// <summary>
    /// Golden FNV-1a 64 hashes (<see cref="MeshHash"/>) for canonical chunks
    /// meshed by <see cref="GreedyMesher"/> with the registries below.
    /// </summary>
    /// <remarks>
    /// The constants were produced by the Task 3 GREEN run of this fixture
    /// (the Task 2 constants were AO placeholders) and are recorded with the
    /// command and counts in
    /// <c>.superpowers/sdd/2026-10-01-s3-meshing/task-3-report.md</c>. Solid,
    /// empty and single-corner chunks keep their Task 2 hashes because every
    /// AO sample of an isolated face reads air (255); the checkerboard and
    /// terrain hashes change with real AO.
    /// </remarks>
    [TestFixture]
    public sealed class GoldenMeshTests
    {
        private static readonly ChunkCoord Origin = new ChunkCoord(0, 0, 0);
        private static readonly GreedyMesher Mesher = new GreedyMesher();

        // Produced by the Task 3 GREEN run of this fixture; the exact command,
        // counts and recomputation recipe are recorded in task-3-report.md.
        private const ulong SolidHash = 0xDDFB9E0705773F21UL;
        private const ulong EmptyHash = 0xA8C7F832281A39C5UL;
        private const ulong TerrainHash = 0xBEC043606861A156UL;
        private const ulong CheckerboardHash = 0x974E1E84E4D580C6UL;
        private const ulong SingleCornerHash = 0x16F1B6539053D181UL;

        private static readonly IBlockRegistry TerrainRegistry = BlockRegistry.Parse(TerrainBlocksJson);

        private const string TerrainBlocksJson = @"[
  { ""Id"": 0, ""Name"": ""Air"",   ""Solid"": false, ""Opaque"": false, ""Hardness"": 0.0, ""AtlasIndexTop"": 0, ""AtlasIndexFront"": 0, ""AtlasIndexSide"": 0 },
  { ""Id"": 1, ""Name"": ""Stone"", ""Solid"": true,  ""Opaque"": true,  ""Hardness"": 1.5, ""AtlasIndexTop"": 1, ""AtlasIndexFront"": 1, ""AtlasIndexSide"": 1 },
  { ""Id"": 2, ""Name"": ""Dirt"",  ""Solid"": true,  ""Opaque"": true,  ""Hardness"": 0.5, ""AtlasIndexTop"": 2, ""AtlasIndexFront"": 2, ""AtlasIndexSide"": 2 },
  { ""Id"": 3, ""Name"": ""Grass"", ""Solid"": true,  ""Opaque"": true,  ""Hardness"": 0.6, ""AtlasIndexTop"": 3, ""AtlasIndexFront"": 4, ""AtlasIndexSide"": 4 }
]";

        [Test]
        public void FullySolidChunkMatchesGoldenHash()
        {
            MeshData mesh = Build(
                TestChunks.FilledSnapshot(Origin, TestChunks.Stone),
                NeighbourSnapshot.Empty,
                TestChunks.Registry);

            AssertGolden(mesh, SolidHash);
        }

        [Test]
        public void EmptyChunkMatchesGoldenHash()
        {
            MeshData mesh = Build(TestChunks.Snapshot(Origin), NeighbourSnapshot.Empty, TestChunks.Registry);

            AssertGolden(mesh, EmptyHash);
        }

        [Test]
        public void TerrainSeedFortyTwoOriginChunkMatchesGoldenHash()
        {
            ChunkSnapshot chunk = new TerrainGenerator().Generate(Origin, 42L).Snapshot();

            MeshData mesh = Build(chunk, NeighbourSnapshot.Empty, TerrainRegistry);

            AssertGolden(mesh, TerrainHash);
        }

        [Test]
        public void Checkerboard2X2X2ChunkMatchesGoldenHash()
        {
            MeshData mesh = Build(Checkerboard2X2X2(), NeighbourSnapshot.Empty, TestChunks.Registry);

            AssertGolden(mesh, CheckerboardHash);
        }

        [Test]
        public void SingleCornerBlockMatchesGoldenHash()
        {
            MeshData mesh = Build(
                TestChunks.Snapshot(Origin, (new Int3(0, 0, 0), TestChunks.Stone)),
                NeighbourSnapshot.Empty,
                TestChunks.Registry);

            AssertGolden(mesh, SingleCornerHash);
        }

        private static MeshData Build(ChunkSnapshot chunk, NeighbourSnapshot neighbours, IBlockRegistry blocks)
        {
            return Mesher.Build(chunk, neighbours, blocks);
        }

        private static ChunkSnapshot Checkerboard2X2X2()
        {
            var blocks = new List<(Int3 Local, BlockId Block)>();
            for (int z = 0; z < ChunkMath.ChunkSize; z++)
            {
                for (int y = 0; y < ChunkMath.ChunkSize; y++)
                {
                    for (int x = 0; x < ChunkMath.ChunkSize; x++)
                    {
                        if (((((x >> 1) + (y >> 1)) + (z >> 1)) & 1) == 0)
                        {
                            blocks.Add((new Int3(x, y, z), TestChunks.Stone));
                        }
                    }
                }
            }

            return TestChunks.Snapshot(Origin, blocks.ToArray());
        }

        private static void AssertGolden(MeshData mesh, ulong expected)
        {
            ulong actual = MeshHash.Hash(mesh);
            Assert.That(
                actual,
                Is.EqualTo(expected),
                $"actual hash 0x{actual:X16} with {mesh.VertexCount} vertices and {mesh.IndexCount} indices");
            mesh.Release();
        }
    }
}

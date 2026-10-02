using System;
using System.Collections.Generic;
using Cubeglass.Voxel;
using NUnit.Framework;

namespace Cubeglass.Mesh.Tests
{
    /// <summary>
    /// Allocation gate for meshing (S3 WI5): after the pool has been warmed,
    /// <see cref="IChunkMesher.Build"/> plus <see cref="MeshData.Release"/>
    /// must allocate nothing on the calling thread.
    /// </summary>
    /// <remarks>
    /// Every gate warms the pool with <see cref="WarmupIterations"/> complete
    /// build/release cycles, then snapshots
    /// <see cref="GC.GetAllocatedBytesForCurrentThread"/> around
    /// <see cref="MeasuredIterations"/> cycles. No NUnit call sits inside a
    /// measured region and every measured result feeds a checked sink. The
    /// gate's sensitivity is proven in <c>docs/notes/s3-gate.md</c>: with a
    /// temporary allocation inside a measured loop the run fails and reports
    /// the probe bytes; the probe is then removed.
    /// </remarks>
    [TestFixture]
    public sealed class AllocationTests
    {
        private const int WarmupIterations = 100;
        private const int MeasuredIterations = 1_000;

        private static readonly ChunkCoord Origin = new ChunkCoord(0, 0, 0);
        private static readonly GreedyMesher Greedy = new GreedyMesher();
        private static readonly CulledMesher Reference = new CulledMesher();

        [Test]
        public void GreedyTerrainBuildAndReleaseAllocateNothingAfterWarmup()
        {
            ChunkSnapshot chunk = new TerrainGenerator().Generate(Origin, 42L).Snapshot();

            AssertNoAllocation(Greedy, chunk, "greedy terrain chunk");
        }

        [Test]
        public void GreedyCheckerboardBuildAndReleaseAllocateNothingAfterWarmup()
        {
            AssertNoAllocation(Greedy, Checkerboard2X2X2(), "greedy checkerboard chunk");
        }

        [Test]
        public void CulledTerrainBuildAndReleaseAllocateNothingAfterWarmup()
        {
            ChunkSnapshot chunk = new TerrainGenerator().Generate(Origin, 42L).Snapshot();

            AssertNoAllocation(Reference, chunk, "culled terrain chunk");
        }

        [Test]
        public void ThrowingBuildReturnsRentedBuffersToThePool()
        {
            ChunkSnapshot unknownBlock = TestChunks.Snapshot(Origin, (new Int3(0, 0, 0), new BlockId(99)));

            Assert.Throws<KeyNotFoundException>(
                () => Greedy.Build(unknownBlock, NeighbourSnapshot.Empty, TestChunks.Registry));
            Assert.Throws<KeyNotFoundException>(
                () => Reference.Build(unknownBlock, NeighbourSnapshot.Empty, TestChunks.Registry));

            AssertNoAllocation(Greedy, TestChunks.FilledSnapshot(Origin, TestChunks.Stone), "greedy solid chunk after a throwing build");
        }

        private static void AssertNoAllocation(IChunkMesher mesher, ChunkSnapshot chunk, string label)
        {
            long warmupSink = Run(mesher, chunk, WarmupIterations);
            Assert.That(warmupSink, Is.GreaterThan(0L), "the warm-up must mesh and release chunks");

            long before = GC.GetAllocatedBytesForCurrentThread();
            long measuredSink = Run(mesher, chunk, MeasuredIterations);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.That(measuredSink, Is.GreaterThan(warmupSink), "the measured loop must run the build/release cycles");
            Assert.That(
                allocated,
                Is.Zero,
                $"{label} allocated {allocated} bytes over {MeasuredIterations} build/release cycles");
        }

        private static long Run(IChunkMesher mesher, ChunkSnapshot chunk, int iterations)
        {
            long sink = 0L;
            for (int i = 0; i < iterations; i++)
            {
                MeshData mesh = mesher.Build(chunk, NeighbourSnapshot.Empty, TestChunks.Registry);
                sink += mesh.VertexCount + mesh.IndexCount;
                mesh.Release();
            }

            return sink;
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
    }
}

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
    /// throw-path gate compares <see cref="MeshBufferPool.OutstandingBuffers"/>
    /// across a failed build and then measures a single following cycle: the
    /// counter detects a one-shot leak deterministically even when a larger
    /// pooled array would otherwise hide it from the allocation snapshot. The
    /// gate's sensitivity is proven in <c>docs/notes/s3-gate.md</c>: with a
    /// temporary probe the run fails and reports the leak; the probe is then
    /// removed.
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
            ChunkSnapshot solid = TestChunks.FilledSnapshot(Origin, TestChunks.Stone);
            MeshBufferPool pool = MeshBufferPool.Shared;

            // Warm up FIRST: a leak on the throw path shows up as an
            // outstanding buffer, and warming up after the throw would
            // silently refill the leaked bucket before it could be observed.
            Run(Greedy, solid, WarmupIterations);
            AssertThrowReturnsEveryRent(Greedy, unknownBlock, pool, "greedy");
            AssertSingleCycleAllocatesNothing(Greedy, solid, "greedy");

            Run(Reference, solid, WarmupIterations);
            AssertThrowReturnsEveryRent(Reference, unknownBlock, pool, "culled");
            AssertSingleCycleAllocatesNothing(Reference, solid, "culled");
        }

        private static void AssertThrowReturnsEveryRent(
            IChunkMesher mesher,
            ChunkSnapshot unknownBlock,
            MeshBufferPool pool,
            string label)
        {
            long outstandingBefore = pool.OutstandingBuffers;

            Assert.Throws<KeyNotFoundException>(
                () => mesher.Build(unknownBlock, NeighbourSnapshot.Empty, TestChunks.Registry));

            long leaked = pool.OutstandingBuffers - outstandingBefore;
            Assert.That(
                leaked,
                Is.Zero,
                $"the {label} throw path leaked {leaked} rented buffer(s)");
        }

        private static void AssertSingleCycleAllocatesNothing(IChunkMesher mesher, ChunkSnapshot chunk, string label)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            long sink = Run(mesher, chunk, 1);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.That(sink, Is.GreaterThan(0L), $"the single measured {label} build must mesh vertices");
            Assert.That(
                allocated,
                Is.Zero,
                $"the {label} throw path left the pool dry: the first build after it allocated {allocated} bytes");
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

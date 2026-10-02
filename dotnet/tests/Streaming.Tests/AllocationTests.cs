using System;
using System.Collections.Generic;
using Cubeglass.CoreMath;
using NUnit.Framework;

namespace Cubeglass.Streaming.Tests
{
    [TestFixture]
    public sealed class AllocationTests
    {
        private const int WarmupIterations = 1_000;
        private const int MeasuredIterations = 2_000;

        [Test]
        public void UpdateAllocatesNothingAfterWarmup()
        {
            var scheduler = new ChunkStreamingScheduler(
                new StreamingConfig
                {
                    ViewDistanceChunks = 4,
                    UnloadHysteresis = 2,
                    MaxLoadsPerFrame = 4,
                    MaxUnloadsPerFrame = 4,
                    MaxMeshUploadsPerFrame = 4,
                    VerticalRadiusChunks = 0,
                },
                42L);

            long warmupSink = RunOscillation(scheduler, WarmupIterations);
            Assert.That(warmupSink, Is.GreaterThan(0L), "the warm-up must emit actions");

            long before = GC.GetAllocatedBytesForCurrentThread();
            long measuredSink = RunOscillation(scheduler, MeasuredIterations);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.That(measuredSink, Is.GreaterThan(0L), "the measured loop must emit actions");
            Assert.That(
                allocated,
                Is.Zero,
                $"Update allocated {allocated} bytes over {MeasuredIterations} frames");
        }

        private static long RunOscillation(ChunkStreamingScheduler scheduler, int iterations)
        {
            long sink = 0L;
            for (int i = 0; i < iterations; i++)
            {
                int leg = (i / 40) % 2;
                int chunkX = leg == 0 ? 0 : -6;
                var position = new Vec3((chunkX * 16) + 8.0, 8.0, 8.0);
                IReadOnlyList<StreamingAction> actions = scheduler.Update(position);
                sink += actions.Count;
            }

            return sink;
        }
    }
}

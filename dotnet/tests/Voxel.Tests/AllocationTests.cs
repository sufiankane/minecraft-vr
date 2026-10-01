using System;
using System.Diagnostics.CodeAnalysis;
using Cubeglass.CoreMath;
using NUnit.Framework;

namespace Cubeglass.Voxel.Tests
{
    /// <summary>
    /// Allocation gate for the voxel hot paths (S2-WI7): <see cref="IRaycaster.Cast"/>,
    /// <see cref="IWorld.Get"/> and <see cref="IWorld.Apply"/> must not allocate.
    /// Every measured region runs after a warm-up, exercises a loaded generated
    /// chunk, contains no NUnit calls and is bracketed by
    /// <see cref="GC.GetAllocatedBytesForCurrentThread"/> snapshots.
    /// </summary>
    [TestFixture]
    public sealed class AllocationTests
    {
        private const int WarmupIterations = 1_000;
        private const int MeasuredIterations = 100_000;
        private const float MaxDistance = 32f;

        private static readonly BlockId Stone = new BlockId(1);
        private static readonly BlockId Air = BlockId.Air;

        [Test]
        [SuppressMessage(
            "Performance",
            "CA1859:Use concrete types when possible for improved performance",
            Justification = "The gate must exercise the frozen IRaycaster/IWorld interfaces consumers call.")]
        public void RaycastCastAllocatesNothing()
        {
            IWorld world = LoadedGeneratedChunk();
            IRaycaster raycaster = new DdaRaycaster();
            Ray[] rays = BuildRays(MeasuredIterations);

            float warmupSink = CastAll(raycaster, world, rays, WarmupIterations);

            long before = GC.GetAllocatedBytesForCurrentThread();
            float measuredSink = CastAll(raycaster, world, rays, MeasuredIterations);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.That(warmupSink, Is.GreaterThan(0f), "the warm-up must hit solid cells");
            Assert.That(measuredSink, Is.GreaterThan(warmupSink), "the measured loop must run the casts");
            Assert.That(
                allocated,
                Is.EqualTo(0L),
                $"DdaRaycaster.Cast allocated {allocated} bytes over {MeasuredIterations} iterations");
        }

        [Test]
        [SuppressMessage(
            "Performance",
            "CA1859:Use concrete types when possible for improved performance",
            Justification = "The gate must exercise the frozen IWorld interface consumers call.")]
        public void WorldGetAndApplyAllocateNothing()
        {
            IWorld world = LoadedGeneratedChunk();
            var cell = new Int3(0, 0, 0);
            BlockId first = world.Get(cell);
            BlockId second = first == Air ? Stone : Air;

            long warmupSink = EditAll(world, cell, first, second, WarmupIterations);

            long before = GC.GetAllocatedBytesForCurrentThread();
            long measuredSink = EditAll(world, cell, first, second, MeasuredIterations);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.That(warmupSink, Is.GreaterThan(0L), "the warm-up must read and apply edits");
            Assert.That(measuredSink, Is.GreaterThan(warmupSink), "the measured loop must run the edits");
            Assert.That(
                allocated,
                Is.EqualTo(0L),
                $"World.Get/Apply allocated {allocated} bytes over {MeasuredIterations} iterations");
        }

        private static World LoadedGeneratedChunk()
        {
            var world = new World();
            world.LoadChunk(new TerrainGenerator().Generate(new ChunkCoord(0, 0, 0), 42L));
            return world;
        }

        private static Ray[] BuildRays(int count)
        {
            var rays = new Ray[count];
            for (int i = 0; i < count; i++)
            {
                float x = (i % ChunkMath.ChunkSize) + 0.5f;
                float z = ((i / ChunkMath.ChunkSize) % ChunkMath.ChunkSize) + 0.5f;
                rays[i] = new Ray(new Vec3(x, 20.0, z), new Vec3(0.0, -1.0, 0.0));
            }

            return rays;
        }

        private static float CastAll(IRaycaster raycaster, IWorld world, Ray[] rays, int iterations)
        {
            float sink = 0f;
            for (int i = 0; i < iterations; i++)
            {
                RayHit? hit = raycaster.Cast(world, rays[i % rays.Length], MaxDistance);
                if (hit.HasValue)
                {
                    RayHit value = hit.GetValueOrDefault();
                    sink += value.Distance + value.Cell.X + value.Normal.Y + value.Block.Value;
                }
            }

            return sink;
        }

        private static long EditAll(IWorld world, Int3 cell, BlockId first, BlockId second, int iterations)
        {
            BlockId current = first;
            BlockId next = second;
            long sink = 0L;
            for (int i = 0; i < iterations; i++)
            {
                sink += world.Get(cell).Value;
                sink += (int)world.Apply(new EditCommand(cell, current, next, i));
                BlockId swap = current;
                current = next;
                next = swap;
            }

            return sink;
        }
    }
}

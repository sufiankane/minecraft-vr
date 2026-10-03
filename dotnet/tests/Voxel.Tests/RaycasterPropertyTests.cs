using System;
using Cubeglass.CoreMath;
using FsCheck;
using FsCheck.Fluent;
using Microsoft.FSharp.Core;
using NUnit.Framework;

namespace Cubeglass.Voxel.Tests
{
    // FsCheck 3.4: a fixed Rnd seed makes each run reproducible; failures
    // report the replay seed so they can be repeated. Origins are bounded to
    // [-15, 15] world units and directions to a non-unit fixed point so the
    // generated rays stay inside the three loaded chunks for at least part of
    // their path.
    [TestFixture]
    public sealed class RaycasterPropertyTests
    {
        private const ulong Seed = 20261001UL;
        private const int MaxTests = 500;
        private const float MaxDistance = 12f;

        private static readonly BlockId Stone = new BlockId(1);
        private static readonly BlockId Dirt = new BlockId(2);
        private static readonly BlockId Grass = new BlockId(3);

        private static readonly (double X, double Y, double Z)[] FiniteDirections =
        {
            (1.0, 0.0, 0.0),
            (-1.0, 0.0, 0.0),
            (0.0, 1.0, 0.0),
            (0.0, -1.0, 0.0),
            (0.0, 0.0, 1.0),
            (0.0, 0.0, -1.0),
            (1.0, 1.0, 0.0),
            (-1.0, -1.0, 0.0),
            (1.0, 0.0, 1.0),
            (-1.0, 0.0, -1.0),
            (0.0, 1.0, 1.0),
            (0.0, -1.0, -1.0),
            (1.0, 1.0, 1.0),
            (-1.0, -1.0, -1.0),
        };

        [Test]
        public void EveryReportedHitIsSolidInRangeAndFacesTheRayOrigin()
        {
            int hits = 0;
            Property property = Prop.ForAll(
                CaseArbitrary(),
                (RayCase c) =>
                {
                    World world = BuildWorld(c.WorldSeed);
                    var ray = new Ray(
                        new Vec3(c.OriginX, c.OriginY, c.OriginZ),
                        new Vec3(c.DirectionX, c.DirectionY, c.DirectionZ));

                    RayHit? result = new DdaRaycaster().Cast(world, ray, MaxDistance);
                    if (!result.HasValue)
                    {
                        return true;
                    }

                    hits++;
                    RayHit hit = result.Value;
                    if (hit.Block == BlockId.Air)
                    {
                        return false;
                    }

                    if (!world.IsLoaded(hit.Cell) || world.Get(hit.Cell) != hit.Block)
                    {
                        return false;
                    }

                    // maxDistance is inclusive; distance zero is valid for an
                    // inside hit (zero normal) and for an entry hit whose origin
                    // lies exactly on the entry face (non-zero normal).
                    if (hit.Distance < 0f || hit.Distance > MaxDistance)
                    {
                        return false;
                    }

                    if (hit.Normal == Int3.Zero)
                    {
                        return hit.Distance == 0f && FloorCell(ray.Origin) == hit.Cell;
                    }

                    var normal = new Vec3(hit.Normal.X, hit.Normal.Y, hit.Normal.Z);
                    return Vec3.Dot(normal, Vec3.Normalized(ray.Direction)) < 0.0;
                });

            Check.One("raycast hit invariants", DeterministicConfig(), property);
            Assert.That(hits, Is.GreaterThan(0), "the property run must observe at least one hit");
        }

        [Test]
        public void AZeroDirectionNeverProducesAHit()
        {
            Property property = Prop.ForAll(
                CaseArbitrary(),
                (RayCase c) =>
                {
                    var world = new World();
                    var ray = new Ray(
                        new Vec3(c.OriginX, c.OriginY, c.OriginZ),
                        new Vec3(0.0, 0.0, 0.0));

                    return !new DdaRaycaster().Cast(world, ray, MaxDistance).HasValue;
                });

            Check.One("zero direction", DeterministicConfig(), property);
        }

        [Test]
        public void EveryFiniteInputTerminates()
        {
            // Terminates is the assertion: the reviewed regression hung on an
            // axis-aligned ray with an infinite distance, so this corpus pins
            // the axis-aligned direction class with finite distances.
            int completed = 0;
            Property property = Prop.ForAll(
                FiniteCaseArbitrary(),
                (FiniteRayCase c) =>
                {
                    World world = BuildWorld(c.WorldSeed);
                    var ray = new Ray(
                        new Vec3(c.OriginX, c.OriginY, c.OriginZ),
                        new Vec3(c.DirectionX, c.DirectionY, c.DirectionZ));

                    _ = new DdaRaycaster().Cast(world, ray, c.MaxDistance);
                    completed++;
                    return true;
                });

            Check.One("finite inputs terminate", DeterministicConfig(), property);
            Assert.That(completed, Is.EqualTo(MaxTests), "every generated finite case must complete");
        }

        [Test]
        public void EveryHitIsTheNearestSolidEntryAlongTheRay()
        {
            // Minimality oracle: enumerate every loaded solid cell and compare
            // the hit distance with the smallest exact slab-entry distance. A
            // traversal that skipped a nearer solid would report a distance
            // strictly above the oracle minimum.
            int hits = 0;
            Property property = Prop.ForAll(
                CaseArbitrary(),
                (RayCase c) =>
                {
                    World world = BuildWorld(c.WorldSeed);
                    var ray = new Ray(
                        new Vec3(c.OriginX, c.OriginY, c.OriginZ),
                        new Vec3(c.DirectionX, c.DirectionY, c.DirectionZ));

                    RayHit? result = new DdaRaycaster().Cast(world, ray, MaxDistance);
                    if (!result.HasValue)
                    {
                        return true;
                    }

                    hits++;
                    double nearest = NearestSolidEntryDistance(world, ray);
                    return result.Value.Distance <= nearest + 1e-4;
                });

            Check.One("nearest hit minimality", DeterministicConfig(), property);
            Assert.That(hits, Is.GreaterThan(0), "the property run must observe at least one hit");
        }

        private static Int3 FloorCell(Vec3 origin)
        {
            return new Int3(
                (int)Math.Floor(origin.X),
                (int)Math.Floor(origin.Y),
                (int)Math.Floor(origin.Z));
        }

        /// <summary>
        /// Smallest exact entry distance over every loaded solid cell, or
        /// positive infinity when the ray enters none. Cells the ray only
        /// grazes (entry and exit coincide) are excluded, mirroring the DDA's
        /// interior-entry semantics; half-open slabs match the floor-cell
        /// convention for axis-parallel rays.
        /// </summary>
        private static double NearestSolidEntryDistance(World world, Ray ray)
        {
            Vec3 origin = ray.Origin;
            Vec3 direction = Vec3.Normalized(ray.Direction);
            if (direction.X == 0.0 && direction.Y == 0.0 && direction.Z == 0.0)
            {
                return double.PositiveInfinity;
            }

            double nearest = double.PositiveInfinity;
            for (int chunkX = -1; chunkX <= 1; chunkX++)
            {
                Chunk? chunk = world.TryGetChunk(new ChunkCoord(chunkX, 0, 0));
                if (chunk is null)
                {
                    continue;
                }

                for (int z = 0; z < ChunkMath.ChunkSize; z++)
                {
                    for (int y = 0; y < ChunkMath.ChunkSize; y++)
                    {
                        for (int x = 0; x < ChunkMath.ChunkSize; x++)
                        {
                            if (chunk.Get(new Int3(x, y, z)) == BlockId.Air)
                            {
                                continue;
                            }

                            var cell = new Int3(
                                (chunkX * ChunkMath.ChunkSize) + x,
                                y,
                                z);
                            double entry = CellEntryDistance(cell, origin, direction);
                            if (entry < nearest)
                            {
                                nearest = entry;
                            }
                        }
                    }
                }
            }

            return nearest;
        }

        private static double CellEntryDistance(Int3 cell, Vec3 origin, Vec3 direction)
        {
            double tEnter = double.NegativeInfinity;
            double tExit = double.PositiveInfinity;
            for (int axis = 0; axis < 3; axis++)
            {
                double o = Component(origin, axis);
                double d = Component(direction, axis);
                double min = axis switch
                {
                    0 => cell.X,
                    1 => cell.Y,
                    _ => cell.Z,
                };
                double max = min + 1.0;

                if (d == 0.0)
                {
                    if (!(o >= min && o < max))
                    {
                        return double.PositiveInfinity;
                    }

                    continue;
                }

                double near = (min - o) / d;
                double far = (max - o) / d;
                if (near > far)
                {
                    (near, far) = (far, near);
                }

                if (near > tEnter)
                {
                    tEnter = near;
                }

                if (far < tExit)
                {
                    tExit = far;
                }
            }

            if (tEnter >= tExit || tExit <= 0.0)
            {
                return double.PositiveInfinity;
            }

            return tEnter > 0.0 ? tEnter : 0.0;
        }

        private static double Component(Vec3 v, int axis)
        {
            return axis switch
            {
                0 => v.X,
                1 => v.Y,
                _ => v.Z,
            };
        }

        private static World BuildWorld(int worldSeed)
        {
            var world = new World();
            foreach (int chunkX in new[] { -1, 0, 1 })
            {
                world.LoadChunk(BuildFilledChunk(new ChunkCoord(chunkX, 0, 0), worldSeed + (chunkX * 101)));
            }

            return world;
        }

        private static Chunk BuildFilledChunk(ChunkCoord coord, int worldSeed)
        {
            var chunk = new Chunk(coord);
            for (int z = 0; z < ChunkMath.ChunkSize; z++)
            {
                for (int y = 0; y < ChunkMath.ChunkSize; y++)
                {
                    for (int x = 0; x < ChunkMath.ChunkSize; x++)
                    {
                        int hash = (x * 73856093) ^ (y * 19349663) ^ (z * 83492791) ^ worldSeed;
                        if ((hash & 7) == 0)
                        {
                            BlockId block = ((hash >> 3) & 3) switch
                            {
                                0 => Stone,
                                1 => Dirt,
                                _ => Grass,
                            };
                            chunk.Set(new Int3(x, y, z), block);
                        }
                    }
                }
            }

            return chunk;
        }

        private static Arbitrary<RayCase> CaseArbitrary()
        {
            return Arb.From(
                from ox in Gen.Choose(-1500, 1500)
                from oy in Gen.Choose(-200, 400)
                from oz in Gen.Choose(-1500, 1500)
                from dx in Gen.Choose(-1000, 1000)
                from dy in Gen.Choose(-1000, 1000)
                from dz in Gen.Choose(-1000, 1000)
                from worldSeed in Gen.Choose(0, 1000000)
                select new RayCase(
                    ox / 100.0,
                    oy / 100.0,
                    oz / 100.0,
                    dx / 1000.0,
                    dy / 1000.0,
                    dz / 1000.0,
                    worldSeed));
        }

        private static Arbitrary<FiniteRayCase> FiniteCaseArbitrary()
        {
            return Arb.From(
                from ox in Gen.Choose(-1500, 1500)
                from oy in Gen.Choose(-200, 400)
                from oz in Gen.Choose(-1500, 1500)
                from directionIndex in Gen.Choose(0, FiniteDirections.Length - 1)
                from distance in Gen.Choose(0, 6400)
                from worldSeed in Gen.Choose(0, 1000000)
                select new FiniteRayCase(
                    ox / 100.0,
                    oy / 100.0,
                    oz / 100.0,
                    FiniteDirections[directionIndex].X,
                    FiniteDirections[directionIndex].Y,
                    FiniteDirections[directionIndex].Z,
                    distance / 100f,
                    worldSeed));
        }

        private static Config DeterministicConfig()
        {
            Replay replay = new Replay(new Rnd(Seed), FSharpOption<int>.None);
            return Config.QuickThrowOnFailure
                .WithMaxTest(MaxTests)
                .WithReplay(FSharpOption<Replay>.Some(replay));
        }

        private readonly record struct RayCase(
            double OriginX,
            double OriginY,
            double OriginZ,
            double DirectionX,
            double DirectionY,
            double DirectionZ,
            int WorldSeed);

        private readonly record struct FiniteRayCase(
            double OriginX,
            double OriginY,
            double OriginZ,
            double DirectionX,
            double DirectionY,
            double DirectionZ,
            float MaxDistance,
            int WorldSeed);
    }
}

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

        private static Int3 FloorCell(Vec3 origin)
        {
            return new Int3(
                (int)Math.Floor(origin.X),
                (int)Math.Floor(origin.Y),
                (int)Math.Floor(origin.Z));
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
    }
}

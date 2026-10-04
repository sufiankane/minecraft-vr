using System;
using System.Collections.Generic;
using System.Diagnostics;
using Cubeglass.CoreMath;
using FsCheck;
using FsCheck.Fluent;
using Microsoft.FSharp.Core;
using NUnit.Framework;

namespace Cubeglass.Voxel.Tests
{
    // Random-box properties: the iterating collision query must agree with an
    // independent element-wise model, and placement must be exactly the
    // half-open cell-cube-versus-player-box test. Both runs must observe hits
    // and misses so an always-true or always-false implementation cannot pass.
    [TestFixture]
    public sealed class CollisionPropertyTests
    {
        private const ulong Seed = 20261001UL;
        private const int MaxTests = 500;

        private static readonly BlockId Stone = new BlockId(1);

        [Test]
        public void OverlapsMatchesTheElementWiseModel()
        {
            int hits = 0;
            int misses = 0;
            Property property = Prop.ForAll(
                CaseArbitrary(),
                (CollisionCase testCase) =>
                {
                    World world = BuildWorld(testCase.Solids);
                    bool actual = VoxelCollision.Overlaps(world, testCase.Box);
                    bool expected = ModelOverlaps(testCase.Solids, testCase.Box);
                    if (actual)
                    {
                        hits++;
                    }
                    else
                    {
                        misses++;
                    }

                    return actual == expected;
                });

            Check.One("collision overlaps versus model", DeterministicConfig(), property);
            Assert.That(hits, Is.GreaterThan(0), "the property never observed an overlap");
            Assert.That(misses, Is.GreaterThan(0), "the property never observed a miss");
        }

        [Test]
        public void CanPlaceMatchesTheCellCubeModel()
        {
            int blocked = 0;
            int allowed = 0;
            Property property = Prop.ForAll(
                CaseArbitrary(),
                (CollisionCase testCase) =>
                {
                    var world = new World();
                    bool actual = VoxelCollision.CanPlace(world, testCase.Probe, testCase.Box);
                    bool expected = !CellCubeOverlaps(testCase.Probe, testCase.Box);
                    if (actual)
                    {
                        allowed++;
                    }
                    else
                    {
                        blocked++;
                    }

                    return actual == expected;
                });

            Check.One("placement versus model", DeterministicConfig(), property);
            Assert.That(blocked, Is.GreaterThan(0), "the property never observed a blocked placement");
            Assert.That(allowed, Is.GreaterThan(0), "the property never observed an allowed placement");
        }

        [Test]
        public void ExtremeBoxesTerminatePromptly()
        {
            int returned = 0;
            int rejected = 0;
            long worstTicks = 0L;
            Property property = Prop.ForAll(
                ExtremeCaseArbitrary(),
                (Aabb box) =>
                {
                    World world = BuildWorld(ExtremeSolids);
                    var stopwatch = Stopwatch.StartNew();
                    try
                    {
                        VoxelCollision.Overlaps(world, box);
                        returned++;
                    }
                    catch (ArgumentException)
                    {
                        rejected++;
                    }

                    stopwatch.Stop();
                    worstTicks = Math.Max(worstTicks, stopwatch.ElapsedTicks);
                    return true;
                });

            Check.One("extreme boxes terminate promptly", DeterministicConfig(), property);
            Assert.That(returned, Is.GreaterThan(0), "the property never observed an accepted box");
            Assert.That(rejected, Is.GreaterThan(0), "the property never observed a rejected box");
            Assert.That(worstTicks, Is.LessThan(TimeSpan.FromSeconds(1).Ticks), "a call exceeded the prompt bound");
        }

        [Test]
        public void FiniteExtremeBoxesMatchTheCellCubeModel()
        {
            int hits = 0;
            int misses = 0;
            Property property = Prop.ForAll(
                FiniteExtremeCaseArbitrary(),
                (Aabb box) =>
                {
                    World world = BuildWorld(ExtremeSolids);
                    bool actual = VoxelCollision.Overlaps(world, box);
                    bool expected = ModelOverlaps(ExtremeSolids, box);
                    if (actual)
                    {
                        hits++;
                    }
                    else
                    {
                        misses++;
                    }

                    return actual == expected;
                });

            Check.One("finite extreme boxes versus model", DeterministicConfig(), property);
            Assert.That(hits, Is.GreaterThan(0), "the property never observed an overlap at an extreme coordinate");
            Assert.That(misses, Is.GreaterThan(0), "the property never observed a miss at an extreme coordinate");
        }

        private static readonly Int3[] ExtremeSolids =
        {
            new Int3(int.MaxValue, 0, 0),
            new Int3(int.MinValue, 0, 0),
        };

        private static Arbitrary<Aabb> ExtremeCaseArbitrary()
        {
            return Arb.From(
                from minX in Gen.Choose(0, 12)
                from maxX in Gen.Choose(0, 12)
                from minY in Gen.Choose(0, 12)
                from maxY in Gen.Choose(0, 12)
                from minZ in Gen.Choose(0, 12)
                from maxZ in Gen.Choose(0, 12)
                select ExtremeBox(minX, maxX, minY, maxY, minZ, maxZ));
        }

        private static Arbitrary<Aabb> FiniteExtremeCaseArbitrary()
        {
            return Arb.From(
                from which in Gen.Choose(0, 1)
                from offsetX in Gen.Choose(-4, 4)
                from offsetY in Gen.Choose(-4, 4)
                from offsetZ in Gen.Choose(-4, 4)
                from extentX in Gen.Choose(0, 6)
                from extentY in Gen.Choose(0, 6)
                from extentZ in Gen.Choose(0, 6)
                select FiniteExtremeBox(which, offsetX, offsetY, offsetZ, extentX, extentY, extentZ));
        }

        private static Aabb ExtremeBox(int minX, int maxX, int minY, int maxY, int minZ, int maxZ)
        {
            double x0 = ExtremeCoordinate(minX);
            double x1 = ExtremeCoordinate(maxX);
            double y0 = ExtremeCoordinate(minY);
            double y1 = ExtremeCoordinate(maxY);
            double z0 = ExtremeCoordinate(minZ);
            double z1 = ExtremeCoordinate(maxZ);
            return new Aabb(
                new Vec3(Math.Min(x0, x1), Math.Min(y0, y1), Math.Min(z0, z1)),
                new Vec3(Math.Max(x0, x1), Math.Max(y0, y1), Math.Max(z0, z1)));
        }

        private static Aabb FiniteExtremeBox(
            int which,
            int offsetX,
            int offsetY,
            int offsetZ,
            int extentX,
            int extentY,
            int extentZ)
        {
            double baseX = which == 0 ? (double)int.MaxValue : (double)int.MinValue;
            double minX = baseX + (offsetX / 4.0);
            double minY = offsetY / 4.0;
            double minZ = offsetZ / 4.0;
            return new Aabb(
                new Vec3(minX, minY, minZ),
                new Vec3(minX + (extentX / 4.0), minY + (extentY / 4.0), minZ + (extentZ / 4.0)));
        }

        private static double ExtremeCoordinate(int selector)
        {
            switch (selector)
            {
                case 0:
                    return double.NegativeInfinity;
                case 1:
                    return -1e12;
                case 2:
                    return (double)int.MinValue - 2.0;
                case 3:
                    return int.MinValue;
                case 4:
                    return (double)int.MinValue + 1.0;
                case 5:
                    return -1.0;
                case 6:
                    return 0.0;
                case 7:
                    return 1.0;
                case 8:
                    return (double)int.MaxValue - 2.0;
                case 9:
                    return int.MaxValue;
                case 10:
                    return (double)int.MaxValue + 1.0;
                case 11:
                    return 1e12;
                default:
                    return double.PositiveInfinity;
            }
        }

        private static bool ModelOverlaps(Int3[] solids, Aabb box)
        {
            for (int i = 0; i < solids.Length; i++)
            {
                if (CellCubeOverlaps(solids[i], box))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool CellCubeOverlaps(Int3 cell, Aabb box)
        {
            var cube = new Aabb(
                new Vec3(cell.X, cell.Y, cell.Z),
                new Vec3(cell.X + 1.0, cell.Y + 1.0, cell.Z + 1.0));
            return cube.Overlaps(box);
        }

        private static World BuildWorld(Int3[] cells)
        {
            var chunks = new Dictionary<ChunkCoord, Chunk>();
            foreach (Int3 cell in cells)
            {
                ChunkCoord coord = ChunkMath.ToChunk(cell);
                if (!chunks.TryGetValue(coord, out Chunk? chunk))
                {
                    chunk = new Chunk(coord);
                    chunks[coord] = chunk;
                }

                chunk.Set(ChunkMath.ToLocal(cell), Stone);
            }

            var world = new World();
            foreach (Chunk chunk in chunks.Values)
            {
                world.LoadChunk(chunk);
            }

            return world;
        }

        private static Arbitrary<CollisionCase> CaseArbitrary()
        {
            Gen<Int3> cells =
                from x in Gen.Choose(-6, 6)
                from y in Gen.Choose(-2, 6)
                from z in Gen.Choose(-6, 6)
                select new Int3(x, y, z);

            return Arb.From(
                from minX in Gen.Choose(-28, 28)
                from minY in Gen.Choose(-8, 28)
                from minZ in Gen.Choose(-28, 28)
                from extentX in Gen.Choose(1, 24)
                from extentY in Gen.Choose(1, 24)
                from extentZ in Gen.Choose(1, 24)
                from probeX in Gen.Choose(-8, 8)
                from probeY in Gen.Choose(-3, 8)
                from probeZ in Gen.Choose(-8, 8)
                from solids in Gen.ListOf(cells)
                select new CollisionCase(
                    new Aabb(
                        new Vec3(Quarter(minX), Quarter(minY), Quarter(minZ)),
                        new Vec3(Quarter(minX + extentX), Quarter(minY + extentY), Quarter(minZ + extentZ))),
                    solids.ToArray(),
                    new Int3(probeX, probeY, probeZ)));
        }

        private static double Quarter(int units)
        {
            return units / 4.0;
        }

        private static Config DeterministicConfig()
        {
            Replay replay = new Replay(new Rnd(Seed), FSharpOption<int>.None);
            return Config.QuickThrowOnFailure
                .WithMaxTest(MaxTests)
                .WithReplay(FSharpOption<Replay>.Some(replay));
        }

        private readonly struct CollisionCase
        {
            internal CollisionCase(Aabb box, Int3[] solids, Int3 probe)
            {
                Box = box;
                Solids = solids;
                Probe = probe;
            }

            internal Aabb Box { get; }

            internal Int3[] Solids { get; }

            internal Int3 Probe { get; }
        }
    }
}

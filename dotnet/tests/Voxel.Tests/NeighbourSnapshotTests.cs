using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace Cubeglass.Voxel.Tests
{
    [TestFixture]
    public sealed class NeighbourSnapshotTests
    {
        private static readonly ChunkCoord Origin = new ChunkCoord(0, 0, 0);
        private static readonly BlockId Stone = new BlockId(1);
        private static readonly BlockId Dirt = new BlockId(2);
        private static readonly Int3 LocalMin = new Int3(0, 0, 0);
        private static readonly Int3 LocalMax = new Int3(15, 15, 15);

        [Test]
        public void GetResolvesAllTwentySixDirections()
        {
            World world = CreateWorldWithEveryNeighbour(out Dictionary<Int3, ChunkCoord> neighbours);
            NeighbourSnapshot snapshot = world.CreateNeighbourSnapshot(Origin);

            foreach (KeyValuePair<Int3, ChunkCoord> entry in neighbours)
            {
                ChunkSnapshot? chunk = snapshot.Get(entry.Key);
                Assert.That(chunk, Is.Not.Null, $"direction {entry.Key} must resolve");
                Assert.That(chunk!.Coord, Is.EqualTo(entry.Value));
            }
        }

        [Test]
        public void GetCellResolvesThroughTheRightNeighbour()
        {
            World world = CreateWorldWithEveryNeighbour(out Dictionary<Int3, ChunkCoord> neighbours);
            NeighbourSnapshot snapshot = world.CreateNeighbourSnapshot(Origin);

            foreach (KeyValuePair<Int3, ChunkCoord> entry in neighbours)
            {
                Int3 minCell = ChunkMath.ToWorld(entry.Value, LocalMin);
                Int3 maxCell = ChunkMath.ToWorld(entry.Value, LocalMax);

                Assert.That(
                    snapshot.GetCell(minCell),
                    Is.EqualTo(Stone),
                    $"cell {minCell} must read from neighbour {entry.Key}");
                Assert.That(
                    snapshot.GetCell(maxCell),
                    Is.EqualTo(Dirt),
                    $"cell {maxCell} must read from neighbour {entry.Key}");
            }
        }

        [Test]
        public void MissingNeighboursReadAsAir()
        {
            var world = new World();
            world.LoadChunk(new Chunk(Origin));
            NeighbourSnapshot snapshot = world.CreateNeighbourSnapshot(Origin);

            for (int z = -1; z <= 1; z++)
            {
                for (int y = -1; y <= 1; y++)
                {
                    for (int x = -1; x <= 1; x++)
                    {
                        if (x == 0 && y == 0 && z == 0)
                        {
                            continue;
                        }

                        var direction = new Int3(x, y, z);
                        Assert.That(snapshot.Get(direction), Is.Null, $"direction {direction} is unloaded");
                        Assert.That(
                            snapshot.GetCell(DirectionCell(direction, LocalMin)),
                            Is.EqualTo(BlockId.Air));
                    }
                }
            }
        }

        [Test]
        public void EmptyResolvesNothingAndReadsAsAir()
        {
            NeighbourSnapshot snapshot = NeighbourSnapshot.Empty;

            for (int z = -1; z <= 1; z++)
            {
                for (int y = -1; y <= 1; y++)
                {
                    for (int x = -1; x <= 1; x++)
                    {
                        var direction = new Int3(x, y, z);
                        if (direction == Int3.Zero)
                        {
                            continue;
                        }

                        Assert.That(snapshot.Get(direction), Is.Null);
                        Assert.That(snapshot.GetCell(DirectionCell(direction, LocalMin)), Is.EqualTo(BlockId.Air));
                    }
                }
            }
        }

        [Test]
        public void CellsInsideTheCentreChunkReadAsAir()
        {
            World world = CreateWorldWithEveryNeighbour(out _);
            NeighbourSnapshot snapshot = world.CreateNeighbourSnapshot(Origin);

            Assert.That(snapshot.GetCell(new Int3(0, 0, 0)), Is.EqualTo(BlockId.Air));
            Assert.That(snapshot.GetCell(new Int3(8, 8, 8)), Is.EqualTo(BlockId.Air));
            Assert.That(snapshot.GetCell(new Int3(15, 15, 15)), Is.EqualTo(BlockId.Air));
        }

        [Test]
        public void SnapshotDoesNotObserveLaterEdits()
        {
            var world = new World();
            world.LoadChunk(new Chunk(Origin));
            var replaced = new ChunkCoord(1, 0, 0);
            world.LoadChunk(TestWorld.CreateChunk(replaced, (LocalMin, Stone)));
            NeighbourSnapshot snapshot = world.CreateNeighbourSnapshot(Origin);

            Int3 cell = ChunkMath.ToWorld(replaced, LocalMin);
            var command = new EditCommand(cell, Stone, Dirt, 1);
            Assert.That(world.Apply(in command), Is.EqualTo(EditResult.Applied));

            Assert.That(world.Get(cell), Is.EqualTo(Dirt));
            Assert.That(snapshot.GetCell(cell), Is.EqualTo(Stone), "the snapshot is a copy");
        }

        [Test]
        public void CellsOutOfTheNeighbourhoodReadAsAir()
        {
            World world = CreateWorldWithEveryNeighbour(out _);
            NeighbourSnapshot snapshot = world.CreateNeighbourSnapshot(Origin);

            Assert.That(snapshot.GetCell(new Int3(32, 0, 0)), Is.EqualTo(BlockId.Air));
            Assert.That(snapshot.GetCell(new Int3(0, -17, 0)), Is.EqualTo(BlockId.Air));
        }

        [Test]
        public void GetRejectsZeroAndOutOfRangeDirections()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => NeighbourSnapshot.Empty.Get(Int3.Zero));
            Assert.Throws<ArgumentOutOfRangeException>(() => NeighbourSnapshot.Empty.Get(new Int3(2, 0, 0)));
            Assert.Throws<ArgumentOutOfRangeException>(() => NeighbourSnapshot.Empty.Get(new Int3(0, -2, 0)));
            Assert.Throws<ArgumentOutOfRangeException>(() => NeighbourSnapshot.Empty.Get(new Int3(0, 0, 2)));
        }

        private static World CreateWorldWithEveryNeighbour(out Dictionary<Int3, ChunkCoord> neighbours)
        {
            var world = new World();
            world.LoadChunk(new Chunk(Origin));
            neighbours = new Dictionary<Int3, ChunkCoord>();

            for (int z = -1; z <= 1; z++)
            {
                for (int y = -1; y <= 1; y++)
                {
                    for (int x = -1; x <= 1; x++)
                    {
                        if (x == 0 && y == 0 && z == 0)
                        {
                            continue;
                        }

                        var direction = new Int3(x, y, z);
                        var coord = new ChunkCoord(x, y, z);
                        world.LoadChunk(TestWorld.CreateChunk(coord, (LocalMin, Stone), (LocalMax, Dirt)));
                        neighbours.Add(direction, coord);
                    }
                }
            }

            return world;
        }

        private static Int3 DirectionCell(Int3 direction, Int3 local)
        {
            return ChunkMath.ToWorld(
                new ChunkCoord(direction.X, direction.Y, direction.Z),
                local);
        }
    }
}

using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace Cubeglass.Voxel.Tests
{
    // Pins the constructor invariant that every edit key is a chunk-local cell
    // in [0, ChunkSize) per axis (ADR-0006). Before this check, (16, 0, 0)
    // aliased (0, 1, 0) at flat index 16 in the codec grid, so it could
    // silently overwrite a legitimate edit, and negative or oversized keys
    // escaped as a raw IndexOutOfRangeException from Serialize.
    [TestFixture]
    public sealed class ChunkDeltaTests
    {
        private static readonly ChunkCoord Origin = new ChunkCoord(0, 0, 0);

        [TestCase(16, 0, 0)]
        [TestCase(-1, 0, 0)]
        [TestCase(0, 0, 16)]
        [TestCase(0, 16, 0)]
        [TestCase(0, -1, 0)]
        [TestCase(0, 0, -1)]
        [TestCase(16, 16, 16)]
        public void ConstructorRejectsAnOutOfRangeCell(int x, int y, int z)
        {
            var edits = new Dictionary<Int3, BlockId> { [new Int3(x, y, z)] = new BlockId(1) };

            var exception = Assert.Throws<ArgumentException>(() => new ChunkDelta(Origin, edits));

            Assert.That(exception!.Message, Does.Contain($"({x}, {y}, {z})"));
        }

        [Test]
        public void ConstructorRejectsTheAliasingCellEvenBesideItsInRangeTwin()
        {
            var edits = new Dictionary<Int3, BlockId>
            {
                [new Int3(16, 0, 0)] = new BlockId(1),
                [new Int3(0, 1, 0)] = new BlockId(2),
            };

            var exception = Assert.Throws<ArgumentException>(() => new ChunkDelta(Origin, edits));

            Assert.That(exception!.Message, Does.Contain("(16, 0, 0)"));
        }

        [Test]
        public void TheInRangeCellWithTheSameFlatIndexStillConstructs()
        {
            var edits = new Dictionary<Int3, BlockId> { [new Int3(0, 1, 0)] = new BlockId(2) };

            var delta = new ChunkDelta(Origin, edits);

            Assert.That(delta.Edits[new Int3(0, 1, 0)], Is.EqualTo(new BlockId(2)));
        }

        [Test]
        public void EmptyStillConstructsForAnyCoordinate()
        {
            var coord = new ChunkCoord(-4, 5, -6);

            ChunkDelta delta = ChunkDelta.Empty(coord);

            Assert.That(delta.Coord, Is.EqualTo(coord));
            Assert.That(delta.Edits, Is.Empty);
        }
    }
}

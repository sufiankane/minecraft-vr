using NUnit.Framework;

namespace Cubeglass.Voxel.Tests
{
    [TestFixture]
    public sealed class ChunkMathTests
    {
        [Test]
        public void ChunkSizeIs16()
        {
            Assert.That(ChunkMath.ChunkSize, Is.EqualTo(16));
        }

        [Test]
        public void ToChunkUsesFloorDivision()
        {
            Assert.That(ChunkMath.ToChunk(new Int3(-1, -1, -1)), Is.EqualTo(new ChunkCoord(-1, -1, -1)));
            Assert.That(ChunkMath.ToChunk(new Int3(-17, 0, 15)), Is.EqualTo(new ChunkCoord(-2, 0, 0)));
        }

        [Test]
        public void ToChunkTruncatesPositiveCoordinates()
        {
            Assert.That(ChunkMath.ToChunk(new Int3(0, 15, 16)), Is.EqualTo(new ChunkCoord(0, 0, 1)));
            Assert.That(ChunkMath.ToChunk(new Int3(31, 32, 33)), Is.EqualTo(new ChunkCoord(1, 2, 2)));
        }

        [Test]
        public void ToLocalUsesFloorModulo()
        {
            Assert.That(ChunkMath.ToLocal(new Int3(-1, -1, -1)), Is.EqualTo(new Int3(15, 15, 15)));
            Assert.That(ChunkMath.ToLocal(new Int3(-17, 0, 15)), Is.EqualTo(new Int3(15, 0, 15)));
        }

        [Test]
        public void ToLocalKeepsInRangeCoordinatesUnchanged()
        {
            Assert.That(ChunkMath.ToLocal(new Int3(0, 7, 15)), Is.EqualTo(new Int3(0, 7, 15)));
        }

        [Test]
        public void ToWorldReconstructsTheOriginalCell()
        {
            Assert.That(
                ChunkMath.ToWorld(new ChunkCoord(-1, -1, -1), new Int3(15, 15, 15)),
                Is.EqualTo(new Int3(-1, -1, -1)));
            Assert.That(
                ChunkMath.ToWorld(new ChunkCoord(-2, 0, 0), new Int3(15, 0, 15)),
                Is.EqualTo(new Int3(-17, 0, 15)));
            Assert.That(
                ChunkMath.ToWorld(new ChunkCoord(1, 2, 3), new Int3(0, 1, 2)),
                Is.EqualTo(new Int3(16, 33, 50)));
        }

        [Test]
        public void ToLocalAlwaysLiesInTheChunkBounds()
        {
            Int3[] cells =
            {
                new Int3(0, 0, 0),
                new Int3(15, 15, 15),
                new Int3(16, 16, 16),
                new Int3(-1, -1, -1),
                new Int3(-16, -16, -16),
                new Int3(-17, 31, -33),
                new Int3(int.MaxValue, int.MinValue, 0),
            };

            foreach (Int3 cell in cells)
            {
                Int3 local = ChunkMath.ToLocal(cell);
                Assert.That(local.X, Is.InRange(0, ChunkMath.ChunkSize - 1), $"cell {cell}");
                Assert.That(local.Y, Is.InRange(0, ChunkMath.ChunkSize - 1), $"cell {cell}");
                Assert.That(local.Z, Is.InRange(0, ChunkMath.ChunkSize - 1), $"cell {cell}");
            }
        }

        [Test]
        public void Int3ZeroIsTheOrigin()
        {
            Assert.That(Int3.Zero, Is.EqualTo(new Int3(0, 0, 0)));
        }

        [Test]
        public void Int3SupportsAdditionAndSubtraction()
        {
            Int3 a = new Int3(1, -2, 3);
            Int3 b = new Int3(-4, 5, -6);

            Assert.That(a + b, Is.EqualTo(new Int3(-3, 3, -3)));
            Assert.That(a - b, Is.EqualTo(new Int3(5, -7, 9)));
        }

        [Test]
        public void Int3EqualityIsByValue()
        {
            Assert.That(new Int3(1, 2, 3), Is.EqualTo(new Int3(1, 2, 3)));
            Assert.That(new Int3(1, 2, 3), Is.Not.EqualTo(new Int3(3, 2, 1)));
            Assert.That(new Int3(1, 2, 3) == new Int3(1, 2, 3), Is.True);
            Assert.That(new Int3(1, 2, 3) != new Int3(1, 2, 4), Is.True);
            Assert.That(new Int3(1, 2, 3).GetHashCode(), Is.EqualTo(new Int3(1, 2, 3).GetHashCode()));
        }

        [Test]
        public void ChunkCoordEqualityIsByValue()
        {
            Assert.That(new ChunkCoord(-1, 0, 1), Is.EqualTo(new ChunkCoord(-1, 0, 1)));
            Assert.That(new ChunkCoord(-1, 0, 1), Is.Not.EqualTo(new ChunkCoord(0, 0, 1)));
            Assert.That(new ChunkCoord(-1, 0, 1) == new ChunkCoord(-1, 0, 1), Is.True);
        }
    }
}

using System.Collections.Generic;
using NUnit.Framework;

namespace Cubeglass.Voxel.Tests
{
    [TestFixture]
    public sealed class ChunkEditPropagationTests
    {
        [Test]
        public void CentreCellAffectsOnlyItsOwnChunk()
        {
            IReadOnlyList<ChunkCoord> affected = ChunkEditPropagation.GetAffectedChunks(new Int3(8, 8, 8));

            Assert.That(affected, Is.EqualTo(new[] { new ChunkCoord(0, 0, 0) }));
        }

        [Test]
        public void FaceAdjacentBorderCellAffectsTwoChunks()
        {
            IReadOnlyList<ChunkCoord> affected = ChunkEditPropagation.GetAffectedChunks(new Int3(0, 8, 8));

            Assert.That(
                affected,
                Is.EqualTo(new[]
                {
                    new ChunkCoord(-1, 0, 0),
                    new ChunkCoord(0, 0, 0),
                }));
        }

        [Test]
        public void OppositeFaceBorderCellAffectsTwoChunks()
        {
            IReadOnlyList<ChunkCoord> affected = ChunkEditPropagation.GetAffectedChunks(new Int3(15, 8, 8));

            Assert.That(
                affected,
                Is.EqualTo(new[]
                {
                    new ChunkCoord(0, 0, 0),
                    new ChunkCoord(1, 0, 0),
                }));
        }

        [Test]
        public void EdgeCellAffectsFourChunks()
        {
            IReadOnlyList<ChunkCoord> affected = ChunkEditPropagation.GetAffectedChunks(new Int3(0, 0, 8));

            Assert.That(
                affected,
                Is.EqualTo(new[]
                {
                    new ChunkCoord(-1, -1, 0),
                    new ChunkCoord(-1, 0, 0),
                    new ChunkCoord(0, -1, 0),
                    new ChunkCoord(0, 0, 0),
                }));
        }

        [Test]
        public void CornerCellAtOriginAffectsEightChunks()
        {
            IReadOnlyList<ChunkCoord> affected = ChunkEditPropagation.GetAffectedChunks(new Int3(0, 0, 0));

            Assert.That(
                affected,
                Is.EqualTo(new[]
                {
                    new ChunkCoord(-1, -1, -1),
                    new ChunkCoord(-1, -1, 0),
                    new ChunkCoord(-1, 0, -1),
                    new ChunkCoord(-1, 0, 0),
                    new ChunkCoord(0, -1, -1),
                    new ChunkCoord(0, -1, 0),
                    new ChunkCoord(0, 0, -1),
                    new ChunkCoord(0, 0, 0),
                }));
        }

        [Test]
        public void CornerCellAtChunkMaxAffectsEightChunks()
        {
            IReadOnlyList<ChunkCoord> affected = ChunkEditPropagation.GetAffectedChunks(new Int3(15, 15, 15));

            Assert.That(
                affected,
                Is.EqualTo(new[]
                {
                    new ChunkCoord(0, 0, 0),
                    new ChunkCoord(0, 0, 1),
                    new ChunkCoord(0, 1, 0),
                    new ChunkCoord(0, 1, 1),
                    new ChunkCoord(1, 0, 0),
                    new ChunkCoord(1, 0, 1),
                    new ChunkCoord(1, 1, 0),
                    new ChunkCoord(1, 1, 1),
                }));
        }

        [Test]
        public void NegativeCornerCellAffectsEightChunks()
        {
            IReadOnlyList<ChunkCoord> affected = ChunkEditPropagation.GetAffectedChunks(new Int3(-1, -1, -1));

            Assert.That(
                affected,
                Is.EqualTo(new[]
                {
                    new ChunkCoord(-1, -1, -1),
                    new ChunkCoord(-1, -1, 0),
                    new ChunkCoord(-1, 0, -1),
                    new ChunkCoord(-1, 0, 0),
                    new ChunkCoord(0, -1, -1),
                    new ChunkCoord(0, -1, 0),
                    new ChunkCoord(0, 0, -1),
                    new ChunkCoord(0, 0, 0),
                }));
        }

        [Test]
        public void ResultsAreSortedByXyzAndContainNoDuplicates()
        {
            IReadOnlyList<ChunkCoord> affected = ChunkEditPropagation.GetAffectedChunks(new Int3(0, 15, 0));

            var seen = new HashSet<ChunkCoord>();
            ChunkCoord previous = default;
            for (int i = 0; i < affected.Count; i++)
            {
                ChunkCoord current = affected[i];
                Assert.That(seen.Add(current), Is.True, "no duplicates");
                if (i > 0)
                {
                    Assert.That(Compare(previous, current), Is.LessThan(0), "strictly increasing (X, Y, Z) order");
                }

                previous = current;
            }

            Assert.That(affected.Count, Is.EqualTo(8));
        }

        [Test]
        public void ExtremeCellDoesNotWrapAffectedChunks()
        {
            IReadOnlyList<ChunkCoord> max = ChunkEditPropagation.GetAffectedChunks(new Int3(int.MaxValue, 0, 0));

            Assert.That(max.Count, Is.EqualTo(4), "the +1 corner lies outside the cell range and is skipped");
            for (int i = 0; i < max.Count; i++)
            {
                Assert.That(max[i].X, Is.EqualTo(134_217_727), "a wrapped corner must not name an unrelated chunk");
            }

            IReadOnlyList<ChunkCoord> min = ChunkEditPropagation.GetAffectedChunks(new Int3(int.MinValue, 0, 0));

            Assert.That(min.Count, Is.EqualTo(4), "the -1 corner lies outside the cell range and is skipped");
            for (int i = 0; i < min.Count; i++)
            {
                Assert.That(min[i].X, Is.EqualTo(-134_217_728), "a wrapped corner must not name an unrelated chunk");
            }
        }

        private static int Compare(ChunkCoord a, ChunkCoord b)
        {
            int result = a.X.CompareTo(b.X);
            if (result != 0)
            {
                return result;
            }

            result = a.Y.CompareTo(b.Y);
            return result != 0 ? result : a.Z.CompareTo(b.Z);
        }
    }
}

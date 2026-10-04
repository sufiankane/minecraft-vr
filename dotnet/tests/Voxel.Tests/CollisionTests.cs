using System;
using System.Diagnostics;
using Cubeglass.CoreMath;
using NUnit.Framework;

namespace Cubeglass.Voxel.Tests
{
    // Boundary cases for the half-open AABB and the collision queries. Every
    // element-wise comparison is Min <= p < Max, so touching a face, edge or
    // corner is not an overlap; unloaded chunks read as air.
    [TestFixture]
    public sealed class CollisionTests
    {
        private static readonly ChunkCoord Origin = new ChunkCoord(0, 0, 0);
        private static readonly BlockId Stone = new BlockId(1);

        [Test]
        public void AabbConstructorRejectsAnInvertedBox()
        {
            Assert.Throws<ArgumentException>(() => new Aabb(new Vec3(1, 0, 0), new Vec3(0, 1, 1)));
            Assert.Throws<ArgumentException>(() => new Aabb(new Vec3(0, 1, 0), new Vec3(1, 0, 1)));
            Assert.Throws<ArgumentException>(() => new Aabb(new Vec3(0, 0, 1), new Vec3(1, 1, 0)));

            var point = new Aabb(new Vec3(0.5, 0.5, 0.5), new Vec3(0.5, 0.5, 0.5));
            Assert.That(point.Min, Is.EqualTo(point.Max));
        }

        [Test]
        public void AabbContainsUsesHalfOpenBounds()
        {
            var box = new Aabb(new Vec3(0, 0, 0), new Vec3(1, 1, 1));

            Assert.That(box.Contains(new Vec3(0, 0, 0)), Is.True);
            Assert.That(box.Contains(new Vec3(0.999, 0.999, 0.999)), Is.True);
            Assert.That(box.Contains(new Vec3(1, 1, 1)), Is.False);
            Assert.That(box.Contains(new Vec3(1, 0, 0)), Is.False);
            Assert.That(box.Contains(new Vec3(-0.001, 0.5, 0.5)), Is.False);
        }

        [Test]
        public void AabbOverlapsUsesHalfOpenBounds()
        {
            var box = new Aabb(new Vec3(0, 0, 0), new Vec3(1, 1, 1));

            Assert.That(box.Overlaps(box), Is.True);
            Assert.That(box.Overlaps(new Aabb(new Vec3(0.5, 0.5, 0.5), new Vec3(2, 2, 2))), Is.True);
            Assert.That(box.Overlaps(new Aabb(new Vec3(1, 0, 0), new Vec3(2, 1, 1))), Is.False);
            Assert.That(box.Overlaps(new Aabb(new Vec3(-1, 0, 0), new Vec3(0, 1, 1))), Is.False);
            Assert.That(box.Overlaps(new Aabb(new Vec3(1, 1, 0), new Vec3(2, 2, 1))), Is.False);
            Assert.That(box.Overlaps(new Aabb(new Vec3(1, 1, 1), new Vec3(2, 2, 2))), Is.False);
            Assert.That(box.Overlaps(new Aabb(new Vec3(2, 0, 0), new Vec3(3, 1, 1))), Is.False);
        }

        [Test]
        public void EmptyBoxesContainNoPointAndOverlapNothing()
        {
            var point = new Aabb(new Vec3(0.5, 0.5, 0.5), new Vec3(0.5, 0.5, 0.5));
            var box = new Aabb(new Vec3(0, 0, 0), new Vec3(1, 1, 1));

            Assert.That(point.Contains(new Vec3(0.5, 0.5, 0.5)), Is.False);
            Assert.That(point.Overlaps(box), Is.False);
            Assert.That(box.Overlaps(point), Is.False);
        }

        [Test]
        public void EmptyBoxNeverCollidesOrBlocksPlacement()
        {
            World world = TestWorld.CreateLoaded(Origin, (Int3.Zero, Stone));
            var point = new Aabb(new Vec3(0.5, 0.5, 0.5), new Vec3(0.5, 0.5, 0.5));

            Assert.That(VoxelCollision.Overlaps(world, point), Is.False);
            Assert.That(VoxelCollision.CanPlace(world, Int3.Zero, point), Is.True);
        }

        [Test]
        public void AdjacentSolidCellDoesNotOverlap()
        {
            World world = TestWorld.CreateLoaded(Origin, (new Int3(1, 0, 0), Stone));
            var box = new Aabb(new Vec3(0, 0, 0), new Vec3(1, 1, 1));

            Assert.That(VoxelCollision.Overlaps(world, box), Is.False);
        }

        [Test]
        public void FaceSharingWithASolidCellDoesNotOverlap()
        {
            World world = TestWorld.CreateLoaded(Origin, (new Int3(1, 0, 0), Stone));

            var before = new Aabb(new Vec3(0, 0, 0), new Vec3(1, 1, 1));
            var after = new Aabb(new Vec3(2, 0, 0), new Vec3(3, 1, 1));

            Assert.That(VoxelCollision.Overlaps(world, before), Is.False);
            Assert.That(VoxelCollision.Overlaps(world, after), Is.False);
        }

        [Test]
        public void BoxStraddlingASolidCellOverlaps()
        {
            World world = TestWorld.CreateLoaded(Origin, (new Int3(1, 0, 0), Stone));

            var box = new Aabb(new Vec3(0.5, 0.25, 0.25), new Vec3(1.5, 0.75, 0.75));

            Assert.That(VoxelCollision.Overlaps(world, box), Is.True);
        }

        [Test]
        public void EdgeAndCornerContactsDoNotOverlap()
        {
            World edge = TestWorld.CreateLoaded(Origin, (new Int3(1, 1, 0), Stone));
            World corner = TestWorld.CreateLoaded(Origin, (new Int3(1, 1, 1), Stone));
            var box = new Aabb(new Vec3(0, 0, 0), new Vec3(1, 1, 1));

            Assert.That(VoxelCollision.Overlaps(edge, box), Is.False);
            Assert.That(VoxelCollision.Overlaps(corner, box), Is.False);
        }

        [Test]
        public void UnloadedCellsAreAir()
        {
            var world = new World();
            var box = new Aabb(new Vec3(0.25, 0.25, 0.25), new Vec3(0.75, 0.75, 0.75));

            Assert.That(world.IsLoaded(Int3.Zero), Is.False);
            Assert.That(VoxelCollision.Overlaps(world, box), Is.False);

            World loadedAir = TestWorld.CreateFilledWorld(Origin, _ => BlockId.Air);
            Assert.That(VoxelCollision.Overlaps(loadedAir, box), Is.False);
        }

        [Test]
        public void NegativeWorldCoordinatesOverlap()
        {
            World world = TestWorld.CreateLoaded(
                new ChunkCoord(-1, -1, -1),
                (new Int3(15, 15, 15), Stone));
            var box = new Aabb(new Vec3(-0.5, -0.5, -0.5), new Vec3(0.5, 0.5, 0.5));

            Assert.That(VoxelCollision.Overlaps(world, box), Is.True);
        }

        [Test]
        public void CanPlaceRefusesTheCellThePlayerOccupies()
        {
            var world = new World();
            var playerBox = new Aabb(new Vec3(0.25, 0.25, 0.25), new Vec3(0.75, 0.75, 0.75));

            Assert.That(VoxelCollision.CanPlace(world, Int3.Zero, playerBox), Is.False);
        }

        [Test]
        public void CanPlaceAllowsAdjacentCells()
        {
            var world = new World();
            var playerBox = new Aabb(new Vec3(0.25, 0.25, 0.25), new Vec3(0.75, 0.75, 0.75));

            Assert.That(VoxelCollision.CanPlace(world, new Int3(1, 0, 0), playerBox), Is.True);
            Assert.That(VoxelCollision.CanPlace(world, new Int3(-1, 0, 0), playerBox), Is.True);
            Assert.That(VoxelCollision.CanPlace(world, new Int3(0, 1, 0), playerBox), Is.True);
            Assert.That(VoxelCollision.CanPlace(world, new Int3(0, 0, -1), playerBox), Is.True);
        }

        [Test]
        public void CanPlaceAllowsCellsThatOnlyTouchThePlayerBox()
        {
            var world = new World();
            var playerBox = new Aabb(new Vec3(0, 0, 0), new Vec3(1, 1, 1));

            Assert.That(VoxelCollision.CanPlace(world, new Int3(1, 0, 0), playerBox), Is.True);
            Assert.That(VoxelCollision.CanPlace(world, new Int3(0, 0, 0), playerBox), Is.False);
        }

        [Test]
        public void CanPlaceDoesNotConsultWorldContent()
        {
            World world = TestWorld.CreateLoaded(Origin, (new Int3(5, 0, 0), Stone));
            var playerBox = new Aabb(new Vec3(0.25, 0.25, 0.25), new Vec3(0.75, 0.75, 0.75));

            Assert.That(VoxelCollision.CanPlace(world, new Int3(5, 0, 0), playerBox), Is.True);
        }

        [Test]
        public void NullWorldIsRejected()
        {
            var box = new Aabb(new Vec3(0, 0, 0), new Vec3(1, 1, 1));

            Assert.Throws<ArgumentNullException>(() => VoxelCollision.Overlaps(null!, box));
            Assert.Throws<ArgumentNullException>(() => VoxelCollision.CanPlace(null!, Int3.Zero, box));
        }

        [Test]
        public void ExtremeBoxThrowsInsteadOfScanningForever()
        {
            var world = new World();
            var box = new Aabb(new Vec3(0, 0, 0), new Vec3(2147483648.0, 1, 1));

            var stopwatch = Stopwatch.StartNew();
            Assert.Throws<ArgumentException>(() => VoxelCollision.Overlaps(world, box));
            stopwatch.Stop();

            Assert.That(stopwatch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(2.0)));
        }

        [Test]
        public void NonFiniteBoundsAreRejected()
        {
            var world = new World();

            Assert.Throws<ArgumentException>(
                () => VoxelCollision.Overlaps(world, new Aabb(new Vec3(0, 0, 0), new Vec3(double.PositiveInfinity, 1, 1))));
            Assert.Throws<ArgumentException>(
                () => VoxelCollision.Overlaps(world, new Aabb(new Vec3(double.NegativeInfinity, 0, 0), new Vec3(1, 1, 1))));
            Assert.Throws<ArgumentException>(
                () => VoxelCollision.Overlaps(world, new Aabb(new Vec3(double.NegativeInfinity, 0, 0), new Vec3(double.PositiveInfinity, 1, 1))));
        }

        [Test]
        public void HugeFiniteBoxIsRejectedByTheCellBudget()
        {
            var world = new World();
            var box = new Aabb(new Vec3(-1e300, -1e300, -1e300), new Vec3(1e300, 1e300, 1e300));

            Assert.Throws<ArgumentException>(() => VoxelCollision.Overlaps(world, box));
        }

        [Test]
        public void BoxEntirelyBeyondTheCellRangeIsEmpty()
        {
            var world = TestWorld.CreateLoaded(Origin, (Int3.Zero, Stone));
            var box = new Aabb(new Vec3(1e12, 0, 0), new Vec3(1e12 + 1, 1, 1));

            Assert.That(VoxelCollision.Overlaps(world, box), Is.False);
        }

        [Test]
        public void OverlapsNearTheMaxCellMatchesTheCellCubeModel()
        {
            var chunk = new ChunkCoord(134_217_727, 0, 0);
            World world = TestWorld.CreateLoaded(chunk, (new Int3(15, 0, 0), Stone));
            var box = new Aabb(new Vec3(2147483646.75, 0.25, 0.25), new Vec3(2147483647.75, 0.75, 0.75));

            Assert.That(VoxelCollision.Overlaps(world, box), Is.True);
        }

        [Test]
        public void CanPlaceAtMaxIntDoesNotWrap()
        {
            var world = new World();
            var playerBox = new Aabb(new Vec3(2147483647.0, 0, 0), new Vec3(2147483648.0, 1, 1));

            Assert.That(VoxelCollision.CanPlace(world, new Int3(int.MaxValue, 0, 0), playerBox), Is.False);
        }

        [Test]
        public void CanPlaceAtMinIntBlocksTheOccupiedCell()
        {
            var world = new World();
            var playerBox = new Aabb(new Vec3(-2147483648.0, 0, 0), new Vec3(-2147483647.0, 1, 1));

            Assert.That(VoxelCollision.CanPlace(world, new Int3(int.MinValue, 0, 0), playerBox), Is.False);
        }
    }
}

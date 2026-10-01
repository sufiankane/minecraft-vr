using System;
using Cubeglass.CoreMath;
using NUnit.Framework;

namespace Cubeglass.Voxel.Tests
{
    [TestFixture]
    public sealed class DdaRaycasterTests
    {
        private static readonly ChunkCoord Origin = new ChunkCoord(0, 0, 0);
        private static readonly BlockId Stone = new BlockId(1);
        private static readonly BlockId Dirt = new BlockId(2);

        [Test]
        public void CastFindsTheFirstSolidCellOnAnAxisAlignedRay()
        {
            World world = TestWorld.CreateLoaded(Origin, (new Int3(2, 0, 0), Stone));
            var ray = new Ray(new Vec3(-0.5, 0.5, 0.5), new Vec3(1.0, 0.0, 0.0));

            RayHit? result = new DdaRaycaster().Cast(world, ray, 10f);

            Assert.That(result, Is.Not.Null);
            RayHit hit = result.GetValueOrDefault();
            Assert.That(hit.Cell, Is.EqualTo(new Int3(2, 0, 0)));
            Assert.That(hit.Normal, Is.EqualTo(new Int3(-1, 0, 0)));
            Assert.That(hit.Distance, Is.EqualTo(2.5f));
            Assert.That(hit.Block, Is.EqualTo(Stone));
        }

        [Test]
        public void CastNormalisesANonUnitDirectionBeforeMeasuringDistance()
        {
            World world = TestWorld.CreateLoaded(Origin, (new Int3(2, 0, 0), Stone));
            var ray = new Ray(new Vec3(-0.5, 0.5, 0.5), new Vec3(7.0, 0.0, 0.0));

            RayHit? result = new DdaRaycaster().Cast(world, ray, 10f);

            Assert.That(result, Is.Not.Null);
            Assert.That(result.GetValueOrDefault().Distance, Is.EqualTo(2.5f));
        }

        [Test]
        public void CastStartingInsideASolidCellReturnsThatCellWithAZeroNormal()
        {
            World world = TestWorld.CreateLoaded(Origin, (new Int3(0, 0, 0), Stone));
            var ray = new Ray(new Vec3(0.5, 0.5, 0.5), new Vec3(1.0, 1.0, 1.0));

            RayHit? result = new DdaRaycaster().Cast(world, ray, 10f);

            Assert.That(result, Is.Not.Null);
            RayHit hit = result.GetValueOrDefault();
            Assert.That(hit.Cell, Is.EqualTo(new Int3(0, 0, 0)));
            Assert.That(hit.Normal, Is.EqualTo(Int3.Zero));
            Assert.That(hit.Distance, Is.Zero);
            Assert.That(hit.Block, Is.EqualTo(Stone));
        }

        [Test]
        public void CastOnADiagonalHitsTheHandComputedFirstSolidCell()
        {
            World world = TestWorld.CreateLoaded(Origin, (new Int3(2, 1, 0), Dirt));
            var ray = new Ray(new Vec3(0.5, 0.5, 0.5), new Vec3(2.0, 1.0, 0.0));

            RayHit? result = new DdaRaycaster().Cast(world, ray, 10f);

            Assert.That(result, Is.Not.Null);
            RayHit hit = result.GetValueOrDefault();
            Assert.That(hit.Cell, Is.EqualTo(new Int3(2, 1, 0)));
            Assert.That(hit.Normal, Is.EqualTo(new Int3(-1, 0, 0)));
            Assert.That(hit.Distance, Is.EqualTo(MathF.Sqrt(5f) * 0.75f).Within(1e-6f));
            Assert.That(hit.Block, Is.EqualTo(Dirt));
        }

        [Test]
        public void CastThroughAnExactEdgePrefersXOverY()
        {
            World world = TestWorld.CreateLoaded(
                Origin,
                (new Int3(1, 0, 0), Stone),
                (new Int3(0, 1, 0), Dirt));
            var ray = new Ray(new Vec3(0.5, 0.5, 0.5), new Vec3(1.0, 1.0, 0.0));

            RayHit? result = new DdaRaycaster().Cast(world, ray, 10f);

            Assert.That(result, Is.Not.Null);
            RayHit hit = result.GetValueOrDefault();
            Assert.That(hit.Cell, Is.EqualTo(new Int3(1, 0, 0)));
            Assert.That(hit.Normal, Is.EqualTo(new Int3(-1, 0, 0)));
            Assert.That(hit.Distance, Is.EqualTo(MathF.Sqrt(0.5f)).Within(1e-6f));
            Assert.That(hit.Block, Is.EqualTo(Stone));
        }

        [Test]
        public void CastThroughAnExactEdgePrefersYOverZ()
        {
            World world = TestWorld.CreateLoaded(
                Origin,
                (new Int3(0, 1, 0), Dirt),
                (new Int3(0, 0, 1), Stone));
            var ray = new Ray(new Vec3(0.5, 0.5, 0.5), new Vec3(0.0, 1.0, 1.0));

            RayHit? result = new DdaRaycaster().Cast(world, ray, 10f);

            Assert.That(result, Is.Not.Null);
            RayHit hit = result.GetValueOrDefault();
            Assert.That(hit.Cell, Is.EqualTo(new Int3(0, 1, 0)));
            Assert.That(hit.Normal, Is.EqualTo(new Int3(0, -1, 0)));
            Assert.That(hit.Distance, Is.EqualTo(MathF.Sqrt(0.5f)).Within(1e-6f));
            Assert.That(hit.Block, Is.EqualTo(Dirt));
        }

        [Test]
        public void CastThroughAnExactCornerPrefersXThenYThenZ()
        {
            World world = TestWorld.CreateLoaded(
                Origin,
                (new Int3(1, 0, 0), Stone),
                (new Int3(0, 1, 0), Dirt),
                (new Int3(0, 0, 1), Stone));
            var ray = new Ray(new Vec3(0.5, 0.5, 0.5), new Vec3(1.0, 1.0, 1.0));

            RayHit? result = new DdaRaycaster().Cast(world, ray, 10f);

            Assert.That(result, Is.Not.Null);
            RayHit hit = result.GetValueOrDefault();
            Assert.That(hit.Cell, Is.EqualTo(new Int3(1, 0, 0)));
            Assert.That(hit.Normal, Is.EqualTo(new Int3(-1, 0, 0)));
            Assert.That(hit.Distance, Is.EqualTo(MathF.Sqrt(0.75f)).Within(1e-6f));
            Assert.That(hit.Block, Is.EqualTo(Stone));
        }

        [Test]
        public void CastReturnsNullWhenTheHitIsBeyondMaxDistance()
        {
            World world = TestWorld.CreateLoaded(Origin, (new Int3(2, 0, 0), Stone));
            var ray = new Ray(new Vec3(-0.5, 0.5, 0.5), new Vec3(1.0, 0.0, 0.0));
            var raycaster = new DdaRaycaster();

            Assert.That(raycaster.Cast(world, ray, 2.49f), Is.Null);
            Assert.That(raycaster.Cast(world, ray, 2.5f), Is.Not.Null);
        }

        [Test]
        public void CastInAnEmptyLoadedWorldReturnsNull()
        {
            World world = TestWorld.CreateLoaded(Origin);
            var ray = new Ray(new Vec3(0.5, 0.5, 0.5), new Vec3(1.0, 0.0, 0.0));

            Assert.That(new DdaRaycaster().Cast(world, ray, 100f), Is.Null);
        }

        [Test]
        public void CastContinuesThroughUnloadedChunksAndHitsALaterLoadedSolid()
        {
            var world = new World();
            world.LoadChunk(TestWorld.CreateChunk(Origin));
            world.LoadChunk(TestWorld.CreateChunk(new ChunkCoord(2, 0, 0), (new Int3(0, 0, 0), Stone)));
            var ray = new Ray(new Vec3(0.5, 0.5, 0.5), new Vec3(1.0, 0.0, 0.0));

            RayHit? result = new DdaRaycaster().Cast(world, ray, 100f);

            Assert.That(result, Is.Not.Null);
            RayHit hit = result.GetValueOrDefault();
            Assert.That(hit.Cell, Is.EqualTo(new Int3(32, 0, 0)));
            Assert.That(hit.Normal, Is.EqualTo(new Int3(-1, 0, 0)));
            Assert.That(hit.Distance, Is.EqualTo(31.5f));
            Assert.That(hit.Block, Is.EqualTo(Stone));
        }

        [Test]
        public void CastThroughOnlyUnloadedSpaceReturnsNull()
        {
            var world = new World();
            var ray = new Ray(new Vec3(0.5, 0.5, 0.5), new Vec3(1.0, 0.0, 0.0));

            Assert.That(new DdaRaycaster().Cast(world, ray, 100f), Is.Null);
        }

        [Test]
        public void CastWithZeroDirectionReturnsNull()
        {
            World world = TestWorld.CreateLoaded(Origin, (new Int3(0, 0, 0), Stone));
            var ray = new Ray(new Vec3(0.5, 0.5, 0.5), new Vec3(0.0, 0.0, 0.0));

            Assert.That(new DdaRaycaster().Cast(world, ray, 10f), Is.Null);
        }

        [Test]
        public void CastWithNegativeMaxDistanceReturnsNull()
        {
            World world = TestWorld.CreateLoaded(Origin, (new Int3(2, 0, 0), Stone));
            var ray = new Ray(new Vec3(-0.5, 0.5, 0.5), new Vec3(1.0, 0.0, 0.0));

            Assert.That(new DdaRaycaster().Cast(world, ray, -1f), Is.Null);
        }

        [Test]
        public void CastStartingOnACellBoundaryTreatsTheFloorCellAsTheOriginCell()
        {
            World solidAtFloor = TestWorld.CreateLoaded(Origin, (new Int3(2, 0, 0), Stone));
            var ray = new Ray(new Vec3(2.0, 0.5, 0.5), new Vec3(1.0, 0.0, 0.0));

            RayHit? inside = new DdaRaycaster().Cast(solidAtFloor, ray, 10f);

            Assert.That(inside, Is.Not.Null);
            Assert.That(inside.GetValueOrDefault().Cell, Is.EqualTo(new Int3(2, 0, 0)));
            Assert.That(inside.GetValueOrDefault().Normal, Is.EqualTo(Int3.Zero));
            Assert.That(inside.GetValueOrDefault().Distance, Is.Zero);

            World solidAhead = TestWorld.CreateLoaded(Origin, (new Int3(3, 0, 0), Dirt));
            RayHit? ahead = new DdaRaycaster().Cast(solidAhead, ray, 10f);

            Assert.That(ahead, Is.Not.Null);
            RayHit hit = ahead.GetValueOrDefault();
            Assert.That(hit.Cell, Is.EqualTo(new Int3(3, 0, 0)));
            Assert.That(hit.Normal, Is.EqualTo(new Int3(-1, 0, 0)));
            Assert.That(hit.Distance, Is.EqualTo(1.0f));
        }

        [Test]
        public void CastTowardsNegativeCoordinatesFindsSolidsInNegativeChunks()
        {
            World world = TestWorld.CreateLoaded(new ChunkCoord(-1, 0, 0), (new Int3(14, 0, 0), Stone));
            var ray = new Ray(new Vec3(2.5, 0.5, 0.5), new Vec3(-1.0, 0.0, 0.0));

            RayHit? result = new DdaRaycaster().Cast(world, ray, 10f);

            Assert.That(result, Is.Not.Null);
            RayHit hit = result.GetValueOrDefault();
            Assert.That(hit.Cell, Is.EqualTo(new Int3(-2, 0, 0)));
            Assert.That(hit.Normal, Is.EqualTo(new Int3(1, 0, 0)));
            Assert.That(hit.Distance, Is.EqualTo(3.5f));
            Assert.That(hit.Block, Is.EqualTo(Stone));
        }

        [Test]
        public void CastDownwardsReportsAnUpwardEntryNormal()
        {
            World world = TestWorld.CreateLoaded(Origin, (new Int3(0, 2, 0), Stone));
            var ray = new Ray(new Vec3(0.5, 4.5, 0.5), new Vec3(0.0, -1.0, 0.0));

            RayHit? result = new DdaRaycaster().Cast(world, ray, 10f);

            Assert.That(result, Is.Not.Null);
            RayHit hit = result.GetValueOrDefault();
            Assert.That(hit.Cell, Is.EqualTo(new Int3(0, 2, 0)));
            Assert.That(hit.Normal, Is.EqualTo(new Int3(0, 1, 0)));
            Assert.That(hit.Distance, Is.EqualTo(1.5f));
        }
    }
}

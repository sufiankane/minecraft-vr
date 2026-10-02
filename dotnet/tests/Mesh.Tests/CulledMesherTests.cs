using System;
using System.Runtime.InteropServices;
using Cubeglass.CoreMath;
using Cubeglass.Voxel;
using NUnit.Framework;

namespace Cubeglass.Mesh.Tests
{
    [TestFixture]
    public sealed class AtlasMapTests
    {
        [Test]
        public void TileUvSizeIsTheReciprocalOfTilesPerRow()
        {
            Assert.That(AtlasMap.TileUvSize(new AtlasLayout(16, 16)).X, Is.EqualTo(1.0F / 16.0F));
            Assert.That(AtlasMap.TileUvSize(new AtlasLayout(4, 32)).X, Is.EqualTo(0.25F));
            Assert.That(AtlasMap.TileUvSize(new AtlasLayout(4, 32)).Y, Is.EqualTo(0.25F));
        }

        [Test]
        public void TileMinWalksRowsOfSixteen()
        {
            var layout = new AtlasLayout(16, 16);

            Assert.That(AtlasMap.TileMin(layout, 0), Is.EqualTo(new Vector2f(0.0F, 0.0F)));
            Assert.That(AtlasMap.TileMin(layout, 1), Is.EqualTo(new Vector2f(1.0F / 16.0F, 0.0F)));
            Assert.That(AtlasMap.TileMin(layout, 15), Is.EqualTo(new Vector2f(15.0F / 16.0F, 0.0F)));
            Assert.That(AtlasMap.TileMin(layout, 16), Is.EqualTo(new Vector2f(0.0F, 1.0F / 16.0F)));
            Assert.That(AtlasMap.TileMin(layout, 17), Is.EqualTo(new Vector2f(1.0F / 16.0F, 1.0F / 16.0F)));
            Assert.That(AtlasMap.TileMin(layout, 255), Is.EqualTo(new Vector2f(15.0F / 16.0F, 15.0F / 16.0F)));
        }

        [Test]
        public void TileMinHandlesAnotherGridSize()
        {
            var layout = new AtlasLayout(4, 32);

            Assert.That(AtlasMap.TileMin(layout, 5), Is.EqualTo(new Vector2f(0.25F, 0.25F)));
            Assert.That(AtlasMap.TileMin(layout, 15), Is.EqualTo(new Vector2f(0.75F, 0.75F)));
        }

        [Test]
        public void TileMinRejectsIndicesOutsideTheGrid()
        {
            var layout = new AtlasLayout(16, 16);

            Assert.Throws<ArgumentOutOfRangeException>(() => AtlasMap.TileMin(layout, -1));
            Assert.Throws<ArgumentOutOfRangeException>(() => AtlasMap.TileMin(layout, 256));
        }

        [Test]
        public void LayoutRejectsNonPositiveSizes()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new AtlasLayout(0, 16));
            Assert.Throws<ArgumentOutOfRangeException>(() => new AtlasLayout(16, 0));
        }
    }

    [TestFixture]
    public sealed class CulledMesherTests
    {
        private static readonly ChunkCoord Origin = new ChunkCoord(0, 0, 0);
        private static readonly ChunkCoord East = new ChunkCoord(1, 0, 0);
        private static readonly ChunkCoord West = new ChunkCoord(-1, 0, 0);
        private static readonly Int3 Min = new Int3(0, 0, 0);
        private static readonly Int3 Max = new Int3(15, 15, 15);
        private static readonly AtlasLayout Layout = new AtlasLayout(16, 16);
        private static readonly CulledMesher Mesher = new CulledMesher();

        [Test]
        public void SingleBlockEmitsSixQuads()
        {
            MeshData mesh = Build(
                TestChunks.Snapshot(Origin, (Min, TestChunks.Stone)),
                NeighbourSnapshot.Empty);

            Assert.That(mesh.VertexCount, Is.EqualTo(24));
            Assert.That(mesh.IndexCount, Is.EqualTo(36));
            Assert.That(mesh.Positions.Length, Is.EqualTo(24));
            Assert.That(mesh.Normals.Length, Is.EqualTo(24));
            Assert.That(mesh.Uvs.Length, Is.EqualTo(24));
            Assert.That(mesh.Ao.Length, Is.EqualTo(24));
            Assert.That(mesh.Indices.Length, Is.EqualTo(36));
        }

        [Test]
        public void TwoAdjacentOpaqueBlocksShareNoFace()
        {
            MeshData mesh = Build(
                TestChunks.Snapshot(Origin, (Min, TestChunks.Stone), (new Int3(1, 0, 0), TestChunks.Stone)),
                NeighbourSnapshot.Empty);

            Assert.That(mesh.VertexCount, Is.EqualTo(40), "ten exposed faces, not twelve");
            Assert.That(mesh.IndexCount, Is.EqualTo(60));
        }

        [Test]
        public void OneBlockEmitsAllSixFacesInDeterministicFaceOrder()
        {
            MeshData mesh = Build(
                TestChunks.Snapshot(Origin, (new Int3(2, 3, 4), TestChunks.Stone)),
                NeighbourSnapshot.Empty);

            var expected = new[]
            {
                new Vector3f(1.0F, 0.0F, 0.0F),
                new Vector3f(-1.0F, 0.0F, 0.0F),
                new Vector3f(0.0F, 1.0F, 0.0F),
                new Vector3f(0.0F, -1.0F, 0.0F),
                new Vector3f(0.0F, 0.0F, 1.0F),
                new Vector3f(0.0F, 0.0F, -1.0F),
            };

            for (int face = 0; face < expected.Length; face++)
            {
                Assert.That(mesh.Normals.Span[(face * 4) + 0], Is.EqualTo(expected[face]), $"face {face}");
                Assert.That(mesh.Normals.Span[(face * 4) + 1], Is.EqualTo(expected[face]), $"face {face}");
                Assert.That(mesh.Normals.Span[(face * 4) + 2], Is.EqualTo(expected[face]), $"face {face}");
                Assert.That(mesh.Normals.Span[(face * 4) + 3], Is.EqualTo(expected[face]), $"face {face}");
            }
        }

        [Test]
        public void FaceAgainstANonOpaqueNeighbourIsEmitted()
        {
            MeshData mesh = Build(
                TestChunks.Snapshot(Origin, (new Int3(15, 0, 0), TestChunks.Stone)),
                TestChunks.Neighbours(Origin, (East, Min, TestChunks.Glass)));

            Assert.That(mesh.VertexCount, Is.EqualTo(24), "glass never culls a face");
        }

        [Test]
        public void TwoAdjacentNonOpaqueBlocksBothEmitTheirFaces()
        {
            MeshData mesh = Build(
                TestChunks.Snapshot(Origin, (Min, TestChunks.Glass), (new Int3(1, 0, 0), TestChunks.Glass)),
                NeighbourSnapshot.Empty);

            Assert.That(mesh.VertexCount, Is.EqualTo(48), "both glass blocks expose all six faces");
        }

        [Test]
        public void BorderFaceAgainstAnOpaqueNeighbourIsCulled()
        {
            MeshData mesh = Build(
                TestChunks.Snapshot(Origin, (new Int3(15, 0, 0), TestChunks.Stone)),
                TestChunks.Neighbours(Origin, (East, Min, TestChunks.Stone)));

            Assert.That(mesh.VertexCount, Is.EqualTo(20), "the +X border face is hidden by the neighbour");
        }

        [Test]
        public void BorderFaceAgainstTheMinusSideNeighbourIsCulled()
        {
            MeshData mesh = Build(
                TestChunks.Snapshot(Origin, (Min, TestChunks.Stone)),
                TestChunks.Neighbours(Origin, (West, new Int3(15, 0, 0), TestChunks.Stone)));

            Assert.That(mesh.VertexCount, Is.EqualTo(20), "the -X border face is hidden by the neighbour");
        }

        [TestCase(1, 0, 0)]
        [TestCase(-1, 0, 0)]
        [TestCase(0, 1, 0)]
        [TestCase(0, -1, 0)]
        [TestCase(0, 0, 1)]
        [TestCase(0, 0, -1)]
        public void BorderFaceIsCulledAgainstAnOpaqueNeighbourInEveryDirection(int dx, int dy, int dz)
        {
            var centreCell = new Int3(dx > 0 ? 15 : 0, dy > 0 ? 15 : 0, dz > 0 ? 15 : 0);
            if (dx == 0 && dy == 0 && dz == 0)
            {
                Assert.Fail("test requires one non-zero direction component");
            }

            var neighbourCell = new Int3(dx < 0 ? 15 : 0, dy < 0 ? 15 : 0, dz < 0 ? 15 : 0);
            var neighbourCoord = new ChunkCoord(dx, dy, dz);

            MeshData mesh = Build(
                TestChunks.Snapshot(Origin, (centreCell, TestChunks.Stone)),
                TestChunks.Neighbours(Origin, (neighbourCoord, neighbourCell, TestChunks.Stone)));

            Assert.That(mesh.VertexCount, Is.EqualTo(20), $"the {dx},{dy},{dz} border face is hidden");
        }

        [Test]
        public void NegativeCoordinateCentreChunkCullsAgainstItsWorldNeighbour()
        {
            var centre = new ChunkCoord(-2, 3, -1);
            var west = new ChunkCoord(-3, 3, -1);

            MeshData mesh = Build(
                TestChunks.Snapshot(centre, (Min, TestChunks.Stone)),
                TestChunks.Neighbours(centre, (west, new Int3(15, 0, 0), TestChunks.Stone)));

            Assert.That(mesh.VertexCount, Is.EqualTo(20), "the -X border face is culled through negative world coordinates");
            Assert.That(mesh.Min, Is.EqualTo(new Vector3f(0.0F, 0.0F, 0.0F)));
            Assert.That(mesh.Max, Is.EqualTo(new Vector3f(1.0F, 1.0F, 1.0F)));
        }

        [Test]
        public void BorderFaceWithoutANeighbourIsEmitted()
        {
            MeshData mesh = Build(
                TestChunks.Snapshot(Origin, (Max, TestChunks.Stone)),
                NeighbourSnapshot.Empty);

            Assert.That(mesh.VertexCount, Is.EqualTo(24), "an absent neighbour reads as air");
        }

        [Test]
        public void BuildRentsFromTheInjectedPool()
        {
            var pool = new MeshBufferPool();
            var mesher = new CulledMesher(Layout, pool);

            MeshData mesh = mesher.Build(
                TestChunks.Snapshot(Origin, (Min, TestChunks.Stone)),
                NeighbourSnapshot.Empty,
                TestChunks.Registry);

            Assert.That(pool.OutstandingBuffers, Is.GreaterThan(0L), "the build must rent from the injected pool");
            mesh.Release();
            Assert.That(pool.OutstandingBuffers, Is.Zero, "release must return every buffer to the injected pool");
        }

        [Test]
        public void ConstructorsRejectNullArguments()
        {
            Assert.Throws<ArgumentNullException>(() => new CulledMesher((AtlasLayout)null!));
            Assert.Throws<ArgumentNullException>(() => new CulledMesher(Layout, (MeshBufferPool)null!));
        }

        [Test]
        public void FullySolidChunkEmitsOnlySurfaceQuads()
        {
            MeshData mesh = Build(
                TestChunks.FilledSnapshot(Origin, TestChunks.Stone),
                NeighbourSnapshot.Empty);

            Assert.That(mesh.VertexCount, Is.EqualTo(6 * 256 * 4));
            Assert.That(mesh.IndexCount, Is.EqualTo(6 * 256 * 6));
        }

        [Test]
        public void EveryQuadWindsCounterClockwiseSeenFromOutside()
        {
            MeshData mesh = Build(
                TestChunks.FilledSnapshot(Origin, TestChunks.Stone),
                NeighbourSnapshot.Empty);

            int quads = mesh.VertexCount / 4;
            for (int quad = 0; quad < quads; quad++)
            {
                int first = quad * 4;
                Vector3f a = mesh.Positions.Span[first + 0];
                Vector3f b = mesh.Positions.Span[first + 1];
                Vector3f c = mesh.Positions.Span[first + 2];
                Vector3f normal = mesh.Normals.Span[first];

                Vector3f cross = Cross(Subtract(b, a), Subtract(c, a));
                Assert.That(cross, Is.EqualTo(normal), $"quad {quad} must wind around its outward normal");
            }
        }

        [Test]
        public void EveryQuadUsesTheFixedTriangleIndexPattern()
        {
            MeshData mesh = Build(
                TestChunks.Snapshot(Origin, (Min, TestChunks.Stone)),
                NeighbourSnapshot.Empty);

            for (int quad = 0; quad < mesh.VertexCount / 4; quad++)
            {
                int vertex = quad * 4;
                int index = quad * 6;
                Assert.That(mesh.Indices.Span[index + 0], Is.EqualTo(vertex + 0));
                Assert.That(mesh.Indices.Span[index + 1], Is.EqualTo(vertex + 1));
                Assert.That(mesh.Indices.Span[index + 2], Is.EqualTo(vertex + 2));
                Assert.That(mesh.Indices.Span[index + 3], Is.EqualTo(vertex + 0));
                Assert.That(mesh.Indices.Span[index + 4], Is.EqualTo(vertex + 2));
                Assert.That(mesh.Indices.Span[index + 5], Is.EqualTo(vertex + 3));
            }
        }

        [Test]
        public void UvsComeFromTheAtlasIndexSelectedForEachFace()
        {
            MeshData mesh = Build(
                TestChunks.Snapshot(Origin, (Min, TestChunks.Grass)),
                NeighbourSnapshot.Empty);

            AssertQuadUsesTile(mesh, 0, AtlasIndexToTile(4), "side +X");
            AssertQuadUsesTile(mesh, 1, AtlasIndexToTile(4), "side -X");
            AssertQuadUsesTile(mesh, 2, AtlasIndexToTile(3), "top +Y");
            AssertQuadUsesTile(mesh, 3, AtlasIndexToTile(3), "bottom -Y");
            AssertQuadUsesTile(mesh, 4, AtlasIndexToTile(4), "front +Z");
            AssertQuadUsesTile(mesh, 5, AtlasIndexToTile(4), "front -Z");
        }

        [Test]
        public void UvsOfASolidChunkStayInsideTheirTileRects()
        {
            MeshData mesh = Build(
                TestChunks.FilledSnapshot(Origin, TestChunks.Stone),
                NeighbourSnapshot.Empty);

            float size = AtlasMap.TileUvSize(Layout).X;
            float uMin = AtlasMap.TileMin(Layout, 1).X;
            float vMin = AtlasMap.TileMin(Layout, 1).Y;
            float uMax = uMin + size;
            float vMax = vMin + size;
            for (int i = 0; i < mesh.Uvs.Length; i++)
            {
                Vector2f uv = mesh.Uvs.Span[i];
                Assert.That(uv.X, Is.InRange(uMin, uMax), $"uv {i} X");
                Assert.That(uv.Y, Is.InRange(vMin, vMax), $"uv {i} Y");
            }
        }

        [Test]
        public void SameInputProducesByteIdenticalStreams()
        {
            ChunkSnapshot chunk = TestChunks.FilledSnapshot(Origin, TestChunks.Stone);
            NeighbourSnapshot neighbours = NeighbourSnapshot.Empty;

            MeshData first = Build(chunk, neighbours);
            MeshData second = Build(chunk, neighbours);

            AssertBytesEqual(first.Positions, second.Positions);
            AssertBytesEqual(first.Normals, second.Normals);
            AssertBytesEqual(first.Uvs, second.Uvs);
            AssertBytesEqual(first.Ao, second.Ao);
            AssertBytesEqual(first.Indices, second.Indices);

            first.Release();
            second.Release();
        }

        [Test]
        public void ReleaseReturnsBuffersExactlyOnce()
        {
            MeshData mesh = Build(
                TestChunks.Snapshot(Origin, (Min, TestChunks.Stone)),
                NeighbourSnapshot.Empty);

            mesh.Release();

            Assert.That(mesh.Positions.IsEmpty, Is.True);
            Assert.That(mesh.Normals.IsEmpty, Is.True);
            Assert.That(mesh.Uvs.IsEmpty, Is.True);
            Assert.That(mesh.Ao.IsEmpty, Is.True);
            Assert.That(mesh.Indices.IsEmpty, Is.True);
            Assert.Throws<InvalidOperationException>(() => mesh.Release());
        }

        [Test]
        public void BoundsCoverEveryVertex()
        {
            MeshData mesh = Build(
                TestChunks.Snapshot(Origin, (new Int3(2, 3, 4), TestChunks.Stone)),
                NeighbourSnapshot.Empty);

            Assert.That(mesh.Min, Is.EqualTo(new Vector3f(2.0F, 3.0F, 4.0F)));
            Assert.That(mesh.Max, Is.EqualTo(new Vector3f(3.0F, 4.0F, 5.0F)));
        }

        [Test]
        public void EmptyChunkHasNoQuadsAndZeroBounds()
        {
            MeshData mesh = Build(TestChunks.Snapshot(Origin), NeighbourSnapshot.Empty);

            Assert.That(mesh.VertexCount, Is.Zero);
            Assert.That(mesh.IndexCount, Is.Zero);
            Assert.That(mesh.Min, Is.EqualTo(Vector3f.Zero));
            Assert.That(mesh.Max, Is.EqualTo(Vector3f.Zero));
        }

        [Test]
        public void UnoccludedBlockCarriesFullAo()
        {
            MeshData mesh = Build(
                TestChunks.Snapshot(Origin, (Min, TestChunks.Stone)),
                NeighbourSnapshot.Empty);

            Assert.That(mesh.Ao.Length, Is.EqualTo(mesh.VertexCount));
            for (int i = 0; i < mesh.Ao.Length; i++)
            {
                Assert.That(mesh.Ao.Span[i], Is.EqualTo(255), $"vertex {i} is unoccluded in open space");
            }
        }

        private static MeshData Build(ChunkSnapshot chunk, NeighbourSnapshot neighbours)
        {
            return Mesher.Build(chunk, neighbours, TestChunks.Registry);
        }

        private static Vector2f AtlasIndexToTile(int atlasIndex)
        {
            return AtlasMap.TileMin(Layout, atlasIndex);
        }

        private static void AssertQuadUsesTile(MeshData mesh, int quad, Vector2f tileMin, string because)
        {
            float size = AtlasMap.TileUvSize(Layout).X;
            var expected = new[]
            {
                tileMin,
                new Vector2f(tileMin.X + size, tileMin.Y),
                new Vector2f(tileMin.X + size, tileMin.Y + size),
                new Vector2f(tileMin.X, tileMin.Y + size),
            };

            for (int corner = 0; corner < 4; corner++)
            {
                Assert.That(
                    mesh.Uvs.Span[(quad * 4) + corner],
                    Is.EqualTo(expected[corner]),
                    $"{because}, corner {corner}");
            }
        }

        private static void AssertBytesEqual<T>(ReadOnlyMemory<T> first, ReadOnlyMemory<T> second)
            where T : struct
        {
            Assert.That(
                MemoryMarshal.AsBytes(first.Span).SequenceEqual(MemoryMarshal.AsBytes(second.Span)),
                Is.True,
                "the streams must be byte-identical");
        }

        private static Vector3f Subtract(Vector3f a, Vector3f b)
        {
            return new Vector3f(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        }

        private static Vector3f Cross(Vector3f a, Vector3f b)
        {
            return new Vector3f(
                (a.Y * b.Z) - (a.Z * b.Y),
                (a.Z * b.X) - (a.X * b.Z),
                (a.X * b.Y) - (a.Y * b.X));
        }
    }
}

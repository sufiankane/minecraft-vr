using System;
using System.Runtime.InteropServices;
using Cubeglass.CoreMath;
using Cubeglass.Voxel;
using NUnit.Framework;

namespace Cubeglass.Mesh.Tests
{
    [TestFixture]
    public sealed class GreedyMesherTests
    {
        private static readonly ChunkCoord Origin = new ChunkCoord(0, 0, 0);
        private static readonly ChunkCoord East = new ChunkCoord(1, 0, 0);
        private static readonly AtlasLayout Layout = new AtlasLayout(16, 16);
        private static readonly GreedyMesher Mesher = new GreedyMesher();

        private static readonly int[] MixedBlockFaceCounts = { 1, 1, 2, 2, 2, 2 };
        private static readonly int[] GlassPairFaceCounts = { 2, 2, 1, 1, 1, 1 };
        private static readonly int[] GlassStoneFaceCounts = { 1, 2, 2, 2, 2, 2 };

        private static readonly Vector3f[] Normals =
        {
            new Vector3f(1.0F, 0.0F, 0.0F),
            new Vector3f(-1.0F, 0.0F, 0.0F),
            new Vector3f(0.0F, 1.0F, 0.0F),
            new Vector3f(0.0F, -1.0F, 0.0F),
            new Vector3f(0.0F, 0.0F, 1.0F),
            new Vector3f(0.0F, 0.0F, -1.0F),
        };

        [Test]
        public void FullySolidChunkMergesToExactlySixQuads()
        {
            MeshData mesh = Build(TestChunks.FilledSnapshot(Origin, TestChunks.Stone), NeighbourSnapshot.Empty);

            Assert.That(mesh.VertexCount, Is.EqualTo(24), "one merged quad per face");
            Assert.That(mesh.IndexCount, Is.EqualTo(36));

            var expected = new[]
            {
                (Face: 0, Plane: 16, MinA: 0, MaxA: 16, MinB: 0, MaxB: 16),
                (Face: 1, Plane: 0, MinA: 0, MaxA: 16, MinB: 0, MaxB: 16),
                (Face: 2, Plane: 16, MinA: 0, MaxA: 16, MinB: 0, MaxB: 16),
                (Face: 3, Plane: 0, MinA: 0, MaxA: 16, MinB: 0, MaxB: 16),
                (Face: 4, Plane: 16, MinA: 0, MaxA: 16, MinB: 0, MaxB: 16),
                (Face: 5, Plane: 0, MinA: 0, MaxA: 16, MinB: 0, MaxB: 16),
            };

            for (int quad = 0; quad < expected.Length; quad++)
            {
                Assert.That(ReadQuad(mesh, quad), Is.EqualTo(expected[quad]), $"quad {quad} in emission order");
            }
        }

        [Test]
        public void MergedQuadUvsTileTheAtlasTileOncePerBlockCell()
        {
            MeshData mesh = Build(TestChunks.FilledSnapshot(Origin, TestChunks.Grass), NeighbourSnapshot.Empty);

            AssertMergedUvs(mesh, FindQuad(mesh, 0), 16, 16, AtlasMap.TileMin(Layout, 4), "side +X");
            AssertMergedUvs(mesh, FindQuad(mesh, 1), 16, 16, AtlasMap.TileMin(Layout, 4), "side -X");
            AssertMergedUvs(mesh, FindQuad(mesh, 2), 16, 16, AtlasMap.TileMin(Layout, 3), "top +Y");
            AssertMergedUvs(mesh, FindQuad(mesh, 3), 16, 16, AtlasMap.TileMin(Layout, 3), "bottom -Y");
            AssertMergedUvs(mesh, FindQuad(mesh, 4), 16, 16, AtlasMap.TileMin(Layout, 4), "front +Z");
            AssertMergedUvs(mesh, FindQuad(mesh, 5), 16, 16, AtlasMap.TileMin(Layout, 4), "front -Z");
        }

        [Test]
        public void SingleBlockMatchesTheReferenceMesherByteForByte()
        {
            ChunkSnapshot chunk = TestChunks.Snapshot(Origin, (new Int3(2, 3, 4), TestChunks.Stone));

            MeshData greedy = Build(chunk, NeighbourSnapshot.Empty);
            MeshData reference = new CulledMesher().Build(chunk, NeighbourSnapshot.Empty, TestChunks.Registry);

            Assert.That(greedy.VertexCount, Is.EqualTo(reference.VertexCount));
            Assert.That(greedy.IndexCount, Is.EqualTo(reference.IndexCount));
            AssertBytesEqual(greedy.Positions, reference.Positions);
            AssertBytesEqual(greedy.Normals, reference.Normals);
            AssertBytesEqual(greedy.Uvs, reference.Uvs);
            AssertBytesEqual(greedy.Ao, reference.Ao);
            AssertBytesEqual(greedy.Indices, reference.Indices);

            greedy.Release();
            reference.Release();
        }

        [Test]
        public void TwoAdjacentSameBlocksMergeEachExposedSide()
        {
            MeshData mesh = Build(
                TestChunks.Snapshot(Origin, (new Int3(0, 0, 0), TestChunks.Stone), (new Int3(1, 0, 0), TestChunks.Stone)),
                NeighbourSnapshot.Empty);

            Assert.That(mesh.VertexCount, Is.EqualTo(24), "six quads: four long sides plus two end caps");

            var expected = new[]
            {
                (Face: 0, Plane: 2, MinA: 0, MaxA: 1, MinB: 0, MaxB: 1),
                (Face: 1, Plane: 0, MinA: 0, MaxA: 1, MinB: 0, MaxB: 1),
                (Face: 2, Plane: 1, MinA: 0, MaxA: 2, MinB: 0, MaxB: 1),
                (Face: 3, Plane: 0, MinA: 0, MaxA: 2, MinB: 0, MaxB: 1),
                (Face: 4, Plane: 1, MinA: 0, MaxA: 2, MinB: 0, MaxB: 1),
                (Face: 5, Plane: 0, MinA: 0, MaxA: 2, MinB: 0, MaxB: 1),
            };

            for (int quad = 0; quad < expected.Length; quad++)
            {
                Assert.That(ReadQuad(mesh, quad), Is.EqualTo(expected[quad]), $"quad {quad} in emission order");
            }

            AssertMergedUvs(mesh, FindQuad(mesh, 2), 1, 2, AtlasMap.TileMin(Layout, 1), "top +Y spans two cells in X");
        }

        [Test]
        public void DifferentBlockIdsDoNotMerge()
        {
            MeshData mesh = Build(
                TestChunks.Snapshot(Origin, (new Int3(0, 0, 0), TestChunks.Stone), (new Int3(1, 0, 0), TestChunks.Grass)),
                NeighbourSnapshot.Empty);

            Assert.That(mesh.VertexCount, Is.EqualTo(40), "two 1x1 quads on every shared plane, one per block");
            Assert.That(CountByFace(mesh), Is.EqualTo(MixedBlockFaceCounts));
        }

        [Test]
        public void DifferentFaceOrientationsDoNotMerge()
        {
            MeshData mesh = Build(
                TestChunks.Snapshot(Origin, (new Int3(0, 0, 0), TestChunks.Glass), (new Int3(1, 0, 0), TestChunks.Glass)),
                NeighbourSnapshot.Empty);

            Assert.That(mesh.VertexCount, Is.EqualTo(32), "eight quads: the two facing quads at x=1 stay separate");
            Assert.That(CountByFace(mesh), Is.EqualTo(GlassPairFaceCounts));
            Assert.That(FindQuadAt(mesh, 0, 1), Is.Not.Negative, "+X quad at the x=1 plane");
            Assert.That(FindQuadAt(mesh, 1, 1), Is.Not.Negative, "-X quad at the x=1 plane");
        }

        [Test]
        public void NonOpaqueAndOpaqueNeverMergeOrCull()
        {
            MeshData mesh = Build(
                TestChunks.Snapshot(Origin, (new Int3(0, 0, 0), TestChunks.Glass), (new Int3(1, 0, 0), TestChunks.Stone)),
                NeighbourSnapshot.Empty);

            Assert.That(mesh.VertexCount, Is.EqualTo(44), "glass keeps five faces, stone keeps six: opaque culls, non-opaque never does");
            Assert.That(CountByFace(mesh), Is.EqualTo(GlassStoneFaceCounts));
            Assert.That(FindQuadAt(mesh, 0, 1), Is.EqualTo(-1), "glass +X against stone is culled");
            Assert.That(FindQuadAt(mesh, 1, 1), Is.Not.Negative, "stone -X against glass is emitted");
        }

        [Test]
        public void BorderFaceAgainstAnOpaqueNeighbourIsCulled()
        {
            MeshData mesh = Build(
                TestChunks.Snapshot(Origin, (new Int3(15, 0, 0), TestChunks.Stone)),
                TestChunks.Neighbours(Origin, (East, new Int3(0, 0, 0), TestChunks.Stone)));

            Assert.That(mesh.VertexCount, Is.EqualTo(20), "the +X border face is hidden by the neighbour");
        }

        [Test]
        public void BorderFaceAgainstANonOpaqueNeighbourIsEmitted()
        {
            MeshData mesh = Build(
                TestChunks.Snapshot(Origin, (new Int3(15, 0, 0), TestChunks.Stone)),
                TestChunks.Neighbours(Origin, (East, new Int3(0, 0, 0), TestChunks.Glass)));

            Assert.That(mesh.VertexCount, Is.EqualTo(24), "glass never culls a face");
        }

        [Test]
        public void SolidChunkSurroundedByOpaqueNeighboursEmitsNothing()
        {
            MeshData mesh = Build(TestChunks.FilledSnapshot(Origin, TestChunks.Stone), SolidNeighbours(Origin));

            Assert.That(mesh.VertexCount, Is.Zero);
            Assert.That(mesh.IndexCount, Is.Zero);
        }

        [Test]
        public void EmptyChunkEmitsNothing()
        {
            MeshData mesh = Build(TestChunks.Snapshot(Origin), NeighbourSnapshot.Empty);

            Assert.That(mesh.VertexCount, Is.Zero);
            Assert.That(mesh.IndexCount, Is.Zero);
            Assert.That(mesh.Min, Is.EqualTo(Vector3f.Zero));
            Assert.That(mesh.Max, Is.EqualTo(Vector3f.Zero));
        }

        [Test]
        public void EveryMergedQuadWindsAroundItsOutwardNormal()
        {
            MeshData mesh = Build(TestChunks.FilledSnapshot(Origin, TestChunks.Stone), NeighbourSnapshot.Empty);

            int quads = mesh.VertexCount / 4;
            for (int quad = 0; quad < quads; quad++)
            {
                int first = quad * 4;
                Vector3f a = mesh.Positions.Span[first + 0];
                Vector3f b = mesh.Positions.Span[first + 1];
                Vector3f c = mesh.Positions.Span[first + 2];
                Vector3f normal = mesh.Normals.Span[first];
                var decoded = ReadQuad(mesh, quad);
                float area = (decoded.MaxA - decoded.MinA) * (decoded.MaxB - decoded.MinB);

                Vector3f cross = Cross(Subtract(b, a), Subtract(c, a));
                var expected = new Vector3f(normal.X * area, normal.Y * area, normal.Z * area);
                Assert.That(cross, Is.EqualTo(expected), $"quad {quad} must wind around its outward normal");
            }
        }

        [Test]
        public void EveryMergedQuadUsesTheFixedTriangleIndexPattern()
        {
            MeshData mesh = Build(TestChunks.FilledSnapshot(Origin, TestChunks.Stone), NeighbourSnapshot.Empty);

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
        public void AoStreamHasOneFullOpenBytePerMergedVertex()
        {
            MeshData mesh = Build(TestChunks.FilledSnapshot(Origin, TestChunks.Stone), NeighbourSnapshot.Empty);

            Assert.That(mesh.Ao.Length, Is.EqualTo(mesh.VertexCount));
            for (int i = 0; i < mesh.Ao.Length; i++)
            {
                Assert.That(mesh.Ao.Span[i], Is.EqualTo(255), $"vertex {i} starts unoccluded before Task 3");
            }
        }

        [Test]
        public void SameInputProducesByteIdenticalStreamsAcrossInstances()
        {
            ChunkSnapshot chunk = TestChunks.Snapshot(
                Origin,
                (new Int3(0, 0, 0), TestChunks.Stone),
                (new Int3(1, 0, 0), TestChunks.Glass),
                (new Int3(1, 1, 0), TestChunks.Grass),
                (new Int3(15, 15, 15), TestChunks.Stone));
            NeighbourSnapshot neighbours = NeighbourSnapshot.Empty;

            var firstMesher = new GreedyMesher();
            var secondMesher = new GreedyMesher();
            MeshData first = firstMesher.Build(chunk, neighbours, TestChunks.Registry);
            MeshData second = secondMesher.Build(chunk, neighbours, TestChunks.Registry);

            AssertBytesEqual(first.Positions, second.Positions);
            AssertBytesEqual(first.Normals, second.Normals);
            AssertBytesEqual(first.Uvs, second.Uvs);
            AssertBytesEqual(first.Ao, second.Ao);
            AssertBytesEqual(first.Indices, second.Indices);

            first.Release();
            second.Release();
        }

        private static MeshData Build(ChunkSnapshot chunk, NeighbourSnapshot neighbours)
        {
            return Mesher.Build(chunk, neighbours, TestChunks.Registry);
        }

        private static (int Face, int Plane, int MinA, int MaxA, int MinB, int MaxB) ReadQuad(MeshData mesh, int quad)
        {
            int first = quad * 4;
            Vector3f normal = mesh.Normals.Span[first];
            int face = FaceOf(normal);
            int axis = face >> 1;
            int aAxis = axis == 0 ? 1 : 0;
            int bAxis = axis == 2 ? 1 : 2;
            int plane = -1;
            int minA = int.MaxValue;
            int maxA = int.MinValue;
            int minB = int.MaxValue;
            int maxB = int.MinValue;

            for (int corner = 0; corner < 4; corner++)
            {
                Vector3f position = mesh.Positions.Span[first + corner];
                int along = Component(position, axis);
                if (plane < 0)
                {
                    plane = along;
                }

                Assert.That(along, Is.EqualTo(plane), $"quad {quad} corner {corner} is planar");
                int a = Component(position, aAxis);
                int b = Component(position, bAxis);
                minA = Math.Min(minA, a);
                maxA = Math.Max(maxA, a);
                minB = Math.Min(minB, b);
                maxB = Math.Max(maxB, b);
            }

            return (face, plane, minA, maxA, minB, maxB);
        }

        private static int FaceOf(Vector3f normal)
        {
            for (int face = 0; face < Normals.Length; face++)
            {
                if (Normals[face] == normal)
                {
                    return face;
                }
            }

            Assert.Fail($"unexpected merged-quad normal {normal}");
            return -1;
        }

        private static int FindQuad(MeshData mesh, int face)
        {
            int found = -1;
            for (int quad = 0; quad < mesh.VertexCount / 4; quad++)
            {
                if (ReadQuad(mesh, quad).Face == face)
                {
                    Assert.That(found, Is.EqualTo(-1), $"exactly one {face} quad");
                    found = quad;
                }
            }

            Assert.That(found, Is.Not.EqualTo(-1), $"a {face} quad exists");
            return found;
        }

        private static int FindQuadAt(MeshData mesh, int face, int plane)
        {
            for (int quad = 0; quad < mesh.VertexCount / 4; quad++)
            {
                var decoded = ReadQuad(mesh, quad);
                if (decoded.Face == face && decoded.Plane == plane)
                {
                    return quad;
                }
            }

            return -1;
        }

        private static int[] CountByFace(MeshData mesh)
        {
            var counts = new int[6];
            for (int quad = 0; quad < mesh.VertexCount / 4; quad++)
            {
                counts[ReadQuad(mesh, quad).Face]++;
            }

            return counts;
        }

        private static void AssertMergedUvs(MeshData mesh, int quad, int uExtent, int vExtent, Vector2f tileMin, string because)
        {
            Vector2f size = AtlasMap.TileUvSize(Layout);
            int[] cornerU = { 0, 1, 1, 0 };
            int[] cornerV = { 0, 0, 1, 1 };

            for (int corner = 0; corner < 4; corner++)
            {
                var expected = new Vector2f(
                    tileMin.X + (cornerU[corner] * uExtent * size.X),
                    tileMin.Y + (cornerV[corner] * vExtent * size.Y));
                Assert.That(mesh.Uvs.Span[(quad * 4) + corner], Is.EqualTo(expected), $"{because}, corner {corner}");
            }
        }

        private static int Component(Vector3f position, int axis)
        {
            switch (axis)
            {
                case 0:
                    return (int)position.X;
                case 1:
                    return (int)position.Y;
                default:
                    return (int)position.Z;
            }
        }

        private static NeighbourSnapshot SolidNeighbours(ChunkCoord center)
        {
            var world = new World();
            world.LoadChunk(new Chunk(center));
            Int3[] directions =
            {
                new Int3(1, 0, 0),
                new Int3(-1, 0, 0),
                new Int3(0, 1, 0),
                new Int3(0, -1, 0),
                new Int3(0, 0, 1),
                new Int3(0, 0, -1),
            };

            foreach (Int3 direction in directions)
            {
                var coord = new ChunkCoord(center.X + direction.X, center.Y + direction.Y, center.Z + direction.Z);
                var neighbour = new Chunk(coord);
                world.LoadChunk(neighbour);
                for (int z = 0; z < ChunkMath.ChunkSize; z++)
                {
                    for (int y = 0; y < ChunkMath.ChunkSize; y++)
                    {
                        for (int x = 0; x < ChunkMath.ChunkSize; x++)
                        {
                            var command = new EditCommand(
                                ChunkMath.ToWorld(coord, new Int3(x, y, z)),
                                BlockId.Air,
                                TestChunks.Stone,
                                0L);
                            world.Apply(in command);
                        }
                    }
                }
            }

            return world.CreateNeighbourSnapshot(center);
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

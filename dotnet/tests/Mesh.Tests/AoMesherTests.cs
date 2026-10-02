using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Cubeglass.CoreMath;
using Cubeglass.Voxel;
using NUnit.Framework;

namespace Cubeglass.Mesh.Tests
{
    /// <summary>
    /// Pins the ADR-0007 ambient-occlusion formula and its byte quantisation
    /// independently of any mesher.
    /// </summary>
    [TestFixture]
    public sealed class AoRuleTests
    {
        [Test]
        public void LevelIsThreeWhenEveryNeighbourIsOpen()
        {
            Assert.That(AmbientOcclusion.Level(false, false, false), Is.EqualTo(3));
        }

        [Test]
        public void OneOpaqueNeighbourCostsOneLevel()
        {
            Assert.That(AmbientOcclusion.Level(true, false, false), Is.EqualTo(2));
            Assert.That(AmbientOcclusion.Level(false, true, false), Is.EqualTo(2));
            Assert.That(AmbientOcclusion.Level(false, false, true), Is.EqualTo(2));
        }

        [Test]
        public void TwoOpaqueNeighboursCostTwoLevels()
        {
            Assert.That(AmbientOcclusion.Level(true, false, true), Is.EqualTo(1));
            Assert.That(AmbientOcclusion.Level(false, true, true), Is.EqualTo(1));
        }

        [Test]
        public void BothSidesOpaqueIsZeroEvenWhenTheCornerIsOpen()
        {
            Assert.That(AmbientOcclusion.Level(true, true, false), Is.EqualTo(0));
            Assert.That(AmbientOcclusion.Level(true, true, true), Is.EqualTo(0));
        }

        [Test]
        public void LevelIsSymmetricInTheTwoSides()
        {
            for (int pattern = 0; pattern < 8; pattern++)
            {
                bool side1 = (pattern & 1) != 0;
                bool side2 = (pattern & 2) != 0;
                bool corner = (pattern & 4) != 0;
                Assert.That(
                    AmbientOcclusion.Level(side1, side2, corner),
                    Is.EqualTo(AmbientOcclusion.Level(side2, side1, corner)),
                    $"pattern {pattern} must not depend on the side order");
            }
        }

        [Test]
        public void EncodeQuantisesLevelsToThePinnedBytes()
        {
            Assert.That(AmbientOcclusion.Encode(0), Is.EqualTo(0));
            Assert.That(AmbientOcclusion.Encode(1), Is.EqualTo(85));
            Assert.That(AmbientOcclusion.Encode(2), Is.EqualTo(170));
            Assert.That(AmbientOcclusion.Encode(3), Is.EqualTo(255));
        }

        [Test]
        public void EncodeRejectsLevelsOutsideZeroToThree()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => AmbientOcclusion.Encode(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => AmbientOcclusion.Encode(4));
        }
    }

    /// <summary>
    /// AO hand cases on both meshers: the three-neighbour rule, the ADR-0007
    /// corner order, neighbour-snapshot reads at borders, the conservative
    /// merge rule (R22) and cross-mesher/determinism invariants.
    /// </summary>
    [TestFixture]
    public sealed class AoMesherTests
    {
        private static readonly ChunkCoord Origin = new ChunkCoord(0, 0, 0);
        private static readonly ChunkCoord East = new ChunkCoord(1, 0, 0);

        private static readonly CulledMesher Reference = new CulledMesher();
        private static readonly GreedyMesher Greedy = new GreedyMesher();

        private static readonly Vector3f[] Normals =
        {
            new Vector3f(1.0F, 0.0F, 0.0F),
            new Vector3f(-1.0F, 0.0F, 0.0F),
            new Vector3f(0.0F, 1.0F, 0.0F),
            new Vector3f(0.0F, -1.0F, 0.0F),
            new Vector3f(0.0F, 0.0F, 1.0F),
            new Vector3f(0.0F, 0.0F, -1.0F),
        };

        private static readonly byte[] FullyOpen = { 255, 255, 255, 255 };
        private static readonly byte[] OneSide = { 255, 255, 170, 170 };
        private static readonly byte[] MirrorOneSide = { 255, 170, 170, 255 };
        private static readonly byte[] CornerOnly = { 170, 255, 255, 255 };
        private static readonly byte[] TwoOccluded = { 85, 170, 255, 255 };
        private static readonly byte[] SidesAndCorner = { 0, 170, 255, 170 };

        [TestCase(false)]
        [TestCase(true)]
        public void SingleBlockInOpenSpaceHasFullyOpenAo(bool greedy)
        {
            var blocks = new (Int3 Local, BlockId Block)[] { (new Int3(5, 5, 5), TestChunks.Stone) };
            MeshData mesh = Build(TestChunks.Snapshot(Origin, blocks), NeighbourSnapshot.Empty, greedy);

            Assert.That(mesh.VertexCount, Is.EqualTo(24));
            for (int i = 0; i < mesh.Ao.Length; i++)
            {
                Assert.That(mesh.Ao.Span[i], Is.EqualTo(255), $"vertex {i} is unoccluded");
            }

            mesh.Release();
        }

        [TestCase(false)]
        [TestCase(true)]
        public void OneOpaqueSideNeighbourShadesTheTwoTouchingCorners(bool greedy)
        {
            var blocks = new (Int3 Local, BlockId Block)[]
            {
                (new Int3(5, 5, 5), TestChunks.Stone),
                (new Int3(6, 5, 6), TestChunks.Stone),
            };
            MeshData mesh = Build(TestChunks.Snapshot(Origin, blocks), NeighbourSnapshot.Empty, greedy);

            AssertQuadAo(mesh, 0, 6, OneSide, "+X face of (5,5,5): (6,5,6) is the +Z side cell of corners 2 and 3");
            AssertQuadAo(mesh, 4, 6, MirrorOneSide, "+Z face of (5,5,5): (6,5,6) is the +X side cell of corners 1 and 2");

            mesh.Release();
        }

        [TestCase(false)]
        [TestCase(true)]
        public void TwoOpaqueNeighboursShadeOneCornerToLevelOne(bool greedy)
        {
            var blocks = new (Int3 Local, BlockId Block)[]
            {
                (new Int3(5, 5, 5), TestChunks.Stone),
                (new Int3(6, 5, 4), TestChunks.Stone),
                (new Int3(6, 4, 4), TestChunks.Stone),
            };
            MeshData mesh = Build(TestChunks.Snapshot(Origin, blocks), NeighbourSnapshot.Empty, greedy);

            AssertQuadAo(mesh, 0, 6, TwoOccluded, "+X face of (5,5,5): corner 0 has one side and the corner cell opaque");

            mesh.Release();
        }

        [TestCase(false)]
        [TestCase(true)]
        public void BothSidesAndTheCornerOccludeToZero(bool greedy)
        {
            var blocks = new (Int3 Local, BlockId Block)[]
            {
                (new Int3(5, 5, 5), TestChunks.Stone),
                (new Int3(6, 4, 5), TestChunks.Stone),
                (new Int3(6, 5, 4), TestChunks.Stone),
                (new Int3(6, 4, 4), TestChunks.Stone),
            };
            MeshData mesh = Build(TestChunks.Snapshot(Origin, blocks), NeighbourSnapshot.Empty, greedy);

            AssertQuadAo(mesh, 0, 6, SidesAndCorner, "+X face of (5,5,5): corner 0 is fully occluded");

            mesh.Release();
        }

        [TestCase(false)]
        [TestCase(true)]
        public void OnlyTheCornerNeighbourShadesToLevelTwo(bool greedy)
        {
            var blocks = new (Int3 Local, BlockId Block)[]
            {
                (new Int3(5, 5, 5), TestChunks.Stone),
                (new Int3(6, 4, 4), TestChunks.Stone),
            };
            MeshData mesh = Build(TestChunks.Snapshot(Origin, blocks), NeighbourSnapshot.Empty, greedy);

            AssertQuadAo(mesh, 0, 6, CornerOnly, "+X face of (5,5,5): only the diagonal cell shades corner 0");

            mesh.Release();
        }

        [TestCase(false)]
        [TestCase(true)]
        public void AoRotatesWithTheOccluderAroundTheFaceNormal(bool greedy)
        {
            var occluders = new[]
            {
                new Int3(5, 6, 6),
                new Int3(6, 6, 5),
                new Int3(5, 6, 4),
                new Int3(4, 6, 5),
            };
            byte[] expected = { 255, 170, 170, 255 };

            for (int rotation = 0; rotation < occluders.Length; rotation++)
            {
                var blocks = new (Int3 Local, BlockId Block)[]
                {
                    (new Int3(5, 5, 5), TestChunks.Stone),
                    (occluders[rotation], TestChunks.Stone),
                };
                MeshData mesh = Build(TestChunks.Snapshot(Origin, blocks), NeighbourSnapshot.Empty, greedy);

                AssertQuadAo(
                    mesh,
                    2,
                    6,
                    expected,
                    $"+Y face of (5,5,5): occluder {occluders[rotation]} must shade the rotated corner pair");

                expected = RotateRight(expected);
                mesh.Release();
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void AoAtABorderReadsTheNeighbourSnapshot(bool greedy)
        {
            var blocks = new (Int3 Local, BlockId Block)[] { (new Int3(15, 5, 5), TestChunks.Stone) };
            NeighbourSnapshot neighbours = TestChunks.Neighbours(Origin, (East, new Int3(0, 5, 6), TestChunks.Stone));
            MeshData mesh = Build(TestChunks.Snapshot(Origin, blocks), neighbours, greedy);

            AssertQuadAo(mesh, 0, 16, OneSide, "+X border face of (15,5,5): the occluder lives in the east chunk");

            mesh.Release();
        }

        [TestCase(false)]
        [TestCase(true)]
        public void AnUnloadedNeighbourReadsAsAirAndNeverShades(bool greedy)
        {
            var blocks = new (Int3 Local, BlockId Block)[] { (new Int3(15, 5, 5), TestChunks.Stone) };
            MeshData mesh = Build(TestChunks.Snapshot(Origin, blocks), NeighbourSnapshot.Empty, greedy);

            AssertQuadAo(mesh, 0, 16, FullyOpen, "+X border face of (15,5,5): no loaded neighbour, no shading");

            mesh.Release();
        }

        [Test]
        public void GreedyDoesNotMergeCellsWhosePerCellAoDiffers()
        {
            var blocks = new (Int3 Local, BlockId Block)[]
            {
                (new Int3(5, 5, 5), TestChunks.Stone),
                (new Int3(6, 5, 5), TestChunks.Stone),
                (new Int3(4, 6, 5), TestChunks.Stone),
            };
            MeshData mesh = Build(TestChunks.Snapshot(Origin, blocks), NeighbourSnapshot.Empty, greedy: true);

            List<int> quads = QuadsAt(mesh, 2, 6);
            Assert.That(quads.Count, Is.EqualTo(2), "(5,5,5) is shaded by (4,6,5) but (6,5,5) is open: no merge");
            Assert.That(QuadAo(mesh, quads[0]), Is.EqualTo(new byte[] { 170, 170, 255, 255 }));
            Assert.That(QuadAo(mesh, quads[1]), Is.EqualTo(FullyOpen));

            mesh.Release();
        }

        [Test]
        public void GreedyMergesCellsWithIdenticalPerCellAoAndCarriesThatAo()
        {
            var blocks = new (Int3 Local, BlockId Block)[]
            {
                (new Int3(5, 5, 5), TestChunks.Stone),
                (new Int3(6, 5, 5), TestChunks.Stone),
                (new Int3(4, 6, 4), TestChunks.Stone),
                (new Int3(5, 6, 4), TestChunks.Stone),
                (new Int3(6, 6, 4), TestChunks.Stone),
                (new Int3(7, 6, 4), TestChunks.Stone),
            };
            MeshData mesh = Build(TestChunks.Snapshot(Origin, blocks), NeighbourSnapshot.Empty, greedy: true);

            List<int> quads = QuadsAt(mesh, 2, 6);
            Assert.That(quads.Count, Is.EqualTo(1), "identical per-cell AO must merge into one quad");
            Assert.That(ReadQuad(mesh, quads[0]), Is.EqualTo((2, 6, 5, 7, 5, 6)));
            Assert.That(QuadAo(mesh, quads[0]), Is.EqualTo(new byte[] { 85, 255, 255, 85 }));

            mesh.Release();
        }

        [Test]
        public void GreedyAoMatchesTheReferenceAtEverySharedVertex()
        {
            ChunkSnapshot chunk = RandomAoSnapshot(2026);
            MeshData greedy = Greedy.Build(chunk, NeighbourSnapshot.Empty, TestChunks.Registry);
            MeshData reference = Reference.Build(chunk, NeighbourSnapshot.Empty, TestChunks.Registry);

            Dictionary<(int X, int Y, int Z, int Face), byte> greedyAo = VertexAo(greedy);
            Dictionary<(int X, int Y, int Z, int Face), byte> referenceAo = VertexAo(reference);

            int shared = 0;
            int mismatches = 0;
            bool shaded = false;
            foreach (KeyValuePair<(int X, int Y, int Z, int Face), byte> pair in greedyAo)
            {
                if (pair.Value != 255)
                {
                    shaded = true;
                }

                if (referenceAo.TryGetValue(pair.Key, out byte other))
                {
                    shared++;
                    if (other != pair.Value)
                    {
                        mismatches++;
                    }
                }
            }

            Assert.That(shaded, Is.True, "the corpus must contain real occlusion");
            Assert.That(shared, Is.GreaterThan(1000), "most greedy vertices must exist in the reference mesh");
            Assert.That(mismatches, Is.Zero, "AO must not depend on the mesher");

            greedy.Release();
            reference.Release();
        }

        [TestCase(false)]
        [TestCase(true)]
        public void SameAoHeavyInputIsByteIdenticalAcrossInstances(bool greedy)
        {
            ChunkSnapshot chunk = RandomAoSnapshot(99);
            IChunkMesher first = greedy ? (IChunkMesher)new GreedyMesher() : new CulledMesher();
            IChunkMesher second = greedy ? (IChunkMesher)new GreedyMesher() : new CulledMesher();
            MeshData firstMesh = first.Build(chunk, NeighbourSnapshot.Empty, TestChunks.Registry);
            MeshData secondMesh = second.Build(chunk, NeighbourSnapshot.Empty, TestChunks.Registry);

            AssertBytesEqual(firstMesh.Positions, secondMesh.Positions);
            AssertBytesEqual(firstMesh.Normals, secondMesh.Normals);
            AssertBytesEqual(firstMesh.Uvs, secondMesh.Uvs);
            AssertBytesEqual(firstMesh.Ao, secondMesh.Ao);
            AssertBytesEqual(firstMesh.Indices, secondMesh.Indices);

            firstMesh.Release();
            secondMesh.Release();
        }

        private static MeshData Build(ChunkSnapshot chunk, NeighbourSnapshot neighbours, bool greedy)
        {
            return greedy
                ? Greedy.Build(chunk, neighbours, TestChunks.Registry)
                : Reference.Build(chunk, neighbours, TestChunks.Registry);
        }

        private static ChunkSnapshot RandomAoSnapshot(int seed)
        {
            var random = new Random(seed);
            var world = new World();
            var chunk = new Chunk(Origin);
            world.LoadChunk(chunk);
            for (int z = 0; z < ChunkMath.ChunkSize; z++)
            {
                for (int y = 0; y < ChunkMath.ChunkSize; y++)
                {
                    for (int x = 0; x < ChunkMath.ChunkSize; x++)
                    {
                        if (random.Next(100) >= 35)
                        {
                            continue;
                        }

                        var block = new BlockId((ushort)(1 + random.Next(3)));
                        var command = new EditCommand(ChunkMath.ToWorld(Origin, new Int3(x, y, z)), BlockId.Air, block, 0L);
                        EditResult result = world.Apply(in command);
                        if (result != EditResult.Applied)
                        {
                            throw new InvalidOperationException($"Test setup failed at ({x},{y},{z}).");
                        }
                    }
                }
            }

            return chunk.Snapshot();
        }

        private static Dictionary<(int X, int Y, int Z, int Face), byte> VertexAo(MeshData mesh)
        {
            var result = new Dictionary<(int X, int Y, int Z, int Face), byte>();
            for (int i = 0; i < mesh.VertexCount; i++)
            {
                Vector3f position = mesh.Positions.Span[i];
                int face = FaceOf(mesh.Normals.Span[i]);
                var key = ((int)position.X, (int)position.Y, (int)position.Z, face);
                byte value = mesh.Ao.Span[i];
                if (result.TryGetValue(key, out byte existing))
                {
                    Assert.That(existing, Is.EqualTo(value), $"mesh vertex {key} has conflicting AO");
                }
                else
                {
                    result.Add(key, value);
                }
            }

            return result;
        }

        private static void AssertQuadAo(MeshData mesh, int face, int plane, byte[] expected, string because)
        {
            List<int> quads = QuadsAt(mesh, face, plane);
            Assert.That(quads.Count, Is.EqualTo(1), $"{because} (found {quads.Count} quads)");
            Assert.That(QuadAo(mesh, quads[0]), Is.EqualTo(expected), because);
        }

        private static List<int> QuadsAt(MeshData mesh, int face, int plane)
        {
            var result = new List<int>();
            int axis = face >> 1;
            for (int quad = 0; quad < mesh.VertexCount / 4; quad++)
            {
                int first = quad * 4;
                if (FaceOf(mesh.Normals.Span[first]) != face)
                {
                    continue;
                }

                if (Component(mesh.Positions.Span[first], axis) == plane)
                {
                    result.Add(quad);
                }
            }

            return result;
        }

        private static (int Face, int Plane, int MinA, int MaxA, int MinB, int MaxB) ReadQuad(MeshData mesh, int quad)
        {
            int first = quad * 4;
            int face = FaceOf(mesh.Normals.Span[first]);
            int axis = face >> 1;
            int aAxis = axis == 0 ? 1 : 0;
            int bAxis = axis == 2 ? 1 : 2;
            int plane = Component(mesh.Positions.Span[first], axis);
            int minA = int.MaxValue;
            int maxA = int.MinValue;
            int minB = int.MaxValue;
            int maxB = int.MinValue;
            for (int corner = 0; corner < 4; corner++)
            {
                Vector3f position = mesh.Positions.Span[first + corner];
                Assert.That(Component(position, axis), Is.EqualTo(plane), $"quad {quad} corner {corner} is planar");
                int a = Component(position, aAxis);
                int b = Component(position, bAxis);
                minA = Math.Min(minA, a);
                maxA = Math.Max(maxA, a);
                minB = Math.Min(minB, b);
                maxB = Math.Max(maxB, b);
            }

            return (face, plane, minA, maxA, minB, maxB);
        }

        private static byte[] QuadAo(MeshData mesh, int quad)
        {
            var result = new byte[4];
            for (int corner = 0; corner < 4; corner++)
            {
                result[corner] = mesh.Ao.Span[(quad * 4) + corner];
            }

            return result;
        }

        private static byte[] RotateRight(byte[] pattern)
        {
            return new[] { pattern[3], pattern[0], pattern[1], pattern[2] };
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

            Assert.Fail($"unexpected normal {normal}");
            return -1;
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

        private static void AssertBytesEqual<T>(ReadOnlyMemory<T> first, ReadOnlyMemory<T> second)
            where T : struct
        {
            Assert.That(
                MemoryMarshal.AsBytes(first.Span).SequenceEqual(MemoryMarshal.AsBytes(second.Span)),
                Is.True,
                "the streams must be byte-identical");
        }
    }
}

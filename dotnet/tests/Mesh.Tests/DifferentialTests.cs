using System;
using Cubeglass.CoreMath;
using Cubeglass.Voxel;
using NUnit.Framework;

namespace Cubeglass.Mesh.Tests
{
    /// <summary>
    /// Differential property tests: on thousands of seeded random chunks the
    /// greedy mesher must cover exactly the exposed-face multiset of the
    /// reference culling mesher, and both must cover exactly the exposed faces
    /// implied by the chunk data (no face between two opaque cells, every
    /// exposed face present exactly once).
    /// </summary>
    /// <remarks>
    /// A canonical multiset of <c>(cell, direction)</c> faces is represented as
    /// a dense count vector of <c>16^3 * 6</c> entries (cell index = x + 16y +
    /// 256z, direction = the ADR-0007 face order). The reference contributes
    /// one entry per quad; the greedy mesher's merged quads are decoded back
    /// into the W x H cells they span on their plane; the independent oracle
    /// walks the chunk and counts every face whose neighbour is not opaque.
    /// </remarks>
    [TestFixture]
    public sealed class DifferentialTests
    {
        private const int CaseCount = 2000;
        private const int Seed = 20261001;
        private const int CellCount = 4096;
        private const int FaceCount = 6;

        private static readonly ChunkCoord Origin = new ChunkCoord(0, 0, 0);
        private static readonly GreedyMesher Greedy = new GreedyMesher();
        private static readonly CulledMesher Reference = new CulledMesher();

        private static readonly Int3[] FaceDirections =
        {
            new Int3(1, 0, 0),
            new Int3(-1, 0, 0),
            new Int3(0, 1, 0),
            new Int3(0, -1, 0),
            new Int3(0, 0, 1),
            new Int3(0, 0, -1),
        };

        private static readonly Vector3f[] FaceNormals =
        {
            new Vector3f(1.0F, 0.0F, 0.0F),
            new Vector3f(-1.0F, 0.0F, 0.0F),
            new Vector3f(0.0F, 1.0F, 0.0F),
            new Vector3f(0.0F, -1.0F, 0.0F),
            new Vector3f(0.0F, 0.0F, 1.0F),
            new Vector3f(0.0F, 0.0F, -1.0F),
        };

        [Test]
        public void GreedyCoversExactlyTheReferenceExposedFacesOnTwoThousandSeededChunks()
        {
            var random = new Random(Seed);
            var oracle = new int[CellCount * FaceCount];
            var referenceCounts = new int[CellCount * FaceCount];
            var greedyCounts = new int[CellCount * FaceCount];
            long totalExposed = 0;
            int chunksWithFaces = 0;
            int neighbourCases = 0;

            for (int i = 0; i < CaseCount; i++)
            {
                ChunkSnapshot chunk = RandomChunk(random);
                NeighbourSnapshot neighbours = (i % 8) == 0
                    ? RandomNeighbours(random, (i % 64) == 0 ? 2 : 0)
                    : NeighbourSnapshot.Empty;
                if ((i % 8) == 0)
                {
                    neighbourCases++;
                }

                Array.Clear(oracle, 0, oracle.Length);
                CountExposedFaces(chunk, neighbours, oracle);

                int exposed = 0;
                for (int index = 0; index < oracle.Length; index++)
                {
                    exposed += oracle[index];
                }

                totalExposed += exposed;
                if (exposed > 0)
                {
                    chunksWithFaces++;
                }

                Array.Clear(referenceCounts, 0, referenceCounts.Length);
                MeshData referenceMesh = Reference.Build(chunk, neighbours, TestChunks.Registry);
                DecodeQuads(referenceMesh, referenceCounts);
                referenceMesh.Release();

                Array.Clear(greedyCounts, 0, greedyCounts.Length);
                MeshData greedyMesh = Greedy.Build(chunk, neighbours, TestChunks.Registry);
                DecodeQuads(greedyMesh, greedyCounts);
                greedyMesh.Release();

                AssertCountsMatch(i, "greedy", greedyCounts, oracle);
                AssertCountsMatch(i, "reference", referenceCounts, oracle);
            }

            Assert.That(neighbourCases, Is.EqualTo(250), "every eighth case reads a neighbour snapshot");
            Assert.That(chunksWithFaces, Is.GreaterThan(1900), "the corpus is not vacuous");
            Assert.That(totalExposed, Is.GreaterThan(1000000), "millions of exposed faces compared");
        }

        private static ChunkSnapshot RandomChunk(Random random)
        {
            var world = new World();
            var chunk = new Chunk(Origin);
            world.LoadChunk(chunk);
            FillRandom(random, world, Origin);
            return chunk.Snapshot();
        }

        private static NeighbourSnapshot RandomNeighbours(Random random, int extra)
        {
            var world = new World();
            world.LoadChunk(new Chunk(Origin));
            var used = new bool[FaceDirections.Length];
            int count = 1 + extra;
            for (int i = 0; i < count; i++)
            {
                int pick;
                do
                {
                    pick = random.Next(FaceDirections.Length);
                }
                while (used[pick]);

                used[pick] = true;
                Int3 direction = FaceDirections[pick];
                var coord = new ChunkCoord(direction.X, direction.Y, direction.Z);
                world.LoadChunk(new Chunk(coord));
                FillRandom(random, world, coord);
            }

            return world.CreateNeighbourSnapshot(Origin);
        }

        private static void FillRandom(Random random, World world, ChunkCoord coord)
        {
            int fillPercent = random.Next(0, 61);
            for (int z = 0; z < ChunkMath.ChunkSize; z++)
            {
                for (int y = 0; y < ChunkMath.ChunkSize; y++)
                {
                    for (int x = 0; x < ChunkMath.ChunkSize; x++)
                    {
                        if (random.Next(100) >= fillPercent)
                        {
                            continue;
                        }

                        var block = new BlockId((ushort)(1 + random.Next(3)));
                        var command = new EditCommand(
                            ChunkMath.ToWorld(coord, new Int3(x, y, z)),
                            BlockId.Air,
                            block,
                            0L);
                        world.Apply(in command);
                    }
                }
            }
        }

        private static void CountExposedFaces(ChunkSnapshot chunk, NeighbourSnapshot neighbours, int[] counts)
        {
            for (int z = 0; z < ChunkMath.ChunkSize; z++)
            {
                for (int y = 0; y < ChunkMath.ChunkSize; y++)
                {
                    for (int x = 0; x < ChunkMath.ChunkSize; x++)
                    {
                        var local = new Int3(x, y, z);
                        if (chunk.Get(local) == BlockId.Air)
                        {
                            continue;
                        }

                        for (int face = 0; face < FaceCount; face++)
                        {
                            if (!IsOpaqueNeighbour(chunk, neighbours, local, FaceDirections[face]))
                            {
                                counts[(Index(local) * FaceCount) + face] = 1;
                            }
                        }
                    }
                }
            }
        }

        private static bool IsOpaqueNeighbour(
            ChunkSnapshot chunk,
            NeighbourSnapshot neighbours,
            Int3 local,
            Int3 direction)
        {
            int x = local.X + direction.X;
            int y = local.Y + direction.Y;
            int z = local.Z + direction.Z;

            BlockId neighbour;
            if (x >= 0 && x < ChunkMath.ChunkSize
                && y >= 0 && y < ChunkMath.ChunkSize
                && z >= 0 && z < ChunkMath.ChunkSize)
            {
                neighbour = chunk.Get(new Int3(x, y, z));
            }
            else
            {
                neighbour = neighbours.GetCell(ChunkMath.ToWorld(chunk.Coord, local) + direction);
            }

            return TestChunks.Registry.Get(neighbour).Opaque;
        }

        private static void DecodeQuads(MeshData mesh, int[] counts)
        {
            int quads = mesh.VertexCount / 4;
            for (int quad = 0; quad < quads; quad++)
            {
                int first = quad * 4;
                Vector3f normal = mesh.Normals.Span[first];
                int face = FaceOf(normal, quad);
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
                    if (mesh.Normals.Span[first + corner] != normal)
                    {
                        throw new InvalidOperationException($"quad {quad} mixes normals");
                    }

                    if (Component(position, axis) != plane)
                    {
                        throw new InvalidOperationException($"quad {quad} is not planar");
                    }

                    int a = Component(position, aAxis);
                    int b = Component(position, bAxis);
                    minA = Math.Min(minA, a);
                    maxA = Math.Max(maxA, a);
                    minB = Math.Min(minB, b);
                    maxB = Math.Max(maxB, b);
                }

                int corners = 0;
                for (int corner = 0; corner < 4; corner++)
                {
                    Vector3f position = mesh.Positions.Span[first + corner];
                    int a = Component(position, aAxis);
                    int b = Component(position, bAxis);
                    corners |= 1 << ((a == maxA ? 1 : 0) + (b == maxB ? 2 : 0));
                }

                if (corners != 15)
                {
                    throw new InvalidOperationException($"quad {quad} is not an axis-aligned rectangle");
                }

                int cellPlane = (face & 1) == 0 ? plane - 1 : plane;
                if (cellPlane < 0 || cellPlane >= ChunkMath.ChunkSize
                    || minA < 0 || maxA > ChunkMath.ChunkSize
                    || minB < 0 || maxB > ChunkMath.ChunkSize)
                {
                    throw new InvalidOperationException($"quad {quad} leaves the chunk");
                }

                for (int b = minB; b < maxB; b++)
                {
                    for (int a = minA; a < maxA; a++)
                    {
                        Int3 cell = CellAt(axis, cellPlane, aAxis, a, bAxis, b);
                        counts[(Index(cell) * FaceCount) + face]++;
                    }
                }
            }
        }

        private static int FaceOf(Vector3f normal, int quad)
        {
            for (int face = 0; face < FaceCount; face++)
            {
                if (FaceNormals[face] == normal)
                {
                    return face;
                }
            }

            throw new InvalidOperationException($"quad {quad} has a non-axis-aligned normal {normal}");
        }

        private static Int3 CellAt(int planeAxis, int plane, int aAxis, int a, int bAxis, int b)
        {
            switch (planeAxis)
            {
                case 0:
                    return aAxis == 1 ? new Int3(plane, a, b) : new Int3(plane, b, a);
                case 1:
                    return aAxis == 0 ? new Int3(a, plane, b) : new Int3(b, plane, a);
                default:
                    return aAxis == 0 ? new Int3(a, b, plane) : new Int3(b, a, plane);
            }
        }

        private static int Index(Int3 local)
        {
            return local.X + (ChunkMath.ChunkSize * local.Y) + (ChunkMath.ChunkSize * ChunkMath.ChunkSize * local.Z);
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

        private static void AssertCountsMatch(int caseIndex, string mesher, int[] counts, int[] oracle)
        {
            if (counts.AsSpan().SequenceEqual(oracle))
            {
                return;
            }

            for (int cell = 0; cell < CellCount; cell++)
            {
                for (int face = 0; face < FaceCount; face++)
                {
                    int index = (cell * FaceCount) + face;
                    if (counts[index] != oracle[index])
                    {
                        int x = cell % ChunkMath.ChunkSize;
                        int y = (cell / ChunkMath.ChunkSize) % ChunkMath.ChunkSize;
                        int z = cell / (ChunkMath.ChunkSize * ChunkMath.ChunkSize);
                        Assert.Fail(
                            $"{mesher} case {caseIndex} cell ({x},{y},{z}) direction {face}: "
                            + $"meshed {counts[index]}, exposed {oracle[index]}");
                    }
                }
            }
        }
    }
}

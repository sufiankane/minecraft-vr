using System;
using System.Collections.Generic;
using Cubeglass.CoreMath;
using Cubeglass.Voxel;
using NUnit.Framework;

namespace Cubeglass.Mesh.Tests
{
    /// <summary>
    /// Border seam tests for Task 3: a 32x16x16 world made of the two chunks
    /// (0,0,0) and (1,0,0) is edited at random cells of the shared x=16
    /// border, and only the chunks reported by
    /// <see cref="ChunkEditPropagation.GetAffectedChunks"/> are rebuilt.
    /// </summary>
    /// <remarks>
    /// <para>
    /// AO seam identity is asserted between quads that continue the same
    /// surface across the border (same face orientation and coincident world
    /// vertex): the two coplanar quads on either side sample the AO plane
    /// outside the border, each side's directly-outward cell is non-opaque
    /// (its face is exposed) and the remaining in-plane cells are shared, so
    /// both sides must store the same byte. Opposite-facing quads at the same
    /// plane sample the planes on opposite sides of the border and are
    /// deliberately not compared.
    /// </para>
    /// <para>
    /// The no-gap check decodes the merged quads of both rebuilt meshes back
    /// into the exposed-face multiset over the whole 32x16x16 world and
    /// compares it element-wise with an independent oracle that walks the
    /// monolithic grid: no missing face, no duplicate face, and no face
    /// between two opaque cells.
    /// </para>
    /// </remarks>
    [TestFixture]
    public sealed class BorderSeamTests
    {
        private const int Width = 32;
        private const int Height = 16;
        private const int Depth = 16;
        private const int FaceCount = 6;
        private const int EditCount = 40;
        private const int SeamX = 16;
        private const int FillPercent = 45;

        private static readonly ChunkCoord WestCoord = new ChunkCoord(0, 0, 0);
        private static readonly ChunkCoord EastCoord = new ChunkCoord(1, 0, 0);

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

        [TestCase(false)]
        [TestCase(true)]
        public void RandomBorderEditsKeepAoIdenticalAndFacesGapless(bool greedy)
        {
            var random = new Random(20261002);
            var world = new World();
            world.LoadChunk(new Chunk(WestCoord));
            world.LoadChunk(new Chunk(EastCoord));
            FillRandom(world, WestCoord, random);
            FillRandom(world, EastCoord, random);

            IChunkMesher mesher = greedy ? (IChunkMesher)new GreedyMesher() : new CulledMesher();
            MeshData west = Build(world, WestCoord, mesher);
            MeshData east = Build(world, EastCoord, mesher);
            var oracle = new int[Width * Height * Depth * FaceCount];
            var meshed = new int[Width * Height * Depth * FaceCount];
            int seamComparisons = 0;

            try
            {
                for (int edit = 0; edit < EditCount; edit++)
                {
                    Int3 cell = new Int3(15 + random.Next(2), random.Next(Height), random.Next(Depth));
                    BlockId current = world.Get(cell);
                    BlockId next = current == BlockId.Air ? TestChunks.Stone : BlockId.Air;
                    var command = new EditCommand(cell, current, next, 0L);
                    Assert.That(world.Apply(in command), Is.EqualTo(EditResult.Applied), $"edit {edit} at {cell}");

                    IReadOnlyList<ChunkCoord> affected = ChunkEditPropagation.GetAffectedChunks(cell);
                    bool rebuiltWest = false;
                    bool rebuiltEast = false;
                    for (int i = 0; i < affected.Count; i++)
                    {
                        if (affected[i] == WestCoord)
                        {
                            MeshData old = west;
                            west = Build(world, WestCoord, mesher);
                            old.Release();
                            rebuiltWest = true;
                        }
                        else if (affected[i] == EastCoord)
                        {
                            MeshData old = east;
                            east = Build(world, EastCoord, mesher);
                            old.Release();
                            rebuiltEast = true;
                        }
                    }

                    Assert.That(
                        rebuiltWest && rebuiltEast,
                        Is.True,
                        $"edit {edit} at {cell} must dirty both seam chunks per ChunkEditPropagation");

                    seamComparisons += AssertAoSeamIdentity(west, east, edit);

                    Array.Clear(oracle, 0, oracle.Length);
                    Array.Clear(meshed, 0, meshed.Length);
                    CountExposedFaces(world, oracle);
                    Decode(west, WestCoord, meshed);
                    Decode(east, EastCoord, meshed);
                    AssertCountsMatch(meshed, oracle, edit);
                }

                Assert.That(seamComparisons, Is.GreaterThan(100), "the AO seam check must not be vacuous");
                TestContext.WriteLine(
                    $"{EditCount} seam edits, {seamComparisons} coincident seam vertex AO comparisons");
            }
            finally
            {
                west.Release();
                east.Release();
            }
        }

        private static int AssertAoSeamIdentity(MeshData west, MeshData east, int edit)
        {
            Dictionary<(int X, int Y, int Z, int Face), byte> westAo = SeamAo(west, 0);
            Dictionary<(int X, int Y, int Z, int Face), byte> eastAo = SeamAo(east, EastCoord.X * ChunkMath.ChunkSize);
            int compared = 0;
            foreach (KeyValuePair<(int X, int Y, int Z, int Face), byte> pair in westAo)
            {
                if (eastAo.TryGetValue(pair.Key, out byte other))
                {
                    Assert.That(
                        pair.Value,
                        Is.EqualTo(other),
                        $"edit {edit}: seam vertex {pair.Key} has west AO {pair.Value} and east AO {other}");
                    compared++;
                }
            }

            return compared;
        }

        private static Dictionary<(int X, int Y, int Z, int Face), byte> SeamAo(MeshData mesh, int worldOriginX)
        {
            var result = new Dictionary<(int X, int Y, int Z, int Face), byte>();
            for (int i = 0; i < mesh.VertexCount; i++)
            {
                Vector3f position = mesh.Positions.Span[i];
                int worldX = (int)position.X + worldOriginX;
                if (worldX != SeamX)
                {
                    continue;
                }

                int face = FaceOf(mesh.Normals.Span[i], i);
                var key = (worldX, (int)position.Y, (int)position.Z, face);
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

        private static void CountExposedFaces(World world, int[] counts)
        {
            for (int x = 0; x < Width; x++)
            {
                for (int y = 0; y < Height; y++)
                {
                    for (int z = 0; z < Depth; z++)
                    {
                        var cell = new Int3(x, y, z);
                        if (world.Get(cell) == BlockId.Air)
                        {
                            continue;
                        }

                        for (int face = 0; face < FaceCount; face++)
                        {
                            Int3 neighbour = cell + FaceDirections[face];
                            if (!TestChunks.Registry.Get(world.Get(neighbour)).Opaque)
                            {
                                counts[(WorldIndex(cell) * FaceCount) + face] = 1;
                            }
                        }
                    }
                }
            }
        }

        private static void Decode(MeshData mesh, ChunkCoord coord, int[] counts)
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
                        Int3 local = CellAt(axis, cellPlane, aAxis, a, bAxis, b);
                        var world = new Int3(
                            local.X + (coord.X * ChunkMath.ChunkSize),
                            local.Y + (coord.Y * ChunkMath.ChunkSize),
                            local.Z + (coord.Z * ChunkMath.ChunkSize));
                        counts[(WorldIndex(world) * FaceCount) + face]++;
                    }
                }
            }
        }

        private static void AssertCountsMatch(int[] meshed, int[] oracle, int edit)
        {
            if (meshed.AsSpan().SequenceEqual(oracle))
            {
                return;
            }

            for (int cell = 0; cell < Width * Height * Depth; cell++)
            {
                for (int face = 0; face < FaceCount; face++)
                {
                    int index = (cell * FaceCount) + face;
                    if (meshed[index] != oracle[index])
                    {
                        int x = cell / (Height * Depth);
                        int remainder = cell % (Height * Depth);
                        int y = remainder / Depth;
                        int z = remainder % Depth;
                        Assert.Fail(
                            $"edit {edit}: world cell ({x},{y},{z}) face {face} has {meshed[index]} meshed faces "
                            + $"but {oracle[index]} exposed ones");
                    }
                }
            }
        }

        private static void FillRandom(World world, ChunkCoord coord, Random random)
        {
            for (int z = 0; z < ChunkMath.ChunkSize; z++)
            {
                for (int y = 0; y < ChunkMath.ChunkSize; y++)
                {
                    for (int x = 0; x < ChunkMath.ChunkSize; x++)
                    {
                        if (random.Next(100) >= FillPercent)
                        {
                            continue;
                        }

                        var block = new BlockId((ushort)(1 + random.Next(3)));
                        var command = new EditCommand(ChunkMath.ToWorld(coord, new Int3(x, y, z)), BlockId.Air, block, 0L);
                        EditResult result = world.Apply(in command);
                        if (result != EditResult.Applied)
                        {
                            throw new InvalidOperationException($"Test setup failed at ({x},{y},{z}) in chunk {coord}.");
                        }
                    }
                }
            }
        }

        private static MeshData Build(World world, ChunkCoord coord, IChunkMesher mesher)
        {
            Chunk chunk = world.GetChunk(coord)
                ?? throw new InvalidOperationException($"chunk {coord} is not loaded");
            return mesher.Build(chunk.Snapshot(), world.CreateNeighbourSnapshot(coord), TestChunks.Registry);
        }

        private static int WorldIndex(Int3 cell)
        {
            return ((cell.X * Height) + cell.Y) * Depth + cell.Z;
        }

        private static int FaceOf(Vector3f normal, int vertex)
        {
            for (int face = 0; face < FaceCount; face++)
            {
                if (FaceNormals[face] == normal)
                {
                    return face;
                }
            }

            throw new InvalidOperationException($"vertex {vertex} has a non-axis-aligned normal {normal}");
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
    }
}

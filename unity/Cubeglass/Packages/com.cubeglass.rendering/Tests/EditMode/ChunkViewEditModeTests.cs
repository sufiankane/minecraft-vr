using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Cubeglass.Mesh;
using Cubeglass.Voxel;
using NUnit.Framework;
using UnityEngine;

namespace Cubeglass.Unity.Rendering.Tests
{
    /// <summary>
    /// EditMode pins for the chunk-view upload path: the ADR-0007 winding
    /// survives the SoA-to-Unity conversion, the pool cap and lifecycle
    /// counters hold, and the slice block registry matches the committed
    /// <c>dotnet/src/Voxel/Content/blocks.json</c> values (read as text; Unity
    /// must not take a System.Text.Json dependency).
    /// </summary>
    public sealed class ChunkViewEditModeTests
    {
        [Test]
        public void SingleTopQuadWindsOutwardInUnityAndKeepsTheAdr0007Order()
        {
            MeshData data = BuildSingleStoneChunk();
            var root = new GameObject("WindingPool");
            try
            {
                var pool = new ChunkViewPool(root.transform, 1);
                try
                {
                    Assert.IsTrue(pool.TryAcquire(out ChunkView view));
                    pool.Upload(view, data);

                    var vertices = new List<Vector3>();
                    var normals = new List<Vector3>();
                    var indices = new List<int>();
                    view.Mesh.GetVertices(vertices);
                    view.Mesh.GetNormals(normals);
                    view.Mesh.GetTriangles(indices, 0);
                    Assert.AreEqual(24, vertices.Count);
                    Assert.AreEqual(36, indices.Count);

                    int topVertex = FindVertexFacing(normals, Vector3.up, "+Y");
                    int topFirst = topVertex - (topVertex % 4);
                    Vector3 topCross = Vector3.Cross(
                        vertices[topFirst + 1] - vertices[topFirst],
                        vertices[topFirst + 2] - vertices[topFirst]);
                    Assert.Greater(
                        Vector3.Dot(topCross.normalized, Vector3.up),
                        0.9999f,
                        "the top quad winds inward in Unity; do not reverse the ADR-0007 index order");
                    Assert.AreEqual(1f, vertices[topFirst].y, 1e-6f, "the top quad must sit on the +Y face plane");

                    int sideVertex = FindVertexFacing(normals, Vector3.right, "+X");
                    int sideFirst = sideVertex - (sideVertex % 4);
                    Vector3 sideCross = Vector3.Cross(
                        vertices[sideFirst + 1] - vertices[sideFirst],
                        vertices[sideFirst + 2] - vertices[sideFirst]);
                    Assert.Greater(
                        Vector3.Dot(sideCross.normalized, Vector3.right),
                        0.9999f,
                        "the +X side quad winds inward in Unity; do not reverse the ADR-0007 index order");
                    Assert.AreEqual(1f, vertices[sideFirst].x, 1e-6f, "the side quad must sit on the +X face plane");

                    AssertQuadIndices(indices, topFirst);
                    AssertQuadIndices(indices, sideFirst);

                    Assert.AreEqual(1, pool.MeshDataBuilds);
                    Assert.AreEqual(1, pool.MeshDataReleases);
                    Assert.AreEqual(0, pool.OutstandingMeshData);
                    Assert.AreEqual(1, pool.ActiveViews);
                }
                finally
                {
                    pool.Dispose();
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void PoolCapRefusesAcquireUntilAViewIsReleased()
        {
            var root = new GameObject("CapPool");
            try
            {
                var pool = new ChunkViewPool(root.transform, 1);
                try
                {
                    Assert.IsTrue(pool.TryAcquire(out ChunkView first));
                    Assert.IsFalse(pool.TryAcquire(out ChunkView second));
                    Assert.IsNull(second);
                    Assert.AreEqual(1, pool.ActiveViews);
                    Assert.AreEqual(1, pool.CreatedViews);

                    pool.Release(first);
                    Assert.AreEqual(0, pool.ActiveViews);
                    Assert.AreEqual(1, pool.PooledViews);
                    Assert.IsTrue(pool.TryAcquire(out ChunkView reused));
                    Assert.AreSame(first, reused);
                }
                finally
                {
                    pool.Dispose();
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void PoolRejectsMisuseAndStillReleasesMeshData()
        {
            var rootA = new GameObject("PoolA");
            var rootB = new GameObject("PoolB");
            try
            {
                var poolA = new ChunkViewPool(rootA.transform, 1);
                var poolB = new ChunkViewPool(rootB.transform, 1);
                try
                {
                    Assert.IsTrue(poolA.TryAcquire(out ChunkView view));
                    Assert.Throws<InvalidOperationException>(
                        () => poolB.Release(view),
                        "a foreign view must not be released into another pool");
                    Assert.IsTrue(view.IsActive, "the rejected foreign release must not change the view");

                    poolA.Release(view);
                    Assert.Throws<InvalidOperationException>(
                        () => poolA.Release(view),
                        "a double release must throw");

                    MeshData data = BuildSingleStoneChunk();
                    Assert.Throws<InvalidOperationException>(
                        () => poolA.Upload(view, data),
                        "uploading into a released view must throw");
                    Assert.AreEqual(1, poolA.MeshDataBuilds, "the rejected upload still took ownership");
                    Assert.AreEqual(1, poolA.MeshDataReleases, "the rejected upload must release the mesh data");
                    Assert.AreEqual(0, poolA.OutstandingMeshData);
                }
                finally
                {
                    poolA.Dispose();
                    poolB.Dispose();
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(rootA);
                UnityEngine.Object.DestroyImmediate(rootB);
            }
        }

        [Test]
        public void SliceBlockRegistryMatchesTheCommittedBlocksJson()
        {
            string repoRoot = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", ".."));
            string blocksPath = Path.Combine(repoRoot, "dotnet", "src", "Voxel", "Content", "blocks.json");
            Assert.IsTrue(File.Exists(blocksPath), "blocks.json not found at " + blocksPath);

            List<BlockEntry> entries = ParseBlockEntries(File.ReadAllText(blocksPath));
            Assert.AreEqual(6, entries.Count, "blocks.json must still define the six S7 slice blocks");
            for (int i = 0; i < entries.Count; i++)
            {
                BlockEntry entry = entries[i];
                BlockDefinition definition = SliceBlockRegistry.Default.Get(new BlockId((ushort)entry.Id));
                Assert.AreEqual(entry.Name, definition.Name);
                Assert.AreEqual(entry.Solid, definition.Solid);
                Assert.AreEqual(entry.Opaque, definition.Opaque);
                Assert.AreEqual(entry.Hardness, definition.Hardness);
                Assert.AreEqual(entry.AtlasIndexTop, definition.AtlasIndexTop);
                Assert.AreEqual(entry.AtlasIndexFront, definition.AtlasIndexFront);
                Assert.AreEqual(entry.AtlasIndexSide, definition.AtlasIndexSide);
            }

            IReadOnlyList<BlockId> placeable = SliceBlockRegistry.Default.Placeable;
            Assert.AreEqual(5, placeable.Count);
            Assert.IsFalse(Contains(placeable, BlockId.Air));
            Assert.IsTrue(Contains(placeable, new BlockId(1)));
            Assert.Throws<KeyNotFoundException>(() => SliceBlockRegistry.Default.Get(new BlockId(6)));
        }

        private static MeshData BuildSingleStoneChunk()
        {
            var coord = new ChunkCoord(0, 0, 0);
            var world = new World();
            var chunk = new Chunk(coord);
            world.LoadChunk(chunk);
            world.Apply(new EditCommand(
                ChunkMath.ToWorld(coord, new Int3(0, 0, 0)),
                BlockId.Air,
                new BlockId(1),
                0L));

            var mesher = new GreedyMesher(new AtlasLayout(16, 16), new MeshBufferPool());
            return mesher.Build(chunk.Snapshot(), NeighbourSnapshot.Empty, SliceBlockRegistry.Default);
        }

        private static int FindVertexFacing(List<Vector3> normals, Vector3 direction, string label)
        {
            for (int i = 0; i < normals.Count; i++)
            {
                if (normals[i] == direction)
                {
                    return i;
                }
            }

            Assert.Fail("the mesh has no " + label + "-facing vertex");
            return -1;
        }

        private static void AssertQuadIndices(List<int> indices, int first)
        {
            int firstIndex = (first / 4) * 6;
            Assert.AreEqual(first + 0, indices[firstIndex + 0]);
            Assert.AreEqual(first + 1, indices[firstIndex + 1]);
            Assert.AreEqual(first + 2, indices[firstIndex + 2]);
            Assert.AreEqual(first + 0, indices[firstIndex + 3]);
            Assert.AreEqual(first + 2, indices[firstIndex + 4]);
            Assert.AreEqual(first + 3, indices[firstIndex + 5]);
        }

        /// <summary>
        /// Minimal parser for the flat <c>blocks.json</c> array: one object per
        /// line, seven scalar fields. Deliberately not a JSON library so Unity
        /// needs no System.Text.Json.
        /// </summary>
        private static List<BlockEntry> ParseBlockEntries(string json)
        {
            var entries = new List<BlockEntry>();
            int cursor = 0;
            while (true)
            {
                int start = json.IndexOf('{', cursor);
                if (start < 0)
                {
                    break;
                }

                int end = json.IndexOf('}', start);
                Assert.Greater(end, start, "unbalanced object in blocks.json");
                string body = json.Substring(start, end - start + 1);
                entries.Add(new BlockEntry
                {
                    Id = ParseInt(body, "Id"),
                    Name = ParseString(body, "Name"),
                    Solid = ParseBool(body, "Solid"),
                    Opaque = ParseBool(body, "Opaque"),
                    Hardness = ParseFloat(body, "Hardness"),
                    AtlasIndexTop = ParseInt(body, "AtlasIndexTop"),
                    AtlasIndexFront = ParseInt(body, "AtlasIndexFront"),
                    AtlasIndexSide = ParseInt(body, "AtlasIndexSide"),
                });
                cursor = end + 1;
            }

            return entries;
        }

        private static string FindRawValue(string body, string key)
        {
            string marker = "\"" + key + "\"";
            int at = body.IndexOf(marker, StringComparison.Ordinal);
            Assert.GreaterOrEqual(at, 0, "blocks.json is missing the key " + key);
            int colon = body.IndexOf(':', at + marker.Length);
            Assert.GreaterOrEqual(colon, 0, "blocks.json has a malformed entry for " + key);
            int start = colon + 1;
            while (start < body.Length && char.IsWhiteSpace(body[start]))
            {
                start++;
            }

            if (start < body.Length && body[start] == '"')
            {
                int close = body.IndexOf('"', start + 1);
                Assert.Greater(close, start, "blocks.json has an unterminated string for " + key);
                return body.Substring(start + 1, close - start - 1);
            }

            int stop = start;
            while (stop < body.Length && body[stop] != ',' && body[stop] != '}')
            {
                stop++;
            }

            return body.Substring(start, stop - start).Trim();
        }

        private static int ParseInt(string body, string key)
        {
            return int.Parse(FindRawValue(body, key), CultureInfo.InvariantCulture);
        }

        private static float ParseFloat(string body, string key)
        {
            return float.Parse(FindRawValue(body, key), CultureInfo.InvariantCulture);
        }

        private static bool ParseBool(string body, string key)
        {
            return bool.Parse(FindRawValue(body, key));
        }

        private static string ParseString(string body, string key)
        {
            return FindRawValue(body, key);
        }

        private static bool Contains(IReadOnlyList<BlockId> ids, BlockId id)
        {
            for (int i = 0; i < ids.Count; i++)
            {
                if (ids[i] == id)
                {
                    return true;
                }
            }

            return false;
        }

        private struct BlockEntry
        {
            internal int Id;
            internal string Name;
            internal bool Solid;
            internal bool Opaque;
            internal float Hardness;
            internal int AtlasIndexTop;
            internal int AtlasIndexFront;
            internal int AtlasIndexSide;
        }
    }
}

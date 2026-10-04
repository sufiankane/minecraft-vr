using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using Cubeglass.CoreMath;
using Cubeglass.Mesh;
using Cubeglass.Voxel;
using NUnit.Framework;
using UnityEngine;

namespace Cubeglass.Unity.Rendering.Tests
{
    /// <summary>
    /// EditMode pins for the chunk-view upload path: the mirrored Unity-space
    /// winding (ADR-0011, R52) after the ADR-0004 conversion, the remesh
    /// neighbourhood covering every <see cref="ChunkEditPropagation"/> result,
    /// the pool cap and lifecycle counters, and the slice block registry
    /// matching the committed <c>dotnet/src/Voxel/Content/blocks.json</c>
    /// values (read as text; Unity must not take a System.Text.Json
    /// dependency).
    /// </summary>
    public sealed class ChunkViewEditModeTests
    {
        private static readonly Vector3 BlockCentre = new Vector3(0.5f, 0.5f, -0.5f);

        [Test]
        public void SingleBlockQuadsWindOutwardInUnityAfterTheMirror()
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

                    // Internal (x, y, z) is uploaded as (x, y, -z): the internal
                    // +Z face is the Unity -Z face at z = -1, and the internal
                    // -Z face is the Unity +Z face at z = 0. The stored normals
                    // are mirrored the same way, so each quad's cross product
                    // must match its converted normal and point away from the
                    // block centre; the winding flip is what keeps the visible
                    // face on the viewer's side.
                    AssertFaceOutward(vertices, normals, indices, new Vector3(0f, 1f, 0f), 1f, "top (+Y -> +Y)");
                    AssertFaceOutward(vertices, normals, indices, new Vector3(1f, 0f, 0f), 1f, "side (+X -> +X)");
                    AssertFaceOutward(vertices, normals, indices, new Vector3(-1f, 0f, 0f), 0f, "side (-X -> -X)");
                    AssertFaceOutward(vertices, normals, indices, new Vector3(0f, -1f, 0f), 0f, "bottom (-Y -> -Y)");
                    AssertFaceOutward(vertices, normals, indices, new Vector3(0f, 0f, 1f), 0f, "side (-Z -> +Z)");
                    AssertFaceOutward(vertices, normals, indices, new Vector3(0f, 0f, -1f), 1f, "side (+Z -> -Z)");

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
        public void RemeshNeighbourhoodCoversEveryAffectedChunk()
        {
            var changed = new ChunkCoord(2, -1, 3);
            var destination = new ChunkCoord[ChunkViewManager.RemeshNeighbourhoodSize];
            int count = ChunkViewManager.FillRemeshNeighbourhood(changed, destination);
            Assert.AreEqual(ChunkViewManager.RemeshNeighbourhoodSize, count);

            var present = new HashSet<ChunkCoord>(destination);
            Assert.AreEqual(ChunkViewManager.RemeshNeighbourhoodSize, present.Count, "the neighbourhood has no duplicates");

            // The event reports the chunk, not the cell, so the manager dirties
            // the Chebyshev-1 neighbourhood. Every cell of the changed chunk
            // must propagate into it for every edit to be covered.
            for (int x = 0; x < ChunkMath.ChunkSize; x += ChunkMath.ChunkSize - 1)
            {
                for (int y = 0; y < ChunkMath.ChunkSize; y += ChunkMath.ChunkSize - 1)
                {
                    for (int z = 0; z < ChunkMath.ChunkSize; z += ChunkMath.ChunkSize - 1)
                    {
                        Int3 cell = ChunkMath.ToWorld(changed, new Int3(x, y, z));
                        IReadOnlyList<ChunkCoord> affected = ChunkEditPropagation.GetAffectedChunks(cell);
                        for (int i = 0; i < affected.Count; i++)
                        {
                            Assert.IsTrue(
                                present.Contains(affected[i]),
                                "affected chunk " + affected[i] + " missing for cell " + cell);
                        }
                    }
                }
            }

            Assert.IsFalse(
                present.Contains(new ChunkCoord(changed.X + 2, changed.Y, changed.Z)),
                "the neighbourhood stays at Chebyshev distance 1");
            Assert.Throws<ArgumentException>(
                () => ChunkViewManager.FillRemeshNeighbourhood(changed, new ChunkCoord[26]),
                "a short destination must be rejected");
            Assert.Throws<ArgumentNullException>(
                () => ChunkViewManager.FillRemeshNeighbourhood(changed, null),
                "a null destination must be rejected");
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
        public void MeshesOverThe16BitVertexLimitUseThe32BitIndexFormat()
        {
            MeshData data = CreateLargeMeshData(65540);
            var root = new GameObject("IndexFormatPool");
            try
            {
                var pool = new ChunkViewPool(root.transform, 1);
                try
                {
                    Assert.IsTrue(pool.TryAcquire(out ChunkView view));
                    pool.Upload(view, data);

                    Assert.AreEqual(
                        UnityEngine.Rendering.IndexFormat.UInt32,
                        view.Mesh.indexFormat,
                        "a build above 65535 vertices must switch to 32-bit indices (review M-4)");
                    Assert.AreEqual(65540, view.Mesh.vertexCount);
                    Assert.AreEqual(pool.MeshDataBuilds, pool.MeshDataReleases, "the upload releases the mesh data");
                    Assert.AreEqual(0, pool.OutstandingMeshData);
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
        public void SmallMeshesStayOnThe16BitIndexFormat()
        {
            MeshData data = BuildSingleStoneChunk();
            var root = new GameObject("IndexFormatSmallPool");
            try
            {
                var pool = new ChunkViewPool(root.transform, 1);
                try
                {
                    Assert.IsTrue(pool.TryAcquire(out ChunkView view));
                    pool.Upload(view, data);
                    Assert.AreEqual(
                        UnityEngine.Rendering.IndexFormat.UInt16,
                        view.Mesh.indexFormat,
                        "the default chunk mesh keeps the cheaper 16-bit format");
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

        /// <summary>
        /// Builds a mesh bigger than the 16-bit index range. <see cref="MeshData"/>
        /// and its initialization are internal to the Cubeglass.Mesh assembly,
        /// so the test rents a wrapper and initializes it through the same
        /// reflection seam the pool contract exposes.
        /// </summary>
        private static MeshData CreateLargeMeshData(int vertexCount)
        {
            const BindingFlags NonPublicInstance = BindingFlags.Instance | BindingFlags.NonPublic;
            var bufferPool = new MeshBufferPool();
            MethodInfo rent = typeof(MeshBufferPool).GetMethod("RentMeshData", NonPublicInstance);
            Assert.IsNotNull(rent, "MeshBufferPool.RentMeshData exists");
            var data = (MeshData)rent.Invoke(bufferPool, null);

            var positions = new Vector3f[vertexCount];
            var normals = new Vector3f[vertexCount];
            var uvs = new Vector2f[vertexCount];
            var ao = new byte[vertexCount];
            int quadCount = vertexCount / 4;
            var indices = new int[quadCount * 6];
            for (int quad = 0; quad < quadCount; quad++)
            {
                int vertex = quad * 4;
                indices[(quad * 6) + 0] = vertex;
                indices[(quad * 6) + 1] = vertex + 1;
                indices[(quad * 6) + 2] = vertex + 2;
                indices[(quad * 6) + 3] = vertex;
                indices[(quad * 6) + 4] = vertex + 2;
                indices[(quad * 6) + 5] = vertex + 3;
            }

            MethodInfo initialize = typeof(MeshData).GetMethod("Initialize", NonPublicInstance);
            Assert.IsNotNull(initialize, "MeshData.Initialize exists");
            initialize.Invoke(data, new object[]
            {
                positions,
                normals,
                uvs,
                ao,
                indices,
                vertexCount,
                indices.Length,
                new Vector3f(0f, 0f, 0f),
                new Vector3f(1f, 1f, 1f),
            });
            return data;
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

            BlockDefinition unknown = SliceBlockRegistry.Default.Get(new BlockId(65000));
            Assert.AreSame(SliceBlockRegistry.Fallback, unknown, "unknown ids resolve to the documented fallback");
            Assert.AreEqual(BlockId.Air, unknown.Id);
            Assert.IsFalse(unknown.Solid);
            Assert.IsFalse(unknown.Opaque);
            Assert.AreEqual(0f, unknown.Hardness);
            Assert.AreEqual(0, unknown.AtlasIndexTop);
            Assert.AreEqual(0, unknown.AtlasIndexFront);
            Assert.AreEqual(0, unknown.AtlasIndexSide);
        }

        /// <summary>
        /// Unity mirror of the pure-module I1 test: a corrupt save can carry
        /// any block id in the codec range; decoding it, applying it and
        /// building the chunk mesh must not throw, and the unknown id must
        /// still render as a visible placeholder (six quads).
        /// </summary>
        [Test]
        public void UnknownBlockIdMeshesAsAVisiblePlaceholderThroughTheChunkMeshPath()
        {
            var coord = new ChunkCoord(0, 0, 0);
            var poison = new BlockId(65000);
            var local = new Int3(8, 8, 8);
            byte[] payload = ChunkDeltaCodec.Serialize(new ChunkDelta(
                coord, new Dictionary<Int3, BlockId> { { local, poison } }));
            Assert.IsTrue(ChunkDeltaCodec.TryDeserialize(payload, out ChunkDelta decoded), "codec round trip");
            Assert.IsNotNull(decoded);

            var world = new World();
            var chunk = new Chunk(coord);
            world.LoadChunk(chunk);
            foreach (KeyValuePair<Int3, BlockId> edit in decoded.Edits)
            {
                Assert.AreEqual(
                    EditResult.Applied,
                    world.Apply(new EditCommand(
                        ChunkMath.ToWorld(coord, edit.Key), BlockId.Air, edit.Value, 0L)),
                    "the decoded unknown id must apply");
            }

            var mesher = new GreedyMesher(new AtlasLayout(16, 16), new MeshBufferPool());
            MeshData data = mesher.Build(chunk.Snapshot(), NeighbourSnapshot.Empty, SliceBlockRegistry.Default);
            try
            {
                Assert.AreEqual(36, data.IndexCount, "the placeholder emits six visible quads");
                Assert.AreEqual(24, data.VertexCount);
                Assert.AreEqual(
                    poison,
                    world.Get(ChunkMath.ToWorld(coord, local)),
                    "the raw unknown id must survive apply untouched");

                BlockDefinition definition = SliceBlockRegistry.Default.Get(poison);
                Assert.AreSame(SliceBlockRegistry.Fallback, definition);
                Assert.IsFalse(definition.Solid, "the placeholder must not block movement");
                Assert.IsFalse(definition.Opaque, "the placeholder must not cull its neighbours");
                Assert.AreEqual(0f, definition.Hardness, "the placeholder is breakable instantly");
                Assert.AreEqual(0, definition.AtlasIndexTop, "the placeholder maps to atlas tile 0");
            }
            finally
            {
                data.Release();
            }
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

        private static void AssertFaceOutward(
            List<Vector3> vertices,
            List<Vector3> normals,
            List<int> indices,
            Vector3 faceNormal,
            float planeOffset,
            string label)
        {
            int first = FindQuadFirstFacing(normals, faceNormal, label);
            for (int i = 0; i < 4; i++)
            {
                float offset = Vector3.Dot(vertices[first + i], faceNormal);
                Assert.AreEqual(
                    planeOffset,
                    offset,
                    1e-5f,
                    label + " must sit on its converted face plane");
            }

            // The rendered triangle winding is the index order, not the vertex
            // array order, so the cross product must be taken through indices.
            int firstIndex = (first / 4) * 6;
            Vector3 a = vertices[indices[firstIndex + 0]];
            Vector3 b = vertices[indices[firstIndex + 1]];
            Vector3 c = vertices[indices[firstIndex + 2]];
            Vector3 cross = Vector3.Cross(b - a, c - a).normalized;
            Assert.Greater(
                Vector3.Dot(cross, faceNormal),
                0.9999f,
                label + " quad winds against its converted outward normal");

            Vector3 centroid = Vector3.zero;
            for (int i = 0; i < 4; i++)
            {
                centroid += vertices[first + i];
            }

            centroid *= 0.25f;
            Vector3 outward = (centroid - BlockCentre).normalized;
            Assert.Greater(
                Vector3.Dot(cross, outward),
                0.9999f,
                label + " quad must be visible from its own side (cross points away from the block)");

            AssertQuadIndices(indices, first, label);
        }

        private static int FindQuadFirstFacing(List<Vector3> normals, Vector3 direction, string label)
        {
            int vertex = FindVertexFacing(normals, direction, label);
            return vertex - (vertex % 4);
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

        private static void AssertQuadIndices(List<int> indices, int first, string label)
        {
            int firstIndex = (first / 4) * 6;
            Assert.AreEqual(first + 0, indices[firstIndex + 0], label + " triangle 0 vertex 0");
            Assert.AreEqual(first + 2, indices[firstIndex + 1], label + " winding must be flipped for the Z mirror");
            Assert.AreEqual(first + 1, indices[firstIndex + 2], label + " winding must be flipped for the Z mirror");
            Assert.AreEqual(first + 0, indices[firstIndex + 3], label + " triangle 1 vertex 0");
            Assert.AreEqual(first + 3, indices[firstIndex + 4], label + " winding must be flipped for the Z mirror");
            Assert.AreEqual(first + 2, indices[firstIndex + 5], label + " winding must be flipped for the Z mirror");
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

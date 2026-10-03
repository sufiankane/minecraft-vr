using System.Collections.Generic;
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
    /// <c>dotnet/src/Voxel/Content/blocks.json</c> values.
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

                    int topVertex = -1;
                    for (int i = 0; i < normals.Count; i++)
                    {
                        if (normals[i] == Vector3.up)
                        {
                            topVertex = i;
                            break;
                        }
                    }

                    Assert.GreaterOrEqual(topVertex, 0, "the mesh has no +Y-facing vertex");
                    int first = topVertex - (topVertex % 4);
                    Vector3 cross = Vector3.Cross(
                        vertices[first + 1] - vertices[first],
                        vertices[first + 2] - vertices[first]);
                    Assert.Greater(
                        Vector3.Dot(cross.normalized, Vector3.up),
                        0.9999f,
                        "the top quad winds inward in Unity; do not reverse the ADR-0007 index order");

                    int firstIndex = (first / 4) * 6;
                    Assert.AreEqual(first + 0, indices[firstIndex + 0]);
                    Assert.AreEqual(first + 1, indices[firstIndex + 1]);
                    Assert.AreEqual(first + 2, indices[firstIndex + 2]);
                    Assert.AreEqual(first + 0, indices[firstIndex + 3]);
                    Assert.AreEqual(first + 2, indices[firstIndex + 4]);
                    Assert.AreEqual(first + 3, indices[firstIndex + 5]);

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
                Object.DestroyImmediate(root);
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
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void SliceBlockRegistryMatchesTheCommittedBlocksJson()
        {
            // Mirrors dotnet/src/Voxel/Content/blocks.json (the JSON source of
            // truth cannot be loaded in Unity: System.Text.Json is not part of
            // Unity's runtime). Update both together.
            AssertBlock(new BlockId(0), "Air", false, false, 0.0f, 0, 0, 0);
            AssertBlock(new BlockId(1), "Stone", true, true, 1.5f, 1, 1, 1);
            AssertBlock(new BlockId(2), "Dirt", true, true, 0.5f, 2, 2, 2);
            AssertBlock(new BlockId(3), "Grass", true, true, 0.6f, 3, 4, 4);
            AssertBlock(new BlockId(4), "Sand", true, true, 0.5f, 5, 5, 5);
            AssertBlock(new BlockId(5), "Wood", true, true, 2.0f, 7, 6, 6);

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

        private static void AssertBlock(
            BlockId id,
            string name,
            bool solid,
            bool opaque,
            float hardness,
            int atlasTop,
            int atlasFront,
            int atlasSide)
        {
            BlockDefinition definition = SliceBlockRegistry.Default.Get(id);
            Assert.AreEqual(name, definition.Name);
            Assert.AreEqual(solid, definition.Solid);
            Assert.AreEqual(opaque, definition.Opaque);
            Assert.AreEqual(hardness, definition.Hardness);
            Assert.AreEqual(atlasTop, definition.AtlasIndexTop);
            Assert.AreEqual(atlasFront, definition.AtlasIndexFront);
            Assert.AreEqual(atlasSide, definition.AtlasIndexSide);
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
    }
}

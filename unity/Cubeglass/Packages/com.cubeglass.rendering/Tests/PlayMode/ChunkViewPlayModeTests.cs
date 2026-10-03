using System.Collections;
using System.Collections.Generic;
using Cubeglass.CoreMath;
using Cubeglass.Streaming;
using Cubeglass.Voxel;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Cubeglass.Unity.Rendering.Tests
{
    /// <summary>
    /// PlayMode invariants for the pooled chunk views and the streaming runtime
    /// (S7 Task 2): per-frame upload budgets, the load/upload/unload lifecycle,
    /// walking ahead/behind, steady-state allocation and the Unity triangle
    /// winding of an uploaded quad.
    /// </summary>
    public sealed class ChunkViewPlayModeTests
    {
        private GameObject root;
        private Transform player;

        [SetUp]
        public void SetUp()
        {
            root = new GameObject("StreamingTestRoot");
            var playerObject = new GameObject("Player");
            playerObject.transform.SetParent(root.transform, false);
            playerObject.transform.position = new Vector3(8f, 8f, 8f);
            player = playerObject.transform;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (root != null)
            {
                Object.Destroy(root);
            }

            yield return null;
        }

        [UnityTest]
        public IEnumerator BurstOfFiftyChunksNeverUploadsMoreThanTheBudgetPerFrame()
        {
            const int Budget = 3;
            ChunkViewManager manager = CreateManager("Burst", SmallConfig(Budget), capacity: 64, out _);

            var chunks = new ChunkCoord[50];
            for (int i = 0; i < chunks.Length; i++)
            {
                chunks[i] = new ChunkCoord(i, 0, 0);
                manager.OnLoad(chunks[i]);
            }

            // Loads are generated one chunk per frame, so let the queue drain
            // first; otherwise generation (not the upload budget) is the
            // limiter and the gate is never exercised.
            for (int frame = 0; frame < 240 && manager.DeferredLoads > 0; frame++)
            {
                manager.ProcessDeferredLoads();
                yield return null;
            }

            Assert.AreEqual(0, manager.DeferredLoads, "the deferred load queue did not drain");
            Assert.AreEqual(0, manager.ActiveViews, "chunks uploaded before the upload phase");

            int next = 0;
            int maxDelta = 0;
            int frames = 0;
            while (next < chunks.Length && frames < 240)
            {
                int before = manager.ActiveViews;
                while (next < chunks.Length && manager.OnUpload(chunks[next]))
                {
                    next++;
                }

                int delta = manager.ActiveViews - before;
                maxDelta = Mathf.Max(maxDelta, delta);
                Assert.LessOrEqual(
                    delta,
                    Budget,
                    "frame {0} uploaded {1} meshes; the configured budget is {2}.",
                    frames,
                    delta,
                    Budget);
                Assert.LessOrEqual(manager.UploadedThisFrame, Budget);
                frames++;
                yield return null;
            }

            Assert.AreEqual(chunks.Length, manager.ActiveViews, "the burst did not finish uploading");
            Assert.AreEqual(Budget, maxDelta, "the budget was never fully used; the gate is not the limiter");
            Assert.AreEqual(chunks.Length, manager.UploadedChunks);
            Assert.AreEqual(0, manager.DeferredLoads);
            Assert.AreEqual(0, manager.OutstandingMeshData);
            Assert.AreEqual(manager.MeshDataBuilds, manager.MeshDataReleases);
        }

        [UnityTest]
        public IEnumerator FullInOutCycleReturnsEveryViewAndReleasesEveryMeshData()
        {
            ChunkViewManager manager = CreateManager("Cycle", SmallConfig(4), capacity: 16, out _);
            ChunkCoord[] first = NineAround(0, 0, 0);
            for (int i = 0; i < first.Length; i++)
            {
                manager.OnLoad(first[i]);
            }

            yield return PumpUntilUploaded(manager, first, 120);
            Assert.AreEqual(first.Length, manager.ActiveViews);
            int highWater = manager.CreatedViews;
            long buildsAfterFirst = manager.MeshDataBuilds;

            for (int i = 0; i < first.Length; i++)
            {
                manager.OnUnload(first[i]);
            }

            Assert.AreEqual(0, manager.ActiveViews, "unload left views active");
            Assert.AreEqual(highWater, manager.CreatedViews, "the view high-water mark grew on unload");
            Assert.AreEqual(highWater, manager.PooledViews, "unloaded views did not return to the pool");
            Assert.AreEqual(0, manager.OutstandingMeshData);

            // A second cycle must reuse the same views rather than grow the pool.
            ChunkCoord[] second = NineAround(-10, 0, 10);
            for (int i = 0; i < second.Length; i++)
            {
                manager.OnLoad(second[i]);
            }

            yield return PumpUntilUploaded(manager, second, 120);
            Assert.AreEqual(second.Length, manager.ActiveViews);
            Assert.AreEqual(highWater, manager.CreatedViews, "the second cycle created new views instead of reusing pooled ones");
            Assert.Greater(manager.MeshDataBuilds, buildsAfterFirst);
            Assert.AreEqual(manager.MeshDataBuilds, manager.MeshDataReleases);

            for (int i = 0; i < second.Length; i++)
            {
                manager.OnUnload(second[i]);
            }

            Assert.AreEqual(0, manager.ActiveViews);
            Assert.AreEqual(0, manager.OutstandingMeshData);
            Assert.AreEqual(manager.CreatedViews, manager.PooledViews);
        }

        [UnityTest]
        public IEnumerator WalkingOneHundredMetresLoadsAheadUnloadsBehindAndStaysWithinTheCap()
        {
            var config = new StreamingConfig
            {
                ViewDistanceChunks = 2,
                UnloadHysteresis = 1,
                VerticalRadiusChunks = 0f,
                MaxLoadsPerFrame = 16,
                MaxUnloadsPerFrame = 16,
                MaxMeshUploadsPerFrame = 8,
            };

            StreamingRuntime runtime = CreateRuntime("Walk", config, poolCapacity: 64);

            int retainedSide = (2 * (config.ViewDistanceChunks + config.UnloadHysteresis)) + 1;
            int maxRetained = retainedSide * retainedSide;
            int observedMax = 0;
            player.position = new Vector3(8f, 8f, 8f);
            for (int step = 0; step < 100; step++)
            {
                player.position = player.position + new Vector3(1f, 0f, 0f);
                runtime.Tick();
                observedMax = Mathf.Max(observedMax, runtime.Views.ActiveViews);
                Assert.LessOrEqual(runtime.Views.ActiveViews, 64, "active views exceeded the pool cap");
                Assert.LessOrEqual(runtime.Views.CreatedViews, 64, "the pool grew past its cap");
                yield return null;
            }

            for (int i = 0; i < 240 && !IsQuiescent(runtime); i++)
            {
                runtime.Tick();
                yield return null;
            }

            Assert.IsTrue(IsQuiescent(runtime), "the walk did not drain");
            Assert.LessOrEqual(observedMax, maxRetained, "more chunks stayed resident than the hysteresis window allows");
            Assert.IsTrue(
                runtime.Scheduler.IsLoaded(new ChunkCoord(6, 0, 0)),
                "the chunk under the walked-to position is not loaded");
            Assert.IsFalse(
                runtime.Scheduler.IsLoaded(new ChunkCoord(0, 0, 0)),
                "the origin chunk is still resident after walking 100 m away");
            Assert.Greater(runtime.Views.UnloadedChunks, 0, "no chunks were unloaded during the walk");
            Assert.LessOrEqual(
                runtime.Views.CreatedViews,
                maxRetained + 8,
                "the view high-water mark exceeded the hysteresis window plus the unload-budget margin");
            Assert.AreEqual(0, runtime.Views.OutstandingMeshData);
        }

        [UnityTest]
        public IEnumerator SteadyStateDoesNotAllocatePerFrame()
        {
            StreamingRuntime runtime = CreateRuntime("Steady", SmallConfig(4), poolCapacity: 32);

            for (int i = 0; i < 240 && !IsQuiescent(runtime); i++)
            {
                runtime.Tick();
                yield return null;
            }

            Assert.IsTrue(IsQuiescent(runtime), "the warm-up did not reach the steady state");
            for (int i = 0; i < 10; i++)
            {
                runtime.Tick();
                yield return null;
            }

            long allocated = 0;
            for (int frame = 0; frame < 120; frame++)
            {
                long before = System.GC.GetAllocatedBytesForCurrentThread();
                runtime.Tick();
                long after = System.GC.GetAllocatedBytesForCurrentThread();
                allocated += after - before;
                yield return null;
            }

            Assert.AreEqual(0, allocated, "the steady-state tick allocated {0} bytes over 120 frames", allocated);
        }

        [UnityTest]
        public IEnumerator UploadedSingleQuadWindsOutwardAndFacesUp()
        {
            var managerObject = new GameObject("Winding");
            managerObject.transform.SetParent(root.transform, false);
            ChunkViewManager manager = managerObject.AddComponent<ChunkViewManager>();
            manager.Initialize(
                SmallConfig(4),
                worldSeed: 17L,
                capacity: 4,
                chunkScheduler: null,
                worldGenerator: new SingleBlockGenerator(new BlockId(1)));

            var coord = new ChunkCoord(0, 0, 0);
            manager.OnLoad(coord);
            for (int frame = 0; frame < 30 && !HasView(manager, coord); frame++)
            {
                manager.ProcessDeferredLoads();
                manager.OnUpload(coord);
                yield return null;
            }

            Assert.IsTrue(manager.TryGetView(coord, out ChunkView view), "the winding chunk never uploaded");
            UnityEngine.Mesh mesh = view.Mesh;
            var vertices = new List<Vector3>();
            var normals = new List<Vector3>();
            var indices = new List<int>();
            mesh.GetVertices(vertices);
            mesh.GetNormals(normals);
            mesh.GetTriangles(indices, 0);

            Assert.AreEqual(24, vertices.Count, "a single block must emit six quads");
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

            Assert.GreaterOrEqual(topVertex, 0, "the uploaded mesh has no +Y-facing vertex");
            int first = topVertex - (topVertex % 4);
            Vector3 cross = Vector3.Cross(
                vertices[first + 1] - vertices[first],
                vertices[first + 2] - vertices[first]);
            Assert.Greater(
                Vector3.Dot(cross.normalized, Vector3.up),
                0.9999f,
                "the top quad does not wind outward in Unity (cross product does not match its +Y normal)");

            int firstIndex = (first / 4) * 6;
            Assert.AreEqual(first + 0, indices[firstIndex + 0], "the uploaded winding was reversed");
            Assert.AreEqual(first + 1, indices[firstIndex + 1]);
            Assert.AreEqual(first + 2, indices[firstIndex + 2]);
            Assert.AreEqual(first + 0, indices[firstIndex + 3]);
            Assert.AreEqual(first + 2, indices[firstIndex + 4]);
            Assert.AreEqual(first + 3, indices[firstIndex + 5]);

            int sideVertex = -1;
            for (int i = 0; i < normals.Count; i++)
            {
                if (normals[i] == Vector3.right)
                {
                    sideVertex = i;
                    break;
                }
            }

            Assert.GreaterOrEqual(sideVertex, 0, "the uploaded mesh has no +X-facing vertex");
            int sideFirst = sideVertex - (sideVertex % 4);
            Vector3 sideCross = Vector3.Cross(
                vertices[sideFirst + 1] - vertices[sideFirst],
                vertices[sideFirst + 2] - vertices[sideFirst]);
            Assert.Greater(
                Vector3.Dot(sideCross.normalized, Vector3.right),
                0.9999f,
                "the +X side quad does not wind outward in Unity (cross product does not match its +X normal)");
        }

        private static StreamingConfig SmallConfig(int uploadBudget)
        {
            return new StreamingConfig
            {
                ViewDistanceChunks = 1,
                UnloadHysteresis = 1,
                VerticalRadiusChunks = 0f,
                MaxLoadsPerFrame = 64,
                MaxUnloadsPerFrame = 64,
                MaxMeshUploadsPerFrame = uploadBudget,
            };
        }

        private ChunkViewManager CreateManager(
            string name,
            StreamingConfig config,
            int capacity,
            out GameObject managerObject)
        {
            managerObject = new GameObject(name);
            managerObject.transform.SetParent(root.transform, false);
            ChunkViewManager manager = managerObject.AddComponent<ChunkViewManager>();
            manager.Initialize(config, worldSeed: 23L, capacity: capacity);
            return manager;
        }

        private StreamingRuntime CreateRuntime(string name, StreamingConfig config, int poolCapacity)
        {
            var runtimeObject = new GameObject(name);
            runtimeObject.transform.SetParent(root.transform, false);
            StreamingRuntime runtime = runtimeObject.AddComponent<StreamingRuntime>();
            runtime.Configure(config, worldSeed: 3L, poolCapacity: poolCapacity, playerTransform: player);
            runtime.AutoUpdate = false;
            return runtime;
        }

        private static IEnumerator PumpUntilUploaded(ChunkViewManager manager, ChunkCoord[] chunks, int maxFrames)
        {
            int next = 0;
            for (int frame = 0; frame < maxFrames && manager.ActiveViews < chunks.Length; frame++)
            {
                manager.ProcessDeferredLoads();
                while (next < chunks.Length && manager.OnUpload(chunks[next]))
                {
                    next++;
                }

                yield return null;
            }
        }

        private static bool HasView(ChunkViewManager manager, ChunkCoord chunk)
        {
            return manager.TryGetView(chunk, out _);
        }

        private static bool IsQuiescent(StreamingRuntime runtime)
        {
            return runtime.Views.DeferredLoads == 0
                && runtime.Scheduler.PendingMeshCount == 0
                && runtime.Views.ActiveViews == runtime.Scheduler.LoadedCount;
        }

        private static ChunkCoord[] NineAround(int x, int y, int z)
        {
            var chunks = new ChunkCoord[9];
            int index = 0;
            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dz = -1; dz <= 1; dz++)
                {
                    chunks[index] = new ChunkCoord(x + dx, y, z + dz);
                    index++;
                }
            }

            return chunks;
        }

        private sealed class SingleBlockGenerator : IWorldGenerator
        {
            private readonly BlockId block;

            internal SingleBlockGenerator(BlockId block)
            {
                this.block = block;
            }

            public Chunk Generate(ChunkCoord coord, long seed)
            {
                var world = new World();
                var chunk = new Chunk(coord);
                world.LoadChunk(chunk);
                world.Apply(new EditCommand(
                    ChunkMath.ToWorld(coord, new Int3(0, 0, 0)),
                    BlockId.Air,
                    block,
                    0L));
                return chunk;
            }
        }
    }
}

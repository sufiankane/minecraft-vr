using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Cubeglass.CoreMath;
using Cubeglass.Gameplay;
using Cubeglass.Streaming;
using Cubeglass.Unity.Input;
using Cubeglass.Voxel;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Cubeglass.Unity.Rendering.Tests
{
    /// <summary>
    /// PlayMode invariants for the pooled chunk views and the streaming runtime
    /// (S7 Task 2, extended in Task 4a): per-frame upload budgets, the
    /// load/upload/unload lifecycle, walking ahead/behind, steady-state
    /// allocation, the mirrored Unity-space winding and chunk placement, dirty
    /// remesh after an edit, and the gaze ray agreeing with the rendered
    /// surface in front.
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
        public IEnumerator DirtyBurstSharesTheUploadBudgetAndDrainsAcrossFrames()
        {
            const int Budget = 2;
            ChunkViewManager manager = CreateManager("DirtyBurst", SmallConfig(Budget), capacity: 16, out _);
            ChunkCoord[] chunks = NineAround(0, 0, 0);
            for (int i = 0; i < chunks.Length; i++)
            {
                manager.OnLoad(chunks[i]);
            }

            yield return PumpUntilUploaded(manager, chunks, 120);
            Assert.AreEqual(chunks.Length, manager.ActiveViews, "the fixture chunks did not upload");

            // Three edits in one frame in the corner, centre and opposite
            // corner chunks; their neighbourhoods union to every loaded chunk.
            long remeshesBefore = manager.RemeshedChunks;
            int[] edited = { 0, 4, 8 };
            for (int i = 0; i < edited.Length; i++)
            {
                Int3 cell = ChunkMath.ToWorld(chunks[edited[i]], new Int3(0, 0, 0));
                Assert.AreEqual(
                    EditResult.Applied,
                    manager.World.Apply(new EditCommand(cell, new BlockId(1), BlockId.Air, 0L)),
                    "fixture break {0} was rejected",
                    i);
            }

            Assert.AreEqual(chunks.Length, manager.DirtyChunks, "every loaded chunk must be dirty in the same frame");

            int processed = manager.ProcessDirtyRemeshes();
            Assert.AreEqual(Budget, processed, "the per-frame upload cap is the limiter");
            Assert.AreEqual(0, manager.ProcessDirtyRemeshes(), "a second pass in the same frame must not exceed the cap");
            Assert.LessOrEqual(manager.UploadedThisFrame, Budget, "the upload cap was exceeded in one frame");

            int frames = 1;
            while (manager.DirtyChunks > 0 && frames < 30)
            {
                manager.ProcessDirtyRemeshes();
                frames++;
                yield return null;
            }

            Assert.AreEqual(0, manager.DirtyChunks, "the dirty queue did not drain");
            Assert.AreEqual(
                chunks.Length,
                manager.RemeshedChunks - remeshesBefore,
                "every dirty chunk must be remeshed exactly once");
            Assert.LessOrEqual(manager.UploadedThisFrame, Budget, "the upload cap was exceeded on the drain frame");
        }

        [UnityTest]
        public IEnumerator RemeshFailureReleasesThePooledView()
        {
            ChunkViewManager manager = CreateManager("RemeshFailure", SmallConfig(4), capacity: 4, out _);
            var coord = new ChunkCoord(0, 0, 0);
            manager.OnLoad(coord);
            yield return PumpUntilUploaded(manager, new[] { coord }, 60);
            Assert.AreEqual(1, manager.ActiveViews, "fixture: the chunk uploaded");

            // Next frame: the upload budget resets so the dirty path is the
            // limiter, not the budget.
            yield return null;

            Int3 cell = ChunkMath.ToWorld(coord, new Int3(0, 0, 0));
            Assert.AreEqual(
                EditResult.Applied,
                manager.World.Apply(new EditCommand(cell, new BlockId(1), BlockId.Air, 0L)),
                "fixture edit was rejected");
            Assert.AreEqual(1, manager.DirtyChunks, "fixture: the chunk is dirty");

            FieldInfo mesherField = typeof(ChunkViewManager).GetField(
                "mesher", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(mesherField, "ChunkViewManager.mesher must exist");
            object mesher = mesherField.GetValue(manager);
            Assert.IsNotNull(mesher, "fixture: the mesher must be initialized");
            mesherField.SetValue(manager, null);
            try
            {
                Assert.Throws<System.NullReferenceException>(
                    () => manager.ProcessDirtyRemeshes(),
                    "a mesher failure must propagate instead of being swallowed");
            }
            finally
            {
                mesherField.SetValue(manager, mesher);
            }

            Assert.AreEqual(0, manager.ActiveViews, "the view must not stay checked out after a failed remesh");
            Assert.AreEqual(1, manager.PooledViews, "the released view must be reusable");
            Assert.AreEqual(0, manager.DirtyChunks, "the dropped view must leave the dirty queue");
            Assert.IsFalse(manager.TryGetView(coord, out _), "the failed view must not stay mapped to the chunk");
            Assert.AreEqual(0, manager.OutstandingMeshData, "no mesh data may leak");
        }

        [UnityTest]
        public IEnumerator SampledPlayerPositionIsConvertedBackToTheInternalFrame()
        {
            StreamingRuntime runtime = CreateRuntime("MirrorWalk", SmallConfig(4), poolCapacity: 16);

            // Unity (8, 8, -40) is internal (8, 8, +40): chunk (0, 0, 2).
            // The mirrored chunk (-3 on z) must stay unloaded.
            player.position = new Vector3(8f, 8f, -40f);
            for (int frame = 0; frame < 240 && !runtime.Scheduler.IsLoaded(new ChunkCoord(0, 0, 2)); frame++)
            {
                runtime.Tick();
                yield return null;
            }

            Assert.IsTrue(
                runtime.Scheduler.IsLoaded(new ChunkCoord(0, 0, 2)),
                "the Unity-space sample was not converted back with z -> -z");
            Assert.IsFalse(
                runtime.Scheduler.IsLoaded(new ChunkCoord(0, 0, -3)),
                "the scheduler must not see the mirrored, unconverted position");
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
        public IEnumerator UploadedSingleQuadWindsOutwardAndIsMirroredInUnity()
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

            // The internal cell (0,0,0) is uploaded as Unity x in [0,1],
            // y in [0,1], z in [-1,0]; its face toward a viewer at +Z is the
            // internal -Z face, whose converted normal is +Z and which sits at
            // Unity z = 0.
            int first = FindQuadFirstFacing(normals, Vector3.forward, "+Z (mirrored -Z)");
            for (int i = 0; i < 4; i++)
            {
                Assert.AreEqual(0f, vertices[first + i].z, 1e-5f, "the mirrored face plane");
            }

            int firstIndex = (first / 4) * 6;
            Vector3 cross = Vector3.Cross(
                vertices[indices[firstIndex + 1]] - vertices[indices[firstIndex + 0]],
                vertices[indices[firstIndex + 2]] - vertices[indices[firstIndex + 0]]);
            Assert.Greater(
                Vector3.Dot(cross.normalized, Vector3.forward),
                0.9999f,
                "the mirrored quad does not wind outward in Unity (cross product does not match its converted +Z normal)");

            // The internal +Z side maps to the Unity -Z side at z = -1.
            int back = FindQuadFirstFacing(normals, Vector3.back, "-Z (mirrored +Z)");
            for (int i = 0; i < 4; i++)
            {
                Assert.AreEqual(-1f, vertices[back + i].z, 1e-5f, "the mirrored +Z face plane");
            }

            Assert.AreEqual(first + 0, indices[firstIndex + 0], "the uploaded winding must be flipped for the mirror");
            Assert.AreEqual(first + 2, indices[firstIndex + 1], "the uploaded winding must be flipped for the mirror");
            Assert.AreEqual(first + 1, indices[firstIndex + 2], "the uploaded winding must be flipped for the mirror");
            Assert.AreEqual(first + 0, indices[firstIndex + 3]);
            Assert.AreEqual(first + 3, indices[firstIndex + 4], "the uploaded winding must be flipped for the mirror");
            Assert.AreEqual(first + 2, indices[firstIndex + 5], "the uploaded winding must be flipped for the mirror");

            Assert.AreEqual(
                Vector3.zero,
                view.GameObject.transform.localPosition,
                "chunk (0,0,0) maps to the Unity origin");

            // A chunk at internal z = 2 is placed at Unity z = -32.
            var mirrored = new ChunkCoord(0, 0, 2);
            manager.OnLoad(mirrored);
            for (int frame = 0; frame < 30 && !HasView(manager, mirrored); frame++)
            {
                manager.ProcessDeferredLoads();
                manager.OnUpload(mirrored);
                yield return null;
            }

            Assert.IsTrue(manager.TryGetView(mirrored, out ChunkView mirroredView), "the mirrored chunk never uploaded");
            Assert.AreEqual(
                -2f * ChunkMath.ChunkSize,
                mirroredView.GameObject.transform.localPosition.z,
                1e-4f,
                "chunk placement uses the converted chunk origin");
        }

        [UnityTest]
        public IEnumerator BreakingABlockRemeshesTheChunkAndPlacingRestoresTheFace()
        {
            var managerObject = new GameObject("Dirty");
            managerObject.transform.SetParent(root.transform, false);
            ChunkViewManager manager = managerObject.AddComponent<ChunkViewManager>();
            manager.Initialize(
                SmallConfig(4),
                worldSeed: 29L,
                capacity: 4,
                chunkScheduler: null,
                worldGenerator: new TwoBlockGenerator(new BlockId(1)));

            var coord = new ChunkCoord(0, 0, 0);
            manager.OnLoad(coord);
            for (int frame = 0; frame < 30 && !HasView(manager, coord); frame++)
            {
                manager.ProcessDeferredLoads();
                manager.OnUpload(coord);
                yield return null;
            }

            Assert.IsTrue(manager.TryGetView(coord, out ChunkView view), "the dirty chunk never uploaded");
            UnityEngine.Mesh mesh = view.Mesh;
            Assert.AreEqual(48, mesh.vertexCount, "two gapped blocks emit twelve quads");
            Assert.IsTrue(HasTopFaceBeyondX(mesh, 2f), "the far block's top face is rendered before the break");

            // Break the far block: the event must mark the (only loaded) chunk
            // and the next frame must remesh it within the upload budget.
            Assert.AreEqual(
                EditResult.Applied,
                manager.World.Apply(new EditCommand(new Int3(2, 0, 0), new BlockId(1), BlockId.Air, 0L)));
            Assert.AreEqual(1, manager.DirtyChunks, "the edited chunk is marked for remesh");

            for (int frame = 0; frame < 10 && manager.RemeshedChunks == 0; frame++)
            {
                manager.ProcessDirtyRemeshes();
                yield return null;
            }

            Assert.AreEqual(1, manager.RemeshedChunks, "the dirty chunk was not remeshed");
            Assert.AreEqual(0, manager.DirtyChunks, "the dirty marker was not cleared");
            Assert.AreEqual(24, mesh.vertexCount, "the broken block's faces are gone from the render mesh");
            Assert.IsFalse(HasTopFaceBeyondX(mesh, 2f), "the broken cell's top face must disappear");

            // Place the block back: the face returns.
            Assert.AreEqual(
                EditResult.Applied,
                manager.World.Apply(new EditCommand(new Int3(2, 0, 0), BlockId.Air, new BlockId(1), 0L)));
            Assert.AreEqual(1, manager.DirtyChunks);

            for (int frame = 0; frame < 10 && manager.RemeshedChunks < 2; frame++)
            {
                manager.ProcessDirtyRemeshes();
                yield return null;
            }

            Assert.AreEqual(2, manager.RemeshedChunks, "the placing edit was not remeshed");
            Assert.AreEqual(48, mesh.vertexCount, "placing restores the removed block's faces");
            Assert.IsTrue(HasTopFaceBeyondX(mesh, 2f), "the restored block's top face is rendered again");
            Assert.AreEqual(0, manager.OutstandingMeshData);
        }

        [UnityTest]
        public IEnumerator GazeRayFromTheCameraHitsTheRenderedSurfaceInFront()
        {
            var managerObject = new GameObject("Gaze");
            managerObject.transform.SetParent(root.transform, false);
            ChunkViewManager manager = managerObject.AddComponent<ChunkViewManager>();
            manager.Initialize(
                SmallConfig(4),
                worldSeed: 31L,
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

            Assert.IsTrue(manager.TryGetView(coord, out ChunkView view), "the gaze chunk never uploaded");

            // The internal block (0,0,0) renders in Unity at z in [-1, 0].
            // A camera at Unity z = 5 facing Unity -Z (rotation 180 about Y)
            // must see that rendered surface in front of it and hit the same
            // internal cell: the mirrored-world regression test.
            var cameraObject = new GameObject("GazeCamera");
            cameraObject.transform.SetParent(root.transform, false);
            Camera camera = cameraObject.AddComponent<Camera>();
            cameraObject.transform.position = new Vector3(0.5f, 0.5f, 5f);
            cameraObject.transform.rotation = Quaternion.Euler(0f, 180f, 0f);

            var renderedVertices = new List<Vector3>();
            var renderedNormals = new List<Vector3>();
            view.Mesh.GetVertices(renderedVertices);
            view.Mesh.GetNormals(renderedNormals);
            bool renderedFaceInFront = false;
            for (int i = 0; i < renderedNormals.Count; i++)
            {
                if (renderedNormals[i] == Vector3.forward && Mathf.Abs(renderedVertices[i].z) <= 1e-4f)
                {
                    renderedFaceInFront = true;
                }
            }

            Assert.IsTrue(renderedFaceInFront, "the converted mesh must have its front face toward the camera");

            Assert.IsTrue(
                GazeTargeting.TryBuildPointerRay(camera, null, out PointerRay pointer),
                "the gaze provider built no pointer ray");
            Assert.IsTrue(pointer.TryToRay(out Cubeglass.Voxel.Ray ray), "the pointer ray is not finite");
            Assert.Greater(ray.Direction.Z, 0.999, "the mirrored camera must look toward internal +Z");

            RayHit? hit = new DdaRaycaster().Cast(manager.World, ray, 10f);
            Assert.IsTrue(hit.HasValue, "the gaze ray must hit the rendered surface in front, not behind");
            Assert.AreEqual(new Int3(0, 0, 0), hit.Value.Cell, "the gaze ray hits the cell rendered in front of it");
        }

        private static bool HasTopFaceBeyondX(UnityEngine.Mesh mesh, float minX)
        {
            var vertices = new List<Vector3>();
            var normals = new List<Vector3>();
            mesh.GetVertices(vertices);
            mesh.GetNormals(normals);
            for (int i = 0; i < normals.Count; i++)
            {
                if (normals[i] == Vector3.up && vertices[i].x >= minX - 1e-4f)
                {
                    return true;
                }
            }

            return false;
        }

        private static int FindQuadFirstFacing(List<Vector3> normals, Vector3 direction, string label)
        {
            for (int i = 0; i < normals.Count; i++)
            {
                if (normals[i] == direction)
                {
                    return i - (i % 4);
                }
            }

            Assert.Fail("the mesh has no " + label + "-facing vertex");
            return -1;
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

        private sealed class TwoBlockGenerator : IWorldGenerator
        {
            private readonly BlockId block;

            internal TwoBlockGenerator(BlockId block)
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
                world.Apply(new EditCommand(
                    ChunkMath.ToWorld(coord, new Int3(2, 0, 0)),
                    BlockId.Air,
                    block,
                    0L));
                return chunk;
            }
        }
    }
}

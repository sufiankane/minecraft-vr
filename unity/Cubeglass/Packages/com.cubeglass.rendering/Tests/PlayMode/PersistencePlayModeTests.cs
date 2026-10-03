using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Cubeglass.CoreMath;
using Cubeglass.Gameplay;
using Cubeglass.Streaming;
using Cubeglass.Unity.Bridge;
using Cubeglass.Unity.Input;
using Cubeglass.Voxel;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Cubeglass.Unity.Rendering.Tests
{
    /// <summary>
    /// PlayMode coverage for S7 Task 4b: the <see cref="FileWorldStore"/>
    /// round trip, <see cref="SaveBatches"/> batching and quit flush, corrupt
    /// delta rejection, boot replay and the non-blocking write path, driven
    /// through <see cref="GameplayBridge"/> where the task calls for it.
    /// </summary>
    public sealed class PersistencePlayModeTests
    {
        private const double Dt = 1.0 / 60.0;
        private const long Seed = 11L;

        private GameObject root;
        private string worldName;
        private FileWorldStore store;

        private StreamingRuntime runtime;
        private Transform streamPlayer;
        private World world;
        private GameplayBridge bridge;
        private SaveBatches saves;
        private FakeInputProvider input;
        private FakePoseProvider pose;
        private Camera gazeCamera;
        private PlayerRoot playerRoot;
        private int surfaceFeetY;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            root = new GameObject("PersistenceTestRoot");
            worldName = "test-" + Guid.NewGuid().ToString("N");
            store = new FileWorldStore(worldName);
            yield return null;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (root != null)
            {
                UnityEngine.Object.Destroy(root);
                root = null;
            }

            yield return null;

            if (store != null)
            {
                store.Dispose();
                string directory = store.WorldDirectory;
                store = null;
                try
                {
                    if (Directory.Exists(directory))
                    {
                        Directory.Delete(directory, true);
                    }
                }
                catch (IOException)
                {
                    // A leftover unique test world never affects other tests.
                }
            }
        }

        [UnityTest]
        public IEnumerator EditedBlocksSurviveAStoreRoundTrip()
        {
            yield return BuildStreamedBridge(Seed);

            var inBetween = new Int3(8, surfaceFeetY, 7);
            var target = new Int3(8, surfaceFeetY, 6);
            TrackedEdit(inBetween, BlockId.Air);
            TrackedEdit(target, new BlockId(1));

            input.Frame = Frame(primary: ButtonState.Held);
            for (int frame = 0; frame < 200 && world.Get(target) != BlockId.Air; frame++)
            {
                bridge.Tick(Dt);
            }

            Assert.AreEqual(BlockId.Air, world.Get(target), "the stone did not break through the bridge");

            var placeCell = new Int3(9, surfaceFeetY, 8);
            var source = new Int3(10, surfaceFeetY, 8);
            TrackedEdit(placeCell, BlockId.Air);
            TrackedEdit(source, new BlockId(1));

            var eye = new Vec3(8.0, surfaceFeetY + PlayerState.BodyHeight, 8.0);
            var faceCenter = new Vec3(10.0, surfaceFeetY + 0.5, 8.5);
            PointerRay pointer = new PointerRay(eye, Vec3.Normalized(faceCenter - eye));
            input.Frame = Frame(secondary: ButtonState.Pressed, pointer: pointer);
            bridge.Tick(Dt);

            Assert.AreEqual(new BlockId(1), world.Get(placeCell), "the placement did not land through the bridge");

            yield return FlushAll();
            Assert.AreEqual(1, store.CountSavedChunks(), "the edits share one chunk and write one delta");

            ChunkCoord coord = ChunkMath.ToChunk(target);
            Assert.AreEqual(coord, ChunkMath.ToChunk(placeCell), "fixture assumption: both edits share a chunk");

            // Rebuild from the same store the documented boot way: fresh World
            // + generate + load.
            var fresh = new World();
            fresh.LoadChunk(new TerrainGenerator().Generate(coord, Seed));
            ChunkDelta delta = null;
            yield return LoadDelta(coord, value => delta = value);
            Assert.IsNotNull(delta, "the delta did not load");

            foreach (KeyValuePair<Int3, BlockId> edit in delta.Edits)
            {
                Int3 cell = ChunkMath.ToWorld(coord, edit.Key);
                EditResult result = fresh.Apply(new EditCommand(cell, fresh.Get(cell), edit.Value, 0L));
                Assert.AreEqual(EditResult.Applied, result, "replaying delta edit at {0} failed", cell);
            }

            Assert.AreEqual(BlockId.Air, fresh.Get(target), "the broken cell did not persist");
            Assert.AreEqual(new BlockId(1), fresh.Get(placeCell), "the placed cell did not persist");
            Assert.AreEqual(
                HashChunk(world.TryGetChunk(coord)),
                HashChunk(fresh.TryGetChunk(coord)),
                "the replayed chunk must hash-equal the live world chunk");
        }

        [UnityTest]
        public IEnumerator BatchingWritesOnTheThirtySecondEdit()
        {
            CreateSaveBatches();
            var coord = new ChunkCoord(0, 0, 0);

            for (int i = 0; i < 31; i++)
            {
                saves.TrackEdit(CellIn(coord, i), new BlockId(1));
            }

            Assert.AreEqual(0, store.SuccessfulWrites, "31 edits must not write");
            Assert.AreEqual(0, store.CountSavedChunks(), "31 edits must not create a file");
            Assert.AreEqual(1, saves.PendingChunks);

            saves.TrackEdit(CellIn(coord, 31), new BlockId(1));
            yield return FlushAll();

            Assert.AreEqual(1, saves.FlushesQueued, "the 32nd edit queues exactly one chunk flush");
            Assert.AreEqual(1, store.SuccessfulWrites, "the 32nd edit writes the delta");
            Assert.AreEqual(1, store.CountSavedChunks());

            ChunkDelta delta = null;
            yield return LoadDelta(coord, value => delta = value);
            Assert.IsNotNull(delta);
            Assert.AreEqual(32, delta.Edits.Count, "the flushed delta carries all 32 cells");
        }

        [UnityTest]
        public IEnumerator TimeFlushWritesAfterTheInterval()
        {
            CreateSaveBatches();
            saves.FlushIntervalSeconds = 2f;
            double now = 0.0;
            saves.Clock = () => now;

            var coord = new ChunkCoord(0, 0, 0);
            saves.TrackEdit(CellIn(coord, 0), new BlockId(4));

            now = 1.5;
            saves.Tick();
            Assert.AreEqual(0, store.SuccessfulWrites, "inside the interval nothing is written");

            now = 2.0;
            saves.Tick();
            yield return FlushAll();
            Assert.AreEqual(1, store.SuccessfulWrites, "the time flush writes after 2 s");
        }

        [UnityTest]
        public IEnumerator CorruptDeltaIsRejectedAndTheChunkGenerates()
        {
            var coord = new ChunkCoord(0, 0, 0);
            File.WriteAllBytes(store.ChunkPath(coord), new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });

            var managerObject = new GameObject("CorruptManager");
            managerObject.transform.SetParent(root.transform, false);
            var manager = managerObject.AddComponent<ChunkViewManager>();
            manager.Store = store;
            manager.Initialize(new StreamingConfig(), 21L, 16);

            manager.OnLoad(coord);

            Assert.AreEqual(1, manager.LoadedChunks, "the chunk generated despite the corrupt delta");
            Assert.AreEqual(1, store.RejectedLoads, "the codec rejection is counted");
            Assert.AreEqual(0, manager.DeltasLoaded, "no delta was applied");
            Assert.AreEqual(
                HashChunk(new TerrainGenerator().Generate(coord, 21L)),
                HashChunk(manager.World.TryGetChunk(coord)),
                "a corrupt delta must leave the generated chunk untouched");

            yield return null;

            var missing = new ChunkCoord(5, 0, 5);
            manager.OnLoad(missing);
            Assert.AreEqual(2, manager.LoadedChunks, "a chunk with no save file generates");
            Assert.AreEqual(1, store.RejectedLoads, "a missing file is not a rejection");
        }

        [UnityTest]
        public IEnumerator QuitFlushPersistsPendingEdits()
        {
            CreateSaveBatches();
            var cell = new Int3(3, 4, 5);
            saves.TrackEdit(cell, new BlockId(2));

            Assert.IsFalse(store.HasSavedChunk(ChunkMath.ToChunk(cell)), "nothing is written before the flush");

            int flushed = saves.Flush();

            Assert.AreEqual(1, flushed, "the dirty chunk was queued");
            Assert.AreEqual(0, saves.TimedOutFlushes, "the bounded wait must succeed on a local disk");
            Assert.AreEqual(1, store.SuccessfulWrites);
            Assert.IsTrue(store.HasSavedChunk(ChunkMath.ToChunk(cell)), "the flush created the delta file");

            // Pin the lifecycle callbacks to the same Flush path: pause and
            // quit each persist the newest complete edit map.
            saves.TrackEdit(new Int3(6, 7, 8), new BlockId(3));
            InvokeLifecycle(saves, "OnApplicationPause", true);
            Assert.AreEqual(2, store.SuccessfulWrites, "OnApplicationPause(true) flushes");

            saves.TrackEdit(new Int3(9, 10, 11), new BlockId(4));
            InvokeLifecycle(saves, "OnApplicationQuit");
            Assert.AreEqual(3, store.SuccessfulWrites, "OnApplicationQuit flushes");

            ChunkDelta delta = null;
            yield return LoadDelta(ChunkMath.ToChunk(cell), value => delta = value);
            Assert.IsNotNull(delta);
            Assert.AreEqual(new BlockId(2), delta.Edits[new Int3(3, 4, 5)], "the first edit persisted");
            Assert.AreEqual(new BlockId(3), delta.Edits[new Int3(6, 7, 8)], "the pause flush persisted");
            Assert.AreEqual(new BlockId(4), delta.Edits[new Int3(9, 10, 11)], "the quit flush persisted");
        }

        [UnityTest]
        public IEnumerator SaveAsyncDoesNotBlockTheMainThread()
        {
            yield return BuildStreamedBridge(31L);

            var cell = new Int3(8, surfaceFeetY, 6);
            ChunkCoord coord = ChunkMath.ToChunk(cell);
            var edits = new Dictionary<Int3, BlockId> { { ChunkMath.ToLocal(cell), BlockId.Air } };
            store.WriteDelay = TimeSpan.FromMilliseconds(300);

            var watch = Stopwatch.StartNew();
            store.SaveAsync(coord, new ChunkDelta(coord, edits), CancellationToken.None);
            watch.Stop();

            Assert.Less(watch.ElapsedMilliseconds, 100, "SaveAsync must return without performing the write");
            Assert.IsFalse(store.HasSavedChunk(coord), "the file cannot exist while the write delay is pending");

            long slowestFrameMs = 0;
            for (int frame = 0; frame < 30; frame++)
            {
                watch.Restart();
                runtime.Tick();
                bridge.Tick(Dt);
                watch.Stop();
                slowestFrameMs = Math.Max(slowestFrameMs, watch.ElapsedMilliseconds);
                yield return null;
            }

            Assert.Less(
                slowestFrameMs,
                250,
                "a pending background write must not stall gameplay ticks (slowest frame {0} ms)",
                slowestFrameMs);

            float deadline = Time.realtimeSinceStartup + 5f;
            while (!store.HasSavedChunk(coord) && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }

            Assert.IsTrue(store.HasSavedChunk(coord), "the delayed background write eventually lands");
            Assert.AreEqual(1, store.SuccessfulWrites);
        }

        private IEnumerator BuildStreamedBridge(long seed)
        {
            streamPlayer = new GameObject("StreamPlayer").transform;
            streamPlayer.SetParent(root.transform, false);
            streamPlayer.position = new Vector3(8f, 8f, 8f);

            var runtimeObject = new GameObject("Streaming");
            runtimeObject.transform.SetParent(root.transform, false);
            runtime = runtimeObject.AddComponent<StreamingRuntime>();
            runtime.Store = store;
            var config = new StreamingConfig
            {
                ViewDistanceChunks = 2,
                UnloadHysteresis = 1,
                VerticalRadiusChunks = 0f,
                MaxLoadsPerFrame = 64,
                MaxUnloadsPerFrame = 64,
                MaxMeshUploadsPerFrame = 8,
            };
            runtime.Configure(config, seed, 64, streamPlayer);
            runtime.AutoUpdate = false;

            for (int frame = 0; frame < 480 && !(ColumnReady(8, 8) && IsQuiescent()); frame++)
            {
                runtime.Tick();
                yield return null;
            }

            Assert.IsTrue(ColumnReady(8, 8), "the streamed world never generated the player's column");
            Assert.IsTrue(IsQuiescent(), "the streamed world did not settle");
            world = runtime.Views.World;
            Assert.IsNotNull(world, "the view manager exposed no world");
            surfaceFeetY = SurfaceY(8, 8) + 1;

            var bridgeObject = new GameObject("Bridge");
            bridgeObject.transform.SetParent(root.transform, false);
            bridge = bridgeObject.AddComponent<GameplayBridge>();
            input = new FakeInputProvider();
            pose = new FakePoseProvider();

            var cameraObject = new GameObject("GazeCamera");
            cameraObject.transform.SetParent(root.transform, false);
            gazeCamera = cameraObject.AddComponent<Camera>();
            gazeCamera.transform.rotation = Quaternion.identity;
            gazeCamera.transform.position = new Vector3(8f, surfaceFeetY + (float)PlayerState.BodyHeight, -8f);

            var playerRootObject = new GameObject("PlayerRoot");
            playerRootObject.transform.SetParent(root.transform, false);
            playerRoot = playerRootObject.AddComponent<PlayerRoot>();
            var headObject = new GameObject("Head");
            headObject.transform.SetParent(playerRootObject.transform, false);
            playerRoot.Head = headObject.transform;

            bridge.Streaming = runtime;
            bridge.InputSource = input;
            bridge.PoseSource = pose;
            bridge.GazeCamera = gazeCamera;
            bridge.PlayerRoot = playerRoot;
            bridge.AutoUpdate = false;
            bridge.EnsureInitialized();
            bridge.Player.Position = new Vec3(8.0, surfaceFeetY, 8.0);
            input.Frame = Frame();

            CreateSaveBatches();
            bridge.SaveBatches = saves;
        }

        private void CreateSaveBatches()
        {
            var savesObject = new GameObject("SaveBatches");
            savesObject.transform.SetParent(root.transform, false);
            saves = savesObject.AddComponent<SaveBatches>();
            saves.Store = store;
            saves.EditsPerFlush = SaveBatches.DefaultEditsPerFlush;
            saves.FlushIntervalSeconds = 600f;
        }

        private void TrackedEdit(Int3 cell, BlockId block)
        {
            EditResult result = world.Apply(new EditCommand(cell, world.Get(cell), block, 0L));
            Assert.AreEqual(EditResult.Applied, result, "fixture edit at {0} was rejected", cell);
            saves.TrackEdit(cell, block);
        }

        private IEnumerator FlushAll()
        {
            Task task = saves.FlushAsync();
            while (!task.IsCompleted)
            {
                yield return null;
            }

            Assert.IsFalse(task.IsFaulted, "the flush faulted: {0}", task.Exception);
        }

        private IEnumerator LoadDelta(ChunkCoord coord, Action<ChunkDelta> assign)
        {
            ValueTask<ChunkDelta> load = store.LoadAsync(coord, CancellationToken.None);
            Task<ChunkDelta> task = load.AsTask();
            while (!task.IsCompleted)
            {
                yield return null;
            }

            Assert.IsFalse(task.IsFaulted, "the delta load faulted: {0}", task.Exception);
            assign(task.Result);
        }

        private static void InvokeLifecycle(SaveBatches target, string method, params object[] args)
        {
            MethodInfo info = typeof(SaveBatches).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(info, "SaveBatches." + method + " must exist");
            info.Invoke(target, args);
        }

        private bool IsQuiescent()
        {
            return runtime.Views.DeferredLoads == 0
                && runtime.Scheduler.PendingMeshCount == 0
                && runtime.Views.ActiveViews == runtime.Scheduler.LoadedCount;
        }

        private bool ColumnReady(int x, int z)
        {
            World streamed = runtime.Views != null ? runtime.Views.World : null;
            if (streamed == null || !streamed.IsLoaded(new Int3(x, 0, z)))
            {
                return false;
            }

            for (int y = 0; y <= 30; y++)
            {
                if (streamed.Get(new Int3(x, y, z)) != BlockId.Air)
                {
                    return true;
                }
            }

            return false;
        }

        private int SurfaceY(int x, int z)
        {
            for (int y = 30; y >= 0; y--)
            {
                if (world.Get(new Int3(x, y, z)) != BlockId.Air)
                {
                    return y;
                }
            }

            Assert.Fail("no solid surface under ({0}, {1})", x, z);
            return 0;
        }

        private static Int3 CellIn(ChunkCoord coord, int index)
        {
            return new Int3(
                (coord.X * ChunkMath.ChunkSize) + (index % ChunkMath.ChunkSize),
                (coord.Y * ChunkMath.ChunkSize) + ((index / ChunkMath.ChunkSize) % ChunkMath.ChunkSize),
                coord.Z * ChunkMath.ChunkSize);
        }

        private static ulong HashChunk(Chunk chunk)
        {
            Assert.IsNotNull(chunk, "the chunk to hash must exist");
            ulong hash = 14695981039346656037UL;
            for (int z = 0; z < ChunkMath.ChunkSize; z++)
            {
                for (int y = 0; y < ChunkMath.ChunkSize; y++)
                {
                    for (int x = 0; x < ChunkMath.ChunkSize; x++)
                    {
                        hash ^= chunk.Get(new Int3(x, y, z)).Value;
                        hash *= 1099511628211UL;
                    }
                }
            }

            return hash;
        }

        private static InputFrame Frame(
            Vector2f move = default,
            float turn = 0f,
            ButtonState primary = ButtonState.Up,
            ButtonState secondary = ButtonState.Up,
            PointerRay? pointer = null)
        {
            return new InputFrame(move, turn, false, pointer, primary, secondary, 0, TrackingQuality.Good);
        }

        private sealed class FakeInputProvider : IInputProvider
        {
            public InputFrame Frame;

            public InputFrame Sample(double timeSeconds)
            {
                return Frame;
            }
        }

        private sealed class FakePoseProvider : IPoseProvider
        {
            public BridgeHeadSample Sample = new BridgeHeadSample { State = TrackState.Stable };

            public bool TryGetLatest(out BridgeHeadSample sample)
            {
                sample = Sample;
                return true;
            }
        }
    }
}

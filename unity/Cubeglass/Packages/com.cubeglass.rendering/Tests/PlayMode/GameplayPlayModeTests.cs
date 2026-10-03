using System.Collections;
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
    /// End-to-end PlayMode coverage for the S7 Task 3 gameplay bridge,
    /// extended in Task 4a: a streamed <see cref="TerrainGenerator"/> world,
    /// the player on its surface, gaze targeting, hardness-timed breaking,
    /// face placement, hotbar cycling, degrees-per-second turn through the
    /// controller, the snap edge on the heading, recentre clearing the head
    /// offset, the post-compaction live-world re-resolve, vignette speed
    /// forwarding and a steady-frame zero-allocation check over the bridge +
    /// HUD path.
    /// </summary>
    public sealed class GameplayPlayModeTests
    {
        private const double Dt = 1.0 / 60.0;
        private const float Tolerance = 1e-4f;

        private GameObject root;
        private StreamingRuntime runtime;
        private Transform streamPlayer;
        private World world;
        private GameplayBridge bridge;
        private FakeInputProvider input;
        private FakePoseProvider pose;
        private GameObject cameraObject;
        private Camera gazeCamera;
        private GameObject playerRootObject;
        private PlayerRoot playerRoot;
        private int surfaceFeetY;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            root = new GameObject("GameplayTestRoot");

            streamPlayer = new GameObject("StreamPlayer").transform;
            streamPlayer.SetParent(root.transform, false);
            streamPlayer.position = new Vector3(8f, 8f, 8f);

            var runtimeObject = new GameObject("Streaming");
            runtimeObject.transform.SetParent(root.transform, false);
            runtime = runtimeObject.AddComponent<StreamingRuntime>();
            var config = new StreamingConfig
            {
                ViewDistanceChunks = 2,
                UnloadHysteresis = 1,
                VerticalRadiusChunks = 0f,
                MaxLoadsPerFrame = 64,
                MaxUnloadsPerFrame = 64,
                MaxMeshUploadsPerFrame = 8,
            };
            runtime.Configure(config, worldSeed: 11L, poolCapacity: 64, playerTransform: streamPlayer);
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

            cameraObject = new GameObject("GazeCamera");
            cameraObject.transform.SetParent(root.transform, false);
            gazeCamera = cameraObject.AddComponent<Camera>();
            gazeCamera.transform.rotation = Quaternion.identity;
            PlaceGazeCamera();

            bridge.Streaming = runtime;
            bridge.InputSource = input;
            bridge.PoseSource = pose;
            bridge.GazeCamera = gazeCamera;
            bridge.AutoUpdate = false;

            playerRootObject = new GameObject("PlayerRoot");
            playerRootObject.transform.SetParent(root.transform, false);
            playerRoot = playerRootObject.AddComponent<PlayerRoot>();
            var headObject = new GameObject("Head");
            headObject.transform.SetParent(playerRootObject.transform, false);
            playerRoot.Head = headObject.transform;
            bridge.PlayerRoot = playerRoot;

            Assert.IsTrue(bridge.EnsureInitialized(), "the bridge did not initialize");
            bridge.Player.Position = new Vec3(8.0, surfaceFeetY, 8.0);
            bridge.Player.HotbarIndex = 0;
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
        public IEnumerator GazeBreakFollowsHardnessAndLeavesAir()
        {
            var target = new Int3(8, surfaceFeetY, 6);
            var inBetween = new Int3(8, surfaceFeetY, 7);
            SetBlock(inBetween, BlockId.Air);
            SetBlock(target, new BlockId(1));

            input.Frame = Frame(primary: ButtonState.Held);

            // 89 frames of 1/60 s is 1.4833 s: below stone's 1.5 s hardness.
            for (int frame = 0; frame < 89; frame++)
            {
                bridge.Tick(Dt);
            }

            Assert.AreNotEqual(BlockId.Air, world.Get(target), "stone broke before its hardness time");
            Assert.IsTrue(bridge.BreakProgress > 0.9f, "break progress approached completion");

            for (int frame = 0; frame < 6 && world.Get(target) != BlockId.Air; frame++)
            {
                bridge.Tick(Dt);
            }

            Assert.AreEqual(BlockId.Air, world.Get(target), "stone did not break after its hardness time");
            Assert.AreEqual(1, bridge.EditsApplied, "exactly one edit landed");
            Assert.IsTrue(bridge.SaveRequested, "the bridge requested a save after the edit");
            yield return null;
        }

        [UnityTest]
        public IEnumerator PlacementOnAFaceWritesTheSelectedBlock()
        {
            var placeCell = new Int3(9, surfaceFeetY, 8);
            var source = new Int3(10, surfaceFeetY, 8);
            SetBlock(placeCell, BlockId.Air);
            SetBlock(source, new BlockId(1));

            var eye = new Vec3(8.0, surfaceFeetY + PlayerState.BodyHeight, 8.0);
            var faceCenter = new Vec3(10.0, surfaceFeetY + 0.5, 8.5);
            PointerRay pointer = new PointerRay(eye, Normalized(faceCenter - eye));

            input.Frame = Frame(secondary: ButtonState.Pressed, pointer: pointer);
            bridge.Tick(Dt);

            Assert.AreEqual(new BlockId(1), world.Get(placeCell), "the face placement wrote the selected block");
            Assert.AreEqual(1, bridge.EditsApplied);
            Assert.IsTrue(bridge.SaveRequested, "the edit raised the save request for Task 4");
            yield return null;
        }

        [UnityTest]
        public IEnumerator BreakAfterWorldCompactionStillRemeshesThroughTheLiveWorld()
        {
            // Regression for the S7 Task 4a fix round: ChunkViewManager replaces
            // its World when it compacts, and GameplayBridge used to cache the
            // old instance, so edits no longer reached the manager's
            // ChunkChanged subscription and the render mesh went stale.
            ChunkViewManager manager = runtime.Views;
            World worldBeforeCompaction = manager.World;
            Assert.AreEqual(0, manager.WorldCompactions, "fixture assumed no compaction yet");
            Vector3 home = streamPlayer.position;

            // Drive the real generation counter through the public path: the
            // sample jumps far away every frame, so one new chunk is generated
            // per frame until worldChunks passes 2 * live + 64 and the manager
            // compacts the neighbour-snapshot world.
            int guard = 0;
            while (manager.WorldCompactions == 0 && guard < 600)
            {
                streamPlayer.position = home + new Vector3(16f * (1 + (guard % 64)), 0f, 0f);
                runtime.Tick();
                guard++;
                yield return null;
            }

            Assert.GreaterOrEqual(manager.WorldCompactions, 1, "the world compaction never triggered");
            World worldAfterCompaction = manager.World;
            Assert.AreNotSame(worldBeforeCompaction, worldAfterCompaction, "compaction must replace the world instance");

            // Let the home column re-stream into the replaced world.
            streamPlayer.position = home;
            for (int frame = 0; frame < 480 && !(ColumnReady(8, 8) && IsQuiescent()); frame++)
            {
                runtime.Tick();
                yield return null;
            }

            Assert.IsTrue(ColumnReady(8, 8) && IsQuiescent(), "the home column did not re-stream after compaction");

            bridge.Tick(Dt);

            // Fixture edits go through the world the bridge will raycast, so
            // the break lands in both the fixed and the pre-fix run; only the
            // mesh change distinguishes them.
            World bridgeWorld = bridge.World;
            var target = new Int3(8, surfaceFeetY, 6);
            var inBetween = new Int3(8, surfaceFeetY, 7);
            SetBlock(bridgeWorld, inBetween, BlockId.Air);
            SetBlock(bridgeWorld, target, new BlockId(1));

            for (int frame = 0; frame < 120 && manager.DirtyChunks > 0; frame++)
            {
                runtime.Tick();
                yield return null;
            }

            Assert.AreEqual(0, manager.DirtyChunks, "the fixture edits did not remesh");
            ChunkCoord chunkCoord = ChunkMath.ToChunk(target);
            Assert.IsTrue(manager.TryGetView(chunkCoord, out ChunkView view), "the target chunk has no active view");
            int before = view.Mesh.vertexCount;

            input.Frame = Frame(primary: ButtonState.Held);
            for (int frame = 0; frame < 120 && bridgeWorld.Get(target) != BlockId.Air; frame++)
            {
                bridge.Tick(Dt);
            }

            Assert.AreEqual(BlockId.Air, bridgeWorld.Get(target), "the stone did not break through the bridge");
            Assert.AreEqual(1, bridge.EditsApplied, "exactly one edit landed");

            for (int frame = 0; frame < 120 && manager.DirtyChunks > 0; frame++)
            {
                runtime.Tick();
                yield return null;
            }

            Assert.AreEqual(0, manager.DirtyChunks, "the break did not reach the live world's remesh queue");
            Assert.IsTrue(manager.TryGetView(chunkCoord, out view), "the target chunk lost its view");
            Assert.AreNotEqual(
                before,
                view.Mesh.vertexCount,
                "the rendered mesh for the edited chunk must change after compaction");
            Assert.AreSame(manager.World, bridge.World, "the bridge must re-resolve the live world after compaction");
        }

        [UnityTest]
        public IEnumerator HotbarCyclesAndWrapsInLockstepWithThePlayerState()
        {
            TickWithHotbar(1);
            Assert.AreEqual(1, bridge.HotbarIndex);
            TickWithHotbar(1);
            Assert.AreEqual(2, bridge.HotbarIndex);
            TickWithHotbar(-1);
            Assert.AreEqual(1, bridge.HotbarIndex);
            TickWithHotbar(-1);
            Assert.AreEqual(0, bridge.HotbarIndex);
            TickWithHotbar(-1);
            Assert.AreEqual(8, bridge.HotbarIndex, "cycling wraps below zero");
            TickWithHotbar(1);
            Assert.AreEqual(0, bridge.HotbarIndex, "cycling wraps above eight");
            yield return null;
        }

        [UnityTest]
        public IEnumerator ScriptedTurnAtFortyFiveDegreesPerSecondChangesTheHeadingByFortyFive()
        {
            // The ruling pins the S4 unit with the pure ScriptedInputProvider:
            // one second of 45 deg/s must integrate to exactly 45 degrees.
            var scripted = new ScriptedInputProvider(new (double, InputFrame)[]
            {
                (0.0, new InputFrame(
                    Vector2f.Zero,
                    45f,
                    false,
                    null,
                    ButtonState.Up,
                    ButtonState.Up,
                    0,
                    TrackingQuality.Good)),
            });

            bridge.InputSource = scripted;
            float before = bridge.Player.YawRadians;
            for (int frame = 0; frame < 60; frame++)
            {
                bridge.Tick(Dt);
            }

            Assert.AreEqual(
                45f,
                (bridge.Player.YawRadians - before) * Mathf.Rad2Deg,
                0.05f,
                "45 deg/s for 1 s must change the internal heading by 45 degrees");
            Assert.Less(
                Quaternion.Angle(Quaternion.Euler(0f, -45f, 0f), playerRootObject.transform.localRotation),
                0.5f,
                "PlayerRoot follows the integrated heading through the single conversion");
            yield return null;
        }

        [UnityTest]
        public IEnumerator SnapEdgeTurnsTheHeadingAndSurvivesPoseTicks()
        {
            var snapObject = new GameObject("SnapTurn");
            snapObject.transform.SetParent(root.transform, false);
            SnapTurn snap = snapObject.AddComponent<SnapTurn>();
            bridge.SnapTurn = snap;

            var snapInput = new FakeSnapInputProvider();
            bridge.InputSource = snapInput;

            snapInput.Frame = Frame();
            snapInput.SnapPending = true;
            bridge.Tick(Dt);

            Assert.AreEqual(45f, snap.AccumulatedDegrees, Tolerance, "exactly one increment");
            Assert.AreEqual(
                -45f,
                bridge.Player.YawRadians * Mathf.Rad2Deg,
                0.01f,
                "the snap lands in the heading (Unity right is a negative internal yaw)");
            Assert.Less(
                Quaternion.Angle(Quaternion.Euler(0f, 45f, 0f), playerRootObject.transform.localRotation),
                1e-3f,
                "PlayerRoot shows the snapped heading");
            Assert.IsFalse(snapInput.SnapPending, "the edge was consumed exactly once");

            snapInput.Frame = Frame();
            for (int frame = 0; frame < 3; frame++)
            {
                bridge.Tick(Dt);
            }

            Assert.AreEqual(
                -45f,
                bridge.Player.YawRadians * Mathf.Rad2Deg,
                0.01f,
                "later pose ticks must not drift or overwrite the snap");
            yield return null;
        }

        [UnityTest]
        public IEnumerator SnapDisabledStillIntegratesContinuousTurn()
        {
            var snapObject = new GameObject("SnapTurn");
            snapObject.transform.SetParent(root.transform, false);
            SnapTurn snap = snapObject.AddComponent<SnapTurn>();
            snap.SnapEnabled = false;
            bridge.SnapTurn = snap;

            var snapInput = new FakeSnapInputProvider();
            bridge.InputSource = snapInput;
            snapInput.Frame = Frame(turn: 90f);
            snapInput.SnapPending = true;

            float before = bridge.Player.YawRadians;
            for (int frame = 0; frame < 30; frame++)
            {
                bridge.Tick(Dt);
            }

            Assert.AreEqual(0f, snap.AccumulatedDegrees, Tolerance, "the disabled snap is a no-op");
            Assert.AreEqual(
                45f,
                (bridge.Player.YawRadians - before) * Mathf.Rad2Deg,
                0.05f,
                "a snap-disabled frame must still turn continuously (no dropped turn)");
            yield return null;
        }

        [UnityTest]
        public IEnumerator RecentreClearsTheHeadingAndTheHeadOffset()
        {
            var headObject = new GameObject("RecentreHead");
            headObject.transform.SetParent(playerRootObject.transform, false);
            var stereo = headObject.AddComponent<StereoRig>();
            var latch = headObject.AddComponent<LateLatchPose>();
            stereo.ApplyEyeLayout();
            latch.Rig = stereo;

            var headPose = new FakePoseProvider { Sample = Yaw90Sample() };
            latch.Provider = headPose;
            bridge.LateLatch = latch;

            latch.TickOnce();
            Assert.Less(
                Quaternion.Angle(Quaternion.Euler(0f, -90f, 0f), headObject.transform.localRotation),
                1e-3f,
                "the head offset is applied before recentring");
            bridge.Player.YawRadians = 0.5f;

            input.Frame = new InputFrame(
                Vector2f.Zero,
                0f,
                true,
                null,
                ButtonState.Up,
                ButtonState.Up,
                0,
                TrackingQuality.Good);
            bridge.Tick(Dt);

            Assert.AreEqual(0f, bridge.Player.YawRadians, 1e-6f, "recentre zeroes the heading");
            Assert.Less(
                Quaternion.Angle(Quaternion.identity, playerRootObject.transform.localRotation),
                1e-3f,
                "PlayerRoot returns to the player's forward");
            Assert.Less(
                Quaternion.Angle(Quaternion.identity, headObject.transform.localRotation),
                1e-3f,
                "the head offset is cleared immediately (local offset reset)");

            latch.TickOnce();
            Assert.Less(
                Quaternion.Angle(Quaternion.identity, headObject.transform.localRotation),
                1e-3f,
                "the next sample stays relative to the recentred baseline");
            yield return null;
        }

        [UnityTest]
        public IEnumerator VignetteOpacityFollowsThePlanarSpeed()
        {
            var overlayObject = new GameObject("Vignette");
            overlayObject.transform.SetParent(root.transform, false);
            MotionVignette vignette = overlayObject.AddComponent<MotionVignette>();
            bridge.MotionVignette = vignette;

            // Airborne so the walk speed is not zeroed by a wall.
            bridge.Player.Position = new Vec3(8.0, surfaceFeetY + 3.0, 8.0);
            input.Frame = Frame(move: new Vector2f(0f, 1f));
            bridge.Tick(Dt);

            Assert.AreEqual(4.5f, bridge.PlanarSpeed, 0.01f, "walk speed forwarded");
            Assert.AreEqual(0.4f, vignette.Opacity, Tolerance, "full opacity at walk speed");

            input.Frame = Frame();
            bridge.Tick(Dt);
            Assert.AreEqual(0f, bridge.PlanarSpeed, 0.01f, "stopping clears the planar speed");
            Assert.AreEqual(0f, vignette.Opacity, Tolerance, "vignette clears when stopped");
            yield return null;
        }

        [UnityTest]
        public IEnumerator SteadyBridgeAndHudFramesAllocateZeroBytes()
        {
            var hudObject = new GameObject("WorldUi");
            hudObject.transform.SetParent(root.transform, false);
            WorldUi hud = hudObject.AddComponent<WorldUi>();
            hud.GazeCamera = gazeCamera;
            hud.AnchorSource = streamPlayer;
            hud.Bridge = bridge;
            hud.Refresh();

            // The debug overlay would be disabled here; no edits are in flight
            // and the provider is neutral, so the bridge reaches its steady path.
            input.Frame = Frame();
            for (int frame = 0; frame < 10; frame++)
            {
                bridge.Tick(Dt);
                hud.Refresh();
                yield return null;
            }

            long allocated = 0;
            for (int frame = 0; frame < 120; frame++)
            {
                long before = System.GC.GetAllocatedBytesForCurrentThread();
                bridge.Tick(Dt);
                hud.Refresh();
                long after = System.GC.GetAllocatedBytesForCurrentThread();
                allocated += after - before;
                yield return null;
            }

            Assert.AreEqual(0, allocated, "steady frames allocated {0} bytes over 120 frames", allocated);
        }

        private static bool IsQuiescent(StreamingRuntime streaming)
        {
            return streaming.Views.DeferredLoads == 0
                && streaming.Scheduler.PendingMeshCount == 0
                && streaming.Views.ActiveViews == streaming.Scheduler.LoadedCount;
        }

        private bool IsQuiescent()
        {
            return IsQuiescent(runtime);
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

        private void SetBlock(Int3 cell, BlockId block)
        {
            SetBlock(runtime.Views.World, cell, block);
        }

        private static void SetBlock(World target, Int3 cell, BlockId block)
        {
            EditResult result = target.Apply(new EditCommand(cell, target.Get(cell), block, 0L));
            Assert.AreEqual(EditResult.Applied, result, "fixture edit at {0} was rejected", cell);
        }

        private void PlaceGazeCamera()
        {
            // The ADR-0004 flip maps internal (x, y, z) to Unity (x, y, -z), so
            // an internal eye at the player flips back here; identity rotation
            // is internal yaw zero (looking down voxel -Z).
            gazeCamera.transform.position = new Vector3(8f, surfaceFeetY + (float)PlayerState.BodyHeight, -8f);
        }

        private void TickWithHotbar(int delta)
        {
            input.Frame = new InputFrame(
                Vector2f.Zero,
                0f,
                false,
                null,
                ButtonState.Up,
                ButtonState.Up,
                delta,
                TrackingQuality.Good);
            bridge.Tick(Dt);
        }

        private InputFrame Frame(
            Vector2f move = default,
            float turn = 0f,
            ButtonState primary = ButtonState.Up,
            ButtonState secondary = ButtonState.Up,
            PointerRay? pointer = null)
        {
            return new InputFrame(move, turn, false, pointer, primary, secondary, 0, TrackingQuality.Good);
        }

        private static Vec3 Normalized(Vec3 value)
        {
            return Vec3.Normalized(value);
        }

        private static BridgeHeadSample Yaw90Sample()
        {
            double half = System.Math.PI / 4.0;
            return new BridgeHeadSample
            {
                HostTime = 1L,
                Pose = new BridgePose
                {
                    Position = new BridgeVec3 { X = 0f, Y = 0f, Z = 0f },
                    Rotation = new BridgeQuat
                    {
                        W = (float)System.Math.Cos(half),
                        X = 0f,
                        Y = (float)System.Math.Sin(half),
                        Z = 0f,
                    },
                },
                State = TrackState.Stable,
                Sequence = 1,
            };
        }

        private sealed class FakeInputProvider : IInputProvider
        {
            public InputFrame Frame;

            public InputFrame Sample(double timeSeconds)
            {
                return Frame;
            }
        }

        private sealed class FakeSnapInputProvider : IInputProvider, ISnapInputSource
        {
            public InputFrame Frame;
            public bool SnapPending;

            public InputFrame Sample(double timeSeconds)
            {
                return Frame;
            }

            public bool ConsumeSnapPressed()
            {
                bool pending = SnapPending;
                SnapPending = false;
                return pending;
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

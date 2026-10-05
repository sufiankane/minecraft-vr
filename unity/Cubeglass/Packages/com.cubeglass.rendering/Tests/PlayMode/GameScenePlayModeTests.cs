using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Cubeglass.CoreMath;
using Cubeglass.Gameplay;
using Cubeglass.Streaming;
using Cubeglass.Unity.Input;
using Cubeglass.Unity.Rendering;
using Cubeglass.Voxel;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Cubeglass.Unity.Rendering.Tests
{
    /// <summary>
    /// End-to-end PlayMode smoke over the committed S7 game scene (Task 4c):
    /// loads <c>Assets/Scenes/Game.unity</c> additively, redirects its store to
    /// a unique temp world through the <see cref="GameBoot"/> test seams, and
    /// runs boot → stream → land → gaze-break → place → flush → reload against
    /// the real scene wiring. Loading the committed scene is deliberate: it
    /// pins the scene the builder writes, and
    /// <see cref="EditorSceneManager.LoadSceneInPlayMode"/> is the documented
    /// way to load a scene in a PlayMode test without adding it to the build
    /// settings (the same pattern the calibration frame-budget test uses).
    /// </summary>
    /// <remarks>
    /// The scene boots with auto-update on; the test turns
    /// <see cref="StreamingRuntime.AutoUpdate"/> and
    /// <see cref="GameplayBridge.AutoUpdate"/> off immediately after loading
    /// (before the first Update) and drives both with fixed 1/60 s ticks, so
    /// timing is deterministic and no real <c>default</c> world is ever
    /// touched. The gaze is aimed by scripting the scene's fallback
    /// <see cref="SyntheticPoseProvider"/> and applying it through the wired
    /// <see cref="LateLatchPose"/>, which exercises the same gaze chain the
    /// scene uses.
    /// </remarks>
    public sealed class GameScenePlayModeTests
    {
        private const string GameScenePath = "Assets/Scenes/Game.unity";
        private const double Dt = 1.0 / 60.0;
        private const int ChunksToMesh = 9;
        private const long WorldSeed = 1L;

        private string worldName;
        private string rootDirectory;
        private Scene gameScene;

        private GameObject reloadRoot;
        private ChunkViewManager reloadManager;
        private FileWorldStore reloadStore;

        private GameBoot boot;
        private StreamingRuntime runtime;
        private GameplayBridge bridge;
        private SaveBatches saves;
        private WorldUi hud;
        private MotionVignette vignette;
        private LateLatchPose latch;
        private SyntheticPoseProvider provider;
        private PoseProviderSelector selector;
        private PlayerRoot playerRoot;
        private StereoRig rig;
        private WindowManager window;
        private FakeInputProvider input;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            Assert.IsNull(GameBoot.WorldNameOverride, "a previous test leaked the world-name override");
            Assert.IsNull(GameBoot.RootDirectoryOverride, "a previous test leaked the root override");
            Assert.IsFalse(
                PoseProviderSelector.ForceSyntheticForTests,
                "a previous test leaked the pose-provider override");

            worldName = "game-scene-" + Guid.NewGuid().ToString("N");
            rootDirectory = Path.Combine(Path.GetTempPath(), "cg-game-scene-" + Guid.NewGuid().ToString("N"));
            GameBoot.WorldNameOverride = worldName;
            GameBoot.RootDirectoryOverride = rootDirectory;
            PoseProviderSelector.ForceSyntheticForTests = true;
            input = new FakeInputProvider();
            yield return null;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            GameBoot.WorldNameOverride = null;
            GameBoot.RootDirectoryOverride = null;
            PoseProviderSelector.ForceSyntheticForTests = false;

            if (reloadRoot != null)
            {
                Object.Destroy(reloadRoot);
                reloadRoot = null;
                reloadManager = null;
            }

            if (reloadStore != null)
            {
                reloadStore.Dispose();
                reloadStore = null;
            }

            if (gameScene.IsValid() && gameScene.isLoaded)
            {
                AsyncOperation unload = SceneManager.UnloadSceneAsync(gameScene);
                if (unload != null)
                {
                    yield return unload;
                }
            }

            gameScene = default;
            yield return null;

            try
            {
                if (Directory.Exists(rootDirectory))
                {
                    Directory.Delete(rootDirectory, true);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        /// <summary>
        /// The committed scene must carry the slice wiring the builder pins:
        /// config, pose selection, store wiring, HUD references and the
        /// streaming-before-bridge execution order.
        /// </summary>
        [UnityTest]
        public IEnumerator CommittedGameSceneWiresTheSlice()
        {
            yield return LoadGameScene();

            Assert.IsNotNull(runtime.Views, "the runtime must build its view manager on Awake");
            Assert.AreEqual(8, runtime.Config.ViewDistanceChunks);
            Assert.AreEqual(4, runtime.Config.MaxLoadsPerFrame);
            Assert.AreEqual(4, runtime.Config.MaxUnloadsPerFrame);
            Assert.AreEqual(4, runtime.Config.MaxMeshUploadsPerFrame);
            Assert.AreEqual(2048, runtime.Views.Capacity, "the view pool cap is pinned");
            Assert.AreSame(rig.transform, runtime.PlayerTransform, "streaming samples the head transform");

            Assert.IsNotNull(selector, "the scene needs a PoseProviderSelector");
            Assert.AreSame(latch, selector.LateLatch);
            Assert.AreSame(provider, selector.SyntheticFallback);
            Assert.IsFalse(selector.UsingBridge, "the test forces the synthetic selection");
            Assert.AreSame(provider, latch.Provider, "the forced synthetic provider must be selected");

            Assert.IsNotNull(boot.Store, "GameBoot must create the persistent store in Start");
            Assert.AreEqual(worldName, boot.Store.WorldName, "the test seam must redirect the world name");
            Assert.AreEqual(rootDirectory, Path.GetDirectoryName(boot.Store.WorldDirectory));
            Assert.AreSame(boot.Store, runtime.Store, "the runtime must load deltas from the boot store");
            Assert.AreSame(boot.Store, saves.Store, "the batch sink must write to the boot store");
            Assert.AreSame(saves, runtime.AppliedEdits, "unflushed edits must survive regeneration");

            Assert.IsTrue(bridge.IsInitialized, "the bridge must resolve the streamed world");
            Assert.AreSame(runtime, bridge.Streaming);
            Assert.AreSame(rig, bridge.Rig);
            Assert.AreSame(playerRoot, bridge.PlayerRoot);
            Assert.AreSame(saves, bridge.SaveBatches);
            Assert.AreSame(bridge, hud.Bridge, "the HUD must read the bridge");
            Assert.AreSame(playerRoot.transform, hud.AnchorSource, "the HUD must anchor on the player root");
            Assert.AreEqual(PlayerRoot.EyeHeightMeters, hud.AnchorEyeHeight, 1e-6f, "the HUD anchors at the rig eye height");
            Assert.AreEqual(
                WorldUi.DefaultAnchorDropMeters,
                hud.AnchorDropMeters,
                1e-6f,
                "the HUD drops the committed world-locked offset");

            Assert.IsNotNull(window, "the release scene must carry the WindowManager");
            Assert.AreSame(rig, window.GetComponent<StereoRig>(), "the window manager shares the rig object");
            Assert.AreEqual(0, window.TargetDisplayIndex, "the primary display is the target");
            Assert.IsFalse(window.ApplyInEditor, "the editor preview must not switch display modes");
            Assert.IsTrue(window.Config.BorderlessFullscreen, "the release scene requests borderless fullscreen");
            Assert.AreEqual(90, window.Config.TargetRefresh, "the release scene requests 90 Hz");

            Assert.Less(
                ExecutionOrderOf(typeof(StreamingRuntime)),
                ExecutionOrderOf(typeof(GameplayBridge)),
                "streaming must tick before the gameplay bridge");

            yield return null;
        }

        /// <summary>
        /// The committed scene's world-space hotbar anchor must be in front of
        /// and inside <b>both</b> eye cameras (TD-012): the strip is real scene
        /// geometry, not a left-eye-only IMGUI pass, and its world-locked drop
        /// (TD-011) keeps it below the eye axis while the bottom edge stays
        /// inside the per-eye frustum.
        /// </summary>
        [UnityTest]
        public IEnumerator HotbarAnchorStaysInsideBothEyeViewports()
        {
            yield return LoadGameScene();

            hud.Refresh();
            Assert.AreSame(playerRoot.transform, hud.AnchorSource, "the HUD must have a resolved body anchor");
            Assert.IsNotNull(hud.HotbarObject, "the hotbar must be a world-space object (TD-012)");
            Assert.IsNotNull(hud.ReticleObject, "the reticle must be a world-space object (TD-012)");
            Assert.IsNotNull(hud.HudMaterial, "the world-space HUD must carry a material");
            Assert.IsTrue(hud.HotbarVisible, "both eye cameras must see the hotbar anchor (TD-012)");
            Assert.IsTrue(hud.ReticleVisible, "both eye cameras must see the gaze reticle (TD-012)");
            Assert.AreEqual(Hotbar.SlotCount, hud.HotbarSlotCount, "every hotbar slot must be a quad");

            Camera left = rig.LeftCamera;
            Camera right = rig.RightCamera;
            Assert.IsNotNull(left, "the left eye camera is the HUD projection camera");
            Assert.IsNotNull(right, "the right eye camera must also see the HUD geometry");
            Vector3 centre = left.WorldToViewportPoint(hud.AnchorWorldPosition);
            Assert.Greater(centre.z, 0f, "the anchor must be in front of the left camera");
            Assert.That(centre.x, Is.InRange(0f, 1f), "the anchor must be inside the left horizontal viewport");
            Assert.That(centre.y, Is.InRange(0f, 1f), "the anchor must be inside the left vertical viewport");
            Assert.Less(centre.y, 0.5f, "the world-locked drop must place the strip below the eye axis");

            Vector3 rightCentre = right.WorldToViewportPoint(hud.AnchorWorldPosition);
            Assert.Greater(rightCentre.z, 0f, "the anchor must be in front of the right camera");
            Assert.That(rightCentre.x, Is.InRange(0f, 1f), "the anchor must be inside the right horizontal viewport");
            Assert.That(rightCentre.y, Is.InRange(0f, 1f), "the anchor must be inside the right vertical viewport");

            Vector3 bottomEdge = hud.AnchorWorldPosition
                - (Vector3.up * (0.5f * WorldUi.DefaultHotbarHeightMeters));
            Vector3 bottom = left.WorldToViewportPoint(bottomEdge);
            Assert.That(
                bottom.y,
                Is.GreaterThan(0f),
                "the strip's bottom edge must not be clipped by the per-eye frustum");
            yield return null;
        }

        /// <summary>
        /// The end-to-end smoke: boot, stream, land, break, place, flush and
        /// rebuild from the same store, plus the no-stuck-state, vignette and
        /// hotbar checks.
        /// </summary>
        [UnityTest]
        public IEnumerator GameSceneEndToEndSmoke()
        {
            yield return LoadGameScene();

            // Boot: stream until the pool holds N views and the spawn chunk is
            // actually meshed (a generated but unuploaded chunk has no vertices).
            for (int frame = 0; frame < 600 && !(runtime.ActiveViews >= ChunksToMesh && SpawnChunkMeshed()); frame++)
            {
                runtime.Tick();
                yield return null;
            }

            Assert.GreaterOrEqual(runtime.ActiveViews, ChunksToMesh, "the boot must mesh at least {0} chunks", ChunksToMesh);
            Assert.IsTrue(SpawnChunkMeshed(), "the spawn chunk must have a non-empty mesh");
            World world = runtime.Views.World;
            Assert.IsNotNull(world, "the view manager must expose the world");

            // Land: the authored spawn is above the surface; gravity must put
            // the player down and keep them there without NaN.
            input.Frame = Frame();
            for (int tick = 0; tick < 600 && !bridge.Player.OnGround; tick++)
            {
                bridge.Tick(Dt);
            }

            Assert.IsTrue(bridge.Player.OnGround, "the player never landed on the generated terrain");
            AssertFinite(bridge.Player.Position, "landing position");
            Assert.AreEqual(0.0, bridge.Player.YawRadians, 1e-9, "the spawn yaw must be zero");

            // Collision has no contact snap, so a landing may rest up to one
            // fall step above the surface; a few seconds settle it within the
            // first post-restore step (0.5 * 25 * dt^2), where it must hold.
            for (int tick = 0; tick < 240; tick++)
            {
                bridge.Tick(Dt);
            }

            Assert.IsTrue(bridge.Player.OnGround, "the player must stay grounded");
            AssertFinite(bridge.Player.Position, "grounded position");
            AssertFinite(bridge.Player.Velocity, "grounded velocity");

            int px = Mathf.FloorToInt((float)bridge.Player.Position.X);
            int pz = Mathf.FloorToInt((float)bridge.Player.Position.Z);
            var playerColumn = new Int3(px, 0, pz);
            Assert.IsTrue(world.IsLoaded(playerColumn), "the player's column must be loaded");
            int surface = SurfaceY(world, px, pz);
            double surfaceTop = surface + 1.0;
            double settledY = bridge.Player.Position.Y;
            Assert.GreaterOrEqual(settledY, surfaceTop, "a grounded player must not sink into the surface");
            Assert.LessOrEqual(settledY, surfaceTop + 0.01, "a settled player must rest on the surface");

            for (int tick = 0; tick < 120; tick++)
            {
                bridge.Tick(Dt);
                AssertFinite(bridge.Player.Position, "settled position");
                AssertFinite(bridge.Player.Velocity, "settled velocity");
            }

            Assert.IsTrue(bridge.Player.OnGround, "the player must stay grounded");
            Assert.AreEqual(settledY, bridge.Player.Position.Y, 1e-9, "a settled player must not drift vertically");
            Assert.AreEqual(0f, bridge.PlanarSpeed, 1e-6f, "a stationary player has no planar speed");
            Assert.AreEqual(0f, vignette.Opacity, 1e-6f, "the vignette must be off when stationary");

            // Break: aim the scripted gaze at the surface block one cell ahead
            // and hold past its hardness. Column surfaces differ by a block in
            // the generated terrain, so every target comes from SurfaceY.
            int breakSurface = SurfaceY(world, px, pz - 1);
            var breakCell = new Int3(px, breakSurface, pz - 1);
            Assert.IsTrue(world.IsLoaded(breakCell), "the break target's chunk must be loaded");
            Assert.AreNotEqual(BlockId.Air, world.Get(breakCell), "fixture: the block ahead must be solid");
            Assert.AreEqual(
                BlockId.Air,
                world.Get(new Int3(breakCell.X, breakCell.Y + 1, breakCell.Z)),
                "fixture: the block above the break target must be air");

            Vec3 eye = bridge.Player.Position + new Vec3(0.0, PlayerState.BodyHeight, 0.0);
            AimGazeAt(new Vec3(breakCell.X + 0.5, breakCell.Y + 0.5, breakCell.Z + 0.5), eye);

            float hardness = SliceBlockRegistry.Default.Get(world.Get(breakCell)).Hardness;
            Assert.Greater(hardness, 0f, "the fixture block must be breakable");
            int belowHardness = Math.Max(1, (int)(hardness / Dt) - 8);
            input.Frame = Frame(primary: ButtonState.Held);
            for (int tick = 0; tick < belowHardness; tick++)
            {
                bridge.Tick(Dt);
            }

            Assert.AreNotEqual(
                BlockId.Air,
                world.Get(breakCell),
                "a hold shorter than the hardness must not break the block");

            for (int tick = 0; tick < 60 && world.Get(breakCell) != BlockId.Air; tick++)
            {
                bridge.Tick(Dt);
            }

            Assert.AreEqual(BlockId.Air, world.Get(breakCell), "holding past the hardness must break the block");
            Assert.GreaterOrEqual(runtime.Views.PreciseEdits, 1L, "the applied edit must reach the precise remesh path (TD-017)");
            Assert.GreaterOrEqual(saves.TrackedEdits, 1L, "the break must be tracked for persistence");
            Assert.GreaterOrEqual(saves.PendingChunks, 1);
            Assert.AreEqual(1, bridge.EditsApplied);

            // Place: aim at the exposed top face of the next column ahead (same
            // chunk, distinct cell) with the hotbar's selected block. The
            // terrain is not flat, so the anchor's true surface decides the aim
            // point; either the top face or a side face is a valid hit.
            int anchorSurface = SurfaceY(world, px, pz - 2);
            var anchor = new Int3(px, anchorSurface, pz - 2);
            Assert.IsTrue(world.IsLoaded(anchor), "the place anchor's chunk must be loaded");
            Assert.AreNotEqual(BlockId.Air, world.Get(anchor), "fixture: the place anchor must be solid");

            AimGazeAt(new Vec3(anchor.X + 0.5, anchor.Y + 1.0, anchor.Z + 0.5), eye);

            int placedCount = 0;
            Int3 placedCell = default;
            BlockId placedBlock = default;
            Action<Int3, BlockId> handler = (cell, block) =>
            {
                placedCount++;
                placedCell = cell;
                placedBlock = block;
            };

            bridge.EditApplied += handler;
            try
            {
                input.Frame = Frame(secondary: ButtonState.Pressed);
                bridge.Tick(Dt);
            }
            finally
            {
                bridge.EditApplied -= handler;
            }

            BlockId expectedPlace = new Hotbar().Get(bridge.HotbarIndex);
            Assert.AreEqual(1, placedCount, "the placement must apply exactly one edit");
            Assert.AreEqual(expectedPlace, placedBlock, "the placed block must be the hotbar selection");
            Assert.AreNotEqual(breakCell, placedCell, "the placed cell must be distinct from the broken cell");
            Assert.AreEqual(expectedPlace, world.Get(placedCell), "the placed cell must hold the hotbar block");
            Assert.GreaterOrEqual(saves.TrackedEdits, 2L, "both edits must be tracked");
            Assert.AreEqual(
                ChunkMath.ToChunk(breakCell),
                ChunkMath.ToChunk(placedCell),
                "fixture: both edits must share a chunk for the single-delta reload");

            FileWorldStore store = boot.Store;
            yield return FlushSaveBatches();
            Assert.GreaterOrEqual(store.SuccessfulWrites, 1L, "the flush must write the delta");
            Assert.AreEqual(1, store.CountSavedChunks(), "both edits share one chunk and write one delta");

            // Hotbar: cycling must move the player state and the HUD together.
            input.Frame = Frame(hotbarDelta: 1);
            bridge.Tick(Dt);
            hud.Refresh();
            Assert.AreEqual(1, bridge.HotbarIndex, "the hotbar must cycle");
            Assert.AreEqual(1, hud.SelectedSlot, "the HUD must show the cycled slot");

            // Reload: unload the scene (disposing its store through GameBoot),
            // then rebuild a fresh store + manager for the same temp world and
            // assert both edits survived.
            ChunkCoord editedChunk = ChunkMath.ToChunk(placedCell);
            AsyncOperation unload = SceneManager.UnloadSceneAsync(gameScene);
            Assert.IsNotNull(unload, "the game scene must be unloadable");
            yield return unload;
            gameScene = default;
            yield return null;

            reloadStore = new FileWorldStore(worldName, rootDirectory);
            reloadRoot = new GameObject("GameSceneReload");
            reloadManager = reloadRoot.AddComponent<ChunkViewManager>();
            reloadManager.Store = reloadStore;
            reloadManager.Initialize(new StreamingConfig(), WorldSeed, 16);
            reloadManager.OnLoad(editedChunk);

            // The stored delta loads on the store's pump (TD-060 M-3);
            // generation does not block and the delta is applied when the
            // background read completes.
            for (int frame = 0; frame < 240 && reloadManager.DeltasLoaded == 0; frame++)
            {
                reloadManager.ProcessDeltaLoads();
                yield return null;
            }

            World reloaded = reloadManager.World;
            Assert.IsNotNull(reloaded, "the reload manager must expose its world");
            Assert.AreEqual(1, reloadManager.DeltasLoaded, "the stored delta must load");
            Assert.AreEqual(2, reloadManager.DeltaEditsApplied, "both edits must be applied over the baseline");
            Assert.AreEqual(0, reloadManager.LiveEditsReapplied, "no merged map exists in a fresh manager");
            Assert.AreEqual(BlockId.Air, reloaded.Get(breakCell), "the break must persist");
            Assert.AreEqual(expectedPlace, reloaded.Get(placedCell), "the placement must persist");
        }

        private IEnumerator LoadGameScene()
        {
            Assert.IsTrue(
                File.Exists(GameScenePath),
                "the committed game scene must exist: " + GameScenePath);

            gameScene = EditorSceneManager.LoadSceneInPlayMode(
                GameScenePath,
                new LoadSceneParameters(LoadSceneMode.Additive));
            yield return null;
            Assert.IsTrue(gameScene.IsValid() && gameScene.isLoaded, "the game scene must load");

            runtime = FindInScene<StreamingRuntime>(gameScene);
            bridge = FindInScene<GameplayBridge>(gameScene);
            saves = FindInScene<SaveBatches>(gameScene);
            hud = FindInScene<WorldUi>(gameScene);
            vignette = FindInScene<MotionVignette>(gameScene);
            latch = FindInScene<LateLatchPose>(gameScene);
            provider = FindInScene<SyntheticPoseProvider>(gameScene);
            selector = FindInScene<PoseProviderSelector>(gameScene);
            playerRoot = FindInScene<PlayerRoot>(gameScene);
            rig = FindInScene<StereoRig>(gameScene);
            window = FindInScene<WindowManager>(gameScene);
            boot = FindInScene<GameBoot>(gameScene);

            Assert.IsNotNull(runtime, "the scene must carry a StreamingRuntime");
            Assert.IsNotNull(bridge, "the scene must carry a GameplayBridge");
            Assert.IsNotNull(saves, "the scene must carry a SaveBatches");
            Assert.IsNotNull(hud, "the scene must carry a WorldUi");
            Assert.IsNotNull(vignette, "the scene must carry a MotionVignette");
            Assert.IsNotNull(latch, "the scene must carry a LateLatchPose");
            Assert.IsNotNull(provider, "the scene must carry a SyntheticPoseProvider");
            Assert.IsNotNull(selector, "the scene must carry a PoseProviderSelector");
            Assert.IsNotNull(playerRoot, "the scene must carry a PlayerRoot");
            Assert.IsNotNull(rig, "the scene must carry a StereoRig");
            Assert.IsNotNull(window, "the scene must carry a WindowManager");
            Assert.IsNotNull(boot, "the scene must carry a GameBoot");

            // Control every tick deterministically after the one scene-load
            // frame (which already ran GameBoot.Start and one auto tick).
            runtime.AutoUpdate = false;
            bridge.AutoUpdate = false;
            bridge.InputSource = input;
            saves.FlushIntervalSeconds = 600f;

            Assert.IsNotNull(boot.Store, "GameBoot must create the store in Start");
            Assert.IsTrue(bridge.IsInitialized, "the bridge must initialize from the scene's streaming runtime");

            Assert.IsFalse(
                selector.UsingBridge,
                "the forced synthetic selection must win over any live bridge region");
            Assert.AreSame(
                provider, latch.Provider,
                "the synthetic provider must be the selected pose source so scripting it moves the gaze");
        }

        private bool SpawnChunkMeshed()
        {
            if (runtime.Views == null || runtime.Views.World == null || runtime.PlayerTransform == null)
            {
                return false;
            }

            Vector3 p = runtime.PlayerTransform.position;
            var cell = new Int3(
                Mathf.FloorToInt(p.x),
                Mathf.FloorToInt(p.y),
                Mathf.FloorToInt(-p.z));
            if (!runtime.Views.TryGetView(ChunkMath.ToChunk(cell), out ChunkView view) || view.Mesh == null)
            {
                return false;
            }

            return view.Mesh.vertexCount > 0;
        }

        private void AimGazeAt(Vec3 targetInternal, Vec3 eyeInternal)
        {
            Vec3 direction = Vec3.Normalized(targetInternal - eyeInternal);
            float yaw = (float)Math.Atan2(-direction.X, -direction.Z);
            float pitch = (float)Math.Asin(direction.Y);
            provider.SetOrientation(yaw * Mathf.Rad2Deg, pitch * Mathf.Rad2Deg, 0f);
            latch.TickOnce();
        }

        private IEnumerator FlushSaveBatches()
        {
            Task task = saves.FlushAsync();
            while (!task.IsCompleted)
            {
                yield return null;
            }

            Assert.IsFalse(task.IsFaulted, "the flush faulted: {0}", task.Exception);
        }

        private static int SurfaceY(World world, int x, int z)
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

        private static void AssertFinite(Vec3 value, string label)
        {
            Assert.IsFalse(double.IsNaN(value.X) || double.IsInfinity(value.X), label + " X must be finite: {0}", value.X);
            Assert.IsFalse(double.IsNaN(value.Y) || double.IsInfinity(value.Y), label + " Y must be finite: {0}", value.Y);
            Assert.IsFalse(double.IsNaN(value.Z) || double.IsInfinity(value.Z), label + " Z must be finite: {0}", value.Z);
        }

        private static int ExecutionOrderOf(Type type)
        {
            var attribute = type.GetCustomAttribute<DefaultExecutionOrder>();
            return attribute != null ? attribute.order : 0;
        }

        private static T FindInScene<T>(Scene scene) where T : Component
        {
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                T found = root.GetComponentInChildren<T>(true);
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }

        private static InputFrame Frame(
            Vector2f move = default,
            float turn = 0f,
            ButtonState primary = ButtonState.Up,
            ButtonState secondary = ButtonState.Up,
            int hotbarDelta = 0)
        {
            return new InputFrame(move, turn, false, null, primary, secondary, hotbarDelta, TrackingQuality.Good);
        }

        private sealed class FakeInputProvider : IInputProvider
        {
            public InputFrame Frame;

            public InputFrame Sample(double timeSeconds)
            {
                return Frame;
            }
        }
    }
}

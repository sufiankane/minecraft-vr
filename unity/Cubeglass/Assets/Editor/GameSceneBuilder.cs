using System;
using System.Collections.Generic;
using Cubeglass.Unity.Input;
using Cubeglass.Unity.Rendering;
using Cubeglass.Voxel;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Cubeglass.Editor
{
    /// <summary>
    /// Deterministic builder for the S7 game scene (Task 4c): a flat starter
    /// ground around the spawn, the player root + stereo rig (bridge-first,
    /// synthetic-fallback pose selection), the fullscreen window manager, the
    /// streaming runtime and view manager, the gameplay bridge with gaze
    /// targeting and the desktop input provider, the world HUD (reticle +
    /// hotbar), the comfort components (vignette and snap turn), batched
    /// persistence and the debug overlay.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Run from the menu (<c>Cubeglass/Build Game Scene</c>) or headless:
    /// <c>unity run unity/Cubeglass -- -executeMethod
    /// Cubeglass.Editor.GameSceneBuilder.Build -quit</c>. The scene is
    /// committed; rebuilding on the same Unity version must produce byte-stable
    /// output, so every object is created in a fixed order with fixed names and
    /// values (no timestamps, no random ids) and the same
    /// <see cref="SceneCanonicalizer"/> pass the calibration builder uses.
    /// </para>
    /// <para>
    /// <b>Spawn.</b> The authored Unity spawn column is <c>(8, 8)</c> (the cell
    /// centre <c>(8.5, 8.5)</c>) and the feet start
    /// <see cref="SpawnHeightAboveSurface"/> metres above the generated
    /// surface top for <see cref="DefaultWorldSeed"/>. The surface height is
    /// sampled at the internal column the Unity cell centre maps to: ADR-0004
    /// mirrors Z, so <c>internalZ = -unityZ</c> and the internal cell
    /// containing <c>-8.5</c> is <c>-9</c> — the sample column is
    /// <c>(8, -9)</c> (<see cref="SampleColumnX"/>/<see cref="SampleColumnZ"/>),
    /// not the naive <c>(8, 8)</c>. For seed 1 the two columns happen to have
    /// the same height, but the builder must not rely on that. The starter
    /// ground plane is a cosmetic flat filler below the surface top, occluded
    /// as soon as the first streamed chunk meshes; voxel collision uses the
    /// generated world, never this plane (its collider is removed).
    /// </para>
    /// <para>
    /// <b>Persistence.</b> <see cref="FileWorldStore"/> cannot be serialized,
    /// so the scene carries a <see cref="GameBoot"/> that creates it in
    /// <c>Start</c> for world name <c>default</c> under
    /// <c>Application.persistentDataPath</c> and wires the streaming runtime
    /// and the <see cref="SaveBatches"/> sink. See <see cref="GameBoot"/> for
    /// the test seams that redirect this to a temp world.
    /// </para>
    /// <para>
    /// <b>Execution order.</b> Streaming first, bridge second, UI last: the
    /// runtime is pinned to -200, the bridge to -100 and the HUD refreshes in
    /// <c>LateUpdate</c>, so the frame's chunk loads are resident before the
    /// bridge steps collision and the HUD reads the tick's result. The
    /// fallback pose provider is static (no <c>SyntheticPoseDrive</c>): on the
    /// desktop fallback the bridge turns the body from
    /// <see cref="UnityInputProvider"/>, and driving the head from the same
    /// input would apply the turn twice.
    /// </para>
    /// </remarks>
    public static class GameSceneBuilder
    {
        /// <summary>The committed game scene path.</summary>
        public const string GameScenePath = "Assets/Scenes/Game.unity";

        /// <summary>The world seed the scene's streaming runtime boots with.</summary>
        public const long DefaultWorldSeed = 1L;

        /// <summary>The world X column the player spawns in (Unity space).</summary>
        public const int SpawnColumnX = 8;

        /// <summary>The world Z column the player spawns in (Unity space).</summary>
        public const int SpawnColumnZ = 8;

        /// <summary>
        /// The internal-frame X column the spawn height is sampled from. The
        /// Unity-space conversion does not mirror X, so it equals
        /// <see cref="SpawnColumnX"/>.
        /// </summary>
        public const int SampleColumnX = SpawnColumnX;

        /// <summary>
        /// The internal-frame Z column the spawn height is sampled from. The
        /// authored Unity spawn sits at the cell centre
        /// <c>SpawnColumnZ + 0.5</c>; ADR-0004 mirrors Z, so the internal
        /// coordinate is <c>-(SpawnColumnZ + 0.5)</c> and its cell is
        /// <c>-SpawnColumnZ - 1</c>.
        /// </summary>
        public const int SampleColumnZ = -SpawnColumnZ - 1;

        /// <summary>Metres the feet start above the generated surface top.</summary>
        public const int SpawnHeightAboveSurface = 2;

        /// <summary>
        /// Maps an authored Unity-space spawn column index to the internal-frame
        /// column whose cell contains the mirrored cell centre. ADR-0004
        /// mirrors Z (<c>internalZ = -unityZ</c>); the authored centre is
        /// <c>column + 0.5</c>, so flooring gives <c>-column - 1</c>. X is not
        /// mirrored.
        /// </summary>
        public static int InternalColumnForUnity(int unityColumn)
        {
            return -unityColumn - 1;
        }

        /// <summary>
        /// The internal-frame sample column for an authored Unity-space spawn
        /// column: the cell the mirrored Unity cell centre falls in. X is not
        /// mirrored; Z is <see cref="InternalColumnForUnity"/> of the column.
        /// </summary>
        public static void SampleColumnForUnitySpawn(
            int unityColumnX, int unityColumnZ, out int sampleX, out int sampleZ)
        {
            sampleX = unityColumnX;
            sampleZ = InternalColumnForUnity(unityColumnZ);
        }

        /// <summary>
        /// The generated surface top (one above the grass layer) for an
        /// authored Unity-space spawn column, sampled at the mirrored internal
        /// column.
        /// </summary>
        public static int SampleSurfaceTop(int unityColumnX, int unityColumnZ, long seed)
        {
            SampleColumnForUnitySpawn(unityColumnX, unityColumnZ, out int sampleX, out int sampleZ);
            return TerrainGenerator.HeightAt(sampleX, sampleZ, seed) + 1;
        }

        /// <summary>
        /// The authored Unity-space spawn position for a spawn column: the cell
        /// centre, <see cref="SpawnHeightAboveSurface"/> metres above the
        /// surface top sampled at the mirrored internal column.
        /// </summary>
        public static Vector3 ComputeSpawnPosition(
            int unityColumnX, int unityColumnZ, long seed, int heightAboveSurface)
        {
            SampleColumnForUnitySpawn(unityColumnX, unityColumnZ, out int sampleX, out int sampleZ);
            return new Vector3(
                unityColumnX + 0.5f,
                TerrainGenerator.HeightAt(sampleX, sampleZ, seed) + 1 + heightAboveSurface,
                -(sampleZ + 0.5f));
        }

        /// <summary>
        /// The committed scene's starter-ground surface top: the builder's
        /// standard spawn column and <see cref="DefaultWorldSeed"/>.
        /// </summary>
        public static int DefaultSurfaceTop()
        {
            return SampleSurfaceTop(SpawnColumnX, SpawnColumnZ, DefaultWorldSeed);
        }

        /// <summary>
        /// The committed scene's player spawn position: the builder's standard
        /// spawn column, <see cref="DefaultWorldSeed"/> and
        /// <see cref="SpawnHeightAboveSurface"/>.
        /// </summary>
        public static Vector3 DefaultSpawnPosition()
        {
            return ComputeSpawnPosition(
                SpawnColumnX, SpawnColumnZ, DefaultWorldSeed, SpawnHeightAboveSurface);
        }

        private const string ScenesFolder = "Assets/Scenes";
        private const float StarterGroundSizeMeters = 16f;
        private const float StarterGroundSinkMeters = 0.02f;

        private static readonly Color StarterGroundColor = new Color(0.24f, 0.45f, 0.19f, 1f);

        /// <summary>
        /// Menu entry: rebuilds <see cref="GameScenePath"/> in place.
        /// </summary>
        [MenuItem("Cubeglass/Build Game Scene")]
        public static void Build()
        {
            try
            {
                BuildSceneAt(GameScenePath);
                BuildSettingsBuilder.Apply();
                Debug.Log("GameSceneBuilder: wrote " + GameScenePath);
            }
            catch (Exception exception)
            {
                Debug.LogError("GameSceneBuilder: failed to build the game scene: " + exception);
                if (Application.isBatchMode)
                {
                    EditorApplication.Exit(1);
                }

                throw;
            }
        }

        /// <summary>
        /// Builds and saves the game scene at <paramref name="scenePath"/>.
        /// </summary>
        public static void BuildSceneAt(string scenePath)
        {
            EnsureFolder(ScenesFolder);
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var materials = new Dictionary<Color, Material>();

            int surfaceTop = DefaultSurfaceTop();
            Vector3 spawn = DefaultSpawnPosition();

            BuildStarterGround(materials, surfaceTop);

            PlayerRoot playerRoot;
            StereoRig rig;
            LateLatchPose latch;
            BuildPlayerSpawn(spawn, out playerRoot, out rig, out latch);
            BuildWindow(rig);

            UnityInputProvider input = BuildInput();
            StreamingRuntime runtime = BuildStreaming(rig);

            GameplayBridge bridge;
            SaveBatches saves;
            BuildGameplay(runtime, rig, latch, playerRoot, input, out bridge, out saves);
            BuildWorldUi(bridge, playerRoot);
            BuildOverlay(latch);
            BuildBoot(runtime, saves, bridge, playerRoot);

            if (!EditorSceneManager.SaveScene(scene, scenePath))
            {
                throw new InvalidOperationException("EditorSceneManager.SaveScene returned false for " + scenePath);
            }

            SceneCanonicalizer.Canonicalize(scenePath);
            AssetDatabase.SaveAssets();
        }

        private static void BuildStarterGround(Dictionary<Color, Material> materials, int surfaceTop)
        {
            CreatePrimitive(
                "StarterGround", null, PrimitiveType.Plane,
                new Vector3(
                    SpawnColumnX + 0.5f,
                    surfaceTop - StarterGroundSinkMeters,
                    SpawnColumnZ + 0.5f),
                Quaternion.identity,
                new Vector3(StarterGroundSizeMeters * 0.1f, 1f, StarterGroundSizeMeters * 0.1f),
                StarterGroundColor, materials);
        }

        private static void BuildPlayerSpawn(
            Vector3 spawn, out PlayerRoot playerRoot, out StereoRig rig, out LateLatchPose latch)
        {
            var rootObject = new GameObject("PlayerRoot");
            rootObject.transform.position = spawn;
            rootObject.transform.rotation = Quaternion.identity;
            playerRoot = rootObject.AddComponent<PlayerRoot>();

            var rigObject = new GameObject("StereoRig");
            rigObject.transform.SetParent(rootObject.transform, false);
            rigObject.transform.localPosition = new Vector3(0f, PlayerRoot.EyeHeightMeters, 0f);

            rig = rigObject.AddComponent<StereoRig>();
            StereoRigConfig config = rig.Config;
            config.IpdMeters = StereoRigConfig.DefaultIpdMeters;
            config.FovDegrees = StereoRigConfig.DefaultFovDegrees;
            config.Near = StereoRigConfig.DefaultNear;
            config.Far = StereoRigConfig.DefaultFar;
            config.BorderlessFullscreen = StereoRigConfig.DefaultBorderlessFullscreen;
            config.TargetRefresh = StereoRigConfig.DefaultTargetRefresh;
            // Pin the nominal side-by-side target so the baked camera FOVs are
            // deterministic instead of depending on the batch-mode screen.
            rig.ApplyEyeLayout(StereoRig.NominalScreenWidth, StereoRig.NominalScreenHeight);

            latch = rigObject.AddComponent<LateLatchPose>();
            latch.Rig = rig;

            var providerObject = new GameObject("PoseFallback");
            providerObject.transform.SetParent(rigObject.transform, false);
            var provider = providerObject.AddComponent<SyntheticPoseProvider>();
            provider.SetPose(0f, 0f, 0f, Vector3.zero);
            provider.SetRates(0f, 0f, 0f);

            var selector = rigObject.AddComponent<PoseProviderSelector>();
            selector.LateLatch = latch;
            selector.SyntheticFallback = provider;

            playerRoot.Head = rigObject.transform;
        }

        private static UnityInputProvider BuildInput()
        {
            var inputObject = new GameObject("PlayerInput");
            return inputObject.AddComponent<UnityInputProvider>();
        }

        /// <summary>
        /// Adds the <see cref="WindowManager"/> to the rig, sharing
        /// <see cref="StereoRig.Config"/> exactly like the calibration scene:
        /// the release build requests borderless fullscreen at the configured
        /// refresh rate on the target display (FR-01). The Editor preview is
        /// left alone (<see cref="WindowManager.ApplyInEditor"/> is false).
        /// </summary>
        private static void BuildWindow(StereoRig rig)
        {
            var window = rig.gameObject.AddComponent<WindowManager>();
            window.Config = rig.Config;
            window.TargetDisplayIndex = 0;
            window.ApplyInEditor = false;
        }

        private static StreamingRuntime BuildStreaming(StereoRig rig)
        {
            var streamingObject = new GameObject("Streaming");
            streamingObject.AddComponent<ChunkViewManager>();
            var runtime = streamingObject.AddComponent<StreamingRuntime>();
            runtime.PlayerTransform = rig.transform;
            return runtime;
        }

        private static void BuildGameplay(
            StreamingRuntime runtime,
            StereoRig rig,
            LateLatchPose latch,
            PlayerRoot playerRoot,
            UnityInputProvider input,
            out GameplayBridge bridge,
            out SaveBatches saves)
        {
            var gameplayObject = new GameObject("Gameplay");
            bridge = gameplayObject.AddComponent<GameplayBridge>();
            var snapTurn = gameplayObject.AddComponent<SnapTurn>();
            var vignette = gameplayObject.AddComponent<MotionVignette>();
            saves = gameplayObject.AddComponent<SaveBatches>();

            bridge.Streaming = runtime;
            bridge.InputProvider = input;
            bridge.LateLatch = latch;
            bridge.Rig = rig;
            bridge.PlayerRoot = playerRoot;
            bridge.SnapTurn = snapTurn;
            bridge.MotionVignette = vignette;
            bridge.SaveBatches = saves;
            bridge.AutoUpdate = true;
        }

        private static void BuildWorldUi(GameplayBridge bridge, PlayerRoot playerRoot)
        {
            var uiObject = new GameObject("WorldUi");
            var ui = uiObject.AddComponent<WorldUi>();
            ui.Bridge = bridge;
            ui.AnchorSource = playerRoot.transform;
            ui.AnchorEyeHeight = PlayerRoot.EyeHeightMeters;
            ui.AnchorDropMeters = WorldUi.DefaultAnchorDropMeters;
            ui.Visible = true;
        }

        private static void BuildOverlay(LateLatchPose latch)
        {
            var overlayObject = new GameObject("DebugOverlay");
            var overlay = overlayObject.AddComponent<DebugOverlay>();
            overlay.LateLatch = latch;
            overlay.Visible = true;
        }

        private static void BuildBoot(
            StreamingRuntime runtime, SaveBatches saves, GameplayBridge bridge, PlayerRoot playerRoot)
        {
            var bootObject = new GameObject("GameBoot");
            var boot = bootObject.AddComponent<GameBoot>();
            boot.WorldName = GameBoot.DefaultWorldName;
            boot.Streaming = runtime;
            boot.Saves = saves;
            boot.Bridge = bridge;
            boot.PlayerRoot = playerRoot;
        }

        private static GameObject CreatePrimitive(
            string name,
            Transform parent,
            PrimitiveType type,
            Vector3 position,
            Quaternion rotation,
            Vector3 scale,
            Color color,
            Dictionary<Color, Material> materials)
        {
            GameObject gameObject = GameObject.CreatePrimitive(type);
            gameObject.name = name;
            if (parent != null)
            {
                gameObject.transform.SetParent(parent, false);
            }

            gameObject.transform.localPosition = position;
            gameObject.transform.localRotation = rotation;
            gameObject.transform.localScale = scale;

            MeshRenderer meshRenderer = gameObject.GetComponent<MeshRenderer>();
            meshRenderer.sharedMaterial = MaterialFor(color, materials);

            Collider collider = gameObject.GetComponent<Collider>();
            if (collider != null)
            {
                Object.DestroyImmediate(collider);
            }

            return gameObject;
        }

        private static Material MaterialFor(Color color, Dictionary<Color, Material> materials)
        {
            if (materials.TryGetValue(color, out Material material))
            {
                return material;
            }

            Shader shader = Shader.Find("Unlit/Color");
            if (shader == null)
            {
                throw new InvalidOperationException("Built-in shader 'Unlit/Color' was not found.");
            }

            material = new Material(shader)
            {
                name = "Game " + ColorUtility.ToHtmlStringRGB(color),
                color = color,
            };
            materials.Add(color, material);
            return material;
        }

        private static void EnsureFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder))
            {
                return;
            }

            int separator = folder.LastIndexOf('/');
            if (separator <= 0)
            {
                throw new InvalidOperationException("Cannot create the asset folder '" + folder + "'.");
            }

            AssetDatabase.CreateFolder(folder.Substring(0, separator), folder.Substring(separator + 1));
        }
    }
}

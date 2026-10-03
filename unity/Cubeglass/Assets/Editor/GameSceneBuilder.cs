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
    /// synthetic-fallback pose selection), the streaming runtime and view
    /// manager, the gameplay bridge with gaze targeting and the desktop input
    /// provider, the world HUD (reticle + hotbar), the comfort components
    /// (vignette and snap turn), batched persistence and the debug overlay.
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
    /// <b>Spawn.</b> The spawn column is <c>(8, 8)</c> and the feet start
    /// <see cref="SpawnHeightAboveSurface"/> metres above the generated
    /// surface top for <see cref="DefaultWorldSeed"/>, so the player lands on
    /// the terrain the streaming runtime generates with its serialized seed 1.
    /// The starter ground plane is a cosmetic flat filler below the surface
    /// top, occluded as soon as the first streamed chunk meshes; voxel
    /// collision uses the generated world, never this plane (its collider is
    /// removed).
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

        /// <summary>The world X column the player spawns in.</summary>
        public const int SpawnColumnX = 8;

        /// <summary>The world Z column the player spawns in.</summary>
        public const int SpawnColumnZ = 8;

        /// <summary>Metres the feet start above the generated surface top.</summary>
        public const int SpawnHeightAboveSurface = 2;

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

            int surfaceTop = TerrainGenerator.HeightAt(SpawnColumnX, SpawnColumnZ, DefaultWorldSeed) + 1;
            var spawn = new Vector3(
                SpawnColumnX + 0.5f,
                surfaceTop + SpawnHeightAboveSurface,
                SpawnColumnZ + 0.5f);

            BuildStarterGround(materials, surfaceTop);

            PlayerRoot playerRoot;
            StereoRig rig;
            LateLatchPose latch;
            BuildPlayerSpawn(spawn, out playerRoot, out rig, out latch);

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

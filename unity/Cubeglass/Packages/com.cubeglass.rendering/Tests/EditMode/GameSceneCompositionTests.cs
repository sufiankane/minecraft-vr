using System.IO;
using Cubeglass.Unity.Input;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Cubeglass.Unity.Rendering.Tests
{
    /// <summary>
    /// Scene-composition pins for the committed release scene (S7 review fix):
    /// <c>Game.unity</c> must carry the <see cref="WindowManager"/> with the
    /// same borderless-fullscreen 90 Hz config as calibration, and the
    /// <see cref="WorldUi"/> must be wired to the eye-height body anchor.
    /// </summary>
    public class GameSceneCompositionTests
    {
        private const string GameScenePath = "Assets/Scenes/Game.unity";

        private Scene scene;

        [TearDown]
        public void CloseScene()
        {
            if (scene.IsValid() && scene.isLoaded)
            {
                EditorSceneManager.CloseScene(scene, true);
            }

            scene = default;
        }

        [Test]
        public void CommittedGameSceneCarriesTheWindowManagerWithTheRigConfig()
        {
            Assert.IsTrue(File.Exists(GameScenePath), "the committed game scene must exist: " + GameScenePath);
            scene = EditorSceneManager.OpenScene(GameScenePath, OpenSceneMode.Additive);

            StereoRig rig = FindInScene<StereoRig>(scene);
            WindowManager window = FindInScene<WindowManager>(scene);
            Assert.IsNotNull(rig, "the game scene must carry the StereoRig");
            Assert.IsNotNull(window, "the game scene must carry the WindowManager");
            Assert.AreSame(
                rig.gameObject,
                window.gameObject,
                "the window manager shares the rig object like the calibration scene");
            Assert.AreEqual(0, window.TargetDisplayIndex, "the primary display is the target");
            Assert.IsFalse(window.ApplyInEditor, "the editor preview must not switch display modes");
            Assert.IsNotNull(window.Config, "the window manager must reference a config");
            Assert.IsTrue(window.Config.BorderlessFullscreen, "the release scene requests borderless fullscreen");
            Assert.AreEqual(90, window.Config.TargetRefresh, "the release scene requests 90 Hz");
            Assert.AreEqual(
                StereoRigConfig.DefaultFovDegrees,
                window.Config.FovDegrees,
                1e-6f,
                "the window config is the rig's config");
            Assert.AreEqual(StereoRigConfig.DefaultIpdMeters, window.Config.IpdMeters, 1e-6f);
        }

        [Test]
        public void CommittedGameScenePinsTheHudEyeAnchor()
        {
            Assert.IsTrue(File.Exists(GameScenePath), "the committed game scene must exist: " + GameScenePath);
            scene = EditorSceneManager.OpenScene(GameScenePath, OpenSceneMode.Additive);

            PlayerRoot playerRoot = FindInScene<PlayerRoot>(scene);
            WorldUi hud = FindInScene<WorldUi>(scene);
            Assert.IsNotNull(playerRoot, "the game scene must carry the PlayerRoot");
            Assert.IsNotNull(hud, "the game scene must carry the WorldUi");
            Assert.AreSame(playerRoot.transform, hud.AnchorSource, "the HUD must anchor on the body transform");
            Assert.AreEqual(PlayerRoot.EyeHeightMeters, hud.AnchorEyeHeight, 1e-6f, "eye-height anchor");
            Assert.AreEqual(WorldUi.DefaultAnchorDropMeters, hud.AnchorDropMeters, 1e-6f, "world-locked drop");
            Assert.AreEqual(
                WorldUi.DefaultAnchorDistanceMeters,
                1.5f,
                1e-6f,
                "the documented forward distance is the committed default");
        }

        private static T FindInScene<T>(Scene target)
            where T : Component
        {
            foreach (GameObject root in target.GetRootGameObjects())
            {
                T found = root.GetComponentInChildren<T>(true);
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }
    }
}

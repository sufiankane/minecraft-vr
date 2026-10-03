using UnityEditor;
using UnityEngine;

namespace Cubeglass.Editor
{
    /// <summary>
    /// Deterministic <see cref="EditorBuildSettings"/> state (S7 Task 4d): the
    /// committed scene list is always <c>[Game, Calibration]</c>, in that
    /// order, so a player build boots into the game scene and the calibration
    /// scene stays available for the frame-budget lane.
    /// </summary>
    /// <remarks>
    /// Run from the menu (<c>Cubeglass/Update Build Settings</c>) or headless:
    /// <c>unity run unity/Cubeglass -- -executeMethod
    /// Cubeglass.Editor.BuildSettingsBuilder.Apply -quit</c>. Both scene
    /// builders invoke <see cref="Apply"/> after saving their scene, so a
    /// rebuild cannot leave the build settings pointing at stale or missing
    /// scene paths.
    /// </remarks>
    public static class BuildSettingsBuilder
    {
        /// <summary>The committed build scene list, in boot order.</summary>
        public static readonly string[] BuildScenes =
        {
            GameSceneBuilder.GameScenePath,
            CalibrationSceneBuilder.CalibrationScenePath,
        };

        /// <summary>
        /// Rewrites <see cref="EditorBuildSettings.scenes"/> to
        /// <see cref="BuildScenes"/>, every entry enabled.
        /// </summary>
        [MenuItem("Cubeglass/Update Build Settings")]
        public static void Apply()
        {
            var scenes = new EditorBuildSettingsScene[BuildScenes.Length];
            for (int i = 0; i < BuildScenes.Length; i++)
            {
                scenes[i] = new EditorBuildSettingsScene(BuildScenes[i], true);
            }

            EditorBuildSettings.scenes = scenes;
            Debug.Log("BuildSettingsBuilder: build scenes set to " + string.Join(", ", BuildScenes));
        }
    }
}

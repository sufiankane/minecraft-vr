using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Cubeglass.Editor
{
    /// <summary>
    /// Batch-mode player build entry points (S7 Task 4d). The release workflow
    /// runs <see cref="BuildWindows64"/> through the Unity CLI after the
    /// licence secrets activate the editor.
    /// </summary>
    /// <remarks>
    /// The scene list is the committed
    /// <see cref="BuildSettingsBuilder.BuildScenes"/> order, written explicitly
    /// into <see cref="BuildPlayerOptions.scenes"/> so the build does not depend
    /// on a dirty editor state, and the output path is derived from the project
    /// root (the directory that contains <c>Assets</c>) so it is stable
    /// regardless of the process working directory. A failed build logs the
    /// report result and exits non-zero for the CLI.
    /// </remarks>
    public static class BuildPlayer
    {
        /// <summary>Player output relative to the Unity project root.</summary>
        public const string WindowsOutputRelativePath = "build/StandaloneWindows64/Cubeglass/Cubeglass.exe";

        /// <summary>
        /// Builds the Windows x64 standalone player for the current tree.
        /// </summary>
        public static void BuildWindows64()
        {
            try
            {
                BuildSettingsBuilder.Apply();
                string projectRoot = Directory.GetParent(Application.dataPath).FullName;
                string outputPath = Path.Combine(
                    projectRoot,
                    WindowsOutputRelativePath.Replace('/', Path.DirectorySeparatorChar));
                var options = new BuildPlayerOptions
                {
                    scenes = BuildSettingsBuilder.BuildScenes,
                    locationPathName = outputPath,
                    target = BuildTarget.StandaloneWindows64,
                    options = BuildOptions.None,
                };

                BuildReport report = BuildPipeline.BuildPlayer(options);
                BuildSummary summary = report.summary;
                if (summary.result != BuildResult.Succeeded)
                {
                    Debug.LogError(
                        "BuildPlayer: the Windows x64 player failed: " + summary.result
                            + " (" + summary.totalErrors + " errors, " + summary.totalWarnings + " warnings)");
                    EditorApplication.Exit(1);
                    return;
                }

                Debug.Log(
                    "BuildPlayer: wrote " + outputPath + " (" + summary.totalSize + " bytes, "
                        + summary.totalTime + ")");
            }
            catch (Exception exception)
            {
                Debug.LogError("BuildPlayer: failed to build the Windows x64 player: " + exception);
                if (Application.isBatchMode)
                {
                    EditorApplication.Exit(1);
                }

                throw;
            }
        }
    }
}

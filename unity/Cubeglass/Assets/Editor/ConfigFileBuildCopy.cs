using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Cubeglass.Editor
{
    /// <summary>
    /// Copies the committed <c>Assets/config.json</c> into the built player's
    /// <c>StreamingAssets/Cubeglass/config.json</c> after every build (TD-014),
    /// so the shipped tuning file is the same one <c>GameConfigFile</c> reads at
    /// runtime. The copy is best-effort: a failure logs an error and leaves the
    /// player using its serialized defaults rather than failing the build (the
    /// loader treats a missing file as a no-op).
    /// </summary>
    public sealed class ConfigFileBuildCopy : IPostprocessBuildWithReport
    {
        /// <summary>Runs after the scene/build post-processors.</summary>
        public int callbackOrder
        {
            get { return 1000; }
        }

        /// <inheritdoc />
        public void OnPostprocessBuild(BuildReport report)
        {
            try
            {
                string projectRoot = Directory.GetParent(Application.dataPath).FullName;
                string source = Path.Combine(projectRoot, "Assets", "config.json");
                if (!File.Exists(source))
                {
                    Debug.LogWarning("[ConfigFileBuildCopy] Assets/config.json is missing; the player keeps its serialized defaults.");
                    return;
                }

                string outputDirectory = Path.GetDirectoryName(report.summary.outputPath);
                string dataDirectory = Path.Combine(outputDirectory, Application.productName + "_Data");
                if (!Directory.Exists(dataDirectory))
                {
                    Debug.LogWarning(
                        "[ConfigFileBuildCopy] no player data directory at '" + dataDirectory
                            + "'; config.json was not copied.");
                    return;
                }

                string destinationDirectory = Path.Combine(
                    dataDirectory, "StreamingAssets", Cubeglass.Unity.Rendering.GameConfigFile.StreamingAssetsSubfolder);
                Directory.CreateDirectory(destinationDirectory);
                string destination = Path.Combine(
                    destinationDirectory, Cubeglass.Unity.Rendering.GameConfigFile.FileName);
                File.Copy(source, destination, true);
                Debug.Log("[ConfigFileBuildCopy] copied config.json to " + destination);
            }
            catch (Exception exception)
            {
                Debug.LogError("[ConfigFileBuildCopy] could not copy config.json: " + exception);
            }
        }
    }
}
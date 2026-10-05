using System.IO;
using System.Text.RegularExpressions;
using Cubeglass.Streaming;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Cubeglass.Unity.Rendering.Tests
{
    /// <summary>
    /// TD-014: the shipped <c>config.json</c> parses, validates and applies
    /// with the documented precedence (built-in defaults &lt; serialized
    /// inspector values &lt; config keys).
    /// </summary>
    public sealed class GameConfigFileTests
    {
        [Test]
        public void ShippedConfigParsesAndCarriesTheDefaults()
        {
            string path = Path.Combine(Application.dataPath, GameConfigFile.FileName);
            Assert.IsTrue(File.Exists(path), "Assets/config.json must ship");

            Assert.IsTrue(GameConfigFile.TryLoadDefault(out GameConfigValues values, out string source, out string error), error);
            Assert.IsNotNull(values);
            Assert.AreEqual(8, values.ViewDistanceChunks, "viewDistanceChunks default");
            Assert.AreEqual(2, values.UnloadHysteresis, "unloadHysteresis default");
            Assert.AreEqual(4, values.MaxLoadsPerFrame, "maxLoadsPerFrame default");
            Assert.AreEqual(4, values.MaxUnloadsPerFrame, "maxUnloadsPerFrame default");
            Assert.AreEqual(4, values.MaxMeshUploadsPerFrame, "maxMeshUploadsPerFrame default");
            Assert.AreEqual(2.0f, values.VerticalRadiusChunks, "verticalRadiusChunks default");
            Assert.AreEqual(0.064f, values.IpdMeters, "ipdMeters default");
            Assert.AreEqual(45.0f, values.FovDegrees, "fovDegrees default");
            Assert.AreEqual(0.05f, values.Near, "near default");
            Assert.AreEqual(500.0f, values.Far, "far default");
            Assert.AreEqual(90, values.TargetRefresh, "targetRefresh default");
        }

        [Test]
        public void PresentKeysWinAndAbsentKeysKeepTheInspectorValue()
        {
            Assert.IsTrue(
                GameConfigFile.TryParse(
                    "{\"ipdMeters\":0.07,\"targetRefresh\":72}", out GameConfigValues values, out string error),
                error);

            var config = new StereoRigConfig
            {
                IpdMeters = 0.05f,
                TargetRefresh = 120,
                FovDegrees = 60f,
            };
            GameConfigFile.ApplyTo(values, config);

            Assert.AreEqual(0.07f, config.IpdMeters, "the file key wins over the inspector");
            Assert.AreEqual(72, config.TargetRefresh, "the file key wins over the inspector");
            Assert.AreEqual(60f, config.FovDegrees, "an absent key keeps the serialized value");
        }

        [Test]
        public void OutOfRangeKeysClampWithAWarning()
        {
            LogAssert.Expect(LogType.Warning, new Regex("viewDistanceChunks .* clamped"));
            LogAssert.Expect(LogType.Warning, new Regex("ipdMeters .* clamped"));

            Assert.IsTrue(
                GameConfigFile.TryParse(
                    "{\"viewDistanceChunks\":999,\"ipdMeters\":-4}", out GameConfigValues values, out string error),
                error);

            Assert.AreEqual(StreamingConfig.MaxViewDistanceChunks, values.ViewDistanceChunks, "clamped high");
            Assert.AreEqual(StereoRigConfig.MinIpdMeters, values.IpdMeters, "clamped low");
        }

        [Test]
        public void MalformedJsonIsRejectedWithoutThrowing()
        {
            LogAssert.Expect(LogType.Warning, new Regex("malformed config JSON"));

            Assert.IsFalse(GameConfigFile.TryParse("not json at all", out GameConfigValues values, out string error));
            Assert.IsNull(values);
            Assert.IsNotNull(error, "the malformed document reports why");

            Assert.IsFalse(GameConfigFile.TryParse(string.Empty, out values, out error));
            Assert.IsNotNull(error, "an empty document reports why");
        }

        [Test]
        public void OversizedConfigIsRejected()
        {
            string path = Path.Combine(
                Path.GetTempPath(), "cg-config-" + System.Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllBytes(path, new byte[GameConfigFile.MaxFileBytes + 1]);
            try
            {
                LogAssert.Expect(LogType.Warning, new Regex("too large"));
                Assert.IsFalse(GameConfigFile.TryLoad(path, out GameConfigValues values, out string error));
                Assert.IsNull(values);
                StringAssert.Contains("too large", error);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Test]
        public void EditorResolutionPrefersAssetsConfigWhenNoStreamingAssetsCopyExists()
        {
            string streaming = Path.Combine(
                Application.streamingAssetsPath, GameConfigFile.StreamingAssetsSubfolder, GameConfigFile.FileName);
            Assert.IsFalse(File.Exists(streaming), "fixture: the editor tree has no committed StreamingAssets copy");
            Assert.AreEqual(
                Path.Combine(Application.dataPath, GameConfigFile.FileName),
                GameConfigFile.ResolvePath(),
                "the editor resolves Assets/config.json");
        }
    }
}
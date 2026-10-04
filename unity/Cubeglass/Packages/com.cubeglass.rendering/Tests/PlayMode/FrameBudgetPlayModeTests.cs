using System;
using System.Collections;
using System.Globalization;
using System.IO;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Cubeglass.Unity.Rendering.PlayTests
{
    /// <summary>
    /// PlayMode frame-budget measurement for the committed calibration scene
    /// (S6 Task 4): 600 measured frames after warm-up, rendered into a
    /// 3840x1080 side-by-side target (1920x1080 per eye) with the
    /// <see cref="DebugOverlay"/> on, and the mean/p95/p99 frame time written to
    /// <c>%TEMP%\s6-frame-budget.json</c> plus the test output.
    /// </summary>
    /// <remarks>
    /// The CLI PlayMode lane has no Game View, so each iteration renders both
    /// eyes explicitly into the shared target with <c>Camera.Render()</c> (the
    /// same pre-cull path the late latch hooks in a normal frame). IMGUI
    /// <c>OnGUI</c> never executes in the lane, so the overlay's paint cost is
    /// not exercised here — the number is the stereo render submission plus the
    /// scene tick, not the Game View present or IMGUI. The measurement forces
    /// the synthetic pose provider (<see cref="PoseProviderSelector.ForceSyntheticForTests"/>)
    /// exactly like the game-scene smoke and budget, so a live bridge region on
    /// the machine cannot change the measured path. The measured number is the
    /// wall time of one full stereo frame (both eye renders) on the machine that
    /// ran the suite; it is evidence, not a hard CI gate, so the test does not
    /// fail when the budget is missed.
    /// </remarks>
    public class FrameBudgetPlayModeTests
    {
        /// <summary>The committed calibration scene the budget is measured in.</summary>
        public const string CalibrationScenePath = "Assets/Scenes/Calibration.unity";

        /// <summary>Per-eye render width at the 1080p target.</summary>
        public const int EyeWidth = 1920;

        /// <summary>Per-eye render height at the 1080p target.</summary>
        public const int EyeHeight = 1080;

        /// <summary>Frames rendered before the measured window (JIT/warm-up).</summary>
        public const int WarmupFrames = 32;

        /// <summary>Measured frames in the budget window.</summary>
        public const int MeasuredFrames = 600;

        /// <summary>ADR-0010 target frame time at 90 Hz.</summary>
        public const double TargetFrameMilliseconds = 11.1;

        private Scene calibrationScene;
        private StereoRig rig;
        private RenderTexture target;
        private RenderTexture previousLeft;
        private RenderTexture previousRight;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            PoseProviderSelector.ForceSyntheticForTests = true;
            yield return null;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            PoseProviderSelector.ForceSyntheticForTests = false;

            if (rig != null)
            {
                if (rig.LeftCamera != null)
                {
                    rig.LeftCamera.targetTexture = previousLeft;
                }

                if (rig.RightCamera != null)
                {
                    rig.RightCamera.targetTexture = previousRight;
                }
            }

            if (target != null)
            {
                target.Release();
                UnityEngine.Object.Destroy(target);
                target = null;
            }

            if (calibrationScene.IsValid() && calibrationScene.isLoaded)
            {
                AsyncOperation unload = SceneManager.UnloadSceneAsync(calibrationScene);
                if (unload != null)
                {
                    yield return unload;
                }
            }

            calibrationScene = default;
            rig = null;
        }

        [UnityTest]
        public IEnumerator CalibrationSceneHoldsTheFrameBudgetWithTheOverlayOn()
        {
            Assert.IsTrue(
                File.Exists(CalibrationScenePath),
                "the committed calibration scene must exist: " + CalibrationScenePath);

            calibrationScene = EditorSceneManager.LoadSceneInPlayMode(
                CalibrationScenePath,
                new LoadSceneParameters(LoadSceneMode.Additive));
            yield return null;

            Assert.IsTrue(calibrationScene.IsValid() && calibrationScene.isLoaded, "calibration scene loaded");

            rig = FindInScene<StereoRig>(calibrationScene);
            DebugOverlay overlay = FindInScene<DebugOverlay>(calibrationScene);
            PoseProviderSelector selector = FindInScene<PoseProviderSelector>(calibrationScene);
            Assert.IsNotNull(rig, "StereoRig in the calibration scene");
            Assert.IsNotNull(overlay, "DebugOverlay in the calibration scene");
            Assert.IsNotNull(selector, "PoseProviderSelector in the calibration scene");
            Assert.IsFalse(
                selector.UsingBridge,
                "the synthetic provider must be forced for a hermetic budget lane");
            Assert.IsFalse(
                selector.PluginUnavailable,
                "forcing the synthetic provider is not a plugin failure and must not be reported as one");
            Assert.IsNotNull(rig.LeftCamera, "left eye camera");
            Assert.IsNotNull(rig.RightCamera, "right eye camera");

            overlay.Visible = true;
            Assert.IsTrue(overlay.Visible, "the overlay is on for the measurement");

            target = new RenderTexture(EyeWidth * 2, EyeHeight, 24, RenderTextureFormat.Default);
            target.name = "CalibrationFrameBudgetTarget";
            target.Create();
            previousLeft = rig.LeftCamera.targetTexture;
            previousRight = rig.RightCamera.targetTexture;
            rig.LeftCamera.targetTexture = target;
            rig.RightCamera.targetTexture = target;

            for (int i = 0; i < WarmupFrames; i++)
            {
                rig.LeftCamera.Render();
                rig.RightCamera.Render();
                yield return null;
            }

            var samples = new double[MeasuredFrames];
            int firstFrame = Time.frameCount;
            for (int i = 0; i < MeasuredFrames; i++)
            {
                long start = System.Diagnostics.Stopwatch.GetTimestamp();
                rig.LeftCamera.Render();
                rig.RightCamera.Render();
                long end = System.Diagnostics.Stopwatch.GetTimestamp();
                samples[i] = (end - start) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                yield return null;
            }

            int frames = Time.frameCount - firstFrame;

            var sorted = (double[])samples.Clone();
            Array.Sort(sorted);
            double sum = 0.0;
            foreach (double sample in samples)
            {
                sum += sample;
            }

            double mean = sum / samples.Length;
            double median = Percentile(sorted, 0.50);
            double p95 = Percentile(sorted, 0.95);
            double p99 = Percentile(sorted, 0.99);

            BudgetReport report = CreateReport(
                overlay.Visible, mean, median, p95, p99, sorted[0], sorted[sorted.Length - 1]);
            string json = JsonUtility.ToJson(report, true);
            string reportPath = Path.Combine(Path.GetTempPath(), "s6-frame-budget.json");
            File.WriteAllText(reportPath, json);

            string summary = string.Format(
                CultureInfo.InvariantCulture,
                "[S6-FRAME-BUDGET] overlay=on {0}x{1} per eye x{2} frames: mean={3:F3} ms p50={4:F3} ms p95={5:F3} ms p99={6:F3} ms (target {7:F1} ms at 90 Hz) -> {8}",
                EyeWidth, EyeHeight, MeasuredFrames, mean, median, p95, p99, TargetFrameMilliseconds, reportPath);
            Debug.Log(summary);
            TestContext.WriteLine(summary);

            Assert.AreEqual(MeasuredFrames, frames, "600 measured frames elapsed");
            Assert.Greater(mean, 0.0, "the measured frame time is positive");
        }

        [Serializable]
        private sealed class BudgetReport
        {
            public string scene;
            public int renderTargetWidth;
            public int renderTargetHeight;
            public int eyeWidth;
            public int eyeHeight;
            public bool overlayVisible;
            public int warmupFrames;
            public int measuredFrames;
            public float targetFrameMilliseconds;
            public float meanMilliseconds;
            public float medianMilliseconds;
            public float p95Milliseconds;
            public float p99Milliseconds;
            public float minMilliseconds;
            public float maxMilliseconds;
            public string processorType;
            public int processorCount;
            public int processorFrequencyMHz;
            public int systemMemorySizeMB;
            public string graphicsDeviceName;
            public string graphicsDeviceVersion;
            public int graphicsMemorySizeMB;
            public string operatingSystem;
            public string unityVersion;
        }

        private static BudgetReport CreateReport(
            bool overlayVisible, double mean, double median, double p95, double p99, double min, double max)
        {
            return new BudgetReport
            {
                scene = CalibrationScenePath,
                renderTargetWidth = EyeWidth * 2,
                renderTargetHeight = EyeHeight,
                eyeWidth = EyeWidth,
                eyeHeight = EyeHeight,
                overlayVisible = overlayVisible,
                warmupFrames = WarmupFrames,
                measuredFrames = MeasuredFrames,
                targetFrameMilliseconds = (float)TargetFrameMilliseconds,
                meanMilliseconds = (float)mean,
                medianMilliseconds = (float)median,
                p95Milliseconds = (float)p95,
                p99Milliseconds = (float)p99,
                minMilliseconds = (float)min,
                maxMilliseconds = (float)max,
                processorType = SystemInfo.processorType,
                processorCount = SystemInfo.processorCount,
                processorFrequencyMHz = SystemInfo.processorFrequency,
                systemMemorySizeMB = SystemInfo.systemMemorySize,
                graphicsDeviceName = SystemInfo.graphicsDeviceName,
                graphicsDeviceVersion = SystemInfo.graphicsDeviceVersion,
                graphicsMemorySizeMB = SystemInfo.graphicsMemorySize,
                operatingSystem = SystemInfo.operatingSystem,
                unityVersion = Application.unityVersion,
            };
        }

        private static double Percentile(double[] sorted, double percentile)
        {
            if (sorted.Length == 0)
            {
                return 0.0;
            }

            int rank = (int)Math.Ceiling(percentile * sorted.Length) - 1;
            return sorted[Math.Min(sorted.Length - 1, Math.Max(0, rank))];
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
    }
}

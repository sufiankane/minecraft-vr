using System;
using System.Collections;
using System.Globalization;
using System.IO;
using Cubeglass.Unity.Input;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Cubeglass.Unity.Rendering.PlayTests
{
    /// <summary>
    /// PlayMode frame-budget measurement for the committed S7 game scene at the
    /// slice view distance of eight chunks (S7 Task 4d): streams the scene's
    /// desired set to drained, then records 600 frames with the overlay off and
    /// 600 frames with the overlay on, and writes mean/p50/p95/p99 to
    /// <c>%TEMP%\s7-game-frame-budget.json</c> plus the test output.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each measured frame drives one deterministic slice tick
    /// (<see cref="StreamingRuntime.Tick"/> then
    /// <see cref="GameplayBridge.Tick"/>) and renders both eye cameras into the
    /// shared 3840x1080 side-by-side target. The timed interval therefore
    /// contains the streaming scheduler + chunk generation/upload work, the
    /// bridge tick (input sample, controller, interaction) and the CPU-side
    /// stereo render submission; GPU completion and Game View presentation are
    /// outside it because the CLI PlayMode lane has no Game View and no present
    /// (the same headless-lane caveat as the S6 calibration measurement). IMGUI
    /// <c>OnGUI</c> never executes in the lane, so "overlay on" means the
    /// overlay is enabled and its read path is live, not that its paint cost is
    /// in the number.
    /// </para>
    /// <para>
    /// Review I-11: each window's recorded frame p99 is asserted against the
    /// documented headless ceiling, so a real regression fails the release
    /// lane. The number is a CPU-side proxy (streaming + bridge tick + render
    /// submission, no present and no GPU completion); the real ADR-0010
    /// 11.1 ms frame budget is HIL/player-verified. Review M-7: the lane
    /// forces <see cref="PoseProviderSelector.ForceSyntheticForTests"/>, so a
    /// live bridge region on the machine cannot take the pose path or add
    /// probe/log work to the measured frames.
    /// </para>
    /// </remarks>
    public class GameFrameBudgetPlayModeTests
    {
        /// <summary>The committed game scene the budget is measured in.</summary>
        public const string GameScenePath = "Assets/Scenes/Game.unity";

        /// <summary>Per-eye render width at the 1080p target.</summary>
        public const int EyeWidth = 1920;

        /// <summary>Per-eye render height at the 1080p target.</summary>
        public const int EyeHeight = 1080;

        /// <summary>Frames rendered before each measured window (JIT/warm-up).</summary>
        public const int WarmupFrames = 32;

        /// <summary>Measured frames per overlay state.</summary>
        public const int MeasuredFrames = 600;

        /// <summary>Consecutive settled frames required before the measured window.</summary>
        public const int DrainStableFrames = 30;

        /// <summary>Upper bound on drain frames before the test fails loudly.</summary>
        public const int DrainFrameCap = 6000;

        /// <summary>ADR-0010 target frame time at 90 Hz.</summary>
        public const double TargetFrameMilliseconds = 11.1;

        /// <summary>
        /// Review I-11 headless p99 ceiling in milliseconds. This is a CPU-side
        /// proxy for frame cost (streaming + bridge tick + render submission,
        /// no present, no GPU wait), not the 11.1 ms GPU budget; the GPU budget
        /// is HIL/player-verified. The review suggested 5 ms, but the release
        /// machine measured 7.8 ms frame p99 (mean 2.8 ms, overlay-off window)
        /// with background load, so the documented headless ceiling is 15 ms:
        /// about twice the observed p99, generous enough not to fail on
        /// scheduler noise, while a real regression still trips it.
        /// </summary>
        public const double HeadlessP99CeilingMilliseconds = 15.0;

        /// <summary>Fixed slice tick; matches the end-to-end game scene smoke.</summary>
        public const double Dt = 1.0 / 60.0;

        private Scene gameScene;
        private StereoRig rig;
        private StreamingRuntime runtime;
        private GameplayBridge bridge;
        private DebugOverlay overlay;
        private RenderTexture target;
        private RenderTexture previousLeft;
        private RenderTexture previousRight;
        private string worldName;
        private string rootDirectory;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            Assert.IsNull(GameBoot.WorldNameOverride, "a previous test leaked the world-name override");
            Assert.IsNull(GameBoot.RootDirectoryOverride, "a previous test leaked the root override");

            // Review M-7: a live bridge region must not change the measured
            // pose path, the probe timing or the fallback log volume.
            PoseProviderSelector.ForceSyntheticForTests = true;
            worldName = "game-budget-" + Guid.NewGuid().ToString("N");
            rootDirectory = Path.Combine(Path.GetTempPath(), "cg-game-budget-" + Guid.NewGuid().ToString("N"));
            GameBoot.WorldNameOverride = worldName;
            GameBoot.RootDirectoryOverride = rootDirectory;
            yield return null;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            GameBoot.WorldNameOverride = null;
            GameBoot.RootDirectoryOverride = null;
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
                Object.Destroy(target);
                target = null;
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
            rig = null;
            runtime = null;
            bridge = null;
            overlay = null;
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

        [UnityTest]
        public IEnumerator GameSceneHoldsTheFrameBudgetAtViewDistanceEight()
        {
            yield return LoadGameScene();

            Assert.AreEqual(8, runtime.Config.ViewDistanceChunks, "the slice measures view distance 8");
            Assert.AreEqual(4, runtime.Config.MaxLoadsPerFrame);
            Assert.AreEqual(4, runtime.Config.MaxUnloadsPerFrame);
            Assert.AreEqual(4, runtime.Config.MaxMeshUploadsPerFrame);

            target = new RenderTexture(EyeWidth * 2, EyeHeight, 24, RenderTextureFormat.Default);
            target.name = "GameFrameBudgetTarget";
            target.Create();
            previousLeft = rig.LeftCamera.targetTexture;
            previousRight = rig.RightCamera.targetTexture;
            rig.LeftCamera.targetTexture = target;
            rig.RightCamera.targetTexture = target;

            overlay.Visible = false;
            yield return DrainInitialLoad();

            int drainedViews = runtime.ActiveViews;
            Assert.GreaterOrEqual(drainedViews, runtime.DesiredChunks, "every desired chunk must hold a view");
            TestContext.WriteLine(string.Format(
                CultureInfo.InvariantCulture,
                "[S7-GAME-FRAME-BUDGET] drained: desired={0} active={1} loaded={2} generated={3} uploaded={4}",
                runtime.DesiredChunks, drainedViews, runtime.LoadedChunks,
                runtime.Views.GeneratedChunks, runtime.Views.UploadedChunks));
            var off = new WindowSamples();
            var on = new WindowSamples();
            yield return MeasureWindow(overlayVisible: false, off);
            overlay.Visible = true;
            yield return MeasureWindow(overlayVisible: true, on);

            BudgetReport report = CreateReport(drainedViews, off, on);
            string json = JsonUtility.ToJson(report, true);
            string reportPath = Path.Combine(Path.GetTempPath(), "s7-game-frame-budget.json");
            File.WriteAllText(reportPath, json);

            LogWindow("off", off);
            LogWindow("on", on);
            TestContext.WriteLine("[S7-GAME-FRAME-BUDGET] report written to " + reportPath);

            Assert.Greater(off.Frame.Length, 0);
            WindowStats offFrame = Summarize(off.Frame);
            WindowStats onFrame = Summarize(on.Frame);
            Assert.LessOrEqual(
                offFrame.P99,
                HeadlessP99CeilingMilliseconds,
                "overlay-off frame p99 {0:F3} ms exceeds the documented headless ceiling {1:F1} ms (review I-11); "
                + "this is a CPU-side proxy, not the 11.1 ms GPU budget",
                offFrame.P99,
                HeadlessP99CeilingMilliseconds);
            Assert.LessOrEqual(
                onFrame.P99,
                HeadlessP99CeilingMilliseconds,
                "overlay-on frame p99 {0:F3} ms exceeds the documented headless ceiling {1:F1} ms (review I-11); "
                + "this is a CPU-side proxy, not the 11.1 ms GPU budget",
                onFrame.P99,
                HeadlessP99CeilingMilliseconds);
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
            overlay = FindInScene<DebugOverlay>(gameScene);
            rig = FindInScene<StereoRig>(gameScene);
            PoseProviderSelector selector = FindInScene<PoseProviderSelector>(gameScene);

            Assert.IsNotNull(runtime, "the scene must carry a StreamingRuntime");
            Assert.IsNotNull(bridge, "the scene must carry a GameplayBridge");
            Assert.IsNotNull(overlay, "the scene must carry a DebugOverlay");
            Assert.IsNotNull(rig, "the scene must carry a StereoRig");
            Assert.IsNotNull(rig.LeftCamera, "left eye camera");
            Assert.IsNotNull(rig.RightCamera, "right eye camera");
            Assert.IsNotNull(selector, "the scene must carry a PoseProviderSelector");
            Assert.IsFalse(
                selector.UsingBridge,
                "the budget lane must run on the forced synthetic provider (review M-7)");
            Assert.IsFalse(
                selector.PluginUnavailable,
                "forcing the synthetic provider is not a plugin failure and must not be reported as one");

            runtime.AutoUpdate = false;
            bridge.AutoUpdate = false;
            Assert.IsNotNull(bridge.Streaming, "the bridge must resolve the scene streaming runtime");
        }

        private IEnumerator DrainInitialLoad()
        {
            int stableFrames = 0;
            int drainFrames = 0;
            while (stableFrames < DrainStableFrames && drainFrames < DrainFrameCap)
            {
                StepFrame();
                drainFrames++;
                stableFrames = InitialLoadSettled() ? stableFrames + 1 : 0;
                yield return null;
            }

            Assert.IsTrue(
                stableFrames >= DrainStableFrames,
                "the initial stream did not settle in {0} frames: desired={1} loaded={2} active={3} deferred={4} pending={5}",
                DrainFrameCap, runtime.DesiredChunks, runtime.LoadedChunks, runtime.ActiveViews,
                runtime.DeferredLoads, runtime.PendingMeshCount);
        }

        private bool InitialLoadSettled()
        {
            return runtime.DeferredLoads == 0
                && runtime.PendingMeshCount == 0
                && runtime.ActiveViews >= runtime.DesiredChunks;
        }

        private IEnumerator MeasureWindow(bool overlayVisible, WindowSamples samples)
        {
            Assert.AreEqual(overlayVisible, overlay.Visible, "the overlay state must be pinned for the window");

            for (int i = 0; i < WarmupFrames; i++)
            {
                StepFrame();
                rig.LeftCamera.Render();
                rig.RightCamera.Render();
                yield return null;
            }

            int firstFrame = Time.frameCount;
            for (int i = 0; i < MeasuredFrames; i++)
            {
                long start = System.Diagnostics.Stopwatch.GetTimestamp();
                StepFrame();
                long renderStart = System.Diagnostics.Stopwatch.GetTimestamp();
                rig.LeftCamera.Render();
                rig.RightCamera.Render();
                long end = System.Diagnostics.Stopwatch.GetTimestamp();
                samples.Frame[i] = Milliseconds(start, end);
                samples.Render[i] = Milliseconds(renderStart, end);
                yield return null;
            }

            int frames = Time.frameCount - firstFrame;
            Assert.AreEqual(MeasuredFrames, frames, "{0} measured frames elapsed", MeasuredFrames);
        }

        private void StepFrame()
        {
            runtime.Tick();
            bridge.Tick(Dt);
        }

        private static double Milliseconds(long start, long end)
        {
            return (end - start) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        }

        private static void LogWindow(string overlayState, WindowSamples samples)
        {
            WindowStats frame = Summarize(samples.Frame);
            WindowStats render = Summarize(samples.Render);
            string summary = string.Format(
                CultureInfo.InvariantCulture,
                "[S7-GAME-FRAME-BUDGET] overlay={0} x{1} frames: frame(ms) mean={2:F3} p50={3:F3} p95={4:F3} p99={5:F3} min={6:F3} max={7:F3} | render(ms) mean={8:F3} p50={9:F3} p95={10:F3} p99={11:F3} (target {12:F1} ms at 90 Hz)",
                overlayState, samples.Frame.Length,
                frame.Mean, frame.P50, frame.P95, frame.P99, frame.Min, frame.Max,
                render.Mean, render.P50, render.P95, render.P99,
                TargetFrameMilliseconds);
            Debug.Log(summary);
            TestContext.WriteLine(summary);
        }

        private static BudgetReport CreateReport(int drainedViews, WindowSamples off, WindowSamples on)
        {
            return new BudgetReport
            {
                scene = GameScenePath,
                viewDistanceChunks = 8,
                renderTargetWidth = EyeWidth * 2,
                renderTargetHeight = EyeHeight,
                eyeWidth = EyeWidth,
                eyeHeight = EyeHeight,
                warmupFrames = WarmupFrames,
                measuredFrames = MeasuredFrames,
                drainedViews = drainedViews,
                targetFrameMilliseconds = (float)TargetFrameMilliseconds,
                headlessP99CeilingMilliseconds = (float)HeadlessP99CeilingMilliseconds,
                overlayOff = WindowReport.From(false, Summarize(off.Frame), Summarize(off.Render)),
                overlayOn = WindowReport.From(true, Summarize(on.Frame), Summarize(on.Render)),
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

        private static WindowStats Summarize(double[] samples)
        {
            var sorted = (double[])samples.Clone();
            Array.Sort(sorted);
            double sum = 0.0;
            foreach (double sample in samples)
            {
                sum += sample;
            }

            return new WindowStats(
                sum / samples.Length,
                Percentile(sorted, 0.50),
                Percentile(sorted, 0.95),
                Percentile(sorted, 0.99),
                sorted[0],
                sorted[sorted.Length - 1]);
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

        private sealed class WindowSamples
        {
            public readonly double[] Frame = new double[MeasuredFrames];
            public readonly double[] Render = new double[MeasuredFrames];
        }

        private readonly struct WindowStats
        {
            public readonly double Mean;
            public readonly double P50;
            public readonly double P95;
            public readonly double P99;
            public readonly double Min;
            public readonly double Max;

            public WindowStats(double mean, double p50, double p95, double p99, double min, double max)
            {
                Mean = mean;
                P50 = p50;
                P95 = p95;
                P99 = p99;
                Min = min;
                Max = max;
            }
        }

        [Serializable]
        private sealed class WindowReport
        {
            public bool overlayVisible;
            public float meanFrameMilliseconds;
            public float medianFrameMilliseconds;
            public float p95FrameMilliseconds;
            public float p99FrameMilliseconds;
            public float minFrameMilliseconds;
            public float maxFrameMilliseconds;
            public float meanRenderMilliseconds;
            public float medianRenderMilliseconds;
            public float p95RenderMilliseconds;
            public float p99RenderMilliseconds;

            public static WindowReport From(bool visible, WindowStats frame, WindowStats render)
            {
                return new WindowReport
                {
                    overlayVisible = visible,
                    meanFrameMilliseconds = (float)frame.Mean,
                    medianFrameMilliseconds = (float)frame.P50,
                    p95FrameMilliseconds = (float)frame.P95,
                    p99FrameMilliseconds = (float)frame.P99,
                    minFrameMilliseconds = (float)frame.Min,
                    maxFrameMilliseconds = (float)frame.Max,
                    meanRenderMilliseconds = (float)render.Mean,
                    medianRenderMilliseconds = (float)render.P50,
                    p95RenderMilliseconds = (float)render.P95,
                    p99RenderMilliseconds = (float)render.P99,
                };
            }
        }

        [Serializable]
        private sealed class BudgetReport
        {
            public string scene;
            public int viewDistanceChunks;
            public int renderTargetWidth;
            public int renderTargetHeight;
            public int eyeWidth;
            public int eyeHeight;
            public int warmupFrames;
            public int measuredFrames;
            public int drainedViews;
            public float targetFrameMilliseconds;
            public float headlessP99CeilingMilliseconds;
            public WindowReport overlayOff;
            public WindowReport overlayOn;
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
    }
}

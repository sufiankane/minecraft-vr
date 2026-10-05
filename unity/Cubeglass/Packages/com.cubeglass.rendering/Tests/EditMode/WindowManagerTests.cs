using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace Cubeglass.Unity.Rendering.Tests
{
    /// <summary>
    /// Window-manager decision logic (the pure parts): when the borderless
    /// switch runs (config + Editor guard) and how the target display index is
    /// clamped. The actual <see cref="Screen"/> mutation is guarded out of the
    /// Editor preview and is not exercised here.
    /// </summary>
    public class WindowManagerTests
    {
        [Test]
        public void BorderlessDisabledNeverApplies()
        {
            Assert.IsFalse(WindowManager.ShouldApplyFullscreen(false, false, true), "player build");
            Assert.IsFalse(WindowManager.ShouldApplyFullscreen(false, true, true), "editor with override");
        }

        [Test]
        public void PlayerBuildAppliesWhenBorderlessIsEnabled()
        {
            Assert.IsTrue(WindowManager.ShouldApplyFullscreen(true, false, false), "player build");
            Assert.IsTrue(WindowManager.ShouldApplyFullscreen(true, false, true), "player build (override irrelevant)");
        }

        [Test]
        public void EditorPreviewStaysWindowedUnlessExplicitlyAllowed()
        {
            Assert.IsFalse(WindowManager.ShouldApplyFullscreen(true, true, false), "editor preview guard");
            Assert.IsTrue(WindowManager.ShouldApplyFullscreen(true, true, true), "editor explicit override");
        }

        [Test]
        public void DisplayIndexClampsToAvailableDisplays()
        {
            Assert.AreEqual(0, WindowManager.ClampDisplayIndex(0, 1), "single display");
            Assert.AreEqual(0, WindowManager.ClampDisplayIndex(5, 1), "out of range with one display");
            Assert.AreEqual(1, WindowManager.ClampDisplayIndex(1, 2), "second display");
            Assert.AreEqual(1, WindowManager.ClampDisplayIndex(9, 2), "clamped to last");
            Assert.AreEqual(0, WindowManager.ClampDisplayIndex(-3, 2), "clamped to first");
            Assert.AreEqual(0, WindowManager.ClampDisplayIndex(2, 0), "no displays known");
        }

        [Test]
        public void ApplyWindowModeIsANoOpInEditorPreview()
        {
            var root = new GameObject("WindowManager");
            try
            {
                var manager = root.AddComponent<WindowManager>();

                Assert.IsFalse(manager.ApplyWindowMode(), "editor preview must not touch the window");
                Assert.IsTrue(manager.Config.BorderlessFullscreen, "default config still borderless");
                Assert.AreEqual(0, manager.TargetDisplayIndex, "default display");
                Assert.IsFalse(manager.ApplyInEditor, "editor override off by default");
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        /// <summary>
        /// R-1: a deserialized scene can carry a raw <c>targetRefresh</c> that
        /// never passed through the setter. <see cref="WindowManager.ApplyWindowMode"/>
        /// must validate the effective config before use so a legacy/default
        /// zero cannot request 1 Hz (and an absurd value cannot pass through).
        /// </summary>
        [Test]
        public void DeserializedStyleRefreshIsRepairedBeforeUse()
        {
            var root = new GameObject("HostileWindowManager");
            try
            {
                var manager = root.AddComponent<WindowManager>();
                SetConfigField(manager.Config, "targetRefresh", 0);

                Assert.IsFalse(manager.ApplyWindowMode(), "editor preview still no-ops");
                Assert.AreEqual(
                    StereoRigConfig.DefaultTargetRefresh,
                    manager.Config.TargetRefresh,
                    "a legacy 0 Hz request resolves to the documented default before use");

                SetConfigField(manager.Config, "targetRefresh", 100000);
                manager.ApplyWindowMode();
                Assert.AreEqual(
                    StereoRigConfig.MaxTargetRefresh,
                    manager.Config.TargetRefresh,
                    "an absurd request clamps to the maximum");
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void RefreshReadBackToleratesQuantisationAndDescribesRealMismatches()
        {
            Assert.IsTrue(
                WindowManager.ModeMatchesRequest(
                    FullScreenMode.FullScreenWindow, 89.7, FullScreenMode.FullScreenWindow, 90),
                "a quantised rate inside the tolerance counts as honoured (M-10)");
            Assert.IsFalse(
                WindowManager.ModeMatchesRequest(
                    FullScreenMode.FullScreenWindow, 60.0, FullScreenMode.FullScreenWindow, 90),
                "a wrong rate is a mismatch");
            Assert.IsFalse(
                WindowManager.ModeMatchesRequest(
                    FullScreenMode.Windowed, 90.0, FullScreenMode.FullScreenWindow, 90),
                "a wrong mode is a mismatch");

            Assert.IsNull(
                WindowManager.DescribeMismatch(
                    FullScreenMode.FullScreenWindow, 90.0, FullScreenMode.FullScreenWindow, 90),
                "no mismatch text when honoured");
            StringAssert.Contains(
                "Windowed",
                WindowManager.DescribeMismatch(
                    FullScreenMode.Windowed, 90.0, FullScreenMode.FullScreenWindow, 90),
                "the mode mismatch names the actual mode");
            StringAssert.Contains(
                "60",
                WindowManager.DescribeMismatch(
                    FullScreenMode.FullScreenWindow, 60.0, FullScreenMode.FullScreenWindow, 90),
                "the refresh mismatch names the actual rate");
        }

        [Test]
        public void CenteredWindowPositionNeverGoesNegative()
        {
            Assert.AreEqual(
                new Vector2Int(960, 540),
                WindowManager.CenteredWindowPosition(3840, 1080, 1920, 0),
                "half of the horizontal surplus, zero vertical surplus");
            Assert.AreEqual(
                new Vector2Int(0, 0),
                WindowManager.CenteredWindowPosition(1280, 720, 3840, 1080),
                "a window larger than the display clamps to the origin");
        }

        private static void SetConfigField(StereoRigConfig config, string field, object value)
        {
            FieldInfo info = typeof(StereoRigConfig).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(info, "StereoRigConfig." + field + " must exist");
            info.SetValue(config, value);
        }
    }
}

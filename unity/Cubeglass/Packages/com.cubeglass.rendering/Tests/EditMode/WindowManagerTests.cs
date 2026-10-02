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
    }
}

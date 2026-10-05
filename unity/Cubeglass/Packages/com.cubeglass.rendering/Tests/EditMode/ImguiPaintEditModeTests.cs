using System;
using System.Collections.Generic;
using System.Reflection;
using Cubeglass.Unity.Input;
using NUnit.Framework;
using UnityEngine;

namespace Cubeglass.Unity.Rendering.Tests
{
    /// <summary>
    /// TD-020: the IMGUI paint paths (debug overlay and comfort vignette) are
    /// invoked directly with a fake <see cref="Event"/> from headless EditMode
    /// tests, so a paint-path exception or layout regression fails a test
    /// instead of silently vanishing in batch runs.
    /// </summary>
    /// <remarks>
    /// <b>Headless caveat.</b> A batch-mode test has no GUI context and
    /// <c>GUI.Box</c>/<c>GUI.DrawTexture</c> throw "You can only call GUI
    /// functions from inside OnGUI" even with <see cref="Event.current"/> set.
    /// The paint classes therefore expose virtual paint methods
    /// (<c>PaintBox</c>/<c>PaintLabel</c>, <c>PaintTexture</c>) and the tests
    /// substitute recording subclasses: the real <c>OnGUI</c> logic runs, the
    /// computed rectangles/opacities are asserted, and only the final
    /// native GUI call is replaced. Actual pixel presentation is covered by the
    /// player and the editor's own OnGUI callback, not by this test. The fake
    /// <see cref="Event"/> is likewise not fully honoured headless (the event
    /// pass guards see no real event), so assertions accept whole paint passes
    /// rather than exactly one per event type.
    /// </remarks>
    public sealed class ImguiPaintEditModeTests
    {
        private GameObject root;

        [SetUp]
        public void SetUp()
        {
            root = new GameObject("ImguiPaintRoot");
        }

        [TearDown]
        public void TearDown()
        {
            if (root != null)
            {
                UnityEngine.Object.DestroyImmediate(root);
                root = null;
            }
        }

        [Test]
        public void WorldUiNoLongerHasAnImGuiPaintPath()
        {
            MethodInfo onGui = typeof(WorldUi).GetMethod(
                "OnGUI", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            Assert.IsNull(onGui, "TD-012 removed the left-eye-only IMGUI HUD; the HUD is world-space now");
        }

        [Test]
        public void OverlayOnGuiRunsHeadlessWithAFakeEvent()
        {
            var overlayObject = new GameObject("Overlay");
            overlayObject.transform.SetParent(root.transform, false);
            var overlay = overlayObject.AddComponent<RecordingOverlay>();
            overlay.Visible = true;

            InvokeOnGui(overlay, EventType.Layout);
            InvokeOnGui(overlay, EventType.Repaint);

            Assert.AreEqual(DebugOverlay.PanelRect, overlay.Boxes[0], "the panel paints at the announced rect");
            Assert.GreaterOrEqual(overlay.Labels.Count, DebugOverlay.RowCount, "at least one label per row");
            Assert.AreEqual(
                0,
                overlay.Labels.Count % DebugOverlay.RowCount,
                "whole paint passes only (the headless fake event may not suppress passes)");
            Assert.AreEqual(18f * (DebugOverlay.RowCount + 1), DebugOverlay.PanelRect.height, "layout value");
        }

        [Test]
        public void OverlayOnGuiWithValuesRunsHeadless()
        {
            var overlayObject = new GameObject("OverlayValues");
            overlayObject.transform.SetParent(root.transform, false);
            var overlay = overlayObject.AddComponent<RecordingOverlay>();
            overlay.CommandAck = 0x2A;
            overlay.Visible = true;

            InvokeOnGui(overlay, EventType.Layout);
            InvokeOnGui(overlay, EventType.Repaint);

            FieldInfo ackField = typeof(DebugOverlay).GetField(
                "commandAck", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(ackField, "the ack row must exist");
            var row = (OverlayText)ackField.GetValue(overlay);
            Assert.IsTrue(row.Version > 0, "the repaint refreshed the row values");
            Assert.AreEqual("ack 0x0000002A", row.Content.text, "the fake repaint path formatted the ack");

            bool paintedAck = false;
            foreach (GUIContent content in overlay.Labels)
            {
                paintedAck |= content != null && content.text == "ack 0x0000002A";
            }

            Assert.IsTrue(paintedAck, "the ack row label was painted through the fake paint seam");
        }

        [Test]
        public void VignetteOnGuiRunsHeadlessAndDrawsPerEyeRects()
        {
            var rigObject = new GameObject("VignetteRig");
            rigObject.transform.SetParent(root.transform, false);
            var rig = rigObject.AddComponent<StereoRig>();
            rig.ApplyEyeLayout(3840, 1080);

            var vignetteObject = new GameObject("Vignette");
            vignetteObject.transform.SetParent(root.transform, false);
            var vignette = vignetteObject.AddComponent<RecordingVignette>();
            vignette.Rig = rig;
            vignette.Speed = 2f;

            Assert.IsNotNull(rig.LeftCamera, "fixture: left eye exists");
            Assert.AreEqual(new Rect(0f, 0f, 0.5f, 1f), rig.LeftCamera.rect, "fixture: left eye viewport");
            Assert.AreSame(rig, vignette.Rig, "fixture: rig wired");

            InvokeOnGui(vignette, EventType.Layout);
            InvokeOnGui(vignette, EventType.Repaint);

            Assert.AreEqual(0.4f, vignette.Opacity, 1e-6f, "full-speed opacity (TD-026 paint path)");

            // TD-026: the paint uses the real (headless) screen size and splits it
            // into two non-overlapping per-eye rects, left then right, each at
            // full opacity; the same mapping at the nominal 3840x1080 target
            // gives the two 1920-wide halves.
            Assert.GreaterOrEqual(vignette.Rects.Count, 2, "at least one paint per eye");
            Assert.AreEqual(0, vignette.Rects.Count % 2, "every pass paints both eyes");
            Rect[] screenRects = MotionVignette.GuiEyeRects(
                rig.LeftCamera, rig.RightCamera, Screen.width, Screen.height);
            for (int i = 0; i < vignette.Rects.Count; i++)
            {
                Assert.AreEqual(
                    screenRects[i % 2],
                    vignette.Rects[i],
                    "paint " + i + " of " + vignette.Rects.Count
                        + " uses the eye rect (screen " + Screen.width + "x" + Screen.height + ")");
                Assert.AreEqual(0.4f, vignette.Opacities[i], 1e-6f, "paint " + i + " opacity");
            }

            Rect[] nominal = MotionVignette.GuiEyeRects(rig.LeftCamera, rig.RightCamera, 3840, 1080);
            Assert.AreEqual(new Rect(0f, 0f, 1920f, 1080f), nominal[0], "nominal left eye rect");
            Assert.AreEqual(new Rect(1920f, 0f, 1920f, 1080f), nominal[1], "nominal right eye rect");
            Assert.AreEqual(0f, nominal[0].xMax - nominal[1].xMin, "the halves tile without a gap");
            Assert.IsFalse(nominal[0].Overlaps(nominal[1]), "the halves never overlap");
        }

        [Test]
        public void VignetteRectConversionFlipsToImGuiCoordinates()
        {
            Assert.AreEqual(
                new Rect(0f, 0f, 1920f, 1080f),
                MotionVignette.GuiRectFromViewport(new Rect(0f, 0f, 0.5f, 1f), 3840, 1080),
                "bottom-left viewport maps to a full-height IMGUI rect");
            Assert.AreEqual(
                new Rect(100f, 150f, 200f, 100f),
                MotionVignette.GuiRectFromViewport(new Rect(0.1f, 0.5f, 0.2f, 0.2f), 1000, 500),
                "IMGUI Y is flipped and the height subtracted (500 - (250 + 100))");
        }

        private static void InvokeOnGui(MonoBehaviour component, EventType type)
        {
            // GetMethod on a derived type does not return private methods of a
            // base class, so walk the hierarchy for the (private) OnGUI.
            MethodInfo onGui = null;
            for (Type candidate = component.GetType(); candidate != null && onGui == null; candidate = candidate.BaseType)
            {
                onGui = candidate.GetMethod(
                    "OnGUI", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            }

            Assert.IsNotNull(onGui, component.GetType().Name + ".OnGUI must exist");

            Event previous = Event.current;
            try
            {
                Event.current = new Event { type = type };
                Assert.DoesNotThrow(
                    () => onGui.Invoke(component, null),
                    component.GetType().Name + ".OnGUI threw for " + type);
            }
            finally
            {
                Event.current = previous;
            }
        }

        /// <summary>Records the overlay paint calls instead of touching the GUI backend.</summary>
        private sealed class RecordingOverlay : DebugOverlay
        {
            public readonly List<Rect> Boxes = new List<Rect>();
            public readonly List<GUIContent> Labels = new List<GUIContent>();

            protected override void PaintBox(Rect rect)
            {
                Boxes.Add(rect);
            }

            protected override void PaintLabel(Rect rect, GUIContent content)
            {
                Labels.Add(content);
            }
        }

        /// <summary>Records the vignette paint calls instead of touching the GUI backend.</summary>
        private sealed class RecordingVignette : MotionVignette
        {
            public readonly List<Rect> Rects = new List<Rect>();
            public readonly List<float> Opacities = new List<float>();

            protected override void PaintTexture(Rect rect, float opacity)
            {
                Rects.Add(rect);
                Opacities.Add(opacity);
            }
        }
    }
}
using System;
using NUnit.Framework;
using UnityEngine;

namespace Cubeglass.Unity.Rendering.Tests
{
    /// <summary>
    /// Overlay formatting helpers and the reusable <see cref="OverlayText"/>
    /// buffers: unchanged buckets do not reformat or rebuild the content, and
    /// unchanged updates allocate nothing.
    /// </summary>
    public class DebugOverlayTests
    {
        [Test]
        public void FormatsFrameTimeInMilliseconds()
        {
            Assert.AreEqual("frame 12.3 ms", OverlayFormat.FrameTimeTenths(123), "tenths");
            Assert.AreEqual("frame 0.0 ms", OverlayFormat.FrameTimeTenths(0), "zero");
            Assert.AreEqual("frame -0.4 ms", OverlayFormat.FrameTimeTenths(-4), "negative");
        }

        [Test]
        public void FormatsPoseRateAndAge()
        {
            Assert.AreEqual("pose 90 Hz", OverlayFormat.PoseRateHz(90), "rate");
            Assert.AreEqual("pose - Hz", OverlayFormat.PoseRateHz(-1), "no rate");
            Assert.AreEqual("age 4 ms", OverlayFormat.PoseAgeMs(4), "age");
            Assert.AreEqual("age - ms", OverlayFormat.PoseAgeMs(-1), "no age");
        }

        [Test]
        public void FormatsTrackingStateAndCommandAck()
        {
            Assert.AreEqual("track Stable", OverlayFormat.TrackingState((int)PoseTrackingState.Stable), "stable");
            Assert.AreEqual("track Lost", OverlayFormat.TrackingState((int)PoseTrackingState.Lost), "lost");
            Assert.AreEqual("track -", OverlayFormat.TrackingState(-1), "none");
            Assert.AreEqual("ack 0x0000000A", OverlayFormat.CommandAck(10), "ack");
            Assert.AreEqual("ack -", OverlayFormat.CommandAck(-1), "no ack");
        }

        [Test]
        public void FormatsPoseSourceBucketsIncludingEveryFallbackReason()
        {
            Assert.AreEqual("pose -", OverlayFormat.PoseSource(0), "no selector");
            Assert.AreEqual("pose bridge", OverlayFormat.PoseSource(1), "bridge");
            foreach (PoseFallbackReason reason in Enum.GetValues(typeof(PoseFallbackReason)))
            {
                Assert.AreEqual(
                    "pose synthetic (" + reason + ")",
                    OverlayFormat.PoseSource(2 + (int)reason),
                    "fallback reason " + reason);
            }
        }

        [Test]
        public void FrameTimeBucketIsStableWithinATenth()
        {
            Assert.AreEqual(
                OverlayFormat.FrameTimeBucket(11.24f),
                OverlayFormat.FrameTimeBucket(11.21f),
                "same tenth");
            Assert.AreNotEqual(
                OverlayFormat.FrameTimeBucket(11.24f),
                OverlayFormat.FrameTimeBucket(11.29f),
                "next tenth");
        }

        [Test]
        public void PoseAgeBucketClampsToDisplayRange()
        {
            Assert.AreEqual(0, OverlayFormat.PoseAgeBucket(-5.0), "negative clamps to zero");
            Assert.AreEqual(4, OverlayFormat.PoseAgeBucket(4.7), "truncates");
            Assert.AreEqual(9999, OverlayFormat.PoseAgeBucket(123456.0), "upper clamp");
        }

        [Test]
        public void FormatsPluginStateForTheOverlay()
        {
            Assert.AreEqual("plugin -", OverlayFormat.PluginState(-1), "no selector");
            Assert.AreEqual("plugin ok", OverlayFormat.PluginState(0), "plugin available");
            Assert.AreEqual("plugin unavailable", OverlayFormat.PluginState(1), "TD-023 plugin flag visible");
        }

        [Test]
        public void FormatsEffectiveEditCounters()
        {
            Assert.AreEqual("edits 12", OverlayFormat.EditCount(12), "effective edits");
            Assert.AreEqual("edits -", OverlayFormat.EditCount(-1), "no runtime");
            Assert.AreEqual("skip 3", OverlayFormat.SkippedEdits(3), "skipped replays");
            Assert.AreEqual("skip -", OverlayFormat.SkippedEdits(-1), "no runtime");
        }

        [Test]
        public void PanelLayoutRowsStayInsideThePanel()
        {
            Rect panel = DebugOverlay.PanelRect;
            Assert.AreEqual(8f, panel.x, "panel x");
            Assert.AreEqual(8f, panel.y, "panel y");
            Assert.AreEqual(240f, panel.width, "panel width");
            Assert.AreEqual(18f * (DebugOverlay.RowCount + 1), panel.height, "panel height fits every row plus padding");
        }

        [Test]
        public void OverlayTextReusesTheBufferAndOnlyRebuildsOnChange()
        {
            int formatCalls = 0;
            var row = new OverlayText(bucket =>
            {
                formatCalls++;
                return "v" + bucket;
            });

            Assert.IsTrue(row.Set(3), "first set changes");
            Assert.AreEqual("v3", row.Content.text, "text");
            Assert.AreEqual(1, row.Version, "version");

            GUIContent buffer = row.Content;
            Assert.IsFalse(row.Set(3), "unchanged bucket");
            Assert.AreSame(buffer, row.Content, "buffer reused");
            Assert.AreEqual(1, formatCalls, "formatter not called for an unchanged bucket");
            Assert.AreEqual(1, row.Version, "version unchanged");

            Assert.IsTrue(row.Set(4), "changed bucket");
            Assert.AreEqual("v4", row.Content.text, "text rebuilt");
            Assert.AreEqual(2, formatCalls, "formatter called once more");
            Assert.AreEqual(2, row.Version, "version bumped");
            Assert.AreEqual(4, row.LastBucket, "last bucket");
        }

        [Test]
        public void UnchangedOverlayTextUpdatesAllocateNothing()
        {
            var row = new OverlayText(OverlayFormat.FrameTimeTenths);
            row.Set(1234);

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++)
            {
                row.Set(1234);
            }

            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.AreEqual(0L, allocated, "unchanged OverlayText.Set allocated bytes");
        }
    }
}

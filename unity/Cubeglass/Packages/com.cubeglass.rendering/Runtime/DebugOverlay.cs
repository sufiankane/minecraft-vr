using System;
using System.Globalization;
using Cubeglass.Unity.Bridge;
using UnityEngine;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace Cubeglass.Unity.Rendering
{
    /// <summary>
    /// Pure formatting helpers for <see cref="DebugOverlay"/>. Every helper is
    /// allocation-free except for the final <c>string</c> it builds, so callers
    /// invoke them only when the integer bucket changes.
    /// </summary>
    public static class OverlayFormat
    {
        /// <summary>Quantises a smoothed frame time to tenths of a millisecond.</summary>
        public static int FrameTimeBucket(float milliseconds)
        {
            return Mathf.RoundToInt(milliseconds * 10f);
        }

        /// <summary>Quantises a pose age to whole milliseconds, clamped to [0, 9999].</summary>
        public static int PoseAgeBucket(double milliseconds)
        {
            return Mathf.Clamp((int)milliseconds, 0, 9999);
        }

        /// <summary>Formats a frame-time bucket (tenths of a millisecond).</summary>
        public static string FrameTimeTenths(int tenths)
        {
            return "frame " + (tenths * 0.1f).ToString("0.0", CultureInfo.InvariantCulture) + " ms";
        }

        /// <summary>Formats a pose-rate bucket in Hz; negative means "no samples yet".</summary>
        public static string PoseRateHz(int hertz)
        {
            return hertz < 0
                ? "pose - Hz"
                : "pose " + hertz.ToString(CultureInfo.InvariantCulture) + " Hz";
        }

        /// <summary>Formats a pose-age bucket in ms; negative means "no samples yet".</summary>
        public static string PoseAgeMs(int milliseconds)
        {
            return milliseconds < 0
                ? "age - ms"
                : "age " + milliseconds.ToString(CultureInfo.InvariantCulture) + " ms";
        }

        /// <summary>Formats a tracking-state bucket (values of <see cref="PoseTrackingState"/>); negative means "none".</summary>
        public static string TrackingState(int state)
        {
            return state < 0
                ? "track -"
                : "track " + ((PoseTrackingState)state).ToString();
        }

        /// <summary>Formats a command-ack bucket; negative means "no ack observed".</summary>
        public static string CommandAck(int ack)
        {
            return ack < 0
                ? "ack -"
                : "ack 0x" + ack.ToString("X8", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Formats a pose-source bucket: 1 is the bridge, 0 is unknown/source
        /// absent, and <c>2 + (int)PoseFallbackReason</c> is the active
        /// synthetic fallback reason (I-1), so a silent fallback is visible.
        /// </summary>
        public static string PoseSource(int bucket)
        {
            if (bucket == 1)
            {
                return "pose bridge";
            }

            if (bucket < 2)
            {
                return "pose -";
            }

            return "pose synthetic (" + ((PoseFallbackReason)(bucket - 2)).ToString() + ")";
        }
    }

    /// <summary>
    /// A reusable IMGUI text row: owns one <see cref="GUIContent"/> and only
    /// rebuilds its string when the integer bucket changes, so an overlay that
    /// repaints with unchanged values allocates nothing.
    /// </summary>
    public sealed class OverlayText
    {
        /// <summary>Builds the display string for a bucket; called only on change.</summary>
        public delegate string Formatter(int bucket);

        private readonly GUIContent content;
        private readonly Formatter formatter;
        private int lastBucket;
        private bool hasValue;

        /// <summary>Creates a row with the given formatter (called on change only).</summary>
        public OverlayText(Formatter formatter)
        {
            if (formatter == null)
            {
                throw new ArgumentNullException(nameof(formatter));
            }

            this.formatter = formatter;
            content = new GUIContent(string.Empty);
        }

        /// <summary>The reusable content drawn by the overlay.</summary>
        public GUIContent Content
        {
            get { return content; }
        }

        /// <summary>Incremented every time the text actually changes.</summary>
        public int Version { get; private set; }

        /// <summary>
        /// The bucket most recently passed to <see cref="Set"/>; 0 before the
        /// first call (use <see cref="Version"/> to tell whether one happened).
        /// </summary>
        public int LastBucket
        {
            get { return lastBucket; }
        }

        /// <summary>
        /// Sets the row to <paramref name="bucket"/>. Returns false without
        /// calling the formatter or replacing the content when the bucket is
        /// unchanged.
        /// </summary>
        public bool Set(int bucket)
        {
            if (hasValue && bucket == lastBucket)
            {
                return false;
            }

            hasValue = true;
            lastBucket = bucket;
            content.text = formatter(bucket);
            Version++;
            return true;
        }
    }

    /// <summary>
    /// IMGUI debug overlay (game view / Editor preview only): smoothed frame
    /// time in ms, pose rate in Hz, pose age in ms, tracking state, the pose
    /// source (bridge vs synthetic fallback reason, I-1) and the last command
    /// ack. Values refresh once per rendered frame and are drawn from
    /// pre-formatted <see cref="GUIContent"/> buffers reused by
    /// <see cref="OverlayText"/>, so repaints with unchanged values allocate
    /// nothing. Disabling the component stops all of its work.
    /// </summary>
    /// <remarks>
    /// Pose rate is derived from the deltas of the applied sample's
    /// <c>HostTime</c>; pose age estimates <c>now - HostTime</c> on the same
    /// monotonic clock family (the synthetic provider and the Windows bridge
    /// both use it). The 5.12 reader ABI exposes no ack read, so the command ack
    /// is whatever the command sender reports through <see cref="CommandAck"/>
    /// (0 renders as "-").
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class DebugOverlay : MonoBehaviour
    {
        private const float Smoothing = 0.1f;
        private const float RateSmoothing = 0.2f;
        private const int RowCount = 6;
        private const float PanelX = 8f;
        private const float PanelY = 8f;
        private const float PanelWidth = 240f;
        private const float RowHeight = 18f;

        private static readonly OverlayText.Formatter FrameTimeFormatter = OverlayFormat.FrameTimeTenths;
        private static readonly OverlayText.Formatter PoseRateFormatter = OverlayFormat.PoseRateHz;
        private static readonly OverlayText.Formatter PoseAgeFormatter = OverlayFormat.PoseAgeMs;
        private static readonly OverlayText.Formatter TrackingFormatter = OverlayFormat.TrackingState;
        private static readonly OverlayText.Formatter PoseSourceFormatter = OverlayFormat.PoseSource;
        private static readonly OverlayText.Formatter CommandAckFormatter = OverlayFormat.CommandAck;

        [SerializeField] private bool visible = true;
        [SerializeField] private LateLatchPose lateLatch;

        private OverlayText frameTime;
        private OverlayText poseRate;
        private OverlayText poseAge;
        private OverlayText tracking;
        private OverlayText poseSource;
        private OverlayText commandAck;

        private float smoothedFrameMs;
        private bool hasFrameTime;
        private double smoothedRateHz;
        private bool hasRate;
        private long lastHostTime;
        private bool hasLastHostTime;
        private int lastRefreshFrame = int.MinValue;

        /// <summary>Show/hide the overlay without disabling the component.</summary>
        public bool Visible
        {
            get { return visible; }
            set { visible = value; }
        }

        /// <summary>Last command ack reported by the command sender; 0 means "none observed".</summary>
        public uint CommandAck { get; set; }

        /// <summary>The late latch the overlay reads; defaults to the sibling component.</summary>
        public LateLatchPose LateLatch
        {
            get { return lateLatch; }
            set { lateLatch = value; }
        }

        /// <summary>
        /// The pose selector whose bridge/fallback state is shown; resolved at
        /// <c>Awake</c> from the scene (the selector sits on the rig object),
        /// or assigned explicitly. Null keeps the row at "-".
        /// </summary>
        public PoseProviderSelector PoseSource { get; set; }

        private void Awake()
        {
            EnsureRows();
            if (lateLatch == null)
            {
                lateLatch = GetComponent<LateLatchPose>();
            }

            if (PoseSource == null)
            {
                PoseSource = FindFirstObjectByType<PoseProviderSelector>(FindObjectsInactive.Include);
            }
        }

        private void EnsureRows()
        {
            if (frameTime != null)
            {
                return;
            }

            frameTime = new OverlayText(FrameTimeFormatter);
            poseRate = new OverlayText(PoseRateFormatter);
            poseAge = new OverlayText(PoseAgeFormatter);
            tracking = new OverlayText(TrackingFormatter);
            poseSource = new OverlayText(PoseSourceFormatter);
            commandAck = new OverlayText(CommandAckFormatter);
        }

        private void OnGUI()
        {
            if (!visible)
            {
                return;
            }

            EnsureRows();

            // Layout fires once per camera and frame; refresh only once so the
            // smoothing and rate deltas see exactly one tick per frame.
            if ((Event.current == null || Event.current.type == EventType.Layout) &&
                Time.frameCount != lastRefreshFrame)
            {
                lastRefreshFrame = Time.frameCount;
                RefreshValues();
            }

            DrawPanel();
        }

        private void RefreshValues()
        {
            float deltaMs = Time.unscaledDeltaTime * 1000f;
            smoothedFrameMs = hasFrameTime
                ? smoothedFrameMs + ((deltaMs - smoothedFrameMs) * Smoothing)
                : deltaMs;
            hasFrameTime = true;
            frameTime.Set(OverlayFormat.FrameTimeBucket(smoothedFrameMs));

            if (lateLatch != null && lateLatch.HasSample)
            {
                BridgeHeadSample sample = lateLatch.LastSample;
                tracking.Set((int)lateLatch.TrackingState);

                if (hasLastHostTime && sample.HostTime > lastHostTime)
                {
                    double seconds = (sample.HostTime - lastHostTime) / 1_000_000_000.0;
                    if (seconds > 0.0)
                    {
                        double hertz = 1.0 / seconds;
                        smoothedRateHz = hasRate
                            ? smoothedRateHz + ((hertz - smoothedRateHz) * RateSmoothing)
                            : hertz;
                        hasRate = true;
                    }
                }

                lastHostTime = sample.HostTime;
                hasLastHostTime = true;

                double ageMs = (NowNanoseconds() - sample.HostTime) / 1_000_000.0;
                poseAge.Set(OverlayFormat.PoseAgeBucket(ageMs));
                poseRate.Set(hasRate ? Mathf.Max(0, Mathf.RoundToInt((float)smoothedRateHz)) : -1);
            }
            else
            {
                tracking.Set(-1);
                poseAge.Set(-1);
                poseRate.Set(-1);
            }

            commandAck.Set(CommandAck == 0 ? -1 : (int)Math.Min(CommandAck, (uint)int.MaxValue));
            poseSource.Set(DescribePoseSource());
        }

        /// <summary>
        /// Maps the selector state to the pose-source bucket: 1 bridge, 0
        /// source absent, otherwise <c>2 + reason</c> (see
        /// <see cref="OverlayFormat.PoseSource"/>).
        /// </summary>
        private int DescribePoseSource()
        {
            PoseProviderSelector selector = PoseSource;
            if (selector == null)
            {
                return 0;
            }

            return selector.UsingBridge ? 1 : 2 + (int)selector.FallbackReason;
        }

        private void DrawPanel()
        {
            GUI.Box(new Rect(PanelX, PanelY, PanelWidth, RowHeight * (RowCount + 1)), GUIContent.none);

            float y = PanelY + (RowHeight * 0.5f);
            DrawRow(y, frameTime);
            y += RowHeight;
            DrawRow(y, poseRate);
            y += RowHeight;
            DrawRow(y, poseAge);
            y += RowHeight;
            DrawRow(y, tracking);
            y += RowHeight;
            DrawRow(y, poseSource);
            y += RowHeight;
            DrawRow(y, commandAck);
        }

        private static void DrawRow(float y, OverlayText row)
        {
            GUI.Label(new Rect(PanelX + 6f, y, PanelWidth - 12f, RowHeight - 2f), row.Content);
        }

        private static long NowNanoseconds()
        {
            return (long)(Stopwatch.GetTimestamp() * (1_000_000_000.0 / Stopwatch.Frequency));
        }
    }
}

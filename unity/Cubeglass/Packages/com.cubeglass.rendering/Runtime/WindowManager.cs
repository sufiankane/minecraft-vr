using System;
using System.Globalization;
using UnityEngine;

namespace Cubeglass.Unity.Rendering
{
    /// <summary>
    /// Applies the borderless-fullscreen presentation once at startup: selects
    /// the configured display, sets <see cref="Screen.fullScreenMode"/> to
    /// <see cref="FullScreenMode.FullScreenWindow"/> and requests that display's
    /// native resolution at the configured refresh rate.
    /// </summary>
    /// <remarks>
    /// The window is only touched when <see cref="StereoRigConfig.BorderlessFullscreen"/>
    /// is enabled and the Editor preview is explicitly allowed (it is not by
    /// default, so the Editor game view keeps its windowed preview). There is no
    /// per-frame work: the mode is applied from <c>Awake</c>, callers re-run
    /// <see cref="ApplyWindowMode"/> after changing the config, and one
    /// <c>Update</c> performs the read-back check. The pure decision logic
    /// (<see cref="ShouldApplyFullscreen"/>, <see cref="ClampDisplayIndex"/>,
    /// <see cref="ModeMatchesRequest"/>, <see cref="CenteredWindowPosition"/>)
    /// is separated so EditMode tests can pin it without touching the screen.
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class WindowManager : MonoBehaviour
    {
        /// <summary>Refresh-rate read-back tolerance in Hz (the OS may quantise).</summary>
        public const double RefreshToleranceHz = 0.5;

        [SerializeField] private StereoRigConfig config = new StereoRigConfig();

        [Tooltip("Display the fullscreen window is sized from (0 = main). Clamped to the connected displays.")]
        [SerializeField] private int targetDisplayIndex;

        [Tooltip("Allow the fullscreen switch in the Editor; off keeps the game view windowed for preview.")]
        [SerializeField] private bool applyInEditor;

        private bool readBackPending;
        private int appliedRefresh;

        /// <summary>Whether the one-frame-after read-back check is still pending.</summary>
        public bool ReadBackPending
        {
            get { return readBackPending; }
        }

        /// <summary>The mismatch the last read-back reported, or null when the requested mode was applied.</summary>
        public string LastMismatch { get; private set; }

        /// <summary>The rig config supplying borderless/refresh settings; never null in use.</summary>
        public StereoRigConfig Config
        {
            get { return config; }
            set { config = value; }
        }

        /// <summary>Display the window is sized from (0 = main), clamped at apply time.</summary>
        public int TargetDisplayIndex
        {
            get { return targetDisplayIndex; }
            set { targetDisplayIndex = value; }
        }

        /// <summary>Whether the Editor is allowed to switch to fullscreen.</summary>
        public bool ApplyInEditor
        {
            get { return applyInEditor; }
            set { applyInEditor = value; }
        }

        private void Awake()
        {
            ApplyWindowMode();
        }

        /// <summary>
        /// One frame after the request, compares the actual fullscreen mode and
        /// refresh rate with the requested ones and warns loudly on a mismatch
        /// (TD-060 M-10): an ignored or degraded mode is visible in the log
        /// instead of silently passing as success.
        /// </summary>
        private void Update()
        {
            if (!readBackPending)
            {
                return;
            }

            readBackPending = false;
            RefreshRate actual = Screen.currentResolution.refreshRateRatio;
            LastMismatch = DescribeMismatch(
                Screen.fullScreenMode,
                actual.value,
                FullScreenMode.FullScreenWindow,
                appliedRefresh);
            if (LastMismatch != null)
            {
                Debug.LogWarning("[WindowManager] " + LastMismatch);
            }
        }

        /// <summary>
        /// Applies the configured window mode. Returns false, leaving the window
        /// untouched, when borderless is disabled or the Editor preview guard
        /// rejects the switch.
        /// </summary>
        /// <remarks>
        /// Review R-1: a deserialized scene can carry an invalid refresh (a
        /// legacy/default-initialised <c>targetRefresh = 0</c> would otherwise
        /// request 1 Hz), so the effective config is validated at the point of
        /// use before anything reads it. Validation is idempotent, so repeated
        /// applies do not log repeatedly.
        /// </remarks>
        public bool ApplyWindowMode()
        {
            StereoRigConfig effective = config ?? new StereoRigConfig();
            effective.Validate();
            if (!ShouldApplyFullscreen(effective.BorderlessFullscreen, Application.isEditor, applyInEditor))
            {
                return false;
            }

            int display = ClampDisplayIndex(targetDisplayIndex, Display.displays.Length);
            ApplyFullscreen(display, effective.TargetRefresh);
            return true;
        }

        /// <summary>
        /// True when the borderless switch should run: enabled in config and
        /// either a player build or an Editor run that explicitly allowed it.
        /// </summary>
        public static bool ShouldApplyFullscreen(bool borderlessEnabled, bool isEditor, bool applyInEditor)
        {
            return borderlessEnabled && (!isEditor || applyInEditor);
        }

        /// <summary>Clamps a requested display index to <c>[0, displayCount - 1]</c>; 0 when none is known.</summary>
        public static int ClampDisplayIndex(int requested, int displayCount)
        {
            if (displayCount <= 1)
            {
                return 0;
            }

            return Mathf.Clamp(requested, 0, displayCount - 1);
        }

        /// <summary>
        /// Whether the actual mode/refresh a read-back observed satisfies the
        /// request. The refresh comparison uses
        /// <see cref="RefreshToleranceHz"/> because the OS quantises rates.
        /// </summary>
        public static bool ModeMatchesRequest(
            FullScreenMode actualMode,
            double actualRefresh,
            FullScreenMode requestedMode,
            int requestedRefresh)
        {
            return actualMode == requestedMode
                && Math.Abs(actualRefresh - requestedRefresh) <= RefreshToleranceHz;
        }

        /// <summary>
        /// A human-readable description of a read-back mismatch, or null when
        /// the request was honoured (TD-060 M-10).
        /// </summary>
        public static string DescribeMismatch(
            FullScreenMode actualMode,
            double actualRefresh,
            FullScreenMode requestedMode,
            int requestedRefresh)
        {
            if (actualMode != requestedMode)
            {
                return "the OS applied fullscreen mode " + actualMode
                    + " instead of the requested " + requestedMode + ".";
            }

            if (Math.Abs(actualRefresh - requestedRefresh) > RefreshToleranceHz)
            {
                return "the OS applied " + actualRefresh.ToString("0.##", CultureInfo.InvariantCulture)
                    + " Hz instead of the requested " + requestedRefresh
                    + " Hz; the display may not support that mode.";
            }

            return null;
        }

        /// <summary>
        /// The window position that centres a <paramref name="windowWidth"/> ×
        /// <paramref name="windowHeight"/> window on a display of the given
        /// size, never negative.
        /// </summary>
        public static Vector2Int CenteredWindowPosition(
            int displayWidth, int displayHeight, int windowWidth, int windowHeight)
        {
            return new Vector2Int(
                Math.Max(0, (displayWidth - windowWidth) / 2),
                Math.Max(0, (displayHeight - windowHeight) / 2));
        }

        private void ApplyFullscreen(int displayIndex, int targetRefresh)
        {
            int width = Screen.currentResolution.width;
            int height = Screen.currentResolution.height;
            bool hasTarget = displayIndex >= 0 && displayIndex < Display.displays.Length;
            if (hasTarget)
            {
                width = Display.displays[displayIndex].systemWidth;
                height = Display.displays[displayIndex].systemHeight;
            }

            // TD-060 M-10: Screen.SetResolution sizes the window on the display
            // it is currently on, so a non-main target needs an explicit move;
            // when the platform refuses, the warning names the S6-HIL OS-level
            // workaround instead of silently sizing the wrong monitor.
            if (hasTarget && displayIndex != 0)
            {
                TryMoveMainWindowTo(displayIndex);
            }

            Screen.fullScreenMode = FullScreenMode.FullScreenWindow;
            Screen.SetResolution(
                width,
                height,
                FullScreenMode.FullScreenWindow,
                new RefreshRate
                {
                    // Defence in depth (review R-1): even if a caller bypassed
                    // ApplyWindowMode's validation, the request can never be
                    // 1 Hz or a non-finite clamp artifact.
                    numerator = (uint)StereoRigConfig.SanitizeTargetRefresh(targetRefresh),
                    denominator = 1u,
                });

            appliedRefresh = StereoRigConfig.SanitizeTargetRefresh(targetRefresh);
            readBackPending = true;
        }

        private static void TryMoveMainWindowTo(int displayIndex)
        {
            try
            {
                var layout = new System.Collections.Generic.List<DisplayInfo>();
                Screen.GetDisplayLayout(layout);
                if (displayIndex < 0 || displayIndex >= layout.Count)
                {
                    Debug.LogWarning(
                        "[WindowManager] display " + displayIndex
                            + " is not in the OS display layout; move the window to it at OS level (S6-HIL workaround).");
                    return;
                }

                DisplayInfo display = layout[displayIndex];
                Screen.MoveMainWindowTo(
                    display,
                    CenteredWindowPosition(display.width, display.height, Screen.width, Screen.height));
            }
            catch (Exception exception)
            {
                Debug.LogWarning(
                    "[WindowManager] could not move the main window to display " + displayIndex
                        + ": " + exception.Message
                        + "; move the window to it at OS level (S6-HIL workaround).");
            }
        }
    }
}

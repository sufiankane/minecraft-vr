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
    /// <c>Update</c>: the mode is applied from <c>Awake</c>, and callers re-run
    /// <see cref="ApplyWindowMode"/> after changing the config. The pure decision
    /// logic (<see cref="ShouldApplyFullscreen"/>, <see cref="ClampDisplayIndex"/>)
    /// is separated so EditMode tests can pin it without touching the screen.
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class WindowManager : MonoBehaviour
    {
        [SerializeField] private StereoRigConfig config = new StereoRigConfig();

        [Tooltip("Display the fullscreen window is sized from (0 = main). Clamped to the connected displays.")]
        [SerializeField] private int targetDisplayIndex;

        [Tooltip("Allow the fullscreen switch in the Editor; off keeps the game view windowed for preview.")]
        [SerializeField] private bool applyInEditor;

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

        private static void ApplyFullscreen(int displayIndex, int targetRefresh)
        {
            int width = Screen.currentResolution.width;
            int height = Screen.currentResolution.height;

            if (displayIndex >= 0 && displayIndex < Display.displays.Length)
            {
                width = Display.displays[displayIndex].systemWidth;
                height = Display.displays[displayIndex].systemHeight;
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
        }
    }
}

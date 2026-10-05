using System;
using Cubeglass.CoreMath;
using Cubeglass.Gameplay;
using UnityEngine;

namespace Cubeglass.Unity.Input
{
    /// <summary>
    /// Samples the legacy Unity input backend into a
    /// <see cref="Cubeglass.Gameplay.InputFrame"/> (S7 Task 3): gamepad (left
    /// stick move, right stick turn, X break / A place, Y recentre, D-pad
    /// left/right snap-turn, LB/RB hotbar) and keyboard/mouse (WASD, mouse-X
    /// look, LMB break hold, RMB place, Q/E hotbar, R recentre, F snap right /
    /// Shift+F snap left).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two modes.</b> The mapping itself lives in the Unity-free
    /// <see cref="InputMapper"/>; this MonoBehaviour only reads device state
    /// into a <see cref="RawInputSample"/>. <see cref="InputMode.Auto"/> picks
    /// the gamepad when the configured axes resolve (they exist in the Input
    /// Manager) and a joystick is connected, otherwise keyboard/mouse. Call
    /// <see cref="RefreshMode"/> after plugging a controller in; the resolved
    /// mode is cached so no per-frame device enumeration allocates.
    /// </para>
    /// <para>
    /// <b>Input System backend (TD-013).</b> When the project enables the new
    /// backend (<c>ENABLE_INPUT_SYSTEM</c>), <see cref="InputMode.InputSystem"/>
    /// reads the same controls from <c>Gamepad</c>/<c>Keyboard</c>/<c>Mouse</c>
    /// through <see cref="InputSystemProvider"/> and the pure
    /// <see cref="InputSystemMapping"/>; in a new-input-only project
    /// <see cref="InputMode.Auto"/> resolves to it because the legacy mapping is
    /// compiled out. While the legacy backend is enabled, Auto keeps the legacy
    /// mapping as the documented fallback and the new adapter is opt-in.
    /// </para>
    /// <para>
    /// <b>Frame edges.</b> <see cref="Sample"/> and <see cref="Update"/> share
    /// one poll per Unity frame (deduplicated by <c>Time.frameCount</c>), so
    /// the mapper's press/release edges are reported on exactly one frame
    /// regardless of which of the two runs first.
    /// </para>
    /// <para>
    /// <b>Turn units.</b> The frame's <c>TurnSnap</c> is degrees per second
    /// (S4, ADR-0008): the gamepad adapter passes the stick deflection with the
    /// configured rate, the mouse adapter converts the per-frame pixel delta to
    /// a rate by dividing by the frame delta. The discrete snap direction
    /// (D-pad/F) is a separate one-shot edge exposed through
    /// <see cref="ConsumeSnapDirection"/> (S7 Task 4a).
    /// </para>
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class UnityInputProvider : MonoBehaviour, IInputProvider, ISnapInputSource
    {
        private const string MouseXAxis = "Mouse X";
        private const float SnapAxisThreshold = 0.5f;

        [Header("Mode")]
        [SerializeField] private InputMode mode = InputMode.Auto;

        [Header("Gamepad axes (legacy Input Manager names)")]
        [SerializeField] private string gamepadMoveAxisX = "Gamepad Move X";
        [SerializeField] private string gamepadMoveAxisY = "Gamepad Move Y";
        [SerializeField] private string gamepadTurnAxisX = "Gamepad Turn X";
        [SerializeField] private string gamepadSnapAxisX = "Gamepad Snap X";

        [Header("Tuning")]
        [SerializeField] private float moveDeadzone = InputMapper.DefaultMoveDeadzone;
        [SerializeField] private float lookDeadzone = InputMapper.DefaultLookDeadzone;
        [SerializeField] private float turnDegreesPerSecond = 90f;
        [SerializeField] private float mouseDegreesPerPixel = 0.2f;
        [SerializeField] private bool swapGamepadButtons;
        [SerializeField] private bool invertTurn;

        private InputMapper mapper;
        private InputSystemProvider inputSystem;
        private InputFrame latest;
        private int lastPollFrame = int.MinValue;
        private bool modeResolved;
        private InputMode resolvedMode = InputMode.KeyboardMouse;

        /// <summary>The most recently polled frame.</summary>
        public InputFrame Latest
        {
            get { return latest; }
        }

        /// <summary>The mode actually sampled (resolved from <see cref="Mode"/>).</summary>
        public InputMode CurrentMode
        {
            get
            {
                EnsureMode();
                return resolvedMode;
            }
        }

        /// <summary>The configured mode; assigning resets the resolved backend.</summary>
        public InputMode Mode
        {
            get { return mode; }
            set
            {
                mode = value;
                RefreshMode();
            }
        }

        /// <summary>The pure mapper; exposed for tests and configuration.</summary>
        public InputMapper Mapper
        {
            get
            {
                EnsureMapper();
                return mapper;
            }
        }

        private void Awake()
        {
            EnsureMapper();
            EnsureMode();
        }

        private void OnEnable()
        {
            EnsureMapper();
            RefreshMode();
        }

        private void Update()
        {
            PollIfStale(Time.unscaledDeltaTime);
        }

        /// <summary>
        /// The S4 provider contract: returns the frame for
        /// <paramref name="timeSeconds"/> (this adapter samples the live device
        /// once per Unity frame and ignores the timeline).
        /// </summary>
        public InputFrame Sample(double timeSeconds)
        {
            PollIfStale(Time.unscaledDeltaTime);
            return latest;
        }

        /// <summary>
        /// Returns the snap-turn direction-change edge exactly once (S7 Task
        /// 4a): -1 left, +1 right. The bridge must call <see cref="Sample"/>
        /// first in the same frame; the per-frame poll dedupe guarantees the
        /// edge is not lost when <see cref="Update"/> also ran.
        /// </summary>
        public int ConsumeSnapDirection()
        {
            EnsureMapper();
            EnsureMode();
            if (resolvedMode == InputMode.InputSystem && inputSystem != null)
            {
                return inputSystem.ConsumeSnapDirection();
            }

            return mapper.ConsumeSnapDirection();
        }

        /// <summary>Resolves <see cref="InputMode.Auto"/> again and clears button edges.</summary>
        public void RefreshMode()
        {
            modeResolved = false;
            inputSystem = null;
            EnsureMode();
            if (mapper != null)
            {
                mapper.Reset();
            }
        }

        /// <summary>Forces a fresh device poll, bypassing the per-frame dedupe.</summary>
        public InputFrame PollNow()
        {
            lastPollFrame = Time.frameCount;
            latest = MapDevices(Time.unscaledDeltaTime);
            return latest;
        }

        private void PollIfStale(float deltaTime)
        {
            int frame = Time.frameCount;
            if (frame == lastPollFrame)
            {
                return;
            }

            lastPollFrame = frame;
            latest = MapDevices(deltaTime);
        }

        private InputFrame MapDevices(float deltaTime)
        {
            EnsureMapper();
            EnsureMode();
            if (resolvedMode == InputMode.InputSystem)
            {
                // The Input System adapter owns its own mapper (it maps raw
                // device state once, edges included), so its frame is returned
                // directly instead of being mapped a second time.
                return SampleInputSystemFrame(deltaTime);
            }

            RawInputSample raw = resolvedMode == InputMode.Gamepad
                ? SampleGamepad(deltaTime)
                : SampleKeyboardMouse(deltaTime);
            return mapper.Map(raw);
        }

        private InputFrame SampleInputSystemFrame(float deltaTime)
        {
#if ENABLE_INPUT_SYSTEM
            if (inputSystem == null)
            {
                inputSystem = new InputSystemProvider(
                    turnDegreesPerSecond,
                    mouseDegreesPerPixel,
                    swapGamepadButtons,
                    invertTurn);
            }

            return inputSystem.PollNow(deltaTime);
#else
            return mapper.Map(RawInputSample.Neutral);
#endif
        }

        private InputMode ResolveMode()
        {
            if (mode != InputMode.Auto)
            {
#if !ENABLE_INPUT_SYSTEM
                // A legacy-only build cannot serve the new adapter; keep the
                // keyboard/mouse mapping instead of a dead neutral frame.
                if (mode == InputMode.InputSystem)
                {
                    return InputMode.KeyboardMouse;
                }
#endif
                return mode;
            }

#if ENABLE_LEGACY_INPUT_MANAGER
            if (AxisAvailable(gamepadMoveAxisX)
                && AxisAvailable(gamepadMoveAxisY)
                && HasConnectedJoystick())
            {
                return InputMode.Gamepad;
            }

            return InputMode.KeyboardMouse;
#elif ENABLE_INPUT_SYSTEM
            // New-input-only projects have no legacy mapping: the Input System
            // adapter owns both gamepad and keyboard/mouse selection.
            return InputMode.InputSystem;
#else
            return InputMode.KeyboardMouse;
#endif
        }

        private void EnsureMode()
        {
            if (modeResolved)
            {
                return;
            }

            resolvedMode = ResolveMode();
            modeResolved = true;
        }

        private void EnsureMapper()
        {
            if (mapper == null)
            {
                mapper = new InputMapper(moveDeadzone, lookDeadzone);
            }
        }

#if ENABLE_LEGACY_INPUT_MANAGER
        private RawInputSample SampleGamepad(float deltaTime)
        {
            float moveX = UnityEngine.Input.GetAxisRaw(gamepadMoveAxisX);
            float moveY = UnityEngine.Input.GetAxisRaw(gamepadMoveAxisY);
            float turn = string.IsNullOrEmpty(gamepadTurnAxisX)
                ? 0f
                : UnityEngine.Input.GetAxisRaw(gamepadTurnAxisX);
            if (invertTurn)
            {
                turn = -turn;
            }

            bool primary = UnityEngine.Input.GetKey(KeyCode.JoystickButton2);
            bool secondary = UnityEngine.Input.GetKey(KeyCode.JoystickButton0);
            if (swapGamepadButtons)
            {
                bool swap = primary;
                primary = secondary;
                secondary = swap;
            }

            // The D-pad deflects the configured axis; the provider applies the
            // coarse half-deflection threshold and the mapper owns the edge.
            float snapAxis = string.IsNullOrEmpty(gamepadSnapAxisX)
                ? 0f
                : UnityEngine.Input.GetAxisRaw(gamepadSnapAxisX);
            int snapDirection = Mathf.Abs(snapAxis) >= SnapAxisThreshold
                ? (snapAxis > 0f ? 1 : -1)
                : 0;

            return new RawInputSample(
                new Vector2f(moveX, moveY),
                new Vector2f(turn, 0f),
                turnDegreesPerSecond,
                primary,
                secondary,
                UnityEngine.Input.GetKey(KeyCode.JoystickButton3),
                snapDirection,
                UnityEngine.Input.GetKey(KeyCode.JoystickButton5),
                UnityEngine.Input.GetKey(KeyCode.JoystickButton4));
        }

        private RawInputSample SampleKeyboardMouse(float deltaTime)
        {
            float moveX = (UnityEngine.Input.GetKey(KeyCode.D) ? 1f : 0f)
                - (UnityEngine.Input.GetKey(KeyCode.A) ? 1f : 0f);
            float moveY = (UnityEngine.Input.GetKey(KeyCode.W) ? 1f : 0f)
                - (UnityEngine.Input.GetKey(KeyCode.S) ? 1f : 0f);
            float look = UnityEngine.Input.GetAxisRaw(MouseXAxis);
            if (invertTurn)
            {
                look = -look;
            }

            // The mouse delta is a per-frame angle; dividing by the frame delta
            // turns it into the S4 degrees-per-second rate that Step integrates.
            float lookDegreesPerSecond = deltaTime > 0f ? mouseDegreesPerPixel / deltaTime : 0f;

            bool snapHeld = UnityEngine.Input.GetKey(KeyCode.F);
            bool snapShift = UnityEngine.Input.GetKey(KeyCode.LeftShift)
                || UnityEngine.Input.GetKey(KeyCode.RightShift);
            int snapDirection = snapHeld ? (snapShift ? -1 : 1) : 0;

            return new RawInputSample(
                new Vector2f(moveX, moveY),
                new Vector2f(look, 0f),
                lookDegreesPerSecond,
                UnityEngine.Input.GetMouseButton(0),
                UnityEngine.Input.GetMouseButton(1),
                UnityEngine.Input.GetKeyDown(KeyCode.R),
                snapDirection,
                UnityEngine.Input.GetKeyDown(KeyCode.E),
                UnityEngine.Input.GetKeyDown(KeyCode.Q));
        }

        private static bool AxisAvailable(string axisName)
        {
            if (string.IsNullOrEmpty(axisName))
            {
                return false;
            }

            try
            {
                UnityEngine.Input.GetAxisRaw(axisName);
                return true;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        private static bool HasConnectedJoystick()
        {
            string[] names = UnityEngine.Input.GetJoystickNames();
            for (int i = 0; i < names.Length; i++)
            {
                if (!string.IsNullOrEmpty(names[i]))
                {
                    return true;
                }
            }

            return false;
        }
#else
        private RawInputSample SampleGamepad(float deltaTime)
        {
            return RawInputSample.Neutral;
        }

        private RawInputSample SampleKeyboardMouse(float deltaTime)
        {
            return RawInputSample.Neutral;
        }
#endif
    }
}

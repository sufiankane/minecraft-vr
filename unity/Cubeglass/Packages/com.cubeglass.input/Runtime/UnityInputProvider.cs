using System;
using Cubeglass.CoreMath;
using Cubeglass.Gameplay;
using UnityEngine;

namespace Cubeglass.Unity.Input
{
    /// <summary>
    /// Samples the legacy Unity input backend into a
    /// <see cref="Cubeglass.Gameplay.InputFrame"/> (S7 Task 3): gamepad (left
    /// stick move, right stick turn, X break / A place, Y recentre, B
    /// snap-turn, LB/RB hotbar) and keyboard/mouse (WASD, mouse-X look, LMB
    /// break hold, RMB place, Q/E hotbar, R recentre, F snap-turn).
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
    /// <b>Frame edges.</b> <see cref="Sample"/> and <see cref="Update"/> share
    /// one poll per Unity frame (deduplicated by <c>Time.frameCount</c>), so
    /// the mapper's press/release edges are reported on exactly one frame
    /// regardless of which of the two runs first.
    /// </para>
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class UnityInputProvider : MonoBehaviour, IInputProvider
    {
        private const string MouseXAxis = "Mouse X";

        [Header("Mode")]
        [SerializeField] private InputMode mode = InputMode.Auto;

        [Header("Gamepad axes (legacy Input Manager names)")]
        [SerializeField] private string gamepadMoveAxisX = "Gamepad Move X";
        [SerializeField] private string gamepadMoveAxisY = "Gamepad Move Y";
        [SerializeField] private string gamepadTurnAxisX = "Gamepad Turn X";

        [Header("Tuning")]
        [SerializeField] private float moveDeadzone = InputMapper.DefaultMoveDeadzone;
        [SerializeField] private float lookDeadzone = InputMapper.DefaultLookDeadzone;
        [SerializeField] private float turnDegreesPerSecond = 90f;
        [SerializeField] private float snapTurnDegrees = InputMapper.DefaultSnapTurnDegrees;
        [SerializeField] private float mouseDegreesPerPixel = 0.2f;
        [SerializeField] private bool swapGamepadButtons;
        [SerializeField] private bool invertTurn;

        private InputMapper mapper;
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

        /// <summary>Resolves <see cref="InputMode.Auto"/> again and clears button edges.</summary>
        public void RefreshMode()
        {
            modeResolved = false;
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
            RawInputSample raw = resolvedMode == InputMode.Gamepad
                ? SampleGamepad(deltaTime)
                : SampleKeyboardMouse(deltaTime);
            return mapper.Map(raw);
        }

        private InputMode ResolveMode()
        {
            if (mode != InputMode.Auto)
            {
                return mode;
            }

#if ENABLE_LEGACY_INPUT_MANAGER
            if (AxisAvailable(gamepadMoveAxisX)
                && AxisAvailable(gamepadMoveAxisY)
                && HasConnectedJoystick())
            {
                return InputMode.Gamepad;
            }
#endif
            return InputMode.KeyboardMouse;
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
                mapper = new InputMapper(moveDeadzone, lookDeadzone, snapTurnDegrees);
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

            return new RawInputSample(
                new Vector2f(moveX, moveY),
                new Vector2f(turn, 0f),
                turnDegreesPerSecond * deltaTime,
                primary,
                secondary,
                UnityEngine.Input.GetKey(KeyCode.JoystickButton3),
                UnityEngine.Input.GetKey(KeyCode.JoystickButton1),
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

            return new RawInputSample(
                new Vector2f(moveX, moveY),
                new Vector2f(look, 0f),
                mouseDegreesPerPixel,
                UnityEngine.Input.GetMouseButton(0),
                UnityEngine.Input.GetMouseButton(1),
                UnityEngine.Input.GetKeyDown(KeyCode.R),
                UnityEngine.Input.GetKeyDown(KeyCode.F),
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

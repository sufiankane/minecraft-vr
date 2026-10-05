using Cubeglass.CoreMath;
using Cubeglass.Gameplay;
using UnityEngine;

#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace Cubeglass.Unity.Input
{
    /// <summary>
    /// Reads the Input System package's gamepad/keyboard/mouse devices into the
    /// shared <see cref="InputSystemMapping"/> (TD-013). This is the new-input
    /// adapter behind <see cref="UnityInputProvider"/>; the mapping table itself
    /// stays Unity-free, so gamepad and keyboard equivalence is pinned in
    /// EditMode tests against the pure snapshots.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The adapter exists only when the project enables the new backend
    /// (<c>ENABLE_INPUT_SYSTEM</c>, i.e. Active Input Handling "Input System
    /// Package" or "Both"). With the legacy backend also enabled,
    /// <see cref="UnityInputProvider"/> keeps legacy as the default and this
    /// adapter is reached by forcing <see cref="InputMode.InputSystem"/>, so
    /// both backends stay exercised and a new-input-only build keeps working.
    /// </para>
    /// <para>
    /// Device selection mirrors the legacy adapter: a connected
    /// <see cref="Gamepad"/> wins, otherwise keyboard+mouse. Reads happen once
    /// per Unity frame (deduplicated by <c>Time.frameCount</c>) so the mapper's
    /// press edges are reported on exactly one frame.
    /// </para>
    /// </remarks>
    public sealed class InputSystemProvider : IInputProvider, ISnapInputSource
    {
        private readonly float turnDegreesPerSecond;
        private readonly float mouseDegreesPerPixel;
        private readonly bool swapButtons;
        private readonly bool invertTurn;
        private readonly InputMapper mapper = new InputMapper();

        private InputFrame latest;
        private int lastPollFrame = int.MinValue;

        /// <summary>
        /// Creates the adapter. The tuning values match
        /// <see cref="UnityInputProvider"/>'s legacy fields.
        /// </summary>
        public InputSystemProvider(
            float turnDegreesPerSecond = 90f,
            float mouseDegreesPerPixel = 0.2f,
            bool swapButtons = false,
            bool invertTurn = false)
        {
            this.turnDegreesPerSecond = turnDegreesPerSecond;
            this.mouseDegreesPerPixel = mouseDegreesPerPixel;
            this.swapButtons = swapButtons;
            this.invertTurn = invertTurn;
        }

#if ENABLE_INPUT_SYSTEM
        /// <summary>True when the Input System reports a usable gamepad.</summary>
        public static bool HasGamepad()
        {
            Gamepad gamepad = Gamepad.current;
            return gamepad != null && gamepad.added;
        }

        /// <summary>Forces a fresh device poll, bypassing the per-frame dedupe.</summary>
        public InputFrame PollNow(float deltaTime)
        {
            lastPollFrame = Time.frameCount;
            latest = mapper.Map(ReadRaw(deltaTime));
            return latest;
        }

        /// <summary>
        /// The S4 provider contract: samples the live devices once per Unity
        /// frame and returns the mapped frame.
        /// </summary>
        public InputFrame Sample(double timeSeconds)
        {
            int frame = Time.frameCount;
            if (frame != lastPollFrame)
            {
                lastPollFrame = frame;
                latest = mapper.Map(ReadRaw(Time.unscaledDeltaTime));
            }

            return latest;
        }

        /// <summary>Snap-turn direction-change edge of the latest sample, consumed once.</summary>
        public int ConsumeSnapDirection()
        {
            return mapper.ConsumeSnapDirection();
        }

        private RawInputSample ReadRaw(float deltaTime)
        {
            Gamepad gamepad = Gamepad.current;
            if (gamepad != null && gamepad.added)
            {
                return InputSystemMapping.FromGamepad(ReadGamepad(gamepad), turnDegreesPerSecond, swapButtons, invertTurn);
            }

            return InputSystemMapping.FromKeyboardMouse(ReadKeyboardMouse(), deltaTime, mouseDegreesPerPixel, invertTurn);
        }

        private static GamepadSnapshot ReadGamepad(Gamepad gamepad)
        {
            float snap = 0f;
            if (gamepad.dpad.left.isPressed)
            {
                snap = -1f;
            }
            else if (gamepad.dpad.right.isPressed)
            {
                snap = 1f;
            }

            return new GamepadSnapshot(
                ToCoreMath(gamepad.leftStick.ReadValue()),
                ToCoreMath(gamepad.rightStick.ReadValue()),
                gamepad.xButton.isPressed,
                gamepad.aButton.isPressed,
                gamepad.yButton.isPressed,
                InputSystemMapping.SnapDirection(snap),
                gamepad.rightShoulder.isPressed,
                gamepad.leftShoulder.isPressed);
        }

        private static Vector2f ToCoreMath(Vector2 value)
        {
            return new Vector2f(value.x, value.y);
        }

        private static KeyboardMouseSnapshot ReadKeyboardMouse()
        {
            Keyboard keyboard = Keyboard.current;
            Mouse mouse = Mouse.current;

            float moveX = 0f;
            float moveY = 0f;
            bool recenter = false;
            bool hotbarNext = false;
            bool hotbarPrev = false;
            int snap = 0;
            if (keyboard != null && keyboard.added)
            {
                moveX = (keyboard.dKey.isPressed ? 1f : 0f) - (keyboard.aKey.isPressed ? 1f : 0f);
                moveY = (keyboard.wKey.isPressed ? 1f : 0f) - (keyboard.sKey.isPressed ? 1f : 0f);
                recenter = keyboard.rKey.wasPressedThisFrame;
                hotbarNext = keyboard.eKey.wasPressedThisFrame;
                hotbarPrev = keyboard.qKey.wasPressedThisFrame;
                if (keyboard.fKey.isPressed)
                {
                    bool shift = keyboard.leftShiftKey.isPressed || keyboard.rightShiftKey.isPressed;
                    snap = shift ? -1 : 1;
                }
            }

            float mouseDeltaX = 0f;
            bool primary = false;
            bool secondary = false;
            if (mouse != null && mouse.added)
            {
                mouseDeltaX = mouse.delta.ReadValue().x;
                primary = mouse.leftButton.isPressed;
                secondary = mouse.rightButton.isPressed;
            }

            return new KeyboardMouseSnapshot(
                new Vector2f(moveX, moveY),
                mouseDeltaX,
                primary,
                secondary,
                recenter,
                snap,
                hotbarNext,
                hotbarPrev);
        }
#else
        /// <summary>Never true without the new backend compiled in.</summary>
        public static bool HasGamepad()
        {
            return false;
        }

        /// <summary>Neutral: the adapter does not exist in a legacy-only project.</summary>
        public InputFrame PollNow(float deltaTime)
        {
            return default;
        }

        /// <summary>Neutral: the adapter does not exist in a legacy-only project.</summary>
        public InputFrame Sample(double timeSeconds)
        {
            return default;
        }

        /// <summary>No snap channel without the new backend compiled in.</summary>
        public int ConsumeSnapDirection()
        {
            return 0;
        }
#endif
    }
}

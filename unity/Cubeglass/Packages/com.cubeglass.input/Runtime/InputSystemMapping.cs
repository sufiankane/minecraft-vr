using System;
using Cubeglass.CoreMath;
using Cubeglass.Gameplay;

namespace Cubeglass.Unity.Input
{
    /// <summary>
    /// One gamepad state snapshot in the shape the Input System adapter reads
    /// from a <c>Gamepad</c> device, free of UnityEngine and the Input System
    /// package so the mapping can be pinned in EditMode tests.
    /// </summary>
    public readonly struct GamepadSnapshot
    {
        public GamepadSnapshot(
            Vector2f leftStick,
            Vector2f rightStick,
            bool primaryDown,
            bool secondaryDown,
            bool recenterDown,
            int snapDirection,
            bool hotbarNextDown,
            bool hotbarPrevDown)
        {
            LeftStick = leftStick;
            RightStick = rightStick;
            PrimaryDown = primaryDown;
            SecondaryDown = secondaryDown;
            RecenterDown = recenterDown;
            SnapDirection = snapDirection;
            HotbarNextDown = hotbarNextDown;
            HotbarPrevDown = hotbarPrevDown;
        }

        /// <summary>Left stick deflection, each axis in [-1, 1].</summary>
        public Vector2f LeftStick { get; }

        /// <summary>Right stick deflection, each axis in [-1, 1].</summary>
        public Vector2f RightStick { get; }

        /// <summary>Break-equivalent face button (Input System gamepad X / west).</summary>
        public bool PrimaryDown { get; }

        /// <summary>Place-equivalent face button (Input System gamepad A / south).</summary>
        public bool SecondaryDown { get; }

        /// <summary>Recentre button (Input System gamepad Y / north).</summary>
        public bool RecenterDown { get; }

        /// <summary>D-pad horizontal: -1 left, +1 right, 0 neutral.</summary>
        public int SnapDirection { get; }

        /// <summary>Next-hotbar shoulder button (right shoulder).</summary>
        public bool HotbarNextDown { get; }

        /// <summary>Previous-hotbar shoulder button (left shoulder).</summary>
        public bool HotbarPrevDown { get; }
    }

    /// <summary>
    /// One keyboard+mouse state snapshot in the shape the Input System adapter
    /// reads from <c>Keyboard</c> and <c>Mouse</c> devices, free of UnityEngine
    /// and the Input System package so the mapping can be pinned in EditMode
    /// tests.
    /// </summary>
    public readonly struct KeyboardMouseSnapshot
    {
        public KeyboardMouseSnapshot(
            Vector2f move,
            float mouseDeltaX,
            bool primaryDown,
            bool secondaryDown,
            bool recenterPressed,
            int snapDirection,
            bool hotbarNextPressed,
            bool hotbarPrevPressed)
        {
            Move = move;
            MouseDeltaX = mouseDeltaX;
            PrimaryDown = primaryDown;
            SecondaryDown = secondaryDown;
            RecenterPressed = recenterPressed;
            SnapDirection = snapDirection;
            HotbarNextPressed = hotbarNextPressed;
            HotbarPrevPressed = hotbarPrevPressed;
        }

        /// <summary>WASD vector: X right, Y forward (W positive).</summary>
        public Vector2f Move { get; }

        /// <summary>This frame's mouse-X pixel delta.</summary>
        public float MouseDeltaX { get; }

        /// <summary>Left mouse button "down now" state.</summary>
        public bool PrimaryDown { get; }

        /// <summary>Right mouse button "down now" state.</summary>
        public bool SecondaryDown { get; }

        /// <summary>Recentre press edge (R).</summary>
        public bool RecenterPressed { get; }

        /// <summary>Snap-turn control: -1 left (Shift+F), +1 right (F), 0 neutral.</summary>
        public int SnapDirection { get; }

        /// <summary>Next-hotbar press edge (E).</summary>
        public bool HotbarNextPressed { get; }

        /// <summary>Previous-hotbar press edge (Q).</summary>
        public bool HotbarPrevPressed { get; }
    }

    /// <summary>
    /// The Unity-free adaptation from Input System device snapshots to the
    /// shared <see cref="RawInputSample"/> (TD-013).
    /// </summary>
    /// <remarks>
    /// Both adapters feed the same <see cref="InputMapper"/>, so the mapping
    /// table stays in one place. The rules are the exact Input System mirror of
    /// the legacy adapter's (<see cref="UnityInputProvider"/>): left stick is
    /// move, right stick X is the turn rate at <c>turnDegreesPerSecond</c>,
    /// mouse-X is a per-frame delta converted to a rate by dividing by the
    /// frame delta, Input System gamepad west = break / south = place /
    /// north = recentre, D-pad X is the snap channel and the shoulders cycle
    /// the hotbar. A device snapshot that is neutral therefore maps to
    /// <see cref="InputFrame"/>-neutral through the mapper.
    /// </remarks>
    public static class InputSystemMapping
    {
        /// <summary>Gamepad D-pad half-deflection threshold; the D-pad is already discrete, kept for parity.</summary>
        public const float SnapAxisThreshold = 0.5f;

        /// <summary>
        /// Maps a gamepad snapshot. <paramref name="swapButtons"/> mirrors the
        /// legacy adapter's swap switch; <paramref name="invertTurn"/> flips the
        /// look sign for either adapter.
        /// </summary>
        public static RawInputSample FromGamepad(
            in GamepadSnapshot snapshot,
            float turnDegreesPerSecond,
            bool swapButtons = false,
            bool invertTurn = false)
        {
            bool primary = snapshot.PrimaryDown;
            bool secondary = snapshot.SecondaryDown;
            if (swapButtons)
            {
                bool swap = primary;
                primary = secondary;
                secondary = swap;
            }

            float turn = invertTurn ? -snapshot.RightStick.X : snapshot.RightStick.X;
            return new RawInputSample(
                snapshot.LeftStick,
                new Vector2f(turn, 0f),
                turnDegreesPerSecond,
                primary,
                secondary,
                snapshot.RecenterDown,
                SnapDirection(snapshot.SnapDirection),
                snapshot.HotbarNextDown,
                snapshot.HotbarPrevDown);
        }

        /// <summary>
        /// Maps a keyboard+mouse snapshot. The mouse delta is a per-frame angle;
        /// dividing by <paramref name="deltaTime"/> turns it into the S4
        /// degrees-per-second rate that <c>InputFrame.TurnSnap</c> integrates,
        /// matching the legacy adapter.
        /// </summary>
        public static RawInputSample FromKeyboardMouse(
            in KeyboardMouseSnapshot snapshot,
            float deltaTime,
            float mouseDegreesPerPixel,
            bool invertTurn = false)
        {
            float look = invertTurn ? -snapshot.MouseDeltaX : snapshot.MouseDeltaX;
            float lookDegreesPerSecond = deltaTime > 0f ? mouseDegreesPerPixel / deltaTime : 0f;
            return new RawInputSample(
                snapshot.Move,
                new Vector2f(look, 0f),
                lookDegreesPerSecond,
                snapshot.PrimaryDown,
                snapshot.SecondaryDown,
                snapshot.RecenterPressed,
                SnapDirection(snapshot.SnapDirection),
                snapshot.HotbarNextPressed,
                snapshot.HotbarPrevPressed);
        }

        /// <summary>
        /// Applies the coarse half-deflection threshold to a snap axis: -1/+1
        /// once the axis reaches <see cref="SnapAxisThreshold"/>, else 0.
        /// </summary>
        public static int SnapDirection(float axis)
        {
            if (float.IsNaN(axis))
            {
                return 0;
            }

            return MathF.Abs(axis) >= SnapAxisThreshold ? (axis > 0f ? 1 : -1) : 0;
        }

        private static int SnapDirection(int direction)
        {
            return direction > 0 ? 1 : direction < 0 ? -1 : 0;
        }
    }
}

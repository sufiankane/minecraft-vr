using System;
using Cubeglass.CoreMath;
using Cubeglass.Gameplay;

namespace Cubeglass.Unity.Input
{
    /// <summary>
    /// The input backend <see cref="UnityInputProvider"/> reads.
    /// </summary>
    public enum InputMode
    {
        /// <summary>Pick a backend from axis availability and a connected joystick.</summary>
        Auto = 0,

        /// <summary>Force the legacy gamepad mapping.</summary>
        Gamepad = 1,

        /// <summary>Force the keyboard/mouse mapping.</summary>
        KeyboardMouse = 2,
    }

    /// <summary>
    /// One device-agnostic raw sample: axes before deadzone/normalisation and
    /// buttons as physical "down now" flags. Deliberately free of UnityEngine
    /// so the whole mapping layer is EditMode-testable.
    /// </summary>
    /// <remarks>
    /// <see cref="Look"/> is in the device's own units and
    /// <see cref="LookDegreesPerUnit"/> converts one unit of <c>Look.X</c> to
    /// the S4 yaw rate in degrees per second: the gamepad adapter passes the
    /// stick deflection with <c>turnDegreesPerSecond</c>, the mouse adapter the
    /// raw per-frame delta with the degrees-per-pixel sensitivity divided by
    /// the frame delta. <c>Look.Y</c> is sampled for future pitch input but
    /// unused by S7 (3DoF).
    /// </remarks>
    public readonly struct RawInputSample
    {
        public RawInputSample(
            Vector2f move,
            Vector2f look,
            float lookDegreesPerUnit,
            bool primaryDown,
            bool secondaryDown,
            bool recenterDown,
            bool snapTurnDown,
            bool hotbarNextDown,
            bool hotbarPrevDown,
            TrackingQuality quality = TrackingQuality.None)
        {
            Move = move;
            Look = look;
            LookDegreesPerUnit = lookDegreesPerUnit;
            PrimaryDown = primaryDown;
            SecondaryDown = secondaryDown;
            RecenterDown = recenterDown;
            SnapTurnDown = snapTurnDown;
            HotbarNextDown = hotbarNextDown;
            HotbarPrevDown = hotbarPrevDown;
            Quality = quality;
        }

        /// <summary>Raw move axes, each normally in [-1, 1].</summary>
        public Vector2f Move { get; }

        /// <summary>Raw look axes, each normally in [-1, 1] for a stick.</summary>
        public Vector2f Look { get; }

        /// <summary>Degrees per second of yaw contributed by one unit of <see cref="Look"/>.X.</summary>
        public float LookDegreesPerUnit { get; }

        /// <summary>Break button physical state (gamepad X, mouse left).</summary>
        public bool PrimaryDown { get; }

        /// <summary>Place button physical state (gamepad A, mouse right).</summary>
        public bool SecondaryDown { get; }

        /// <summary>Recentre button physical state (gamepad Y, R).</summary>
        public bool RecenterDown { get; }

        /// <summary>Snap-turn button physical state (gamepad B, F).</summary>
        public bool SnapTurnDown { get; }

        /// <summary>Next-hotbar-slot button physical state (gamepad RB, E).</summary>
        public bool HotbarNextDown { get; }

        /// <summary>Previous-hotbar-slot button physical state (gamepad LB, Q).</summary>
        public bool HotbarPrevDown { get; }

        /// <summary>Tracking quality carried through to the frame.</summary>
        public TrackingQuality Quality { get; }

        /// <summary>The all-zero sample: neutral move/look and every button up.</summary>
        public static RawInputSample Neutral => default;
    }

    /// <summary>
    /// The pure mapping layer from <see cref="RawInputSample"/> to
    /// <see cref="InputFrame"/> (S7 Task 3). No UnityEngine types, so the
    /// mapping table and its edge semantics are pinned in EditMode tests.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Mapping table.</b> Both backends build one raw sample and this mapper
    /// turns it into the same <see cref="InputFrame"/>:
    /// </para>
    /// <list type="table">
    /// <item><term>Move</term><description>gamepad left stick / WASD, radial
    /// deadzone then clamped to the unit disc (S4 normalised move).</description></item>
    /// <item><term>TurnSnap</term><description>gamepad right stick X or mouse
    /// delta X scaled by <see cref="RawInputSample.LookDegreesPerUnit"/>, in
    /// degrees per second; device-right look maps to a negative rate because
    /// internal yaw is the negative of Unity yaw (ADR-0004/ADR-0008). A
    /// snap-turn press (B/F) is not part of the frame: it is exposed
    /// separately through <see cref="SnapPressed"/> and
    /// <see cref="ConsumeSnapPressed"/> (S7 Task 4a).</description></item>
    /// <item><term>Primary</term><description>gamepad X / mouse left through the
    /// S4 <see cref="ButtonState"/> edge machine (break, hold).</description></item>
    /// <item><term>Secondary</term><description>gamepad A / mouse right through
    /// the same edge machine (place, edge).</description></item>
    /// <item><term>RecenterPressed</term><description>gamepad Y / R, one frame
    /// on the press edge.</description></item>
    /// <item><term>HotbarDelta</term><description>gamepad RB/LB or E/Q, -1/0/+1
    /// only on the press edge; next wins when both press in one frame.</description></item>
    /// <item><term>Pointer, Quality</term><description>not produced here: the
    /// frame carries the raw sample's quality and a null pointer; GameplayBridge
    /// adds gaze targeting and the pose-derived quality.</description></item>
    /// </list>
    /// <para>
    /// <b>Allocation.</b> <see cref="Map"/> stores seven previous button flags and
    /// allocates nothing.
    /// </para>
    /// </remarks>
    public sealed class InputMapper
    {
        /// <summary>Default radial deadzone for the move axes.</summary>
        public const float DefaultMoveDeadzone = 0.15f;

        /// <summary>Default radial deadzone for the look axes.</summary>
        public const float DefaultLookDeadzone = 0.15f;

        private bool primaryDown;
        private bool secondaryDown;
        private bool recenterDown;
        private bool snapTurnDown;
        private bool hotbarNextDown;
        private bool hotbarPrevDown;

        public InputMapper(
            float moveDeadzone = DefaultMoveDeadzone,
            float lookDeadzone = DefaultLookDeadzone)
        {
            MoveDeadzone = SanitizeDeadzone(moveDeadzone, DefaultMoveDeadzone);
            LookDeadzone = SanitizeDeadzone(lookDeadzone, DefaultLookDeadzone);
        }

        /// <summary>Radial deadzone applied to <see cref="RawInputSample.Move"/>.</summary>
        public float MoveDeadzone { get; }

        /// <summary>Radial deadzone applied to <see cref="RawInputSample.Look"/>.</summary>
        public float LookDeadzone { get; }

        /// <summary>
        /// True when the latest <see cref="Map"/> call saw a snap-turn press
        /// edge; reading <see cref="ConsumeSnapPressed"/> clears it.
        /// </summary>
        public bool SnapPressed { get; private set; }

        /// <summary>
        /// Returns the snap-turn press edge of the latest <see cref="Map"/> call
        /// exactly once. Snap turn is not part of the S4
        /// <see cref="InputFrame"/>; the bridge consumes this additive channel.
        /// </summary>
        public bool ConsumeSnapPressed()
        {
            bool pressed = SnapPressed;
            SnapPressed = false;
            return pressed;
        }

        /// <summary>
        /// Maps one raw sample to a frame and advances the button-edge state.
        /// Called once per frame (the Unity adapter dedupes by frame count), so
        /// a press edge is reported on exactly one frame.
        /// </summary>
        public InputFrame Map(in RawInputSample raw)
        {
            Vector2f move = ClampToUnit(ApplyRadialDeadzone(raw.Move, MoveDeadzone));
            Vector2f look = ApplyRadialDeadzone(raw.Look, LookDeadzone);

            // Device-right look is a negative TurnSnap: positive internal yaw
            // turns toward -X, which is Unity left (ADR-0004/ADR-0008).
            float turn = -look.X * raw.LookDegreesPerUnit;
            SnapPressed = raw.SnapTurnDown && !snapTurnDown;

            int hotbarDelta = 0;
            if (raw.HotbarNextDown && !hotbarNextDown)
            {
                hotbarDelta = 1;
            }
            else if (raw.HotbarPrevDown && !hotbarPrevDown)
            {
                hotbarDelta = -1;
            }

            bool recenter = raw.RecenterDown && !recenterDown;
            ButtonState primary = Edge(raw.PrimaryDown, primaryDown);
            ButtonState secondary = Edge(raw.SecondaryDown, secondaryDown);

            primaryDown = raw.PrimaryDown;
            secondaryDown = raw.SecondaryDown;
            recenterDown = raw.RecenterDown;
            snapTurnDown = raw.SnapTurnDown;
            hotbarNextDown = raw.HotbarNextDown;
            hotbarPrevDown = raw.HotbarPrevDown;

            return new InputFrame(
                move,
                turn,
                recenter,
                null,
                primary,
                secondary,
                hotbarDelta,
                raw.Quality);
        }

        /// <summary>Forgets every button edge; the next down is a press again.</summary>
        public void Reset()
        {
            primaryDown = false;
            secondaryDown = false;
            recenterDown = false;
            snapTurnDown = false;
            hotbarNextDown = false;
            hotbarPrevDown = false;
            SnapPressed = false;
        }

        /// <summary>
        /// The S4 button edge machine: up->down is <see cref="ButtonState.Pressed"/>,
        /// held is <see cref="ButtonState.Held"/>, down->up is
        /// <see cref="ButtonState.Released"/>, steady up is
        /// <see cref="ButtonState.Up"/>.
        /// </summary>
        public static ButtonState Edge(bool down, bool wasDown)
        {
            if (down)
            {
                return wasDown ? ButtonState.Held : ButtonState.Pressed;
            }

            return wasDown ? ButtonState.Released : ButtonState.Up;
        }

        /// <summary>
        /// Hard radial deadzone: a vector whose magnitude is at or below
        /// <paramref name="deadzone"/> becomes zero, everything else passes
        /// through unchanged. NaN input and NaN deadzone are zero.
        /// </summary>
        public static Vector2f ApplyRadialDeadzone(Vector2f value, float deadzone)
        {
            double x = value.X;
            double y = value.Y;
            double magnitude = Math.Sqrt((x * x) + (y * y));
            if (!(magnitude > deadzone))
            {
                return Vector2f.Zero;
            }

            return value;
        }

        /// <summary>
        /// Clamps a move vector to the unit disc, preserving its direction:
        /// vectors at or inside the disc pass through, longer ones are scaled
        /// to length one. NaN input is zero.
        /// </summary>
        public static Vector2f ClampToUnit(Vector2f value)
        {
            if (float.IsNaN(value.X) || float.IsNaN(value.Y))
            {
                return Vector2f.Zero;
            }

            double x = value.X;
            double y = value.Y;
            double lengthSquared = (x * x) + (y * y);
            if (!(lengthSquared > 1.0))
            {
                return value;
            }

            double inverse = 1.0 / Math.Sqrt(lengthSquared);
            return new Vector2f((float)(x * inverse), (float)(y * inverse));
        }

        private static float SanitizeDeadzone(float value, float fallback)
        {
            if (float.IsNaN(value))
            {
                return fallback;
            }

            if (value < 0f)
            {
                return 0f;
            }

            return value > 1f ? 1f : value;
        }
    }
}

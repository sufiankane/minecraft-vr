using Cubeglass.CoreMath;
using Cubeglass.Gameplay;
using NUnit.Framework;

namespace Cubeglass.Unity.Input.Tests
{
    /// <summary>
    /// TD-013: the Input System adapter's pure mapping produces the same
    /// frames as the legacy adapter for equivalent gamepad and keyboard/mouse
    /// actions, so switching backends cannot change gameplay. The snapshots are
    /// the exact shape <see cref="InputSystemProvider"/> reads from the Input
    /// System devices; the device read itself is a thin projection, so the
    /// equivalence contract lives here.
    /// </summary>
    public class InputSystemMappingTests
    {
        private const float Tolerance = 1e-5f;
        private const float TurnDegreesPerSecond = 90f;
        private const float MouseDegreesPerPixel = 0.2f;
        private const float DeltaTime = 1f / 60f;

        [Test]
        public void NeutralGamepadAndKeyboardSnapshotsMapToNeutralFrames()
        {
            InputFrame pad = new InputMapper().Map(
                InputSystemMapping.FromGamepad(default, TurnDegreesPerSecond));
            Assert.AreEqual(0f, pad.Move.X, Tolerance);
            Assert.AreEqual(0f, pad.Move.Y, Tolerance);
            Assert.AreEqual(0f, pad.TurnSnap, Tolerance);
            Assert.AreEqual(ButtonState.Up, pad.Primary);

            InputFrame keys = new InputMapper().Map(
                InputSystemMapping.FromKeyboardMouse(default, DeltaTime, MouseDegreesPerPixel));
            Assert.AreEqual(0f, keys.Move.X, Tolerance);
            Assert.AreEqual(0f, keys.Move.Y, Tolerance);
            Assert.AreEqual(0f, keys.TurnSnap, Tolerance);
            Assert.AreEqual(ButtonState.Up, keys.Primary);
        }

        [Test]
        public void EquivalentInputSystemGamepadAndKeyboardActionsProduceIdenticalFrames()
        {
            AssertEquivalent(
                "move forward full",
                Gamepad(move: new Vector2f(0f, 1f)),
                Keyboard(move: new Vector2f(0f, 1f)));

            AssertEquivalent(
                "move diagonal full",
                Gamepad(move: new Vector2f(1f, 1f)),
                Keyboard(move: new Vector2f(1f, 1f)));

            AssertEquivalent(
                "continuous turn full rate",
                Gamepad(stickTurn: 1f),
                Keyboard(mouseDelta: TurnDegreesPerSecond * DeltaTime / MouseDegreesPerPixel));

            AssertEquivalent(
                "break press",
                Gamepad(primaryDown: true),
                Keyboard(primaryDown: true));

            AssertEquivalent(
                "place press",
                Gamepad(secondaryDown: true),
                Keyboard(secondaryDown: true));

            AssertEquivalent(
                "recentre press",
                Gamepad(recenterDown: true),
                Keyboard(recenterDown: true));

            AssertEquivalent(
                "snap turn right press",
                Gamepad(snapDirection: 1),
                Keyboard(snapDirection: 1));

            AssertEquivalent(
                "snap turn left press",
                Gamepad(snapDirection: -1),
                Keyboard(snapDirection: -1));

            AssertEquivalent(
                "hotbar next press",
                Gamepad(hotbarNextDown: true),
                Keyboard(hotbarNextDown: true));

            AssertEquivalent(
                "hotbar previous press",
                Gamepad(hotbarPrevDown: true),
                Keyboard(hotbarPrevDown: true));
        }

        [Test]
        public void InvertTurnFlipsBothAdaptersTheSameWay()
        {
            RawInputSample pad = InputSystemMapping.FromGamepad(
                Gamepad(stickTurn: 1f), TurnDegreesPerSecond, invertTurn: true);
            RawInputSample keys = InputSystemMapping.FromKeyboardMouse(
                Keyboard(mouseDelta: TurnDegreesPerSecond * DeltaTime / MouseDegreesPerPixel),
                DeltaTime,
                MouseDegreesPerPixel,
                invertTurn: true);
            RawInputSample padDefault = InputSystemMapping.FromGamepad(
                Gamepad(stickTurn: 1f), TurnDegreesPerSecond);
            RawInputSample keysDefault = InputSystemMapping.FromKeyboardMouse(
                Keyboard(mouseDelta: TurnDegreesPerSecond * DeltaTime / MouseDegreesPerPixel),
                DeltaTime,
                MouseDegreesPerPixel);

            // Device-right look maps to a negative internal yaw rate; invert
            // flips both raw adapters to the same positive rate.
            Assert.AreEqual(
                -TurnDegreesPerSecond,
                new InputMapper().Map(padDefault).TurnSnap,
                Tolerance,
                "pad default");
            Assert.AreEqual(
                -TurnDegreesPerSecond,
                new InputMapper().Map(keysDefault).TurnSnap,
                Tolerance,
                "keys default");
            Assert.AreEqual(
                TurnDegreesPerSecond,
                new InputMapper().Map(pad).TurnSnap,
                Tolerance,
                "pad inverted");
            Assert.AreEqual(
                TurnDegreesPerSecond,
                new InputMapper().Map(keys).TurnSnap,
                Tolerance,
                "keys inverted");
        }

        [Test]
        public void SwapButtonsSwapsGamepadPrimaryAndSecondary()
        {
            RawInputSample raw = InputSystemMapping.FromGamepad(
                Gamepad(primaryDown: true), TurnDegreesPerSecond, swapButtons: true);
            Assert.IsFalse(raw.PrimaryDown, "primary becomes the secondary button");
            Assert.IsTrue(raw.SecondaryDown, "the held primary moved to secondary");
        }

        [Test]
        public void SnapAxisThresholdIsCoarseAndNaNNeutral()
        {
            Assert.AreEqual(0, InputSystemMapping.SnapDirection(0.4f), "below threshold");
            Assert.AreEqual(1, InputSystemMapping.SnapDirection(0.5f), "at threshold right");
            Assert.AreEqual(-1, InputSystemMapping.SnapDirection(-0.6f), "left");
            Assert.AreEqual(0, InputSystemMapping.SnapDirection(float.NaN), "NaN is neutral");
            Assert.AreEqual(0, InputSystemMapping.SnapDirection(0), "an already-discrete zero passes through");
        }

        [Test]
        public void SnapDirectionSnapshotNormalisesSigns()
        {
            RawInputSample raw = InputSystemMapping.FromGamepad(
                Gamepad(snapDirection: -1), TurnDegreesPerSecond);
            Assert.AreEqual(-1, raw.SnapDirection, "snapshot direction is already discrete");
        }

        private static void AssertEquivalent(string action, in GamepadSnapshot pad, in KeyboardMouseSnapshot keys)
        {
            var padMapper = new InputMapper();
            var keyMapper = new InputMapper();
            InputFrame padFrame = padMapper.Map(InputSystemMapping.FromGamepad(pad, TurnDegreesPerSecond));
            InputFrame keyFrame = keyMapper.Map(
                InputSystemMapping.FromKeyboardMouse(keys, DeltaTime, MouseDegreesPerPixel));

            Assert.AreEqual(padFrame.Move.X, keyFrame.Move.X, Tolerance, action + " (move x)");
            Assert.AreEqual(padFrame.Move.Y, keyFrame.Move.Y, Tolerance, action + " (move y)");
            Assert.AreEqual(padFrame.TurnSnap, keyFrame.TurnSnap, Tolerance, action + " (turn)");
            Assert.AreEqual(padFrame.RecenterPressed, keyFrame.RecenterPressed, action + " (recentre)");
            Assert.AreEqual(padFrame.Primary, keyFrame.Primary, action + " (primary)");
            Assert.AreEqual(padFrame.Secondary, keyFrame.Secondary, action + " (secondary)");
            Assert.AreEqual(padFrame.HotbarDelta, keyFrame.HotbarDelta, action + " (hotbar)");
            Assert.AreEqual(
                padMapper.ConsumeSnapDirection(),
                keyMapper.ConsumeSnapDirection(),
                action + " (snap edge)");
        }

        private static GamepadSnapshot Gamepad(
            Vector2f move = default,
            float stickTurn = 0f,
            bool primaryDown = false,
            bool secondaryDown = false,
            bool recenterDown = false,
            int snapDirection = 0,
            bool hotbarNextDown = false,
            bool hotbarPrevDown = false)
        {
            return new GamepadSnapshot(
                move,
                new Vector2f(stickTurn, 0f),
                primaryDown,
                secondaryDown,
                recenterDown,
                snapDirection,
                hotbarNextDown,
                hotbarPrevDown);
        }

        private static KeyboardMouseSnapshot Keyboard(
            Vector2f move = default,
            float mouseDelta = 0f,
            bool primaryDown = false,
            bool secondaryDown = false,
            bool recenterDown = false,
            int snapDirection = 0,
            bool hotbarNextDown = false,
            bool hotbarPrevDown = false)
        {
            return new KeyboardMouseSnapshot(
                move,
                mouseDelta,
                primaryDown,
                secondaryDown,
                recenterDown,
                snapDirection,
                hotbarNextDown,
                hotbarPrevDown);
        }
    }
}

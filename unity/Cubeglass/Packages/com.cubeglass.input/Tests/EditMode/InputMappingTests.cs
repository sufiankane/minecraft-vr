using Cubeglass.CoreMath;
using Cubeglass.Gameplay;
using NUnit.Framework;

namespace Cubeglass.Unity.Input.Tests
{
    /// <summary>
    /// Pins the pure <see cref="InputMapper"/> mapping table (S7 Task 3,
    /// reworked in Task 4a): equivalent gamepad and keyboard/mouse actions
    /// produce the identical <see cref="InputFrame"/>, the S4 button edges
    /// survive, <c>TurnSnap</c> is a degrees-per-second yaw rate, and the snap
    /// press is a one-shot signal outside the frame.
    /// </summary>
    public class InputMappingTests
    {
        private const float Tolerance = 1e-5f;
        private const float TurnDegreesPerSecond = 90f;
        private const float MouseDegreesPerPixel = 0.2f;
        private const float DeltaTime = 1f / 60f;

        [Test]
        public void NeutralSampleMapsToNeutralFrame()
        {
            var mapper = new InputMapper();
            InputFrame frame = mapper.Map(RawInputSample.Neutral);

            Assert.AreEqual(0f, frame.Move.X, Tolerance);
            Assert.AreEqual(0f, frame.Move.Y, Tolerance);
            Assert.AreEqual(0f, frame.TurnSnap, Tolerance);
            Assert.IsFalse(frame.RecenterPressed);
            Assert.IsFalse(frame.Pointer.HasValue);
            Assert.AreEqual(ButtonState.Up, frame.Primary);
            Assert.AreEqual(ButtonState.Up, frame.Secondary);
            Assert.AreEqual(0, frame.HotbarDelta);
            Assert.AreEqual(TrackingQuality.None, frame.Quality);
        }

        [Test]
        public void EquivalentGamepadAndKeyboardActionsProduceIdenticalFrames()
        {
            // Every row is one action expressed the way each adapter would
            // sample it: the raw values differ (stick vs WASD, stick rate vs
            // mouse pixels) but the mapped frames must be identical.
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
                "snap turn press",
                Gamepad(snapTurnDown: true),
                Keyboard(snapTurnDown: true));

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
        public void ButtonEdgesFollowTheS4Sequence()
        {
            var mapper = new InputMapper();
            var primary = new[] { ButtonState.Pressed, ButtonState.Held, ButtonState.Released, ButtonState.Up, ButtonState.Up };
            var secondary = new[] { ButtonState.Pressed, ButtonState.Held, ButtonState.Released, ButtonState.Up, ButtonState.Up };

            for (int frame = 0; frame < primary.Length; frame++)
            {
                bool down = frame == 0 || frame == 1;
                InputFrame mapped = mapper.Map(new RawInputSample(
                    Vector2f.Zero,
                    Vector2f.Zero,
                    0f,
                    down,
                    down,
                    false,
                    false,
                    false,
                    false));

                Assert.AreEqual(primary[frame], mapped.Primary, "primary frame {0}", frame);
                Assert.AreEqual(secondary[frame], mapped.Secondary, "secondary frame {0}", frame);
            }
        }

        [Test]
        public void SnapTurnIsASingleOneShotEdgeOutsideTheFrame()
        {
            var mapper = new InputMapper(lookDeadzone: 0f);
            InputFrame pressed = mapper.Map(Sample(snapTurnDown: true));
            Assert.IsTrue(mapper.SnapPressed, "the press edge is exposed");
            Assert.IsTrue(mapper.ConsumeSnapPressed(), "the first read consumes it");
            Assert.IsFalse(mapper.ConsumeSnapPressed(), "the signal is one-shot");
            Assert.AreEqual(0f, pressed.TurnSnap, Tolerance, "snap is not a TurnSnap rate");

            InputFrame held = mapper.Map(Sample(snapTurnDown: true));
            Assert.IsFalse(mapper.SnapPressed, "hold does not repeat");
            Assert.AreEqual(0f, held.TurnSnap, Tolerance);

            InputFrame released = mapper.Map(Sample());
            Assert.IsFalse(mapper.SnapPressed, "release re-arms");

            mapper.Map(Sample(snapTurnDown: true));
            Assert.IsTrue(mapper.ConsumeSnapPressed(), "a second press snaps again");
        }

        [Test]
        public void SnapEdgeDoesNotDisturbContinuousTurn()
        {
            var mapper = new InputMapper(lookDeadzone: 0f);
            InputFrame frame = mapper.Map(new RawInputSample(
                Vector2f.Zero,
                new Vector2f(1f, 0f),
                TurnDegreesPerSecond,
                false,
                false,
                false,
                true,
                false,
                false));

            Assert.IsTrue(mapper.SnapPressed, "the snap edge lands");
            Assert.AreEqual(
                -TurnDegreesPerSecond,
                frame.TurnSnap,
                Tolerance,
                "device-right look stays a continuous negative yaw rate (ADR-0004)");
        }

        [Test]
        public void HotbarDeltaIsSingleEdgeAndNextWins()
        {
            var mapper = new InputMapper(lookDeadzone: 0f);
            Assert.AreEqual(1, mapper.Map(Sample(hotbarNextDown: true)).HotbarDelta);
            Assert.AreEqual(0, mapper.Map(Sample(hotbarNextDown: true)).HotbarDelta, "hold does not repeat");

            Assert.AreEqual(-1, mapper.Map(Sample(hotbarPrevDown: true)).HotbarDelta);
            Assert.AreEqual(0, mapper.Map(Sample(hotbarPrevDown: true)).HotbarDelta);
            Assert.AreEqual(0, mapper.Map(Sample()).HotbarDelta, "release re-arms");

            Assert.AreEqual(1, mapper.Map(Sample(hotbarNextDown: true, hotbarPrevDown: true)).HotbarDelta, "next wins the same-frame tie");
            Assert.AreEqual(0, mapper.Map(Sample(hotbarNextDown: true, hotbarPrevDown: true)).HotbarDelta, "both still-held keys do not repeat");
        }

        [Test]
        public void RecentreIsASingleEdge()
        {
            var mapper = new InputMapper(lookDeadzone: 0f);
            Assert.IsTrue(mapper.Map(Sample(recenterDown: true)).RecenterPressed);
            Assert.IsFalse(mapper.Map(Sample(recenterDown: true)).RecenterPressed, "hold does not repeat");
            Assert.IsFalse(mapper.Map(Sample()).RecenterPressed);
            Assert.IsTrue(mapper.Map(Sample(recenterDown: true)).RecenterPressed);
        }

        [Test]
        public void MoveDeadzoneRejectsNoiseAndClampsToTheUnitDisc()
        {
            Vector2f noisy = InputMapper.ClampToUnit(InputMapper.ApplyRadialDeadzone(new Vector2f(0.1f, 0.1f), 0.15f));
            Assert.AreEqual(Vector2f.Zero, noisy, "below the deadzone is zero");

            Vector2f exact = InputMapper.ApplyRadialDeadzone(new Vector2f(0.2f, 0f), 0.15f);
            Assert.AreEqual(0.2f, exact.X, Tolerance, "above the deadzone passes through");
            Assert.AreEqual(0f, exact.Y, Tolerance);

            Vector2f longDiagonal = InputMapper.ClampToUnit(new Vector2f(3f, 4f));
            Assert.AreEqual(0.6f, longDiagonal.X, Tolerance);
            Assert.AreEqual(0.8f, longDiagonal.Y, Tolerance);

            Vector2f inside = InputMapper.ClampToUnit(new Vector2f(0.5f, 0.5f));
            Assert.AreEqual(0.5f, inside.X, Tolerance, "inside the disc is unchanged");
            Assert.AreEqual(0.5f, inside.Y, Tolerance);

            InputFrame mapped = new InputMapper().Map(new RawInputSample(
                new Vector2f(1f, 1f),
                Vector2f.Zero,
                0f,
                false,
                false,
                false,
                false,
                false,
                false));
            float length = (float)System.Math.Sqrt((mapped.Move.X * mapped.Move.X) + (mapped.Move.Y * mapped.Move.Y));
            Assert.AreEqual(1f, length, Tolerance, "diagonal move is normalised to the unit disc");
        }

        [Test]
        public void LookDeadzoneGatesContinuousTurn()
        {
            var mapper = new InputMapper(lookDeadzone: 0.15f);
            InputFrame below = mapper.Map(new RawInputSample(
                Vector2f.Zero,
                new Vector2f(0.1f, 0f),
                90f,
                false,
                false,
                false,
                false,
                false,
                false));
            Assert.AreEqual(0f, below.TurnSnap, Tolerance, "look noise is deadzoned");

            InputFrame above = mapper.Map(new RawInputSample(
                Vector2f.Zero,
                new Vector2f(0.5f, 0f),
                90f,
                false,
                false,
                false,
                false,
                false,
                false));
            Assert.AreEqual(
                -45f,
                above.TurnSnap,
                Tolerance,
                "above the deadzone maps to the negative degrees-per-second yaw rate");
        }

        [Test]
        public void QualityPassesThroughAndPointerStaysNull()
        {
            var mapper = new InputMapper();
            InputFrame frame = mapper.Map(new RawInputSample(
                Vector2f.Zero,
                Vector2f.Zero,
                0f,
                false,
                false,
                false,
                false,
                false,
                false,
                TrackingQuality.Degraded));

            Assert.AreEqual(TrackingQuality.Degraded, frame.Quality);
            Assert.IsFalse(frame.Pointer.HasValue);
        }

        [Test]
        public void ResetReArmsEveryEdge()
        {
            var mapper = new InputMapper(lookDeadzone: 0f);
            Assert.AreEqual(ButtonState.Pressed, mapper.Map(Sample(primaryDown: true)).Primary);
            Assert.AreEqual(ButtonState.Held, mapper.Map(Sample(primaryDown: true)).Primary);

            mapper.Reset();
            Assert.AreEqual(ButtonState.Pressed, mapper.Map(Sample(primaryDown: true)).Primary, "a reset turns held into a fresh press");
        }

        private static void AssertEquivalent(string action, RawInputSample gamepad, RawInputSample keyboard)
        {
            var gamepadMapper = new InputMapper();
            var keyboardMapper = new InputMapper();
            InputFrame gamepadFrame = gamepadMapper.Map(gamepad);
            InputFrame keyboardFrame = keyboardMapper.Map(keyboard);

            Assert.AreEqual(gamepadFrame.Move.X, keyboardFrame.Move.X, Tolerance, "{0}: move X", action);
            Assert.AreEqual(gamepadFrame.Move.Y, keyboardFrame.Move.Y, Tolerance, "{0}: move Y", action);
            Assert.AreEqual(gamepadFrame.TurnSnap, keyboardFrame.TurnSnap, Tolerance, "{0}: turn", action);
            Assert.AreEqual(gamepadFrame.RecenterPressed, keyboardFrame.RecenterPressed, "{0}: recentre", action);
            Assert.AreEqual(gamepadFrame.Primary, keyboardFrame.Primary, "{0}: primary", action);
            Assert.AreEqual(gamepadFrame.Secondary, keyboardFrame.Secondary, "{0}: secondary", action);
            Assert.AreEqual(gamepadFrame.HotbarDelta, keyboardFrame.HotbarDelta, "{0}: hotbar", action);
            Assert.AreEqual(gamepadFrame.Quality, keyboardFrame.Quality, "{0}: quality", action);
            Assert.AreEqual(gamepadMapper.SnapPressed, keyboardMapper.SnapPressed, "{0}: snap edge", action);
        }

        private static RawInputSample Gamepad(
            Vector2f move = default,
            float stickTurn = 0f,
            bool primaryDown = false,
            bool secondaryDown = false,
            bool recenterDown = false,
            bool snapTurnDown = false,
            bool hotbarNextDown = false,
            bool hotbarPrevDown = false)
        {
            return new RawInputSample(
                move,
                new Vector2f(stickTurn, 0f),
                TurnDegreesPerSecond,
                primaryDown,
                secondaryDown,
                recenterDown,
                snapTurnDown,
                hotbarNextDown,
                hotbarPrevDown);
        }

        private static RawInputSample Keyboard(
            Vector2f move = default,
            float mouseDelta = 0f,
            bool primaryDown = false,
            bool secondaryDown = false,
            bool recenterDown = false,
            bool snapTurnDown = false,
            bool hotbarNextDown = false,
            bool hotbarPrevDown = false)
        {
            return new RawInputSample(
                move,
                new Vector2f(mouseDelta, 0f),
                MouseDegreesPerPixel / DeltaTime,
                primaryDown,
                secondaryDown,
                recenterDown,
                snapTurnDown,
                hotbarNextDown,
                hotbarPrevDown);
        }

        private static RawInputSample Sample(
            bool primaryDown = false,
            bool secondaryDown = false,
            bool recenterDown = false,
            bool snapTurnDown = false,
            bool hotbarNextDown = false,
            bool hotbarPrevDown = false)
        {
            return new RawInputSample(
                Vector2f.Zero,
                Vector2f.Zero,
                0f,
                primaryDown,
                secondaryDown,
                recenterDown,
                snapTurnDown,
                hotbarNextDown,
                hotbarPrevDown);
        }
    }
}

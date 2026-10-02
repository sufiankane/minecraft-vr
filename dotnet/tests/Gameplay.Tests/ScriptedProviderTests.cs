using System;
using System.Collections.Generic;
using Cubeglass.CoreMath;
using Cubeglass.Voxel;
using NUnit.Framework;

namespace Cubeglass.Gameplay.Tests
{
    // Input contract tests: the neutral default frame, the scripted provider's
    // sampling rule and the support types dossier 5.11 names but never defines
    // (ADR-0008).
    [TestFixture]
    public sealed class ScriptedInputProviderTests
    {
        [Test]
        public void EmptyTimelineSamplesTheNeutralFrame()
        {
            var provider = new ScriptedInputProvider(Array.Empty<(double, InputFrame)>());

            Assert.That(provider.Count, Is.EqualTo(0));
            AssertNeutral(provider.Sample(-1.0));
            AssertNeutral(provider.Sample(0.0));
            AssertNeutral(provider.Sample(double.MaxValue));
        }

        [Test]
        public void EarlierThanTheFirstFrameSamplesTheFirstFrame()
        {
            var provider = new ScriptedInputProvider(new[] { (1.0, Frame(1f)), (2.0, Frame(2f)) });

            Assert.That(provider.Sample(0.0).Move.X, Is.EqualTo(1f));
            Assert.That(provider.Sample(0.999).Move.X, Is.EqualTo(1f));
        }

        [Test]
        public void ExactTimesSampleThatFrame()
        {
            var provider = new ScriptedInputProvider(new[] { (1.0, Frame(1f)), (2.0, Frame(2f)) });

            Assert.That(provider.Sample(1.0).Move.X, Is.EqualTo(1f));
            Assert.That(provider.Sample(2.0).Move.X, Is.EqualTo(2f));
        }

        [Test]
        public void BetweenTimesSamplesTheMostRecentEarlierFrame()
        {
            var provider = new ScriptedInputProvider(
                new[] { (1.0, Frame(1f)), (2.0, Frame(2f)), (4.0, Frame(4f)) });

            Assert.That(provider.Sample(1.5).Move.X, Is.EqualTo(1f));
            Assert.That(provider.Sample(3.999).Move.X, Is.EqualTo(2f));
        }

        [Test]
        public void AfterTheLastFrameSamplesTheLastFrame()
        {
            var provider = new ScriptedInputProvider(new[] { (1.0, Frame(1f)), (2.0, Frame(2f)) });

            Assert.That(provider.Sample(1000.0).Move.X, Is.EqualTo(2f));
        }

        [Test]
        public void CountReportsTheTimelineLength()
        {
            var provider = new ScriptedInputProvider(new[] { (0.0, Frame(1f)), (1.0, Frame(2f)) });

            Assert.That(provider.Count, Is.EqualTo(2));
        }

        [Test]
        public void NullTimelineIsRejected()
        {
            Assert.Throws<ArgumentNullException>(() => new ScriptedInputProvider(null!));
        }

        [Test]
        public void DuplicateOrDecreasingTimesAreRejected()
        {
            Assert.Throws<ArgumentException>(
                () => new ScriptedInputProvider(new[] { (1.0, Frame(1f)), (1.0, Frame(2f)) }));
            Assert.Throws<ArgumentException>(
                () => new ScriptedInputProvider(new[] { (2.0, Frame(1f)), (1.0, Frame(2f)) }));
        }

        [Test]
        public void NonFiniteTimesAreRejected()
        {
            Assert.Throws<ArgumentException>(
                () => new ScriptedInputProvider(new[] { (double.NaN, Frame(1f)) }));
            Assert.Throws<ArgumentException>(
                () => new ScriptedInputProvider(new[] { (double.PositiveInfinity, Frame(1f)) }));
            Assert.Throws<ArgumentException>(
                () => new ScriptedInputProvider(new[] { (double.NegativeInfinity, Frame(1f)) }));
        }

        [Test]
        public void SamplingIsIndependentOfLaterSourceMutation()
        {
            var source = new List<(double, InputFrame)> { (0.0, Frame(1f)), (1.0, Frame(2f)) };
            var provider = new ScriptedInputProvider(source);

            source[0] = (5.0, Frame(9f));
            source.Add((10.0, Frame(9f)));

            Assert.That(provider.Count, Is.EqualTo(2));
            Assert.That(provider.Sample(0.5).Move.X, Is.EqualTo(1f));
            Assert.That(provider.Sample(1.5).Move.X, Is.EqualTo(2f));
        }

        [Test]
        public void RepeatedSamplesAreIdentical()
        {
            var provider = new ScriptedInputProvider(
                new[] { (0.0, Frame(1f, ButtonState.Held)), (2.0, Frame(3f)) });

            InputFrame first = provider.Sample(1.0);
            InputFrame second = provider.Sample(1.0);

            Assert.That(second.Move.X, Is.EqualTo(first.Move.X));
            Assert.That(second.Primary, Is.EqualTo(first.Primary));
            Assert.That(second.Quality, Is.EqualTo(first.Quality));
        }

        private static InputFrame Frame(float moveX, ButtonState primary = ButtonState.Up)
        {
            return new InputFrame(
                new Vector2f(moveX, 0f), 0f, false, null, primary, ButtonState.Up, 0, TrackingQuality.Good);
        }

        private static void AssertNeutral(InputFrame frame)
        {
            Assert.That(frame.Move, Is.EqualTo(Vector2f.Zero));
            Assert.That(frame.TurnSnap, Is.EqualTo(0f));
            Assert.That(frame.RecenterPressed, Is.False);
            Assert.That(frame.Pointer.HasValue, Is.False);
            Assert.That(frame.Primary, Is.EqualTo(ButtonState.Up));
            Assert.That(frame.Secondary, Is.EqualTo(ButtonState.Up));
            Assert.That(frame.HotbarDelta, Is.EqualTo(0));
            Assert.That(frame.Quality, Is.EqualTo(TrackingQuality.None));
        }
    }

    [TestFixture]
    public sealed class InputFrameTests
    {
        [Test]
        public void DefaultIsTheNeutralFrame()
        {
            InputFrame frame = default;

            Assert.That(frame.Move, Is.EqualTo(Vector2f.Zero));
            Assert.That(frame.TurnSnap, Is.EqualTo(0f));
            Assert.That(frame.RecenterPressed, Is.False);
            Assert.That(frame.Pointer.HasValue, Is.False);
            Assert.That(frame.Primary, Is.EqualTo(ButtonState.Up));
            Assert.That(frame.Secondary, Is.EqualTo(ButtonState.Up));
            Assert.That(frame.HotbarDelta, Is.EqualTo(0));
            Assert.That(frame.Quality, Is.EqualTo(TrackingQuality.None));

            Assert.That(InputFrame.Neutral.Move, Is.EqualTo(frame.Move));
            Assert.That(InputFrame.Neutral.Quality, Is.EqualTo(frame.Quality));
        }

        [Test]
        public void ConstructorPreservesEveryField()
        {
            var pointer = new PointerRay(new Vec3(1, 2, 3), new Vec3(0, -1, 0));
            var frame = new InputFrame(
                new Vector2f(0.25f, -0.5f),
                12.5f,
                true,
                pointer,
                ButtonState.Pressed,
                ButtonState.Released,
                -1,
                TrackingQuality.Good);

            Assert.That(frame.Move.X, Is.EqualTo(0.25f));
            Assert.That(frame.Move.Y, Is.EqualTo(-0.5f));
            Assert.That(frame.TurnSnap, Is.EqualTo(12.5f));
            Assert.That(frame.RecenterPressed, Is.True);
            Assert.That(frame.Pointer.HasValue, Is.True);
            PointerRay pointerValue = frame.Pointer.GetValueOrDefault();
            Assert.That(pointerValue.Origin, Is.EqualTo(new Vec3(1, 2, 3)));
            Assert.That(pointerValue.Direction, Is.EqualTo(new Vec3(0, -1, 0)));
            Assert.That(frame.Primary, Is.EqualTo(ButtonState.Pressed));
            Assert.That(frame.Secondary, Is.EqualTo(ButtonState.Released));
            Assert.That(frame.HotbarDelta, Is.EqualTo(-1));
            Assert.That(frame.Quality, Is.EqualTo(TrackingQuality.Good));
        }
    }

    [TestFixture]
    public sealed class PointerRayTests
    {
        [Test]
        public void TryToRayNormalizesTheDirectionAndKeepsTheOrigin()
        {
            var pointer = new PointerRay(new Vec3(1, 2, 3), new Vec3(3, 4, 0));

            Assert.That(pointer.TryToRay(out Ray ray), Is.True);
            Assert.That(ray.Origin, Is.EqualTo(new Vec3(1, 2, 3)));
            Assert.That(ray.Direction.NearlyEquals(new Vec3(0.6, 0.8, 0.0), 1e-12), Is.True);
            Assert.That(Math.Abs(Vec3.Length(ray.Direction) - 1.0) < 1e-12, Is.True);
        }

        [Test]
        public void TryToRayKeepsAnAlreadyUnitDirection()
        {
            var pointer = new PointerRay(new Vec3(0, 0, 0), new Vec3(0, 0, -1));

            Assert.That(pointer.TryToRay(out Ray ray), Is.True);
            Assert.That(ray.Direction.NearlyEquals(new Vec3(0, 0, -1), 1e-12), Is.True);
        }

        [Test]
        public void TryToRayRejectsDegenerateAndNonFiniteRays()
        {
            Assert.False(new PointerRay(new Vec3(0, 0, 0), new Vec3(0, 0, 0)).TryToRay(out _));
            Assert.False(new PointerRay(new Vec3(0, 0, 0), new Vec3(double.NaN, 0, 0)).TryToRay(out _));
            Assert.False(new PointerRay(new Vec3(0, 0, 0), new Vec3(0, double.PositiveInfinity, 0)).TryToRay(out _));
            Assert.False(new PointerRay(new Vec3(double.NaN, 0, 0), new Vec3(0, 0, -1)).TryToRay(out _));
            Assert.False(new PointerRay(new Vec3(0, 0, double.NegativeInfinity), new Vec3(0, 0, -1)).TryToRay(out _));

            // A direction so small that its squared length underflows to zero is
            // degenerate, as is one whose squared length overflows to infinity.
            Assert.False(new PointerRay(new Vec3(0, 0, 0), new Vec3(1e-320, 0, 0)).TryToRay(out _));
            Assert.False(new PointerRay(new Vec3(0, 0, 0), new Vec3(1e308, 0, 0)).TryToRay(out _));
        }

        [Test]
        public void RejectedTryToRayLeavesTheDefaultRay()
        {
            bool converted = new PointerRay(new Vec3(0, 0, 0), new Vec3(0, 0, 0)).TryToRay(out Ray ray);

            Assert.That(converted, Is.False);
            Assert.That(ray.Origin, Is.EqualTo(new Vec3(0, 0, 0)));
            Assert.That(ray.Direction, Is.EqualTo(new Vec3(0, 0, 0)));
        }
    }

    [TestFixture]
    public sealed class SupportTypeTests
    {
        [Test]
        public void GestureOutputDefaultsAreOff()
        {
            GestureOutput output = default;

            Assert.That(output.Pinching, Is.False);
            Assert.That(output.Fist, Is.False);
            Assert.That(output.PaletteFlick, Is.False);
            Assert.That(output.PinchStrength, Is.EqualTo(0f));
        }

        [Test]
        public void GestureOutputPreservesValues()
        {
            var output = new GestureOutput(true, true, true, 0.75f);

            Assert.That(output.Pinching, Is.True);
            Assert.That(output.Fist, Is.True);
            Assert.That(output.PaletteFlick, Is.True);
            Assert.That(output.PinchStrength, Is.EqualTo(0.75f));
        }

        [Test]
        public void InteractionResultDefaultsAreIdle()
        {
            InteractionResult result = default;

            Assert.That(result.BreakInProgress, Is.False);
            Assert.That(result.BreakProgress, Is.EqualTo(0f));
            Assert.That(result.Edited, Is.False);
            Assert.That(result.Target.HasValue, Is.False);
        }

        [Test]
        public void InteractionResultPreservesValues()
        {
            var result = new InteractionResult(true, 0.5f, true, new Int3(1, 2, 3));

            Assert.That(result.BreakInProgress, Is.True);
            Assert.That(result.BreakProgress, Is.EqualTo(0.5f));
            Assert.That(result.Edited, Is.True);
            Assert.That(result.Target.GetValueOrDefault(), Is.EqualTo(new Int3(1, 2, 3)));
        }

        [Test]
        public void HandsInputDefaultsAreUntracked()
        {
            HandsInput hands = default;

            Assert.That(hands.Tracked, Is.False);
            Assert.That(hands.Left.HasValue, Is.False);
            Assert.That(hands.Right.HasValue, Is.False);
        }

        [Test]
        public void HandsInputPreservesHandFrames()
        {
            var left = new HandFrame(
                new Vector3f(1, 2, 3),
                new Vector3f(4, 5, 6),
                new Vector3f(7, 8, 9),
                new Vector3f(10, 11, 12),
                new Vector3f(13, 14, 15),
                new Vector3f(16, 17, 18));
            var hands = new HandsInput(true, left, null);

            Assert.That(hands.Tracked, Is.True);
            Assert.That(hands.Left.HasValue, Is.True);
            Assert.That(hands.Left.GetValueOrDefault().Wrist, Is.EqualTo(new Vector3f(1, 2, 3)));
            Assert.That(hands.Left.GetValueOrDefault().ThumbTip, Is.EqualTo(new Vector3f(4, 5, 6)));
            Assert.That(hands.Left.GetValueOrDefault().IndexTip, Is.EqualTo(new Vector3f(7, 8, 9)));
            Assert.That(hands.Left.GetValueOrDefault().MiddleTip, Is.EqualTo(new Vector3f(10, 11, 12)));
            Assert.That(hands.Left.GetValueOrDefault().RingTip, Is.EqualTo(new Vector3f(13, 14, 15)));
            Assert.That(hands.Left.GetValueOrDefault().LittleTip, Is.EqualTo(new Vector3f(16, 17, 18)));
            Assert.That(hands.Right.HasValue, Is.False);
        }
    }
}

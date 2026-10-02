using System;
using Cubeglass.CoreMath;
using NUnit.Framework;

namespace Cubeglass.Gameplay.Tests
{
    // Gesture recogniser contract (dossier 5.11, ADR-0008): a pure hysteresis
    // state machine over mock hands. Pinch and fist latch between their band
    // edges; a release followed by a re-pinch inside 250 ms reports a
    // one-frame palette flick; tracking loss holds outputs for 200 ms and then
    // clears state and timers; the left hand drives a frame when present.
    [TestFixture]
    public sealed class GestureRecognizerTests
    {
        private const double Dt = 0.1;

        [Test]
        public void NominalScaleMatchesTheMockHandScale()
        {
            Assert.That(GestureRecognizer.NominalHandScale, Is.EqualTo(MockHands.Scale));
        }

        [Test]
        public void OpenHandProducesNeutralOutput()
        {
            var recognizer = new GestureRecognizer();

            GestureOutput output = Step(recognizer, MockHands.Open());

            Assert.That(output.Pinching, Is.False);
            Assert.That(output.Fist, Is.False);
            Assert.That(output.PaletteFlick, Is.False);
            Assert.That(output.PinchStrength, Is.EqualTo(0f));
            Assert.That(recognizer.TrackingLost, Is.False);
        }

        [Test]
        public void PinchActivatesAndReportsTheMappedStrength()
        {
            var recognizer = new GestureRecognizer();

            GestureOutput deep = Step(recognizer, MockHands.Pinch(1.5f));

            Assert.That(deep.Pinching, Is.True);
            Assert.That(deep.PaletteFlick, Is.False);
            Assert.That(deep.PinchStrength, Is.EqualTo(1f).Within(1e-4f), "ratio 0.4 clamps to full strength");

            GestureOutput mid = Step(recognizer, MockHands.Pinch(0.5f));

            Assert.That(mid.Pinching, Is.True, "ratio 0.6 is inside the held band");
            Assert.That(mid.PinchStrength, Is.EqualTo(0.5f).Within(1e-4f));

            GestureOutput light = Step(recognizer, MockHands.Pinch(0.05f));

            Assert.That(light.Pinching, Is.True, "ratio 0.69 is just inside the open edge");
            Assert.That(light.PinchStrength, Is.EqualTo(0.05f).Within(1e-4f));
        }

        [Test]
        public void PinchInsideTheBandDoesNotActivateFromOpen()
        {
            var recognizer = new GestureRecognizer();

            Assert.That(Step(recognizer, MockHands.Pinch(0.5f)).Pinching, Is.False, "ratio 0.6 is in the band");
            Assert.That(Step(recognizer, MockHands.Pinch(0.05f)).Pinching, Is.False, "ratio 0.69 is in the band");
            Assert.That(Step(recognizer, MockHands.Pinch(1.5f)).Pinching, Is.True, "ratio 0.4 crosses the closed edge");
        }

        [Test]
        public void PinchReleasesAtTheOpenEdgeAndStrengthDropsToZero()
        {
            var recognizer = new GestureRecognizer();
            Step(recognizer, MockHands.Pinch(1.5f));

            GestureOutput justInside = Step(recognizer, MockHands.Pinch(0.05f));
            Assert.That(justInside.Pinching, Is.True, "ratio 0.69 is below the open edge");

            GestureOutput outside = Step(recognizer, MockHands.Pinch(-0.05f));
            Assert.That(outside.Pinching, Is.False, "ratio 0.71 crosses the open edge");
            Assert.That(outside.PinchStrength, Is.EqualTo(0f));
        }

        [Test]
        public void PinchOscillationInTheBandCannotFlipWithoutCrossingTheOppositeEdge()
        {
            var held = new GestureRecognizer();
            Step(held, MockHands.Pinch(1.5f));
            for (int i = 0; i < 10; i++)
            {
                GestureOutput output = Step(held, MockHands.Pinch(i % 2 == 0 ? 0.5f : 0.25f));
                Assert.That(output.Pinching, Is.True, $"oscillation in the band must hold the pinch at step {i}");
            }

            GestureOutput released = Step(held, MockHands.Open());
            Assert.That(released.Pinching, Is.False, "crossing the open edge releases the pinch");

            var open = new GestureRecognizer();
            for (int i = 0; i < 10; i++)
            {
                GestureOutput output = Step(open, MockHands.Pinch(i % 2 == 0 ? 0.5f : 0.25f));
                Assert.That(output.Pinching, Is.False, $"oscillation in the band must not activate at step {i}");
            }

            Assert.That(Step(open, MockHands.Pinch(1.5f)).Pinching, Is.True, "crossing the closed edge activates");
        }

        [Test]
        public void FistActivatesAtTheClosedEdgeAndHoldsThroughTheBand()
        {
            var recognizer = new GestureRecognizer();

            GestureOutput fist = Step(recognizer, MockHands.Fist());
            Assert.That(fist.Fist, Is.True);
            Assert.That(fist.Pinching, Is.False, "the thumb stays clear of the index tip");

            GestureOutput shallow = Step(recognizer, CurledFingers(0.41f));
            Assert.That(shallow.Fist, Is.True, "0.41 is inside the held band");

            GestureOutput deeper = Step(recognizer, CurledFingers(0.39f));
            Assert.That(deeper.Fist, Is.True, "crossing the closed edge keeps the fist");

            GestureOutput released = Step(recognizer, CurledFingers(0.61f));
            Assert.That(released.Fist, Is.False, "0.61 crosses the open edge");
        }

        [Test]
        public void FistInsideTheBandDoesNotActivateFromOpen()
        {
            var recognizer = new GestureRecognizer();

            Assert.That(Step(recognizer, CurledFingers(0.41f)).Fist, Is.False, "0.41 is in the band");
            Assert.That(Step(recognizer, CurledFingers(0.59f)).Fist, Is.False, "0.59 is in the band");
            Assert.That(Step(recognizer, CurledFingers(0.39f)).Fist, Is.True, "0.39 crosses the closed edge");
        }

        [Test]
        public void FistOscillationInTheBandCannotFlipWithoutCrossingTheOppositeEdge()
        {
            var held = new GestureRecognizer();
            Step(held, MockHands.Fist());
            for (int i = 0; i < 10; i++)
            {
                GestureOutput output = Step(held, CurledFingers(i % 2 == 0 ? 0.59f : 0.41f));
                Assert.That(output.Fist, Is.True, $"oscillation in the band must hold the fist at step {i}");
            }

            Assert.That(Step(held, CurledFingers(0.61f)).Fist, Is.False, "0.61 crosses the open edge");

            var open = new GestureRecognizer();
            for (int i = 0; i < 10; i++)
            {
                GestureOutput output = Step(open, CurledFingers(i % 2 == 0 ? 0.59f : 0.41f));
                Assert.That(output.Fist, Is.False, $"oscillation in the band must not activate at step {i}");
            }

            Assert.That(Step(open, CurledFingers(0.39f)).Fist, Is.True, "0.39 crosses the closed edge");
        }

        [Test]
        public void FistIgnoresTheThumbPose()
        {
            var recognizer = new GestureRecognizer();
            HandFrame wideThumb = MockHands.Override(MockHands.Fist(), thumbTip: new Vector3f(0.3f, 0f, 0f));

            GestureOutput output = Step(recognizer, wideThumb);

            Assert.That(output.Fist, Is.True, "the fist measure excludes the thumb tip");
            Assert.That(output.Pinching, Is.False);
        }

        [Test]
        public void FistAndPinchAreIndependentAndCanBothBeActive()
        {
            var recognizer = new GestureRecognizer();
            HandFrame closedPinch = MockHands.Override(
                MockHands.Fist(), thumbTip: new Vector3f(0.02f, 0.01f, -0.035f));

            GestureOutput output = Step(recognizer, closedPinch);

            Assert.That(output.Pinching, Is.True);
            Assert.That(output.Fist, Is.True);
        }

        [Test]
        public void FlickFiresOnARepinchInsideTheWindowForExactlyOneFrame()
        {
            var recognizer = new GestureRecognizer();
            Step(recognizer, MockHands.Pinch(1.5f));

            GestureOutput released = Step(recognizer, MockHands.Open());
            Assert.That(released.Pinching, Is.False);
            Assert.That(released.PaletteFlick, Is.False);

            GestureOutput flick = Step(recognizer, MockHands.Pinch(1.5f));
            Assert.That(flick.PaletteFlick, Is.True, "a re-pinch 0.1 s after release is inside the 250 ms window");
            Assert.That(flick.Pinching, Is.True);
            Assert.That(flick.PinchStrength, Is.EqualTo(1f).Within(1e-4f));

            GestureOutput next = Step(recognizer, MockHands.Pinch(0.5f));
            Assert.That(next.PaletteFlick, Is.False, "the flick lasts exactly one frame");
            Assert.That(next.Pinching, Is.True);
        }

        [Test]
        public void FlickAtTheWindowBoundaryIsInclusive()
        {
            var recognizer = new GestureRecognizer();
            Step(recognizer, MockHands.Pinch(1.5f));
            Step(recognizer, MockHands.Open());

            GestureOutput flick = Step(recognizer, MockHands.Pinch(1.5f), GestureRecognizer.PaletteFlickWindowSeconds);

            Assert.That(flick.PaletteFlick, Is.True, "a re-pinch at exactly 250 ms is inside the window");
        }

        [Test]
        public void FlickOutsideTheWindowDoesNotFireAndThePinchStillEnters()
        {
            var recognizer = new GestureRecognizer();
            Step(recognizer, MockHands.Pinch(1.5f));
            Step(recognizer, MockHands.Open());

            GestureOutput late = Step(recognizer, MockHands.Pinch(1.5f), GestureRecognizer.PaletteFlickWindowSeconds + 0.05);

            Assert.That(late.PaletteFlick, Is.False, "a re-pinch past 250 ms is a fresh pinch");
            Assert.That(late.Pinching, Is.True);

            Step(recognizer, MockHands.Open());
            GestureOutput again = Step(recognizer, MockHands.Pinch(1.5f));
            Assert.That(again.PaletteFlick, Is.True, "a fresh release and quick re-pinch flicks again");
        }

        [Test]
        public void FlickWindowIsFrozenDuringShortTrackingLoss()
        {
            var recognizer = new GestureRecognizer();
            Step(recognizer, MockHands.Pinch(1.5f));
            Step(recognizer, MockHands.Open());

            GestureOutput held = Step(recognizer, MockHands.Untracked());
            Assert.That(held.Pinching, Is.False, "a released pinch stays released while held");

            GestureOutput flick = Step(recognizer, MockHands.Pinch(1.5f));

            Assert.That(flick.PaletteFlick, Is.True, "a 100 ms loss does not consume the flick window");
        }

        [Test]
        public void UntrackedLossHoldsOutputsThenClearsPastTheTimeout()
        {
            var recognizer = new GestureRecognizer();
            Step(recognizer, MockHands.Pinch(1.5f));

            GestureOutput atPointOne = Step(recognizer, MockHands.Untracked());
            Assert.That(atPointOne.Pinching, Is.True, "loss at or below 200 ms holds the outputs");
            Assert.That(atPointOne.PinchStrength, Is.EqualTo(1f).Within(1e-4f));
            Assert.That(recognizer.TrackingLost, Is.False);

            GestureOutput atPointTwo = Step(recognizer, MockHands.Untracked());
            Assert.That(atPointTwo.Pinching, Is.True, "exactly 200 ms of loss still holds");

            GestureOutput cleared = Step(recognizer, MockHands.Untracked());
            Assert.That(cleared.Pinching, Is.False, "the first frame past 200 ms clears");
            Assert.That(cleared.Fist, Is.False);
            Assert.That(cleared.PaletteFlick, Is.False);
            Assert.That(cleared.PinchStrength, Is.EqualTo(0f));
            Assert.That(recognizer.TrackingLost, Is.True);

            GestureOutput stillLost = Step(recognizer, MockHands.Untracked());
            Assert.That(stillLost.Pinching, Is.False);

            GestureOutput recovered = Step(recognizer, MockHands.Open());
            Assert.That(recognizer.TrackingLost, Is.False);
            Assert.That(recovered.Pinching, Is.False);

            Assert.That(Step(recognizer, MockHands.Pinch(1.5f)).Pinching, Is.True, "recovery re-arms from clear");
        }

        [Test]
        public void ShortTrackingLossPreservesTheHystereticState()
        {
            var recognizer = new GestureRecognizer();
            Step(recognizer, MockHands.Pinch(1.5f));
            Step(recognizer, MockHands.Untracked());

            GestureOutput inBand = Step(recognizer, MockHands.Pinch(0.5f));
            Assert.That(inBand.Pinching, Is.True, "ratio 0.6 holds the pinch across a 100 ms loss");

            GestureOutput released = Step(recognizer, MockHands.Open());
            Assert.That(released.Pinching, Is.False);
        }

        [Test]
        public void TrackingLossClearResetsTheFlickWindow()
        {
            var recognizer = new GestureRecognizer();
            Step(recognizer, MockHands.Pinch(1.5f));
            Step(recognizer, MockHands.Open());

            Step(recognizer, MockHands.Untracked());
            Step(recognizer, MockHands.Untracked());
            GestureOutput cleared = Step(recognizer, MockHands.Untracked());
            Assert.That(cleared.PaletteFlick, Is.False);
            Assert.That(recognizer.TrackingLost, Is.True);

            GestureOutput recovered = Step(recognizer, MockHands.Pinch(1.5f));
            Assert.That(recovered.Pinching, Is.True);
            Assert.That(recovered.PaletteFlick, Is.False, "a loss clear discards the armed flick window");
        }

        [Test]
        public void HandSelectionPrefersLeftThenFallsBackToRight()
        {
            Assert.That(
                new GestureRecognizer().Update(MockHands.Both(MockHands.Open(), MockHands.Pinch(1.5f)), Dt).Pinching,
                Is.False,
                "the left hand is present and open");

            Assert.That(
                new GestureRecognizer().Update(MockHands.Right(MockHands.Pinch(1.5f)), Dt).Pinching,
                Is.True,
                "the right hand drives when the left is absent");

            Assert.That(
                new GestureRecognizer().Update(MockHands.Both(MockHands.Pinch(1.5f), MockHands.Open()), Dt).Pinching,
                Is.True,
                "the left hand drives when both are present");
        }

        [Test]
        public void TrackedFramesWithoutAHandBehaveLikeLoss()
        {
            var recognizer = new GestureRecognizer();
            Step(recognizer, MockHands.Pinch(1.5f));

            GestureOutput held = Step(recognizer, MockHands.Both(null, null));
            Assert.That(held.Pinching, Is.True);

            Step(recognizer, MockHands.Both(null, null));
            GestureOutput cleared = Step(recognizer, MockHands.Both(null, null));

            Assert.That(cleared.Pinching, Is.False);
            Assert.That(recognizer.TrackingLost, Is.True);
        }

        [Test]
        public void DegenerateScaleOrJointsHoldTheState()
        {
            var recognizer = new GestureRecognizer();
            Step(recognizer, MockHands.Pinch(1.5f));

            HandFrame zeroScale = MockHands.Override(MockHands.Pinch(1.5f), middleTip: new Vector3f(0f, 0f, 0f));
            GestureOutput onZeroScale = Step(recognizer, zeroScale);
            Assert.That(onZeroScale.Pinching, Is.True, "a zero pinch scale holds the state");
            Assert.That(onZeroScale.PinchStrength, Is.EqualTo(1f).Within(1e-4f));

            HandFrame nonFinite = MockHands.Override(
                MockHands.Pinch(1.5f), thumbTip: new Vector3f(float.NaN, 0f, 0f));
            Assert.That(Step(recognizer, nonFinite).Pinching, Is.True, "a non-finite joint holds the state");

            GestureOutput released = Step(recognizer, MockHands.Open());
            Assert.That(released.Pinching, Is.False);
        }

        [Test]
        public void MoveTranslatesWithoutChangingTheGesture()
        {
            var pinch = new GestureRecognizer();
            Assert.That(Step(pinch, MockHands.Move(MockHands.Pinch(1.5f), 1.5f, -0.5f, 2f)).Pinching, Is.True);

            var fist = new GestureRecognizer();
            Assert.That(Step(fist, MockHands.Move(MockHands.Fist(), -3f, 0.25f, 7f)).Fist, Is.True);
        }

        [Test]
        public void DtMustBeFiniteAndNonNegative()
        {
            var recognizer = new GestureRecognizer();

            Assert.Throws<ArgumentOutOfRangeException>(() => Step(recognizer, MockHands.Open(), -0.01));
            Assert.Throws<ArgumentOutOfRangeException>(() => Step(recognizer, MockHands.Open(), double.NaN));
            Assert.Throws<ArgumentOutOfRangeException>(() => Step(recognizer, MockHands.Open(), double.PositiveInfinity));
            Assert.Throws<ArgumentOutOfRangeException>(() => Step(recognizer, MockHands.Open(), double.NegativeInfinity));

            Assert.That(Step(recognizer, MockHands.Pinch(1.5f), 0.0).Pinching, Is.True, "dt 0 still evaluates the pose");
        }

        [Test]
        public void SameScriptProducesIdenticalOutputs()
        {
            (HandsInput Hands, double Dt)[] script =
            {
                (MockHands.Left(MockHands.Open()), 0.1),
                (MockHands.Left(MockHands.Pinch(1.5f)), 0.1),
                (MockHands.Left(MockHands.Pinch(0.5f)), 0.1),
                (MockHands.Left(MockHands.Open()), 0.1),
                (MockHands.Left(MockHands.Pinch(1.5f)), 0.1),
                (MockHands.Left(MockHands.Fist()), 0.1),
                (MockHands.Untracked(), 0.1),
                (MockHands.Untracked(), 0.1),
                (MockHands.Untracked(), 0.1),
                (MockHands.Right(MockHands.Fist()), 0.1),
                (MockHands.Both(MockHands.Pinch(1.5f), MockHands.Open()), 0.1),
                (MockHands.Both(MockHands.Open(), MockHands.Pinch(1.5f)), 0.1),
            };

            var first = new GestureRecognizer();
            var second = new GestureRecognizer();
            for (int i = 0; i < script.Length; i++)
            {
                GestureOutput a = first.Update(script[i].Hands, script[i].Dt);
                GestureOutput b = second.Update(script[i].Hands, script[i].Dt);
                AssertSame(a, b, i);
            }

            Assert.That(second.TrackingLost, Is.EqualTo(first.TrackingLost));
        }

        [Test]
        public void UpdateAllocatesNothing()
        {
            (HandsInput Hands, double Dt)[] script =
            {
                (MockHands.Left(MockHands.Open()), 0.1),
                (MockHands.Left(MockHands.Pinch(1.5f)), 0.1),
                (MockHands.Left(MockHands.Pinch(0.5f)), 0.1),
                (MockHands.Left(MockHands.Open()), 0.1),
                (MockHands.Right(MockHands.Fist()), 0.1),
                (MockHands.Untracked(), 0.1),
                (MockHands.Both(MockHands.Pinch(1.5f), MockHands.Open()), 0.1),
            };

            var recognizer = new GestureRecognizer();
            for (int i = 0; i < 1_000; i++)
            {
                recognizer.Update(script[i % script.Length].Hands, script[i % script.Length].Dt);
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100_000; i++)
            {
                recognizer.Update(script[i % script.Length].Hands, script[i % script.Length].Dt);
            }

            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.That(allocated, Is.EqualTo(0L), $"GestureRecognizer.Update allocated {allocated} bytes");
        }

        // ----- helpers -----------------------------------------------------

        private static GestureOutput Step(GestureRecognizer recognizer, HandFrame hand, double dt = Dt)
        {
            return recognizer.Update(MockHands.Left(hand), dt);
        }

        private static GestureOutput Step(GestureRecognizer recognizer, HandsInput hands, double dt = Dt)
        {
            return recognizer.Update(hands, dt);
        }

        private static HandFrame CurledFingers(float fistRatio)
        {
            var tip = new Vector3f(0f, 0f, -(fistRatio * MockHands.Scale));
            return MockHands.Override(
                MockHands.Open(),
                indexTip: tip,
                middleTip: tip,
                ringTip: tip,
                littleTip: tip);
        }

        private static void AssertSame(GestureOutput expected, GestureOutput actual, int step)
        {
            Assert.That(actual.Pinching, Is.EqualTo(expected.Pinching), $"step {step} Pinching");
            Assert.That(actual.Fist, Is.EqualTo(expected.Fist), $"step {step} Fist");
            Assert.That(actual.PaletteFlick, Is.EqualTo(expected.PaletteFlick), $"step {step} PaletteFlick");
            Assert.That(actual.PinchStrength, Is.EqualTo(expected.PinchStrength), $"step {step} PinchStrength");
        }
    }
}

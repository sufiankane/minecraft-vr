using Cubeglass.CoreMath;
using Cubeglass.Gameplay;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Cubeglass.Unity.Input.Tests
{
    /// <summary>
    /// Pins the calibration pose drive's integration units and snap routing
    /// (S7 review fix): <see cref="InputFrame.TurnSnap"/> is a degrees-per-second
    /// rate scaled by the frame delta, and the discrete snap edge is consumed
    /// once per frame through <see cref="ISnapInputSource"/> exactly like
    /// <see cref="GameplayBridge"/>.
    /// </summary>
    public class SyntheticPoseDriveTests
    {
        private const float Tolerance = 1e-4f;

        private GameObject root;
        private SyntheticPoseProvider provider;
        private SyntheticPoseDrive drive;

        [SetUp]
        public void CreateDrive()
        {
            root = new GameObject("SyntheticPoseDrive");
            provider = root.AddComponent<SyntheticPoseProvider>();
            provider.SetPose(0f, 0f, 0f, Vector3.zero);
            provider.SetRates(0f, 0f, 0f);
            drive = root.AddComponent<SyntheticPoseDrive>();
            drive.Provider = provider;
        }

        [TearDown]
        public void DestroyDrive()
        {
            Object.DestroyImmediate(root);
            root = null;
            provider = null;
            drive = null;
        }

        [Test]
        public void TurnSnapRateIsIntegratedWithDeltaTime()
        {
            drive.Advance(Frame(turnSnap: 90f), 0, 1f / 60f);
            Assert.AreEqual(
                -1.5f,
                drive.YawDegrees,
                Tolerance,
                "90 deg/s for one 1/60 s frame is 1.5 deg, not 90; a missing * dt is ~60x too sensitive");
        }

        [Test]
        public void MoveYawRateScalesWithDeltaTime()
        {
            drive.Advance(Frame(move: new Vector2f(1f, 0f)), 0, 0.5f);
            Assert.AreEqual(-22.5f, drive.YawDegrees, Tolerance, "45 deg/s * 0.5 s");
        }

        [Test]
        public void TurnSnapIntegrationIsFramerateIndependent()
        {
            drive.Advance(Frame(turnSnap: 45f), 0, 0.5f);
            float half = drive.YawDegrees;
            drive.Advance(Frame(turnSnap: 45f), 0, 0.5f);
            Assert.AreEqual(half * 2f, drive.YawDegrees, Tolerance, "two half-second frames are one second of turning");
        }

        [Test]
        public void InvalidDeltaTimeDoesNotMoveTheView()
        {
            drive.Advance(Frame(turnSnap: 90f, move: new Vector2f(1f, 1f)), 0, float.NaN);
            Assert.AreEqual(0f, drive.YawDegrees, Tolerance);
            Assert.AreEqual(0f, drive.PitchDegrees, Tolerance);

            drive.Advance(Frame(turnSnap: 90f, move: new Vector2f(1f, 1f)), 0, float.PositiveInfinity);
            Assert.AreEqual(0f, drive.YawDegrees, Tolerance);
            Assert.AreEqual(0f, drive.PitchDegrees, Tolerance);
        }

        [Test]
        public void ConsumedSnapEdgeIsADiscreteStepAndSamplesFirst()
        {
            FakeInputSource fake = root.AddComponent<FakeInputSource>();
            fake.Frame = Frame();
            drive.Input = fake;

            fake.PendingSnap = 1;
            drive.AdvanceFromSource(1f / 60f);
            Assert.IsTrue(fake.SampleBeforeConsume, "the frame must be sampled before the edge is consumed");
            Assert.AreEqual(1, fake.SampleCalls);
            Assert.AreEqual(1, fake.ConsumeCalls);
            Assert.AreEqual(-45f, drive.YawDegrees, Tolerance, "right snap decreases internal yaw by the increment");

            drive.AdvanceFromSource(1f / 60f);
            Assert.AreEqual(-45f, drive.YawDegrees, Tolerance, "a held key must not re-apply the edge");

            fake.PendingSnap = -1;
            drive.AdvanceFromSource(1f / 60f);
            Assert.AreEqual(0f, drive.YawDegrees, Tolerance, "left snap restores the yaw");
        }

        [Test]
        public void RecenterZeroesTheAccumulatedOrientation()
        {
            drive.Advance(Frame(move: new Vector2f(1f, 1f)), 0, 1f);
            Assert.AreNotEqual(0f, drive.YawDegrees);
            Assert.AreNotEqual(0f, drive.PitchDegrees);

            drive.Advance(Frame(recenter: true), 0, 1f / 60f);
            Assert.AreEqual(0f, drive.YawDegrees, Tolerance);
            Assert.AreEqual(0f, drive.PitchDegrees, Tolerance);
        }

        private static InputFrame Frame(
            Vector2f move = default,
            float turnSnap = 0f,
            bool recenter = false)
        {
            return new InputFrame(
                move,
                turnSnap,
                recenter,
                null,
                ButtonState.Up,
                ButtonState.Up,
                0,
                TrackingQuality.Good);
        }

        private sealed class FakeInputSource : MonoBehaviour, IInputProvider, ISnapInputSource
        {
            public InputFrame Frame;
            public int PendingSnap;
            public int SampleCalls;
            public int ConsumeCalls;
            public bool SampleBeforeConsume { get; private set; }

            private bool sampled;

            public InputFrame Sample(double timeSeconds)
            {
                SampleCalls++;
                sampled = true;
                return Frame;
            }

            public int ConsumeSnapDirection()
            {
                ConsumeCalls++;
                SampleBeforeConsume = sampled;
                sampled = false;
                int direction = PendingSnap;
                PendingSnap = 0;
                return direction;
            }
        }
    }
}

using NUnit.Framework;

namespace Cubeglass.Streaming.Tests
{
    /// <summary>
    /// Pins the deterministic five-minute S7 session (plan Task 1b).
    /// </summary>
    /// <remarks>
    /// The canonical run is the committed timeline: 18,000 steps of 1/60 s
    /// through <see cref="SessionHarness"/> with the default streaming config.
    /// <see cref="GoldenWorldHash"/> was produced by one run and is committed
    /// unchanged; the second test replays the same timeline at 1/30 s for half
    /// the steps and requires the same hash, which holds because every event
    /// time is a canonical 1/30 s multiple and every edit uses a world-fixed
    /// pointer ray.
    /// </remarks>
    [TestFixture]
    public sealed class GoldenSessionTests
    {
        /// <summary>
        /// Pinned by the first canonical run (recorded in the Task 1b report);
        /// commit the value unchanged.
        /// </summary>
        public const ulong GoldenWorldHash = 0xB38A50148C01A643UL;

        private const long SessionSeed = 20261003L;

        [Test]
        public void CanonicalFiveMinuteSessionMatchesTheGoldenHash()
        {
            var harness = new SessionHarness(new StreamingConfig(), SessionSeed);
            SessionResult result = harness.Run(SessionHarness.CanonicalSteps, SessionHarness.CanonicalDt);

            Assert.That(
                result.EditsApplied,
                Is.EqualTo(SessionHarness.ExpectedEditCount),
                "every scripted break and place must apply");
            Assert.That(result.ChunksLoaded, Is.GreaterThan(0), "the session must load chunks");
            Assert.That(result.QuadsEmitted, Is.GreaterThan(0), "the session must mesh quads");
            Assert.That(harness.WalkCompleted, Is.True, "the player must reach the final waypoint");
            Assert.That(harness.Unloads, Is.Zero, "the pinned route never leaves the retention box");
            Assert.That(result.WorldHash, Is.EqualTo(GoldenWorldHash), "pinned golden world hash");
        }

        [Test]
        public void HalfStepDtReplaysTheSameTimelineToTheSameHash()
        {
            var canonical = new SessionHarness(new StreamingConfig(), SessionSeed);
            SessionResult fine = canonical.Run(SessionHarness.CanonicalSteps, SessionHarness.CanonicalDt);

            var coarse = new SessionHarness(new StreamingConfig(), SessionSeed);
            SessionResult half = coarse.Run(
                SessionHarness.CanonicalSteps / 2,
                SessionHarness.CanonicalDt * 2.0);

            Assert.That(fine.EditsApplied, Is.EqualTo(SessionHarness.ExpectedEditCount));
            Assert.That(half.EditsApplied, Is.EqualTo(SessionHarness.ExpectedEditCount));
            Assert.That(coarse.WalkCompleted, Is.True);
            Assert.That(
                half.WorldHash,
                Is.EqualTo(fine.WorldHash),
                "the same timeline at half the frame rate must reach the same world");
            Assert.That(half.WorldHash, Is.EqualTo(GoldenWorldHash));
        }
    }
}

using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Cubeglass.Unity.Bridge.Tests
{
    /// <summary>
    /// Shared fixture for the native bridge tests. The native test-only writer
    /// is a process-wide single-writer singleton, so every test creates a fresh
    /// region in <see cref="CreateWriter"/> and closes it in
    /// <see cref="CloseWriter"/>, and each test uses one, and only one, writer.
    /// </summary>
    public abstract class NativeBridgeTestBase
    {
        protected BridgeClient Client { get; private set; }

        [SetUp]
        public void CreateWriter()
        {
            string pluginDir = Path.Combine(Application.dataPath, "Plugins", "win-x64");
            string pluginPath = Path.Combine(pluginDir, "cg_unity_bridge.dll");
            string writerPath = Path.Combine(pluginDir, "cg_bridge_test_support.dll");
            if (!File.Exists(pluginPath) || !File.Exists(writerPath))
            {
                Assert.Fail(
                    $"native bridge plugins not found ('{pluginPath}', '{writerPath}'); build them first " +
                    "(cpp: cmake --build --preset windows-msvc --target cg_bridge cg_bridge_test_support) and copy " +
                    "them to Assets/Plugins/win-x64 (scripts/ci-local.ps1 does this before the Unity lane; " +
                    "cg_bridge_test_support.dll is test-only and is never shipped)");
            }

            NativeBridge.cg_test_writer_close(); // safe when no writer is open
            Assert.AreEqual(BridgeStatus.Ok, NativeBridge.cg_test_writer_create(), "cg_test_writer_create");
        }

        [TearDown]
        public void CloseWriter()
        {
            if (Client != null)
            {
                Client.Dispose();
                Client = null;
            }

            NativeBridge.cg_test_writer_close();
        }

        protected BridgeClient Open()
        {
            Assert.IsTrue(BridgeClient.TryOpen(out BridgeClient client), "BridgeClient.TryOpen");
            Assert.IsNotNull(client);
            Client = client;
            return client;
        }

        protected static void SetFreshHeartbeat()
        {
            // The Editor runtime's Stopwatch epoch does not match the native
            // steady_clock, so tests cannot derive "now" portably. A heartbeat
            // at the top of the int64 range is far future for any real native
            // clock value (the subtraction cannot overflow for now >= 0).
            Assert.AreEqual(
                BridgeStatus.Ok,
                NativeBridge.cg_test_writer_set_heartbeat(long.MaxValue),
                "cg_test_writer_set_heartbeat(fresh)");
        }

        protected static void SetStaleHeartbeat()
        {
            // The native steady_clock epoch (boot) is always more than 250 ms
            // before a running test, so heartbeat 0 is far past the staleness
            // window. The exact 250 ms predicate is pinned natively by
            // cg::bridge::is_stale in the C++ layout tests.
            Assert.AreEqual(
                BridgeStatus.Ok,
                NativeBridge.cg_test_writer_set_heartbeat(0L),
                "cg_test_writer_set_heartbeat(stale)");
        }
    }

    /// <summary>Scripted head/hand samples and field-wise comparisons.</summary>
    internal static class BridgeSamples
    {
        internal static BridgeHeadSample Head(uint sequence)
        {
            return new BridgeHeadSample
            {
                HostTime = (long)sequence * 1_000_000L,
                Pose = new BridgePose
                {
                    Position = new BridgeVec3 { X = (float)sequence + 0.25f, Y = -0.5f * sequence, Z = 0.125f },
                    Rotation = new BridgeQuat { W = 0.5f, X = -0.25f, Y = 0.75f, Z = (float)sequence / 1_000_000f },
                },
                State = TrackState.Stable,
                Sequence = sequence,
            };
        }

        internal static BridgeHandFrame Hands(uint sequence)
        {
            return new BridgeHandFrame
            {
                CaptureTime = (long)sequence,
                PublishTime = (long)sequence * 2L,
                PredictedFor = (long)sequence * 3L,
                Sequence = sequence,
                Hand0 = ExpectedHand(sequence, 0),
                Hand1 = ExpectedHand(sequence, 1),
            };
        }

        internal static BridgeHand ExpectedHand(uint sequence, int handIndex)
        {
            var hand = new BridgeHand
            {
                Present = 1,
                Handedness = (byte)handIndex,
                Confidence = 0.5f + 0.25f * handIndex,
                Velocity = new BridgeVec3
                {
                    X = (float)sequence,
                    Y = -(float)sequence,
                    Z = 0.25f * handIndex + 0.5f,
                },
            };
            for (int joint = 0; joint < 21; joint++)
            {
                SetJoint(ref hand, joint, new BridgeVec3
                {
                    X = (float)sequence + joint,
                    Y = -(float)sequence,
                    Z = handIndex,
                });
            }

            return hand;
        }

        internal static bool HeadMatches(BridgeHeadSample sample)
        {
            if (sample.Sequence == 0)
            {
                return false;
            }

            BridgeHeadSample expected = Head(sample.Sequence);
            return sample.HostTime == expected.HostTime
                && Vec3Equals(sample.Pose.Position, expected.Pose.Position)
                && sample.Pose.Rotation.W == expected.Pose.Rotation.W
                && sample.Pose.Rotation.X == expected.Pose.Rotation.X
                && sample.Pose.Rotation.Y == expected.Pose.Rotation.Y
                && sample.Pose.Rotation.Z == expected.Pose.Rotation.Z
                && sample.State == TrackState.Stable;
        }

        internal static bool HeadEquals(BridgeHeadSample expected, BridgeHeadSample actual)
        {
            return expected.HostTime == actual.HostTime
                && Vec3Equals(expected.Pose.Position, actual.Pose.Position)
                && expected.Pose.Rotation.W == actual.Pose.Rotation.W
                && expected.Pose.Rotation.X == actual.Pose.Rotation.X
                && expected.Pose.Rotation.Y == actual.Pose.Rotation.Y
                && expected.Pose.Rotation.Z == actual.Pose.Rotation.Z
                && expected.State == actual.State
                && expected.Sequence == actual.Sequence;
        }

        internal static bool HandsMatch(BridgeHandFrame frame)
        {
            return frame.Sequence != 0
                && HandEquals(ExpectedHand(frame.Sequence, 0), frame.Hand0)
                && HandEquals(ExpectedHand(frame.Sequence, 1), frame.Hand1);
        }

        internal static bool HandEquals(BridgeHand expected, BridgeHand actual)
        {
            if (expected.Present != actual.Present
                || expected.Handedness != actual.Handedness
                || expected.Reserved0 != actual.Reserved0
                || expected.Reserved1 != actual.Reserved1
                || expected.Confidence != actual.Confidence
                || !Vec3Equals(expected.Velocity, actual.Velocity))
            {
                return false;
            }

            for (int joint = 0; joint < 21; joint++)
            {
                if (!Vec3Equals(GetJoint(expected, joint), GetJoint(actual, joint)))
                {
                    return false;
                }
            }

            return true;
        }

        internal static void AssertHeadEquals(BridgeHeadSample expected, BridgeHeadSample actual)
        {
            Assert.AreEqual(expected.HostTime, actual.HostTime, "host_time");
            AssertVec3Equals("position", expected.Pose.Position, actual.Pose.Position);
            Assert.AreEqual(expected.Pose.Rotation.W, actual.Pose.Rotation.W, "rotation.w");
            Assert.AreEqual(expected.Pose.Rotation.X, actual.Pose.Rotation.X, "rotation.x");
            Assert.AreEqual(expected.Pose.Rotation.Y, actual.Pose.Rotation.Y, "rotation.y");
            Assert.AreEqual(expected.Pose.Rotation.Z, actual.Pose.Rotation.Z, "rotation.z");
            Assert.AreEqual(expected.State, actual.State, "state");
            Assert.AreEqual(expected.Sequence, actual.Sequence, "sequence");
        }

        internal static void AssertHandEquals(string name, BridgeHand expected, BridgeHand actual)
        {
            Assert.AreEqual(expected.Present, actual.Present, name + ".present");
            Assert.AreEqual(expected.Handedness, actual.Handedness, name + ".handedness");
            Assert.AreEqual(expected.Reserved0, actual.Reserved0, name + ".reserved0");
            Assert.AreEqual(expected.Reserved1, actual.Reserved1, name + ".reserved1");
            Assert.AreEqual(expected.Confidence, actual.Confidence, name + ".confidence");
            AssertVec3Equals(name + ".velocity", expected.Velocity, actual.Velocity);
            for (int joint = 0; joint < 21; joint++)
            {
                AssertVec3Equals(name + ".joint" + joint, GetJoint(expected, joint), GetJoint(actual, joint));
            }
        }

        internal static bool Vec3Equals(BridgeVec3 expected, BridgeVec3 actual)
        {
            return expected.X == actual.X && expected.Y == actual.Y && expected.Z == actual.Z;
        }

        private static void AssertVec3Equals(string name, BridgeVec3 expected, BridgeVec3 actual)
        {
            Assert.AreEqual(expected.X, actual.X, name + ".x");
            Assert.AreEqual(expected.Y, actual.Y, name + ".y");
            Assert.AreEqual(expected.Z, actual.Z, name + ".z");
        }

        private static BridgeVec3 GetJoint(BridgeHand hand, int index)
        {
            switch (index)
            {
                case 0: return hand.Joint0;
                case 1: return hand.Joint1;
                case 2: return hand.Joint2;
                case 3: return hand.Joint3;
                case 4: return hand.Joint4;
                case 5: return hand.Joint5;
                case 6: return hand.Joint6;
                case 7: return hand.Joint7;
                case 8: return hand.Joint8;
                case 9: return hand.Joint9;
                case 10: return hand.Joint10;
                case 11: return hand.Joint11;
                case 12: return hand.Joint12;
                case 13: return hand.Joint13;
                case 14: return hand.Joint14;
                case 15: return hand.Joint15;
                case 16: return hand.Joint16;
                case 17: return hand.Joint17;
                case 18: return hand.Joint18;
                case 19: return hand.Joint19;
                case 20: return hand.Joint20;
                default: throw new ArgumentOutOfRangeException(nameof(index));
            }
        }

        private static void SetJoint(ref BridgeHand hand, int index, BridgeVec3 value)
        {
            switch (index)
            {
                case 0: hand.Joint0 = value; return;
                case 1: hand.Joint1 = value; return;
                case 2: hand.Joint2 = value; return;
                case 3: hand.Joint3 = value; return;
                case 4: hand.Joint4 = value; return;
                case 5: hand.Joint5 = value; return;
                case 6: hand.Joint6 = value; return;
                case 7: hand.Joint7 = value; return;
                case 8: hand.Joint8 = value; return;
                case 9: hand.Joint9 = value; return;
                case 10: hand.Joint10 = value; return;
                case 11: hand.Joint11 = value; return;
                case 12: hand.Joint12 = value; return;
                case 13: hand.Joint13 = value; return;
                case 14: hand.Joint14 = value; return;
                case 15: hand.Joint15 = value; return;
                case 16: hand.Joint16 = value; return;
                case 17: hand.Joint17 = value; return;
                case 18: hand.Joint18 = value; return;
                case 19: hand.Joint19 = value; return;
                case 20: hand.Joint20 = value; return;
                default: throw new ArgumentOutOfRangeException(nameof(index));
            }
        }
    }

    public class NativeBridgeTests : NativeBridgeTestBase
    {
        [Test]
        public void ReadBeforeFirstPublishIsNotReady()
        {
            BridgeClient client = Open();

            Assert.IsFalse(client.TryReadHead(out _), "a fresh region has no head sample");
            Assert.AreEqual(BridgeStatus.NotReady, client.LastStatus);
            Assert.IsFalse(client.TryReadHands(out _), "a fresh region has no hand frame");
            Assert.AreEqual(BridgeStatus.NotReady, client.LastStatus);
        }

        [Test]
        public void HeadRoundTripPreservesEveryField()
        {
            BridgeClient client = Open();
            SetFreshHeartbeat();

            var published = new BridgeHeadSample
            {
                HostTime = 1_234_567_890_123L,
                Pose = new BridgePose
                {
                    Position = new BridgeVec3 { X = 2.5f, Y = -3.75f, Z = 0.125f },
                    Rotation = new BridgeQuat { W = 0.5f, X = -0.25f, Y = 0.75f, Z = -1.5f },
                },
                State = TrackState.Unstable,
                Sequence = 77,
            };
            Assert.AreEqual(
                BridgeStatus.Ok,
                NativeBridge.cg_test_writer_publish_head(ref published),
                "cg_test_writer_publish_head");

            Assert.IsTrue(client.TryReadHead(out BridgeHeadSample read), "TryReadHead");
            Assert.AreEqual(BridgeStatus.Ok, client.LastStatus);
            BridgeSamples.AssertHeadEquals(published, read);
        }

        [Test]
        public void HandsRoundTripPreservesEveryJointAndVelocity()
        {
            BridgeClient client = Open();
            SetFreshHeartbeat();

            BridgeHandFrame published = BridgeSamples.Hands(321);
            Assert.AreEqual(
                BridgeStatus.Ok,
                NativeBridge.cg_test_writer_publish_hands(ref published),
                "cg_test_writer_publish_hands");

            Assert.IsTrue(client.TryReadHands(out BridgeHandFrame read), "TryReadHands");
            Assert.AreEqual(BridgeStatus.Ok, client.LastStatus);
            Assert.AreEqual(published.CaptureTime, read.CaptureTime, "capture_time");
            Assert.AreEqual(published.PublishTime, read.PublishTime, "publish_time");
            Assert.AreEqual(published.PredictedFor, read.PredictedFor, "predicted_for");
            Assert.AreEqual(published.Sequence, read.Sequence, "sequence");
            BridgeSamples.AssertHandEquals("hand0", published.Hand0, read.Hand0);
            BridgeSamples.AssertHandEquals("hand1", published.Hand1, read.Hand1);
        }

        [Test]
        public void FarFutureHeartbeatKeepsTrackingStateAndHandsFresh()
        {
            BridgeClient client = Open();
            SetFreshHeartbeat();

            BridgeHeadSample head = BridgeSamples.Head(11);
            Assert.AreEqual(BridgeStatus.Ok, NativeBridge.cg_test_writer_publish_head(ref head));
            Assert.IsTrue(client.TryReadHead(out BridgeHeadSample readHead));
            Assert.AreEqual(TrackState.Stable, readHead.State, "fresh head must keep its tracking state");

            BridgeHandFrame hands = BridgeSamples.Hands(11);
            Assert.AreEqual(BridgeStatus.Ok, NativeBridge.cg_test_writer_publish_hands(ref hands));
            Assert.IsTrue(client.TryReadHands(out BridgeHandFrame readHands));
            Assert.AreEqual(11u, readHands.Sequence);
        }

        [Test]
        public void FarPastHeartbeatMarksTheHeadLostAndWithholdsTheHands()
        {
            BridgeClient client = Open();
            SetFreshHeartbeat();

            BridgeHeadSample head = BridgeSamples.Head(12);
            Assert.AreEqual(BridgeStatus.Ok, NativeBridge.cg_test_writer_publish_head(ref head));
            BridgeHandFrame hands = BridgeSamples.Hands(12);
            Assert.AreEqual(BridgeStatus.Ok, NativeBridge.cg_test_writer_publish_hands(ref hands));

            Assert.IsTrue(client.TryReadHead(out BridgeHeadSample freshHead));
            Assert.AreEqual(TrackState.Stable, freshHead.State);
            Assert.IsTrue(client.TryReadHands(out _));

            SetStaleHeartbeat();

            Assert.IsTrue(client.TryReadHead(out BridgeHeadSample staleHead), "a stale head still returns its sample");
            Assert.AreEqual(TrackState.Lost, staleHead.State);
            Assert.AreEqual(head.HostTime, staleHead.HostTime);
            Assert.AreEqual(head.Sequence, staleHead.Sequence);

            Assert.IsFalse(client.TryReadHands(out _), "stale hands must not return a frame");
            Assert.AreEqual(BridgeStatus.NotReady, client.LastStatus);
        }

        [Test]
        public void SendCommandStoresCommandPlusOneAndAckRoundTrips()
        {
            BridgeClient client = Open();

            Assert.IsTrue(client.SendCommand(1), "SendCommand(1)");
            Assert.AreEqual(BridgeStatus.Ok, client.LastStatus);
            Assert.AreEqual(
                BridgeStatus.Ok,
                NativeBridge.cg_test_writer_read_command(out uint command, out uint ack));
            Assert.AreEqual(2u, command, "cmd + 1 is stored; 0 stays idle");
            Assert.AreEqual(0u, ack);

            // Repeat sends may store the same word: no monotonic requirement.
            Assert.IsTrue(client.SendCommand(1));
            Assert.AreEqual(BridgeStatus.Ok, NativeBridge.cg_test_writer_read_command(out command, out ack));
            Assert.AreEqual(2u, command);
            Assert.AreEqual(0u, ack);

            // The service acks through the writer-side seam; 5.12 has no
            // reader-side read-ack function.
            Assert.AreEqual(BridgeStatus.Ok, NativeBridge.cg_test_writer_ack_command(2));
            Assert.AreEqual(BridgeStatus.Ok, NativeBridge.cg_test_writer_read_command(out command, out ack));
            Assert.AreEqual(2u, command);
            Assert.AreEqual(2u, ack, "cg_test_writer_ack_command round-trips");

            Assert.IsFalse(client.SendCommand(0), "0 is the idle word and is rejected");
            Assert.AreEqual(BridgeStatus.InvalidArg, client.LastStatus);
            Assert.IsFalse(client.SendCommand(uint.MaxValue), "UINT32_MAX would wrap to idle and is rejected");
            Assert.AreEqual(BridgeStatus.InvalidArg, client.LastStatus);
            Assert.AreEqual(BridgeStatus.Ok, NativeBridge.cg_test_writer_read_command(out command, out ack));
            Assert.AreEqual(2u, command, "rejected commands leave the word untouched");
            Assert.AreEqual(2u, ack);
        }

        [Test]
        public void TornRawHeadPublishesAreNeverAccepted()
        {
            BridgeClient client = Open();
            SetFreshHeartbeat();

            BridgeHeadSample good = BridgeSamples.Head(9);
            Assert.AreEqual(BridgeStatus.Ok, NativeBridge.cg_test_writer_publish_head(ref good));
            Assert.IsTrue(client.TryReadHead(out BridgeHeadSample baseline));

            // The injected payload differs from the baseline in every derived
            // field, so a reader that skipped the seqlock validation would
            // return this sample and fail the assertions below.
            BridgeHeadSample injected = BridgeSamples.Head(1009);

            // seq_a odd: the writer is mid-publish, so every bounded attempt
            // retries and the call times out. The last injected pair must be
            // even/even so the slot can recover below (a real publish bumps
            // seq_a by two, so an odd leftover would never clear).
            Assert.AreEqual(BridgeStatus.Ok, NativeBridge.cg_test_writer_publish_head_raw(1, 1, ref injected));
            AssertNoMismatchedSample(client, baseline, injected);

            // Even seq_a with a mismatched seq_b: a copy is never accepted.
            Assert.AreEqual(BridgeStatus.Ok, NativeBridge.cg_test_writer_publish_head_raw(2, 4, ref injected));
            AssertNoMismatchedSample(client, baseline, injected);

            // Even seq_a with a larger mismatched seq_b.
            Assert.AreEqual(BridgeStatus.Ok, NativeBridge.cg_test_writer_publish_head_raw(4, 6, ref injected));
            AssertNoMismatchedSample(client, baseline, injected);

            // A proper publish recovers the slot.
            Assert.AreEqual(BridgeStatus.Ok, NativeBridge.cg_test_writer_publish_head(ref good));
            Assert.IsTrue(client.TryReadHead(out BridgeHeadSample recovered));
            Assert.IsTrue(BridgeSamples.HeadEquals(baseline, recovered), "the recovered sample is the good one");
        }

        [Test]
        public void FailedOpenReportsNotReadyThroughTheStatusOverload()
        {
            NativeBridge.cg_test_writer_close();

            Assert.IsFalse(BridgeClient.TryOpen(out BridgeClient client, out BridgeStatus status));
            Assert.IsNull(client);
            Assert.AreEqual(BridgeStatus.NotReady, status);
        }

        [Test]
        public void DisposeIsIdempotentAndInvalidatesTheClient()
        {
            BridgeClient client = Open();

            client.Dispose();
            client.Dispose(); // the second dispose is a no-op

            Assert.IsFalse(client.IsOpen);
            Assert.IsFalse(client.TryReadHead(out _));
            Assert.AreEqual(BridgeStatus.InvalidArg, client.LastStatus);
            Assert.IsFalse(client.SendCommand(1));
            Assert.AreEqual(BridgeStatus.InvalidArg, client.LastStatus);
        }

        private static void AssertNoMismatchedSample(
            BridgeClient client, BridgeHeadSample baseline, BridgeHeadSample injected)
        {
            for (int attempt = 0; attempt < 8; attempt++)
            {
                if (client.TryReadHead(out BridgeHeadSample sample))
                {
                    Assert.IsFalse(
                        BridgeSamples.HeadEquals(injected, sample),
                        "the reader accepted a payload published behind mismatched seqlock counters");
                    Assert.IsTrue(
                        BridgeSamples.HeadEquals(baseline, sample),
                        "an unexpected sample was returned");
                }
                else
                {
                    Assert.AreEqual(BridgeStatus.Timeout, client.LastStatus, "torn counters must time out");
                }
            }
        }
    }
}

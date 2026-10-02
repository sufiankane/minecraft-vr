using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace Cubeglass.Unity.Bridge.Tests
{
    /// <summary>
    /// One writer thread (the native writer is a single-instance, single-writer
    /// singleton) publishes 20k head and hand samples through a C# Task while
    /// the test thread reads both slots and recomputes every payload field from
    /// the sample's own sequence counter. A torn or unvalidated copy shows up
    /// as a mismatch; a bounded retry budget means the reader may time out, but
    /// never observe a mismatched sample and never loops unboundedly.
    /// </summary>
    public class NativeBridgeStressTests : NativeBridgeTestBase
    {
        private const uint SampleCount = 20_000;
        private const uint WarmupSequence = 1;
        private const int DrainAttempts = 10_000;

        [Test]
        public void ConcurrentWriterAndReaderNeverObserveTornSamples()
        {
            BridgeClient client = Open();
            SetFreshHeartbeat();

            // Warm the writer with one synchronous publish: the first read below
            // then always has a valid sample to return, and the writer task
            // starts its bulk run only after that read has completed.
            BridgeHeadSample warmupHead = BridgeSamples.Head(WarmupSequence);
            Assert.AreEqual(
                BridgeStatus.Ok,
                NativeBridge.cg_test_writer_publish_head(ref warmupHead),
                "warm-up publish_head");
            BridgeHandFrame warmupHands = BridgeSamples.Hands(WarmupSequence);
            Assert.AreEqual(
                BridgeStatus.Ok,
                NativeBridge.cg_test_writer_publish_hands(ref warmupHands),
                "warm-up publish_hands");

            var writerGate = new ManualResetEventSlim(false);
            Exception writerFailure = null;
            Task writer = Task.Run(() =>
            {
                try
                {
                    // Do not race the reader: wait until it has read the warm-up
                    // sample, so the overlapping reads counted below are real.
                    if (!writerGate.Wait(TimeSpan.FromSeconds(30)))
                    {
                        throw new TimeoutException("reader did not reach the stress loop");
                    }

                    for (uint sequence = WarmupSequence + 1; sequence <= SampleCount; sequence++)
                    {
                        BridgeHeadSample head = BridgeSamples.Head(sequence);
                        if (NativeBridge.cg_test_writer_publish_head(ref head) != BridgeStatus.Ok)
                        {
                            throw new InvalidOperationException("publish_head failed at sequence " + sequence);
                        }

                        BridgeHandFrame hands = BridgeSamples.Hands(sequence);
                        if (NativeBridge.cg_test_writer_publish_hands(ref hands) != BridgeStatus.Ok)
                        {
                            throw new InvalidOperationException("publish_hands failed at sequence " + sequence);
                        }
                    }
                }
                catch (Exception exception)
                {
                    writerFailure = exception;
                }
            });

            long headReads = 0;
            long handReads = 0;
            long headReadsWhileWriting = 0;
            long handReadsWhileWriting = 0;
            long mismatches = 0;
            ReadHead(client, ref headReads, ref mismatches);
            ReadHands(client, ref handReads, ref mismatches);
            writerGate.Set();

            var elapsed = Stopwatch.StartNew();
            while (!writer.IsCompleted && elapsed.ElapsedMilliseconds < 30_000)
            {
                ReadHead(client, ref headReads, ref mismatches);
                ReadHands(client, ref handReads, ref mismatches);
                if (!writer.IsCompleted)
                {
                    headReadsWhileWriting++;
                    handReadsWhileWriting++;
                }
            }

            Assert.IsTrue(writer.Wait(TimeSpan.FromSeconds(30)), "writer task did not finish");
            Assert.IsTrue(writerFailure == null, writerFailure == null ? null : writerFailure.ToString());

            // The writer has stopped, so the slots are stable: a bounded drain
            // must reach the final sample and it must match its own sequence.
            Assert.IsTrue(
                TryReadHeadUntilSequence(client, SampleCount, ref headReads, ref mismatches),
                "final head sample not observed");
            Assert.IsTrue(
                TryReadHandsUntilSequence(client, SampleCount, ref handReads, ref mismatches),
                "final hand frame not observed");

            Assert.AreEqual(0L, mismatches, "torn or mismatched samples observed");
            Assert.Greater(headReadsWhileWriting, 0L, "no head sample was read while the writer task was running");
            Assert.Greater(handReadsWhileWriting, 0L, "no hand frame was read while the writer task was running");
            Assert.Greater(headReads, 0L, "no head sample was read");
            Assert.Greater(handReads, 0L, "no hand frame was read");
        }

        private static void ReadHead(BridgeClient client, ref long reads, ref long mismatches)
        {
            if (client.TryReadHead(out BridgeHeadSample sample))
            {
                reads++;
                if (!BridgeSamples.HeadMatches(sample))
                {
                    mismatches++;
                }
            }
            else
            {
                ExpectTransient(client.LastStatus, "head");
            }
        }

        private static void ReadHands(BridgeClient client, ref long reads, ref long mismatches)
        {
            if (client.TryReadHands(out BridgeHandFrame frame))
            {
                reads++;
                if (!BridgeSamples.HandsMatch(frame))
                {
                    mismatches++;
                }
            }
            else
            {
                ExpectTransient(client.LastStatus, "hands");
            }
        }

        private static bool TryReadHeadUntilSequence(
            BridgeClient client, uint sequence, ref long reads, ref long mismatches)
        {
            for (int attempt = 0; attempt < DrainAttempts; attempt++)
            {
                if (client.TryReadHead(out BridgeHeadSample sample))
                {
                    reads++;
                    if (!BridgeSamples.HeadMatches(sample))
                    {
                        mismatches++;
                    }

                    if (sample.Sequence == sequence)
                    {
                        return true;
                    }
                }
                else
                {
                    ExpectTransient(client.LastStatus, "head");
                }
            }

            return false;
        }

        private static bool TryReadHandsUntilSequence(
            BridgeClient client, uint sequence, ref long reads, ref long mismatches)
        {
            for (int attempt = 0; attempt < DrainAttempts; attempt++)
            {
                if (client.TryReadHands(out BridgeHandFrame frame))
                {
                    reads++;
                    if (!BridgeSamples.HandsMatch(frame))
                    {
                        mismatches++;
                    }

                    if (frame.Sequence == sequence)
                    {
                        return true;
                    }
                }
                else
                {
                    ExpectTransient(client.LastStatus, "hands");
                }
            }

            return false;
        }

        private static void ExpectTransient(BridgeStatus status, string slot)
        {
            Assert.IsTrue(
                status == BridgeStatus.NotReady || status == BridgeStatus.Timeout,
                slot + " read failed with unexpected status " + status);
        }
    }
}

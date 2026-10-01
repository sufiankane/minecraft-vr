using System;
using NUnit.Framework;

namespace Cubeglass.CoreMath.Tests
{
    [TestFixture]
    public sealed class ClockMapperTests
    {
        // Host time for an SDK instant when the true clock offset is offsetSeconds.
        private static long HostAt(double sdkSeconds, double offsetSeconds)
        {
            return HostTime.ToNanoseconds(sdkSeconds + offsetSeconds);
        }

        [Test]
        public void ConstantOffsetMapsExactly()
        {
            ClockMapper mapper = new ClockMapper();
            for (int i = 1; i <= 3; ++i)
            {
                double sdk = (double)i;
                mapper.AddSample(sdk, HostAt(sdk, 0.0005));
            }

            Assert.That(mapper.SampleCount, Is.EqualTo(3));
            Assert.That(mapper.OffsetSeconds, Is.EqualTo(0.0005).Within(1e-9));
            Assert.That(mapper.Map(4.0), Is.EqualTo(4_000_500_000L));
        }

        [Test]
        public void EvenSampleCountUsesMeanOfTwoMiddleOffsets()
        {
            ClockMapper mapper = new ClockMapper();
            mapper.AddSample(1.0, 1_000_400_000L);
            mapper.AddSample(2.0, 2_000_500_000L);
            mapper.AddSample(3.0, 3_000_600_000L);
            mapper.AddSample(4.0, 4_000_400_000L);

            Assert.That(mapper.OffsetSeconds, Is.EqualTo(0.00045).Within(1e-12));
            Assert.That(mapper.Map(5.0), Is.EqualTo(5_000_450_000L));
        }

        [Test]
        public void IsReadyAtEightSamples()
        {
            ClockMapper mapper = new ClockMapper();
            for (int i = 0; i < 7; ++i)
            {
                double sdk = (double)i;
                mapper.AddSample(sdk, HostAt(sdk, 0.001));
                Assert.That(mapper.IsReady(), Is.False, $"after {mapper.SampleCount} samples");
            }

            const double Sdk = 7.0;
            mapper.AddSample(Sdk, HostAt(Sdk, 0.001));
            Assert.That(mapper.IsReady(), Is.True);
            Assert.That(mapper.SampleCount, Is.EqualTo(8));
        }

        [Test]
        public void MedianTracksJitteredOffsetWithinHalfMillisecond()
        {
            ClockMapper mapper = new ClockMapper();
            Random generator = new Random(20261001);

            const double Offset = 0.005;
            const double Jitter = 0.0015;
            const int Samples = 200;
            for (int i = 0; i < Samples; ++i)
            {
                double sdk = 0.02 * (double)i;
                // The C++ suite draws its jitter from mt19937; the C# mirror uses
                // the pinned System.Random stream instead, so the scenario (not
                // the exact sample stream) is what the two tests share.
                double unit = generator.NextDouble();
                double jitter = ((unit * 2.0) - 1.0) * Jitter;
                mapper.AddSample(sdk, HostAt(sdk, Offset + jitter));
            }

            Assert.That(mapper.SampleCount, Is.EqualTo(ClockMapper.WindowSize));
            Assert.That(Math.Abs(mapper.OffsetSeconds - Offset), Is.LessThan(0.0005));
        }

        [Test]
        public void MedianTracksDriftWithinOneMillisecond()
        {
            ClockMapper mapper = new ClockMapper();
            const double DriftPerSecond = 0.002;
            const double StepSeconds = 0.02;
            const int Samples = 250;

            for (int i = 0; i < Samples; ++i)
            {
                double sdk = StepSeconds * (double)i;
                double trueOffset = DriftPerSecond * sdk;
                mapper.AddSample(sdk, HostAt(sdk, trueOffset));
                if (i >= 100)
                {
                    Assert.That(
                        Math.Abs(mapper.OffsetSeconds - trueOffset),
                        Is.LessThan(0.001),
                        $"at sample {i}");
                }
            }
        }

        [Test]
        public void WindowEvictsOldOffsets()
        {
            ClockMapper mapper = new ClockMapper();
            const double OffsetA = 0.001;
            const double OffsetB = 0.010;

            for (int i = 0; i < 32; ++i)
            {
                double sdk = 0.02 * (double)i;
                mapper.AddSample(sdk, HostAt(sdk, OffsetA));
            }

            Assert.That(mapper.SampleCount, Is.EqualTo(ClockMapper.WindowSize));
            Assert.That(mapper.OffsetSeconds, Is.EqualTo(OffsetA).Within(1e-9));

            for (int i = 32; i < 64; ++i)
            {
                double sdk = 0.02 * (double)i;
                mapper.AddSample(sdk, HostAt(sdk, OffsetB));
            }

            Assert.That(mapper.SampleCount, Is.EqualTo(ClockMapper.WindowSize));
            Assert.That(mapper.OffsetSeconds, Is.EqualTo(OffsetB).Within(1e-9));
            Assert.That(mapper.Map(100.0), Is.EqualTo(100_010_000_000L));
        }

        [Test]
        public void IgnoresNonFiniteSamples()
        {
            ClockMapper mapper = new ClockMapper();
            mapper.AddSample(1.0, HostAt(1.0, 0.5));
            long mappedBefore = mapper.Map(2.0);

            mapper.AddSample(double.NaN, 9_000_000_000L);
            mapper.AddSample(double.PositiveInfinity, 9_000_000_000L);
            mapper.AddSample(double.NegativeInfinity, 9_000_000_000L);

            Assert.That(mapper.SampleCount, Is.EqualTo(1));
            Assert.That(mapper.Map(2.0), Is.EqualTo(mappedBefore));
        }

        [Test]
        public void MapBeforeFirstSampleIsPureConversion()
        {
            ClockMapper mapper = new ClockMapper();

            Assert.That(mapper.SampleCount, Is.EqualTo(0));
            Assert.That(mapper.IsReady(), Is.False);
            Assert.That(mapper.OffsetSeconds, Is.EqualTo(0.0));
            Assert.That(mapper.Map(4.0), Is.EqualTo(HostTime.ToNanoseconds(4.0)));
            Assert.That(mapper.Map(-1.25), Is.EqualTo(HostTime.ToNanoseconds(-1.25)));
        }

        [Test]
        public void NonFiniteMapQueryIsZero()
        {
            ClockMapper mapper = new ClockMapper();
            mapper.AddSample(1.0, HostAt(1.0, 0.5));

            Assert.That(mapper.Map(double.NaN), Is.EqualTo(0L));
            Assert.That(mapper.Map(double.PositiveInfinity), Is.EqualTo(0L));
            Assert.That(mapper.Map(double.NegativeInfinity), Is.EqualTo(0L));

            ClockMapper empty = new ClockMapper();
            Assert.That(empty.Map(double.NaN), Is.EqualTo(0L));
        }
    }
}

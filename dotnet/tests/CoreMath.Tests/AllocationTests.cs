using System;
using NUnit.Framework;

namespace Cubeglass.CoreMath.Tests
{
    /// <summary>
    /// Allocation gate for the core-math hot paths (S1-WI2f). The measured loop
    /// runs after a warm-up so JIT compilation and static initialization are not
    /// counted, and every NUnit assertion happens outside the measured region.
    /// </summary>
    [TestFixture]
    public sealed class AllocationTests
    {
        private const int WarmupIterations = 1_000;
        private const int MeasuredIterations = 100_000;

        [Test]
        public void CoreMathOperationsAllocateNothing()
        {
            Quat rotation = Quat.FromAxisAngle(new Vec3(0.0, 1.0, 0.0), 0.7);
            Quat other = Quat.FromAxisAngle(new Vec3(1.0, 0.0, 0.0), 1.2);
            Vec3 point = new Vec3(1.0, 2.0, 3.0);
            Pose parent = new Pose(new Vec3(1.0, 2.0, 3.0), rotation);
            Pose child = new Pose(new Vec3(-0.5, 0.25, 1.5), other);
            float[] sdk = { 0.1F, 0.2F, 0.3F, 0.0F, 0.0F, 0.0F, 1.0F };
            ClockMapper mapper = new ClockMapper();

            double warmupSink = RunOperations(rotation, other, point, parent, child, sdk, mapper, WarmupIterations);

            long before = GC.GetAllocatedBytesForCurrentThread();
            double measuredSink = RunOperations(rotation, other, point, parent, child, sdk, mapper, MeasuredIterations);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.That(warmupSink, Is.GreaterThan(0.0), "the warm-up must run the operations");
            Assert.That(measuredSink, Is.GreaterThan(warmupSink), "the measured loop must run the operations");
            Assert.That(
                allocated,
                Is.EqualTo(0L),
                $"Core-math operations allocated {allocated} bytes over {MeasuredIterations} iterations");
        }

        private static double RunOperations(
            Quat rotation,
            Quat other,
            Vec3 point,
            Pose parent,
            Pose child,
            float[] sdk,
            ClockMapper mapper,
            int iterations)
        {
            double sink = 0.0;
            for (int i = 0; i < iterations; ++i)
            {
                double t = (double)i / (double)iterations;
                Vec3 rotated = rotation.Rotate(point);
                Quat interpolated = Quat.Slerp(rotation, other, t);
                Pose composed = Pose.Compose(parent, child);
                Pose inverted = composed.Inverse();
                double sdkSeconds = 10.0 + (0.01 * i);
                mapper.AddSample(sdkSeconds, 10_000_500_000L + (10_000_000L * i));
                long mapped = mapper.Map(sdkSeconds);
                Pose fromSdk = Pose.FromSdk(sdk);
                sink += rotated.X + interpolated.W + composed.Position.X + inverted.Position.Y + mapped +
                        fromSdk.Position.Z;
            }

            return sink;
        }
    }
}

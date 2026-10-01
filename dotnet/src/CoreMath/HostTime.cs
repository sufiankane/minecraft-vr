using System;

namespace Cubeglass.CoreMath
{
    /// <summary>
    /// Nanoseconds on the one monotonic host timeline (ADR-0004).
    /// </summary>
    public static class HostTime
    {
        /// <summary>Nanoseconds in one second.</summary>
        public const long NanosecondsPerSecond = 1_000_000_000;

        /// <summary>
        /// Converts nanoseconds on the host timeline to seconds. Uses division
        /// by <see cref="NanosecondsPerSecond"/> (not multiplication by
        /// <c>1e-9</c>) to mirror the C++ expression exactly.
        /// </summary>
        public static double ToSeconds(long nanoseconds)
        {
            return (double)nanoseconds / (double)NanosecondsPerSecond;
        }

        /// <summary>
        /// Converts <paramref name="seconds"/> to nanoseconds, rounding to the
        /// nearest nanosecond with halfway cases away from zero, the same rule
        /// <c>ClockMapper</c> uses.
        /// </summary>
        public static long ToNanoseconds(double seconds)
        {
            return (long)Math.Round(
                seconds * (double)NanosecondsPerSecond,
                MidpointRounding.AwayFromZero);
        }
    }
}

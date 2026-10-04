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
        /// 2^63, the smallest magnitude a <see cref="double"/> cannot round
        /// into <see cref="long"/>; the C++ expression uses the same constant.
        /// </summary>
        private const double MaxNanos = (double)long.MaxValue;

        /// <summary>
        /// -2^63, the largest negative magnitude a <see cref="double"/> cannot
        /// round into <see cref="long"/>.
        /// </summary>
        private const double MinNanos = (double)long.MinValue;

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
        /// <remarks>
        /// Total for every <see cref="double"/> and independent of the
        /// runtime's float-to-integer cast behaviour: NaN maps to 0 and a value
        /// whose nanosecond result lies outside the host range (a hostile
        /// finite input or an infinity) saturates at
        /// <see cref="long.MinValue"/>/<see cref="long.MaxValue"/> instead of
        /// relying on an unspecified conversion. This mirrors the C++
        /// <c>time.hpp</c> contract (M-11).
        /// </remarks>
        public static long ToNanoseconds(double seconds)
        {
            if (double.IsNaN(seconds))
            {
                return 0L;
            }

            double nanoseconds = seconds * (double)NanosecondsPerSecond;
            if (nanoseconds >= MaxNanos)
            {
                return long.MaxValue;
            }

            if (nanoseconds <= MinNanos)
            {
                return long.MinValue;
            }

            return (long)Math.Round(nanoseconds, MidpointRounding.AwayFromZero);
        }
    }
}

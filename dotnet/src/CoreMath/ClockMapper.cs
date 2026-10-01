using System;

namespace Cubeglass.CoreMath
{
    /// <summary>
    /// Estimates the offset between SDK seconds and the host timeline (ADR-0004).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="AddSample"/> records
    /// <c>host_time / 1e9 - sdk_seconds</c> for the most recent sample in a
    /// fixed window of <see cref="WindowSize"/> samples. <see cref="OffsetSeconds"/>
    /// is the median of the window's offsets (odd count: the middle value; even
    /// count: the mean of the two middle values) computed from a sorted stack
    /// copy. <see cref="Map"/> applies that median:
    /// <c>round((sdk_seconds + offset) * 1e9)</c> with halfway cases away from
    /// zero, the same rule <see cref="HostTime.ToNanoseconds"/> uses.
    /// </para>
    /// <para>
    /// Defined behaviours:
    /// <list type="bullet">
    /// <item><description>before any sample the offset is zero, so
    /// <c>Map(sdk) == HostTime.ToNanoseconds(sdk)</c>;</description></item>
    /// <item><description>a non-finite <c>sdk_seconds</c> passed to
    /// <see cref="AddSample"/> is ignored;</description></item>
    /// <item><description><see cref="Map"/> of a non-finite <c>sdk_seconds</c>
    /// returns 0 (never undefined);</description></item>
    /// <item><description><see cref="IsReady"/> reports true from
    /// <see cref="ReadySampleCount"/> samples onward.</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// The window deliberately lags a step change in the offset by up to
    /// <c>WindowSize / 2</c> samples; the median keeps the estimate robust to
    /// outlier callbacks. <see cref="HostTime"/> samples are integral, so
    /// <see cref="AddSample"/> never rejects them.
    /// </para>
    /// <para>
    /// Not thread-safe: callers must serialise access to one instance. Every
    /// operation allocates nothing; <see cref="OffsetSeconds"/> sorts a stack
    /// copy of the window.
    /// </para>
    /// </remarks>
    public sealed class ClockMapper
    {
        /// <summary>Number of <c>(sdk_seconds, host_time)</c> samples retained (ADR-0004).</summary>
        public const int WindowSize = 32;

        /// <summary>Minimum held samples for <see cref="IsReady"/> (ADR-0004).</summary>
        public const int ReadySampleCount = 8;

        private readonly double[] _offsets = new double[WindowSize];
        private int _nextSample;
        private int _sampleCount;

        /// <summary>
        /// Records one <c>(sdk_seconds, host_time)</c> pair as its offset in
        /// seconds.
        /// </summary>
        /// <param name="sdkSeconds">SDK instant in seconds; non-finite values are ignored.</param>
        /// <param name="hostTime">Host timeline nanoseconds for the same instant.</param>
        /// <remarks>
        /// Non-finite <paramref name="sdkSeconds"/> values are ignored: they
        /// neither enter the window nor change <see cref="SampleCount"/>. Once
        /// the window is full the oldest sample is evicted. Allocates nothing.
        /// </remarks>
        public void AddSample(double sdkSeconds, long hostTime)
        {
            if (!double.IsFinite(sdkSeconds))
            {
                return;
            }

            _offsets[_nextSample] =
                ((double)hostTime / (double)HostTime.NanosecondsPerSecond) - sdkSeconds;
            _nextSample = (_nextSample + 1) % WindowSize;
            if (_sampleCount < WindowSize)
            {
                ++_sampleCount;
            }
        }

        /// <summary>
        /// Median offset in seconds of the samples currently held; 0 before any
        /// sample.
        /// </summary>
        /// <remarks>
        /// Odd count takes the middle value of the sorted window, even count the
        /// mean of the two middle values. Allocates nothing.
        /// </remarks>
        public double OffsetSeconds
        {
            get
            {
                if (_sampleCount == 0)
                {
                    return 0.0;
                }

                Span<double> sorted = stackalloc double[WindowSize];
                _offsets.AsSpan(0, _sampleCount).CopyTo(sorted);
                SortAscending(sorted.Slice(0, _sampleCount));

                int middle = _sampleCount / 2;
                if ((_sampleCount % 2) != 0)
                {
                    return sorted[middle];
                }

                return (sorted[middle - 1] + sorted[middle]) / 2.0;
            }
        }

        /// <summary>
        /// Maps an SDK instant to the host timeline:
        /// <c>round((sdk_seconds + OffsetSeconds) * 1e9)</c>, rounding halfway
        /// cases away from zero.
        /// </summary>
        /// <param name="sdkSeconds">SDK instant in seconds; non-finite values return 0.</param>
        /// <returns>
        /// The host timeline nanosecond value. For finite input the precondition
        /// is that the rounded result fits in <see cref="long"/>. Allocates
        /// nothing.
        /// </returns>
        public long Map(double sdkSeconds)
        {
            if (!double.IsFinite(sdkSeconds))
            {
                return 0L;
            }

            double offsetSeconds = OffsetSeconds;
            return (long)Math.Round(
                (sdkSeconds + offsetSeconds) * (double)HostTime.NanosecondsPerSecond,
                MidpointRounding.AwayFromZero);
        }

        /// <summary>True once <see cref="SampleCount"/> is at least <see cref="ReadySampleCount"/>.</summary>
        public bool IsReady()
        {
            return _sampleCount >= ReadySampleCount;
        }

        /// <summary>Number of samples currently held, 0 to <see cref="WindowSize"/>.</summary>
        public int SampleCount
        {
            get { return _sampleCount; }
        }

        private static void SortAscending(Span<double> values)
        {
            for (int i = 1; i < values.Length; ++i)
            {
                double current = values[i];
                int j = i - 1;
                while (j >= 0 && values[j] > current)
                {
                    values[j + 1] = values[j];
                    --j;
                }

                values[j + 1] = current;
            }
        }
    }
}

using System;
using System.Collections.Generic;

namespace Cubeglass.Gameplay
{
    /// <summary>
    /// A pure <see cref="IInputProvider"/> that replays a pre-sorted timeline
    /// of frames (ADR-0008).
    /// </summary>
    /// <remarks>
    /// The timeline is copied at construction, so later mutations of the
    /// caller's list do not change samples. Frame times must be finite and
    /// strictly increasing. <see cref="Sample"/> returns the frame of the
    /// latest entry with <c>time &lt;= timeSeconds</c>; a time before the first
    /// entry returns the first frame and an empty timeline returns the neutral
    /// frame.
    /// </remarks>
    public sealed class ScriptedInputProvider : IInputProvider
    {
        private readonly (double Time, InputFrame Frame)[] _frames;

        /// <summary>Creates a provider over a copy of <paramref name="frames"/>.</summary>
        /// <exception cref="ArgumentNullException"><paramref name="frames"/> is null.</exception>
        /// <exception cref="ArgumentException">
        /// A frame time is NaN or infinite, or the times are not strictly
        /// increasing.
        /// </exception>
        public ScriptedInputProvider(IReadOnlyList<(double time, InputFrame frame)> frames)
        {
            if (frames is null)
            {
                throw new ArgumentNullException(nameof(frames));
            }

            _frames = new (double Time, InputFrame Frame)[frames.Count];
            double previous = double.NegativeInfinity;

            for (int i = 0; i < frames.Count; i++)
            {
                (double time, InputFrame frame) = frames[i];
                if (double.IsNaN(time) || double.IsInfinity(time))
                {
                    throw new ArgumentException("Frame times must be finite.", nameof(frames));
                }

                if (time <= previous)
                {
                    throw new ArgumentException("Frame times must be strictly increasing.", nameof(frames));
                }

                previous = time;
                _frames[i] = (time, frame);
            }
        }

        /// <summary>The number of frames in the timeline.</summary>
        public int Count => _frames.Length;

        /// <inheritdoc />
        public InputFrame Sample(double timeSeconds)
        {
            if (_frames.Length == 0)
            {
                return default;
            }

            if (!(timeSeconds >= _frames[0].Time))
            {
                return _frames[0].Frame;
            }

            int low = 0;
            int high = _frames.Length - 1;
            while (low < high)
            {
                int middle = low + ((high - low + 1) / 2);
                if (_frames[middle].Time <= timeSeconds)
                {
                    low = middle;
                }
                else
                {
                    high = middle - 1;
                }
            }

            return _frames[low].Frame;
        }
    }
}

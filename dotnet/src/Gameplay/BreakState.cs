using System;
using Cubeglass.Voxel;

namespace Cubeglass.Gameplay
{
    /// <summary>
    /// The accumulated hold-to-break progress on one target cell (ADR-0008).
    /// </summary>
    /// <remarks>
    /// A value type, so the service holds one instance without allocating.
    /// <see cref="RequiredSeconds"/> is <c>max(0.05, hardness)</c> from the
    /// target block's <see cref="BlockDefinition.Hardness"/>;
    /// <see cref="Progress"/> is the fraction accumulated, clamped to
    /// <c>[0, 1]</c>. The service replaces the state whenever the target cell
    /// changes, which resets progress.
    /// </remarks>
    public readonly struct BreakState
    {
        /// <summary>Creates a break state for <paramref name="cell"/>.</summary>
        /// <param name="cell">The target cell being broken.</param>
        /// <param name="accumulatedSeconds">Hold time already accumulated, at least zero.</param>
        /// <param name="requiredSeconds">Hold time needed, strictly positive.</param>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="accumulatedSeconds"/> is negative or non-finite, or
        /// <paramref name="requiredSeconds"/> is not strictly positive or is
        /// non-finite.
        /// </exception>
        public BreakState(Int3 cell, double accumulatedSeconds, double requiredSeconds)
        {
            if (!(accumulatedSeconds >= 0.0) || double.IsInfinity(accumulatedSeconds))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(accumulatedSeconds),
                    accumulatedSeconds,
                    "Accumulated time must be finite and non-negative.");
            }

            if (!(requiredSeconds > 0.0) || double.IsInfinity(requiredSeconds))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(requiredSeconds),
                    requiredSeconds,
                    "Required time must be finite and strictly positive.");
            }

            Cell = cell;
            AccumulatedSeconds = accumulatedSeconds;
            RequiredSeconds = requiredSeconds;
        }

        /// <summary>The target cell being broken.</summary>
        public Int3 Cell { get; }

        /// <summary>Hold time already accumulated, in seconds.</summary>
        public double AccumulatedSeconds { get; }

        /// <summary>Hold time needed to break the target, in seconds.</summary>
        public double RequiredSeconds { get; }

        /// <summary><see cref="AccumulatedSeconds"/> / <see cref="RequiredSeconds"/>, clamped to 1.</summary>
        public float Progress
        {
            get
            {
                double progress = AccumulatedSeconds / RequiredSeconds;
                return progress >= 1.0 ? 1f : (float)progress;
            }
        }

        /// <summary>Whether the accumulated time reached the required time.</summary>
        public bool IsComplete => AccumulatedSeconds >= RequiredSeconds;

        /// <summary>Returns this state with <paramref name="dt"/> seconds added.</summary>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="dt"/> is negative, NaN or infinite.
        /// </exception>
        public BreakState Accumulate(double dt)
        {
            if (!(dt >= 0.0) || double.IsInfinity(dt))
            {
                throw new ArgumentOutOfRangeException(nameof(dt), dt, "dt must be finite and non-negative.");
            }

            return new BreakState(Cell, AccumulatedSeconds + dt, RequiredSeconds);
        }
    }
}

using System;
using System.Globalization;

namespace Cubeglass.Streaming
{
    /// <summary>
    /// Tunables for <see cref="ChunkStreamingScheduler"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The defaults are the S7 vertical-slice values: an eight-chunk horizontal
    /// view distance, a two-chunk unload hysteresis and four actions per frame
    /// for each budget. The Unity adapter reads
    /// <see cref="MaxMeshUploadsPerFrame"/> for its renderer budget.
    /// </para>
    /// <para>
    /// Every property is validated in its <c>init</c> accessor, so an invalid
    /// configuration cannot be constructed and a running scheduler cannot be
    /// reconfigured into a bad state: <see cref="ViewDistanceChunks"/> must be
    /// in [1, <see cref="MaxViewDistanceChunks"/>], <see cref="UnloadHysteresis"/>
    /// in [0, <see cref="MaxUnloadHysteresis"/>], each budget in
    /// [0, <see cref="MaxActionsPerFrame"/>] and <see cref="VerticalRadiusChunks"/>
    /// finite in [0, <see cref="MaxVerticalRadiusChunks"/>]. Out-of-range values
    /// throw <see cref="ArgumentOutOfRangeException"/> at construction. The caps
    /// bound the desired box (at most 65^3 chunks), keep every radius sum clear
    /// of <see cref="int"/> overflow, and keep a constructed config's action
    /// budgets meaningful.
    /// </para>
    /// </remarks>
    public sealed class StreamingConfig
    {
        /// <summary>Smallest horizontal chunk radius that construction accepts.</summary>
        public const int MinViewDistanceChunks = 1;

        /// <summary>Largest horizontal chunk radius that construction accepts.</summary>
        public const int MaxViewDistanceChunks = 32;

        /// <summary>Largest unload hysteresis that construction accepts.</summary>
        public const int MaxUnloadHysteresis = 32;

        /// <summary>Largest per-frame action budget that construction accepts.</summary>
        public const int MaxActionsPerFrame = 4096;

        /// <summary>Largest vertical chunk radius that construction accepts.</summary>
        public const float MaxVerticalRadiusChunks = 32f;

        private int _viewDistanceChunks = 8;
        private int _unloadHysteresis = 2;
        private int _maxLoadsPerFrame = 4;
        private int _maxUnloadsPerFrame = 4;
        private int _maxMeshUploadsPerFrame = 4;
        private float _verticalRadiusChunks = 2f;

        /// <summary>Horizontal chunk radius kept desired around the player's chunk.</summary>
        /// <exception cref="ArgumentOutOfRangeException">
        /// The value is outside [1, <see cref="MaxViewDistanceChunks"/>].
        /// </exception>
        public int ViewDistanceChunks
        {
            get => _viewDistanceChunks;
            init => _viewDistanceChunks = ValidateInt(
                value, MinViewDistanceChunks, MaxViewDistanceChunks, nameof(ViewDistanceChunks));
        }

        /// <summary>Extra chunks a resident chunk may drift beyond the view distance before it unloads.</summary>
        /// <exception cref="ArgumentOutOfRangeException">
        /// The value is outside [0, <see cref="MaxUnloadHysteresis"/>].
        /// </exception>
        public int UnloadHysteresis
        {
            get => _unloadHysteresis;
            init => _unloadHysteresis = ValidateInt(value, 0, MaxUnloadHysteresis, nameof(UnloadHysteresis));
        }

        /// <summary>Maximum Load actions emitted per update.</summary>
        /// <exception cref="ArgumentOutOfRangeException">
        /// The value is outside [0, <see cref="MaxActionsPerFrame"/>].
        /// </exception>
        public int MaxLoadsPerFrame
        {
            get => _maxLoadsPerFrame;
            init => _maxLoadsPerFrame = ValidateInt(value, 0, MaxActionsPerFrame, nameof(MaxLoadsPerFrame));
        }

        /// <summary>Maximum Unload actions emitted per update.</summary>
        /// <exception cref="ArgumentOutOfRangeException">
        /// The value is outside [0, <see cref="MaxActionsPerFrame"/>].
        /// </exception>
        public int MaxUnloadsPerFrame
        {
            get => _maxUnloadsPerFrame;
            init => _maxUnloadsPerFrame = ValidateInt(value, 0, MaxActionsPerFrame, nameof(MaxUnloadsPerFrame));
        }

        /// <summary>Maximum Upload actions emitted per update.</summary>
        /// <exception cref="ArgumentOutOfRangeException">
        /// The value is outside [0, <see cref="MaxActionsPerFrame"/>].
        /// </exception>
        public int MaxMeshUploadsPerFrame
        {
            get => _maxMeshUploadsPerFrame;
            init => _maxMeshUploadsPerFrame = ValidateInt(value, 0, MaxActionsPerFrame, nameof(MaxMeshUploadsPerFrame));
        }

        /// <summary>Vertical chunk radius kept desired around the player's chunk.</summary>
        /// <exception cref="ArgumentOutOfRangeException">
        /// The value is not finite or lies outside
        /// [0, <see cref="MaxVerticalRadiusChunks"/>].
        /// </exception>
        public float VerticalRadiusChunks
        {
            get => _verticalRadiusChunks;
            init => _verticalRadiusChunks = ValidateVerticalRadius(value, nameof(VerticalRadiusChunks));
        }

        private static int ValidateInt(int value, int min, int max, string name)
        {
            if (value < min || value > max)
            {
                throw new ArgumentOutOfRangeException(
                    name,
                    value,
                    string.Format(CultureInfo.InvariantCulture, "Value must be in [{0}, {1}].", min, max));
            }

            return value;
        }

        private static float ValidateVerticalRadius(float value, string name)
        {
            if (!float.IsFinite(value) || value < 0f || value > MaxVerticalRadiusChunks)
            {
                throw new ArgumentOutOfRangeException(
                    name,
                    value,
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "Value must be finite and in [0, {0}].",
                        MaxVerticalRadiusChunks));
            }

            return value;
        }
    }
}

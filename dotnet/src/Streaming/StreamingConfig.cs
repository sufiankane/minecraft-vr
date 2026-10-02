namespace Cubeglass.Streaming
{
    /// <summary>
    /// Tunables for <see cref="ChunkStreamingScheduler"/>.
    /// </summary>
    /// <remarks>
    /// The defaults are the S7 vertical-slice values: an eight-chunk horizontal
    /// view distance, a two-chunk unload hysteresis and four actions per frame
    /// for each budget. The Unity adapter reads
    /// <see cref="MaxMeshUploadsPerFrame"/> for its renderer budget.
    /// </remarks>
    public sealed class StreamingConfig
    {
        /// <summary>Horizontal chunk radius kept desired around the player's chunk.</summary>
        public int ViewDistanceChunks { get; set; } = 8;

        /// <summary>Extra chunks a resident chunk may drift beyond the view distance before it unloads.</summary>
        public int UnloadHysteresis { get; set; } = 2;

        /// <summary>Maximum Load actions emitted per update.</summary>
        public int MaxLoadsPerFrame { get; set; } = 4;

        /// <summary>Maximum Unload actions emitted per update.</summary>
        public int MaxUnloadsPerFrame { get; set; } = 4;

        /// <summary>Maximum Upload actions emitted per update.</summary>
        public int MaxMeshUploadsPerFrame { get; set; } = 4;

        /// <summary>Vertical chunk radius kept desired around the player's chunk.</summary>
        public float VerticalRadiusChunks { get; set; } = 2f;
    }
}

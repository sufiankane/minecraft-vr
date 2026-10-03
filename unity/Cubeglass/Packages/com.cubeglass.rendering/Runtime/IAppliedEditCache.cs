using Cubeglass.Voxel;

namespace Cubeglass.Unity.Rendering
{
    /// <summary>
    /// The in-memory accumulated edit map a host keeps for chunks that are or
    /// were resident (S7 Task 4b fix round). Both boot and regeneration paths
    /// consult it so a delta loaded from disk is merged with (not replaced by)
    /// the session's edits, and an unload/reload cannot revert edits whose file
    /// has not been written yet.
    /// </summary>
    /// <remarks>
    /// <see cref="SaveBatches"/> is the shipped implementation. A manager only
    /// ever reads the accumulated map and seeds loaded cells; it never counts
    /// loaded cells as unsaved edits.
    /// </remarks>
    public interface IAppliedEditCache
    {
        /// <summary>
        /// Seeds the accumulated map for <paramref name="coord"/> with the cells
        /// of a delta loaded from the store. Cells already present are kept
        /// (the session's value is newer); the loaded cells are not counted as
        /// unsaved edits.
        /// </summary>
        void TrackLoadedDelta(ChunkCoord coord, ChunkDelta delta);

        /// <summary>
        /// Returns the chunk's complete in-memory edit map, including loaded
        /// cells and edits not yet flushed, or false when the chunk has no
        /// accumulated edits.
        /// </summary>
        bool TryGetAccumulatedDelta(ChunkCoord coord, out ChunkDelta delta);
    }
}

using Cubeglass.Voxel;

namespace Cubeglass.Streaming
{
    /// <summary>
    /// Receives the streaming actions of one update in the scheduler's
    /// deterministic order.
    /// </summary>
    /// <remarks>
    /// The callbacks run on the caller's thread. A chunk passed to
    /// <see cref="OnLoad"/> is resident as soon as the action was emitted, so a
    /// host that completes the load later only needs to call
    /// <see cref="ChunkStreamingScheduler.NotifyLoaded"/> to keep the scheduler
    /// informed. <see cref="OnUpload"/> returns false when the host's own upload
    /// budget is exhausted: the upload stays pending and no further uploads are
    /// attempted that frame.
    /// </remarks>
    public interface IStreamingTarget
    {
        /// <summary>Load the chunk into memory.</summary>
        void OnLoad(ChunkCoord chunk);

        /// <summary>Release the chunk and any resources built for it.</summary>
        void OnUnload(ChunkCoord chunk);

        /// <summary>Upload the ready mesh of the chunk; false keeps it pending.</summary>
        bool OnUpload(ChunkCoord chunk);
    }
}

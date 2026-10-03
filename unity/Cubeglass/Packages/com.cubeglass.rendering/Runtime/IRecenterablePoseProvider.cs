namespace Cubeglass.Unity.Rendering
{
    /// <summary>
    /// An <see cref="IPoseProvider"/> that can recentre its own origin
    /// (S7 Task 4a, ADR-0011): after <see cref="Recentre"/> the samples it
    /// emits are relative to the head pose at the call, so
    /// <see cref="LateLatchPose"/> keeps an identity baseline and the view
    /// returns to the player's forward.
    /// </summary>
    public interface IRecenterablePoseProvider : IPoseProvider
    {
        /// <summary>
        /// Recentres the source. Sources that cannot be recentred (for example
        /// the native bridge before a command word is pinned) leave this
        /// interface unimplemented; the late latch then falls back to its local
        /// offset reset.
        /// </summary>
        void Recentre();
    }
}

namespace Cubeglass.Mesh
{
    /// <summary>
    /// Owns the reusable mesh streams of <see cref="MeshData"/> (ADR-0007).
    /// </summary>
    /// <remarks>
    /// Task 1 ships the shell only: <see cref="Rent{T}"/> allocates a fresh
    /// array and <see cref="Return{T}"/> drops it, so builds allocate as the
    /// task brief allows. Task 4 replaces the internals with capacity-bucketed
    /// storage, a warm-up API and the zero-allocation gate; the rent/return
    /// seam and <see cref="MeshData.Release"/> stay as they are.
    /// </remarks>
    public sealed class MeshBufferPool
    {
        /// <summary>The process-wide pool used by the meshers.</summary>
        public static MeshBufferPool Shared { get; } = new MeshBufferPool();

#pragma warning disable CA1822 // Rent/Return are instance seams: Task 4 pools per instance.
        /// <summary>
        /// Returns an array of at least <paramref name="minCapacity"/> elements.
        /// </summary>
        internal T[] Rent<T>(int minCapacity)
        {
            return new T[minCapacity];
        }

        /// <summary>
        /// Accepts a buffer previously returned by <see cref="Rent{T}"/>.
        /// </summary>
        internal void Return<T>(T[] buffer)
        {
            _ = buffer;
        }
#pragma warning restore CA1822
    }
}

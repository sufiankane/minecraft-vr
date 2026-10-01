namespace Cubeglass.Voxel
{
    /// <summary>
    /// Finds the first solid cell a ray enters (dossier section 5.9).
    /// </summary>
    public interface IRaycaster
    {
        /// <summary>
        /// Casts <paramref name="ray"/> through <paramref name="w"/> up to and
        /// including <paramref name="maxDistance"/> world units (measured along
        /// the normalised direction) and returns the first solid cell entered,
        /// or null when no solid cell is reached in range. A hit at distance
        /// zero is either an inside hit (<see cref="Int3.Zero"/> normal) or an
        /// origin exactly on the hit cell's entry face (non-zero entry normal).
        /// </summary>
        RayHit? Cast(IWorld w, Ray ray, float maxDistance);
    }
}

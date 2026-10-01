namespace Cubeglass.Voxel
{
    /// <summary>
    /// Finds the first solid cell a ray enters (dossier section 5.9).
    /// </summary>
    public interface IRaycaster
    {
        /// <summary>
        /// Casts <paramref name="ray"/> through <paramref name="w"/> up to
        /// <paramref name="maxDistance"/> world units and returns the first
        /// solid cell entered, or null when no solid cell is reached in range.
        /// </summary>
        RayHit? Cast(IWorld w, Ray ray, float maxDistance);
    }
}

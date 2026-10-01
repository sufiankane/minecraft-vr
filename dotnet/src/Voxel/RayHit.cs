namespace Cubeglass.Voxel
{
    /// <summary>
    /// The first solid cell entered by a ray (dossier section 5.9).
    /// </summary>
    /// <param name="Cell">The hit cell in world cell coordinates.</param>
    /// <param name="Normal">
    /// The entry face normal, pointing toward the ray origin, or
    /// <see cref="Int3.Zero"/> for a ray that starts inside the solid cell.
    /// </param>
    /// <param name="Distance">
    /// Distance from the ray origin along the normalised direction, in world
    /// units; zero for an inside hit.
    /// </param>
    /// <param name="Block">The solid block at <paramref name="Cell"/>.</param>
    public readonly record struct RayHit(Int3 Cell, Int3 Normal, float Distance, BlockId Block);
}

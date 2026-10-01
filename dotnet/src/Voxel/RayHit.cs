namespace Cubeglass.Voxel
{
    /// <summary>
    /// The first solid cell entered by a ray (dossier section 5.9).
    /// </summary>
    /// <param name="Cell">The hit cell in world cell coordinates.</param>
    /// <param name="Normal">
    /// The entry face normal, pointing toward the ray origin, or
    /// <see cref="Int3.Zero"/> for a ray that starts inside the solid cell.
    /// An origin lying exactly on the hit cell's entry face keeps the non-zero
    /// entry normal.
    /// </param>
    /// <param name="Distance">
    /// Distance from the ray origin along the normalised direction, in world
    /// units. Always in <c>[0, maxDistance]</c> for a successful cast; zero for
    /// an inside hit and for an entry hit whose origin lies exactly on the entry
    /// face. The normal distinguishes the two zero-distance cases: it is
    /// <see cref="Int3.Zero"/> only for an inside hit.
    /// </param>
    /// <param name="Block">The solid block at <paramref name="Cell"/>.</param>
    public readonly record struct RayHit(Int3 Cell, Int3 Normal, float Distance, BlockId Block);
}

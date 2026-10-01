using Cubeglass.CoreMath;

namespace Cubeglass.Voxel
{
    /// <summary>
    /// A world-space ray for voxel picking (dossier section 5.9).
    /// </summary>
    /// <remarks>
    /// <see cref="Direction"/> need not be unit length: <see cref="IRaycaster"/>
    /// normalises it internally and measures every returned distance in world
    /// units along that normalised direction.
    /// </remarks>
    public readonly record struct Ray(Vec3 Origin, Vec3 Direction);
}

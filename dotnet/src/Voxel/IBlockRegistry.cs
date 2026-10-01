using System.Collections.Generic;

namespace Cubeglass.Voxel
{
#pragma warning disable CA1716 // Get is frozen by dossier section 5.9.
    /// <summary>
    /// Read-only view of the block definition table (dossier section 5.9).
    /// </summary>
    public interface IBlockRegistry
    {
        /// <summary>
        /// Returns the definition of <paramref name="id"/>.
        /// </summary>
        /// <exception cref="KeyNotFoundException">
        /// No definition with that id exists.
        /// </exception>
        BlockDefinition Get(BlockId id);

        /// <summary>
        /// Every placeable block id in definition order; excludes
        /// <see cref="BlockId.Air"/>.
        /// </summary>
        IReadOnlyList<BlockId> Placeable { get; }
    }
#pragma warning restore CA1716
}

using System.Collections.Generic;

namespace Cubeglass.Voxel
{
#pragma warning disable CA1716 // Get is frozen by dossier section 5.9.
    /// <summary>
    /// Read-only view of the block definition table (dossier section 5.9).
    /// </summary>
    /// <remarks>
    /// The section 5.9 member shape is unchanged, but <see cref="Get"/> is
    /// total by contract: every 16-bit id resolves. An id with no definition
    /// returns the documented fallback (air-equivalent, atlas tile 0) instead
    /// of throwing, so a corrupted save can never stop a frame on lookup.
    /// </remarks>
    public interface IBlockRegistry
    {
        /// <summary>
        /// Returns the definition of <paramref name="id"/>, or the documented
        /// fallback definition when that id has no definition. Never throws
        /// for an id.
        /// </summary>
        BlockDefinition Get(BlockId id);

        /// <summary>
        /// Every placeable block id in definition order; excludes
        /// <see cref="BlockId.Air"/>.
        /// </summary>
        IReadOnlyList<BlockId> Placeable { get; }
    }
#pragma warning restore CA1716
}

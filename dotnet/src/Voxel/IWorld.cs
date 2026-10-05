using System;

namespace Cubeglass.Voxel
{
#pragma warning disable CA1716 // BlockId/IWorld member names are frozen by dossier section 5.9.
    /// <summary>
    /// The read and single-mutation surface of the voxel world (dossier
    /// section 5.9).
    /// </summary>
    public interface IWorld
    {
        /// <summary>
        /// Returns the block at <paramref name="cell"/>, or
        /// <see cref="BlockId.Air"/> when its chunk is not loaded.
        /// </summary>
        BlockId Get(Int3 cell);

        /// <summary>Whether the chunk that owns <paramref name="cell"/> is loaded.</summary>
        bool IsLoaded(Int3 cell);

        /// <summary>
        /// Applies the only mutation path: rejected when the cell's chunk is
        /// unloaded or the current block differs from <c>Expected</c>.
        /// </summary>
        EditResult Apply(in EditCommand cmd);

        /// <summary>
        /// Raised exactly once per applied edit with the owning chunk, the
        /// edited cell and the block transition (ADR-0013), so consumers can
        /// dirty the exact affected chunk set. Subscriber exceptions are
        /// isolated by <see cref="World"/>; a throwing subscriber does not
        /// abort the edit or the remaining subscribers.
        /// </summary>
        event Action<ChunkEdit> ChunkChanged;
    }
#pragma warning restore CA1716
}

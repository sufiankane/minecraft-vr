namespace Cubeglass.Voxel
{
    /// <summary>
    /// The full context of one applied edit (ADR-0013): the owning chunk, the
    /// edited cell and the block transition.
    /// </summary>
    /// <remarks>
    /// Carried by <see cref="IWorld.ChunkChanged"/> so consumers can dirty the
    /// exact affected chunk set
    /// (<see cref="ChunkEditPropagation.FillAffectedChunks"/>) instead of the
    /// conservative Chebyshev-1 neighbourhood. <see cref="Previous"/> is the
    /// value the cell held before the edit (the accepted
    /// <see cref="EditCommand.Expected"/>) and <see cref="New"/> is the value
    /// written.
    /// </remarks>
    /// <param name="Chunk">The chunk that owns <see cref="Cell"/>.</param>
    /// <param name="Cell">The edited cell in world cell coordinates.</param>
    /// <param name="Previous">The block id the cell held before the edit.</param>
    /// <param name="New">The block id written by the edit.</param>
    public readonly record struct ChunkEdit(ChunkCoord Chunk, Int3 Cell, BlockId Previous, BlockId New);
}

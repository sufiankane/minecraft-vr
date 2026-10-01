namespace Cubeglass.Voxel
{
    /// <summary>
    /// A single edit of one world cell (dossier section 5.9).
    /// </summary>
    /// <param name="Cell">The world cell to change.</param>
    /// <param name="Expected">
    /// The block the cell must currently hold; a mismatch rejects the command.
    /// </param>
    /// <param name="New">The block to write when <paramref name="Expected"/> matches.</param>
    /// <param name="Tick">The logical tick that produced the command; not interpreted here.</param>
    public readonly record struct EditCommand(Int3 Cell, BlockId Expected, BlockId New, long Tick);
}

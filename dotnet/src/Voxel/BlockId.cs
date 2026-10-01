namespace Cubeglass.Voxel
{
    /// <summary>
    /// Identifies a block type by its numeric id.
    /// </summary>
    /// <remarks>
    /// Air is id 0 (ADR-0006), so a zero-initialised cell array is air. Value
    /// type with structural equality; the numeric value is what generation,
    /// hashing and persistence use.
    /// </remarks>
    public readonly record struct BlockId(ushort Value)
    {
        /// <summary>The empty block, id 0.</summary>
        public static BlockId Air => new BlockId(0);
    }
}

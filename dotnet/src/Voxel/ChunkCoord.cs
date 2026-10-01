namespace Cubeglass.Voxel
{
    /// <summary>
    /// The integer coordinate of a 16x16x16 chunk on the three world axes.
    /// </summary>
    /// <remarks>
    /// Chunk <c>(0, 0, 0)</c> owns cells <c>[0, 15]</c> per axis; negative
    /// coordinates belong to floor-divided chunks (ADR-0006). Value type with
    /// structural equality.
    /// </remarks>
    public readonly record struct ChunkCoord(int X, int Y, int Z);
}

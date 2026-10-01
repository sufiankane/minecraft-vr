namespace Cubeglass.Voxel
{
    /// <summary>
    /// The immutable properties of one block type (dossier section 5.9).
    /// </summary>
    /// <param name="Id">The block id; unique within a registry.</param>
    /// <param name="Name">The data-file name, for example <c>Grass</c>.</param>
    /// <param name="Solid">Whether the block collides with players.</param>
    /// <param name="Opaque">Whether the block hides neighbouring faces.</param>
    /// <param name="Hardness">Break hardness; zero for air, non-negative.</param>
    /// <param name="AtlasIndexTop">Texture atlas index of the top face.</param>
    /// <param name="AtlasIndexFront">Texture atlas index of the front face.</param>
    /// <param name="AtlasIndexSide">Texture atlas index of the other side faces.</param>
    public sealed record BlockDefinition(
        BlockId Id,
        string Name,
        bool Solid,
        bool Opaque,
        float Hardness,
        int AtlasIndexTop,
        int AtlasIndexFront,
        int AtlasIndexSide);
}

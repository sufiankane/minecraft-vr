namespace Cubeglass.Voxel
{
    /// <summary>
    /// Deterministic chunk generation (dossier section 5.9). The same
    /// <paramref name="c"/> and <paramref name="seed"/> always produce equal
    /// chunk contents; implementations are pure and never touch IO, clocks or
    /// shared state.
    /// </summary>
    public interface IWorldGenerator
    {
        /// <summary>Generates the chunk at <paramref name="c"/> for <paramref name="seed"/>.</summary>
        Chunk Generate(ChunkCoord c, long seed);
    }
}

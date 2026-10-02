using Cubeglass.Voxel;

namespace Cubeglass.Mesh
{
    /// <summary>
    /// Produces engine-neutral mesh data for one chunk (dossier section 5.10).
    /// </summary>
    public interface IChunkMesher
    {
        // Pure. Neighbour access through a read-only snapshot so meshing can run on a worker thread.
        MeshData Build(ChunkSnapshot chunk, NeighbourSnapshot neighbours, IBlockRegistry blocks);
    }
}

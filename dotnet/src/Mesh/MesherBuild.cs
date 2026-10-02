using System;
using Cubeglass.CoreMath;
using Cubeglass.Voxel;

namespace Cubeglass.Mesh
{
    /// <summary>
    /// The five pooled streams one build appends to, with the shared
    /// rent/finish/return lifecycle.
    /// </summary>
    /// <remarks>
    /// This is a mutable struct so a build holds all five streams without
    /// allocating wrappers; the face-specific logic receives it by
    /// <c>ref</c> and appends through the fields.
    /// </remarks>
    internal struct MeshBuffers
    {
        private const int InitialVertexCapacity = 1024;
        private const int InitialIndexCapacity = 2048;

        internal PooledStream<Vector3f> Positions;
        internal PooledStream<Vector3f> Normals;
        internal PooledStream<Vector2f> Uvs;
        internal PooledStream<byte> Ao;
        internal PooledStream<int> Indices;

        internal MeshBuffers(MeshBufferPool pool)
        {
            Positions = new PooledStream<Vector3f>(pool, InitialVertexCapacity);
            Normals = new PooledStream<Vector3f>(pool, InitialVertexCapacity);
            Uvs = new PooledStream<Vector2f>(pool, InitialVertexCapacity);
            Ao = new PooledStream<byte>(pool, InitialVertexCapacity);
            Indices = new PooledStream<int>(pool, InitialIndexCapacity);
        }

        /// <summary>
        /// Computes the inclusive bounds and hands every stream to a pooled
        /// <see cref="MeshData"/>; the streams are detached, so only the
        /// returned mesh owes them to the pool.
        /// </summary>
        internal MeshData Finish(MeshBufferPool pool)
        {
            int vertexCount = Positions.Count;
            int indexCount = Indices.Count;
            Vector3f min = Vector3f.Zero;
            Vector3f max = Vector3f.Zero;
            if (vertexCount > 0)
            {
                min = Positions[0];
                max = Positions[0];
                for (int i = 1; i < vertexCount; i++)
                {
                    Vector3f position = Positions[i];
                    min = new Vector3f(
                        Math.Min(min.X, position.X),
                        Math.Min(min.Y, position.Y),
                        Math.Min(min.Z, position.Z));
                    max = new Vector3f(
                        Math.Max(max.X, position.X),
                        Math.Max(max.Y, position.Y),
                        Math.Max(max.Z, position.Z));
                }
            }

            MeshData mesh = pool.RentMeshData();
            mesh.Initialize(
                Positions.Detach(),
                Normals.Detach(),
                Uvs.Detach(),
                Ao.Detach(),
                Indices.Detach(),
                vertexCount,
                indexCount,
                min,
                max);
            return mesh;
        }

        /// <summary>Returns every still-attached stream to the pool.</summary>
        internal void Return()
        {
            Positions.Return();
            Normals.Return();
            Uvs.Return();
            Ao.Return();
            Indices.Return();
        }
    }

    /// <summary>
    /// The per-face emit loop of one mesher, called by
    /// <see cref="MesherBuild.Run"/> inside the shared rent/return lifecycle.
    /// </summary>
    internal interface IMeshEmitter
    {
        /// <summary>Appends the mesher's faces for one chunk.</summary>
        void Emit(
            ChunkSnapshot chunk,
            NeighbourSnapshot neighbours,
            IBlockRegistry blocks,
            ref MeshBuffers buffers);
    }

    /// <summary>
    /// The build scaffolding shared by <see cref="CulledMesher"/> and
    /// <see cref="GreedyMesher"/>: argument validation, the pooled stream
    /// rent/finish/return flow, and the catch that gives every rent back.
    /// </summary>
    /// <remarks>
    /// The flow is identical in both meshers and byte-for-byte observable
    /// behaviour is unchanged; only the per-face emit loop differs and stays
    /// in each mesher.
    /// </remarks>
    internal static class MesherBuild
    {
        internal static MeshData Run(
            MeshBufferPool pool,
            IMeshEmitter emitter,
            ChunkSnapshot chunk,
            NeighbourSnapshot neighbours,
            IBlockRegistry blocks)
        {
            if (chunk is null)
            {
                throw new ArgumentNullException(nameof(chunk));
            }

            if (neighbours is null)
            {
                throw new ArgumentNullException(nameof(neighbours));
            }

            if (blocks is null)
            {
                throw new ArgumentNullException(nameof(blocks));
            }

            var buffers = new MeshBuffers(pool);
            try
            {
                emitter.Emit(chunk, neighbours, blocks, ref buffers);
                return buffers.Finish(pool);
            }
            catch
            {
                buffers.Return();
                throw;
            }
        }
    }
}

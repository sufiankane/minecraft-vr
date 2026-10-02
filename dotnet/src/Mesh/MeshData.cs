using System;
using Cubeglass.CoreMath;

namespace Cubeglass.Mesh
{
    /// <summary>
    /// Engine-neutral mesh buffers for one chunk in structure-of-arrays form
    /// (dossier section 5.10).
    /// </summary>
    /// <remarks>
    /// The five stream fields are exactly the section 5.10 declaration;
    /// <see cref="VertexCount"/>, <see cref="IndexCount"/>, <see cref="Min"/>,
    /// <see cref="Max"/> and <see cref="Release"/> are additive and recorded
    /// in ADR-0007. The streams are windows over private arrays owned by the
    /// origin <see cref="MeshBufferPool"/>: Task 1 allocates fresh arrays per
    /// build (no pooling yet), Task 4 adds capacity buckets and the zero
    /// allocation gate. <see cref="Release"/> returns every buffer exactly
    /// once; a second call throws.
    /// </remarks>
    public sealed class MeshData
    {
#pragma warning disable CA1051 // Dossier section 5.10 freezes these as public fields.
        public ReadOnlyMemory<Vector3f> Positions;

        public ReadOnlyMemory<Vector3f> Normals;

        public ReadOnlyMemory<Vector2f> Uvs;

        public ReadOnlyMemory<byte> Ao;

        public ReadOnlyMemory<int> Indices;
#pragma warning restore CA1051

        private readonly MeshBufferPool _pool;
        private readonly Vector3f[] _positions;
        private readonly Vector3f[] _normals;
        private readonly Vector2f[] _uvs;
        private readonly byte[] _ao;
        private readonly int[] _indices;
        private bool _released;

        internal MeshData(
            MeshBufferPool pool,
            Vector3f[] positions,
            Vector3f[] normals,
            Vector2f[] uvs,
            byte[] ao,
            int[] indices,
            int vertexCount,
            int indexCount,
            Vector3f min,
            Vector3f max)
        {
            _pool = pool;
            _positions = positions;
            _normals = normals;
            _uvs = uvs;
            _ao = ao;
            _indices = indices;

            Positions = new ReadOnlyMemory<Vector3f>(positions, 0, vertexCount);
            Normals = new ReadOnlyMemory<Vector3f>(normals, 0, vertexCount);
            Uvs = new ReadOnlyMemory<Vector2f>(uvs, 0, vertexCount);
            Ao = new ReadOnlyMemory<byte>(ao, 0, vertexCount);
            Indices = new ReadOnlyMemory<int>(indices, 0, indexCount);

            VertexCount = vertexCount;
            IndexCount = indexCount;
            Min = min;
            Max = max;
        }

        /// <summary>The number of vertices in every vertex stream.</summary>
        public int VertexCount { get; }

        /// <summary>The number of indices, six per emitted quad.</summary>
        public int IndexCount { get; }

        /// <summary>
        /// The inclusive minimum of every position, or the origin for an empty
        /// mesh.
        /// </summary>
        public Vector3f Min { get; }

        /// <summary>
        /// The inclusive maximum of every position, or the origin for an empty
        /// mesh.
        /// </summary>
        public Vector3f Max { get; }

        /// <summary>
        /// Returns every buffer to its origin pool. Call exactly once.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// <see cref="Release"/> was already called.
        /// </exception>
        public void Release()
        {
            if (_released)
            {
                throw new InvalidOperationException("MeshData.Release() may be called only once.");
            }

            _released = true;
            Positions = default;
            Normals = default;
            Uvs = default;
            Ao = default;
            Indices = default;

            _pool.Return(_positions);
            _pool.Return(_normals);
            _pool.Return(_uvs);
            _pool.Return(_ao);
            _pool.Return(_indices);
        }
    }
}

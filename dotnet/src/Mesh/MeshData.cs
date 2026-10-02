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
    /// in ADR-0007. The streams are windows over full-capacity arrays owned by
    /// the origin <see cref="MeshBufferPool"/>, which also pools the
    /// <see cref="MeshData"/> wrapper itself so a steady-state build allocates
    /// nothing. <see cref="Release"/> returns every buffer exactly once, then
    /// the wrapper; a second call throws. Each buffer is returned and its
    /// window cleared before the next return, so a mid-sequence throw cannot
    /// leave an already-returned array for a retry. Using a mesh after
    /// <see cref="Release"/> (or calling <see cref="Release"/> twice) is
    /// invalid; the wrapper may already have been re-issued to another build.
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
        private Vector3f[] _positions = Array.Empty<Vector3f>();
        private Vector3f[] _normals = Array.Empty<Vector3f>();
        private Vector2f[] _uvs = Array.Empty<Vector2f>();
        private byte[] _ao = Array.Empty<byte>();
        private int[] _indices = Array.Empty<int>();
        private bool _released = true;

        internal MeshData(MeshBufferPool pool)
        {
            _pool = pool;
        }

        /// <summary>The number of vertices in every vertex stream.</summary>
        public int VertexCount { get; private set; }

        /// <summary>The number of indices, six per emitted quad.</summary>
        public int IndexCount { get; private set; }

        /// <summary>
        /// The inclusive minimum of every position, or the origin for an empty
        /// mesh.
        /// </summary>
        public Vector3f Min { get; private set; }

        /// <summary>
        /// The inclusive maximum of every position, or the origin for an empty
        /// mesh.
        /// </summary>
        public Vector3f Max { get; private set; }

        /// <summary>
        /// Wraps freshly built, pool-owned arrays as the five public windows.
        /// Called by the meshers before the mesh is returned to the caller.
        /// </summary>
        internal void Initialize(
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
            _released = false;
        }

        /// <summary>
        /// Returns every buffer to its origin pool, then the wrapper. Call
        /// exactly once. A buffer is cleared as soon as its return succeeds,
        /// so if a return throws, a retry can only return the buffers that
        /// were not returned yet.
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

            _pool.Return(_positions);
            _positions = Array.Empty<Vector3f>();
            Positions = default;

            _pool.Return(_normals);
            _normals = Array.Empty<Vector3f>();
            Normals = default;

            _pool.Return(_uvs);
            _uvs = Array.Empty<Vector2f>();
            Uvs = default;

            _pool.Return(_ao);
            _ao = Array.Empty<byte>();
            Ao = default;

            _pool.Return(_indices);
            _indices = Array.Empty<int>();
            Indices = default;

            _released = true;
            _pool.ReturnMeshData(this);
        }
    }
}

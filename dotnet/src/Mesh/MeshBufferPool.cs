using System;
using System.Collections.Generic;

namespace Cubeglass.Mesh
{
    /// <summary>
    /// Owns the reusable mesh streams of <see cref="MeshData"/> (ADR-0007).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Buffers live in per-element-type capacity buckets: an array of length
    /// <c>L</c> is stored in the bucket of exact capacity <c>L</c>, and
    /// <see cref="Rent{T}"/> pops the smallest pooled array with length at
    /// least the requested minimum, allocating only when no bucket fits. The
    /// five streams, the <see cref="GreedyMesher"/> scratch mask and the
    /// <see cref="MeshData"/> wrappers are all rented from here, so after a
    /// warm-up of one build/release cycle per chunk shape meshing allocates
    /// nothing. Warming the pool is exactly that: build and release the
    /// shapes the game will mesh; there is no separate allocation API.
    /// </para>
    /// <para>
    /// <see cref="MeshData.Release"/> returns each buffer and clears its
    /// window immediately, then returns the wrapper last, so a mid-sequence
    /// throw can only leave not-yet-returned buffers for a retry and can never
    /// strand a returned one. The pool is not thread-safe: use one instance
    /// per meshing thread (the process-wide <see cref="Shared"/> is for
    /// single-threaded tests, tools and one worker at a time).
    /// </para>
    /// </remarks>
    public sealed class MeshBufferPool
    {
        /// <summary>The process-wide pool used by the meshers.</summary>
        public static MeshBufferPool Shared { get; } = new MeshBufferPool();

        private readonly Dictionary<Type, object> _buckets = new Dictionary<Type, object>();
        private readonly Stack<MeshData> _meshData = new Stack<MeshData>();
        private long _rented;
        private long _returned;

        /// <summary>
        /// The number of stream/mask buffers currently checked out of the
        /// pool (rents minus returns). A successful build/release cycle leaves
        /// it unchanged; the throw-path gate uses it to prove that a failed
        /// build returns every rent.
        /// </summary>
        internal long OutstandingBuffers => _rented - _returned;

        /// <summary>
        /// Returns a pooled array of at least <paramref name="minCapacity"/>
        /// elements, allocating one only when no capacity bucket is large
        /// enough.
        /// </summary>
        internal T[] Rent<T>(int minCapacity)
        {
            if (minCapacity < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(minCapacity), minCapacity, "Capacity must be non-negative.");
            }

            if (minCapacity == 0)
            {
                return Array.Empty<T>();
            }

            _rented++;
            return GetOrAddBuckets<T>().Rent(minCapacity);
        }

        /// <summary>
        /// Accepts a buffer previously returned by <see cref="Rent{T}"/>.
        /// Zero-length buffers are not pooled.
        /// </summary>
        internal void Return<T>(T[] buffer)
        {
            if (buffer is null)
            {
                throw new ArgumentNullException(nameof(buffer));
            }

            if (buffer.Length > 0)
            {
                _returned++;
                GetOrAddBuckets<T>().Return(buffer);
            }
        }

        /// <summary>Returns a pooled <see cref="MeshData"/> wrapper.</summary>
        internal MeshData RentMeshData()
        {
            return _meshData.Count > 0 ? _meshData.Pop() : new MeshData(this);
        }

        /// <summary>Stores a released <see cref="MeshData"/> wrapper for reuse.</summary>
        internal void ReturnMeshData(MeshData mesh)
        {
            if (mesh is null)
            {
                throw new ArgumentNullException(nameof(mesh));
            }

            _meshData.Push(mesh);
        }

        private CapacityBuckets<T> GetOrAddBuckets<T>()
        {
            if (_buckets.TryGetValue(typeof(T), out object? existing))
            {
                return (CapacityBuckets<T>)existing;
            }

            var buckets = new CapacityBuckets<T>();
            _buckets.Add(typeof(T), buckets);
            return buckets;
        }

        /// <summary>
        /// Exact-capacity stacks for one element type, ordered by ascending
        /// capacity. Rents scan the order, so the reuser is the smallest
        /// pooled array that fits.
        /// </summary>
        private sealed class CapacityBuckets<T>
        {
            private readonly List<int> _capacities = new List<int>();
            private readonly List<Stack<T[]>> _pools = new List<Stack<T[]>>();

            internal T[] Rent(int minCapacity)
            {
                for (int i = 0; i < _capacities.Count; i++)
                {
                    if (_capacities[i] >= minCapacity && _pools[i].Count > 0)
                    {
                        return _pools[i].Pop();
                    }
                }

                return new T[minCapacity];
            }

            internal void Return(T[] buffer)
            {
                int capacity = buffer.Length;
                int index = 0;
                while (index < _capacities.Count && _capacities[index] < capacity)
                {
                    index++;
                }

                if (index < _capacities.Count && _capacities[index] == capacity)
                {
                    _pools[index].Push(buffer);
                    return;
                }

                var pool = new Stack<T[]>();
                pool.Push(buffer);
                _capacities.Insert(index, capacity);
                _pools.Insert(index, pool);
            }
        }
    }

    /// <summary>
    /// A growable count-plus-array stream over <see cref="MeshBufferPool"/>
    /// arrays. The meshers append into it and hand the full-capacity array to
    /// <see cref="MeshData"/> with <see cref="Detach"/>; a build that fails
    /// mid-way gives the array back with <see cref="Return"/>.
    /// </summary>
    /// <remarks>
    /// This is a mutable struct so a build can hold five streams without
    /// allocating wrappers; pass it by <c>ref</c> to helpers that append.
    /// </remarks>
    internal struct PooledStream<T>
    {
        private readonly MeshBufferPool _pool;
        private T[] _buffer;
        private int _count;

        internal PooledStream(MeshBufferPool pool, int initialCapacity)
        {
            _pool = pool;
            _buffer = pool.Rent<T>(initialCapacity);
            _count = 0;
        }

        internal readonly int Count => _count;

        internal readonly T this[int index] => _buffer[index];

        internal void Add(T value)
        {
            if (_count == _buffer.Length)
            {
                Grow(_count + 1);
            }

            _buffer[_count] = value;
            _count++;
        }

        /// <summary>
        /// Transfers the buffer to the caller (typically
        /// <see cref="MeshData.Initialize"/>); the stream is empty afterwards.
        /// </summary>
        internal T[] Detach()
        {
            T[] buffer = _buffer;
            _buffer = Array.Empty<T>();
            _count = 0;
            return buffer;
        }

        /// <summary>Returns the buffer to the pool without transferring it.</summary>
        internal void Return()
        {
            if (_buffer.Length > 0)
            {
                _pool.Return(_buffer);
            }

            _buffer = Array.Empty<T>();
            _count = 0;
        }

        private void Grow(int minCapacity)
        {
            int capacity = Math.Max(minCapacity, _buffer.Length * 2);
            T[] grown = _pool.Rent<T>(capacity);
            if (_count > 0)
            {
                Array.Copy(_buffer, grown, _count);
            }

            if (_buffer.Length > 0)
            {
                _pool.Return(_buffer);
            }

            _buffer = grown;
        }
    }
}

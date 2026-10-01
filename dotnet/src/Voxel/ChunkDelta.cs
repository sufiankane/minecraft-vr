using System;
using System.Collections.Generic;

namespace Cubeglass.Voxel
{
    /// <summary>
    /// The edited cells of one chunk relative to its generated baseline
    /// (ADR-0006): the chunk coordinate plus local cell to block mappings.
    /// </summary>
    /// <remarks>
    /// Immutable: the constructor copies the edit map, and equality compares
    /// the coordinate and every entry, so save then load can be asserted
    /// directly. The codec that turns deltas into bytes is a later addition;
    /// this type is the plain store contract.
    /// </remarks>
    public sealed class ChunkDelta : IEquatable<ChunkDelta>
    {
        private static readonly Dictionary<Int3, BlockId> NoEdits = new Dictionary<Int3, BlockId>();

        private readonly Dictionary<Int3, BlockId> _edits;

        /// <summary>Creates a delta for <paramref name="coord"/> with a copy of <paramref name="edits"/>.</summary>
        public ChunkDelta(ChunkCoord coord, IReadOnlyDictionary<Int3, BlockId> edits)
        {
            if (edits is null)
            {
                throw new ArgumentNullException(nameof(edits));
            }

            Coord = coord;
            _edits = new Dictionary<Int3, BlockId>(edits);
        }

        /// <summary>The chunk these edits belong to.</summary>
        public ChunkCoord Coord { get; }

        /// <summary>Local cell to block mappings; empty for an untouched chunk.</summary>
        public IReadOnlyDictionary<Int3, BlockId> Edits => _edits;

        /// <summary>A delta with no edits for <paramref name="coord"/>.</summary>
        public static ChunkDelta Empty(ChunkCoord coord)
        {
            return new ChunkDelta(coord, NoEdits);
        }

        public bool Equals(ChunkDelta? other)
        {
            if (other is null)
            {
                return false;
            }

            if (ReferenceEquals(this, other))
            {
                return true;
            }

            if (Coord != other.Coord || _edits.Count != other._edits.Count)
            {
                return false;
            }

            foreach (KeyValuePair<Int3, BlockId> edit in _edits)
            {
                if (!other._edits.TryGetValue(edit.Key, out BlockId value) || value != edit.Value)
                {
                    return false;
                }
            }

            return true;
        }

        public override bool Equals(object? obj)
        {
            return obj is ChunkDelta other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = Coord.GetHashCode();
                int edits = 0;
                foreach (KeyValuePair<Int3, BlockId> edit in _edits)
                {
                    edits ^= edit.Key.GetHashCode() ^ edit.Value.GetHashCode();
                }

                return ((hash * 397) ^ edits) ^ _edits.Count;
            }
        }
    }
}

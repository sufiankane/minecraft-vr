using System;
using System.Collections.Generic;
using System.Globalization;

namespace Cubeglass.Voxel
{
    /// <summary>
    /// The edited cells of one chunk relative to its generated baseline
    /// (ADR-0006): the chunk coordinate plus local cell to block mappings.
    /// </summary>
    /// <remarks>
    /// Immutable: the constructor copies the edit map, and equality compares
    /// the coordinate and every entry, so save then load can be asserted
    /// directly. Every key is a chunk-local cell in
    /// <c>[0, ChunkMath.ChunkSize)</c> per axis, rejected at construction
    /// otherwise, so a delta can never alias one cell onto another or index
    /// outside the flat grid. The codec that turns deltas into bytes is a
    /// later addition; this type is the plain store contract.
    /// </remarks>
    public sealed class ChunkDelta : IEquatable<ChunkDelta>
    {
        private static readonly Dictionary<Int3, BlockId> NoEdits = new Dictionary<Int3, BlockId>();

        private readonly Dictionary<Int3, BlockId> _edits;

        /// <summary>Creates a delta for <paramref name="coord"/> with a copy of <paramref name="edits"/>.</summary>
        /// <exception cref="ArgumentNullException"><paramref name="edits"/> is null.</exception>
        /// <exception cref="ArgumentException">
        /// An edit key is not a chunk-local cell in
        /// <c>[0, ChunkMath.ChunkSize)</c> per axis; the message names the
        /// offending cell.
        /// </exception>
        public ChunkDelta(ChunkCoord coord, IReadOnlyDictionary<Int3, BlockId> edits)
        {
            if (edits is null)
            {
                throw new ArgumentNullException(nameof(edits));
            }

            foreach (Int3 cell in edits.Keys)
            {
                if (cell.X < 0 || cell.X >= ChunkMath.ChunkSize
                    || cell.Y < 0 || cell.Y >= ChunkMath.ChunkSize
                    || cell.Z < 0 || cell.Z >= ChunkMath.ChunkSize)
                {
                    throw new ArgumentException(
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "Edit cell ({0}, {1}, {2}) is outside the chunk-local range [0, {3}).",
                            cell.X,
                            cell.Y,
                            cell.Z,
                            ChunkMath.ChunkSize),
                        nameof(edits));
                }
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

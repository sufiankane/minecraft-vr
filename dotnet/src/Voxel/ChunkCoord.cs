using System;

namespace Cubeglass.Voxel
{
    /// <summary>
    /// The integer coordinate of a 16x16x16 chunk on the three world axes.
    /// </summary>
    /// <remarks>
    /// Chunk <c>(0, 0, 0)</c> owns cells <c>[0, 15]</c> per axis; negative
    /// coordinates belong to floor-divided chunks (ADR-0006). Value type with
    /// structural equality. The dossier writes this contract as a
    /// <c>readonly record struct</c>; the project is pinned to C# 9, so the
    /// positional record members are written out explicitly with the same
    /// public surface (see ADR-0005).
    /// </remarks>
    public readonly struct ChunkCoord : IEquatable<ChunkCoord>
    {
        public ChunkCoord(int x, int y, int z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public int X { get; }

        public int Y { get; }

        public int Z { get; }

        public bool Equals(ChunkCoord other)
        {
            return X == other.X && Y == other.Y && Z == other.Z;
        }

        public override bool Equals(object? obj)
        {
            return obj is ChunkCoord other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(X, Y, Z);
        }

        public static bool operator ==(ChunkCoord left, ChunkCoord right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(ChunkCoord left, ChunkCoord right)
        {
            return !left.Equals(right);
        }

        public void Deconstruct(out int x, out int y, out int z)
        {
            x = X;
            y = Y;
            z = Z;
        }

        public override string ToString()
        {
            return $"({X}, {Y}, {Z})";
        }
    }
}

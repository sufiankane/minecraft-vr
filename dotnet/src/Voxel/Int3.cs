using System;

namespace Cubeglass.Voxel
{
    /// <summary>
    /// An integer lattice cell or chunk coordinate on the three world axes.
    /// </summary>
    /// <remarks>
    /// Value type with structural equality. Floor behaviour lives in
    /// <see cref="ChunkMath"/>; this type only carries the three components.
    /// The dossier writes this contract as a <c>readonly record struct</c>; the
    /// project is pinned to C# 9, so the positional record members are written
    /// out explicitly with the same public surface (see ADR-0005).
    /// </remarks>
    public readonly struct Int3 : IEquatable<Int3>
    {
        public Int3(int x, int y, int z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public int X { get; }

        public int Y { get; }

        public int Z { get; }

        /// <summary>The origin cell <c>(0, 0, 0)</c>.</summary>
        public static Int3 Zero => default;

        public static Int3 operator +(Int3 a, Int3 b)
        {
            return new Int3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        }

        public static Int3 operator -(Int3 a, Int3 b)
        {
            return new Int3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        }

        public bool Equals(Int3 other)
        {
            return X == other.X && Y == other.Y && Z == other.Z;
        }

        public override bool Equals(object? obj)
        {
            return obj is Int3 other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(X, Y, Z);
        }

        public static bool operator ==(Int3 left, Int3 right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(Int3 left, Int3 right)
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

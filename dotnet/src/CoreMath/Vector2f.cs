using System;
using System.Globalization;

namespace Cubeglass.CoreMath
{
    /// <summary>
    /// A two-component vector of 32-bit floats: the interchange type for
    /// texture coordinates in engine-neutral mesh data (dossier section 5.10).
    /// </summary>
    /// <remarks>
    /// Pixel coordinates have origin top-left (ADR-0004). Section 5.10 names
    /// <c>Vector2f</c> without defining it, so this type and
    /// <see cref="Vector3f"/> are additive to the S1 contract, recorded in
    /// ADR-0007. Hand-written to keep <c>Cubeglass.CoreMath</c> at C# 9
    /// (no record structs); a value type whose operations are total and
    /// allocate nothing.
    /// </remarks>
    public readonly struct Vector2f : IEquatable<Vector2f>
    {
        public Vector2f(float x, float y)
        {
            X = x;
            Y = y;
        }

        public float X { get; }

        public float Y { get; }

        /// <summary>The origin <c>(0, 0)</c>.</summary>
        public static Vector2f Zero => default;

        public bool Equals(Vector2f other)
        {
            return X == other.X && Y == other.Y;
        }

        public override bool Equals(object? obj)
        {
            return obj is Vector2f other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = 17;
                hash = (hash * 31) + X.GetHashCode();
                hash = (hash * 31) + Y.GetHashCode();
                return hash;
            }
        }

        public static bool operator ==(Vector2f a, Vector2f b)
        {
            return a.Equals(b);
        }

        public static bool operator !=(Vector2f a, Vector2f b)
        {
            return !a.Equals(b);
        }

        public override string ToString()
        {
            return string.Format(CultureInfo.InvariantCulture, "({0}, {1})", X, Y);
        }
    }
}

using System;
using System.Globalization;

namespace Cubeglass.CoreMath
{
    /// <summary>
    /// A three-component vector of 32-bit floats: the interchange type for
    /// vertex positions and normals in engine-neutral mesh data (dossier
    /// section 5.10).
    /// </summary>
    /// <remarks>
    /// Right-handed, Y up, forward -Z, X right (ADR-0004). Section 5.10 names
    /// <c>Vector3f</c> without defining it, so this type and
    /// <see cref="Vector2f"/> are additive to the S1 contract, recorded in
    /// ADR-0007. Hand-written to keep <c>Cubeglass.CoreMath</c> at C# 9
    /// (no record structs); a value type whose operations are total and
    /// allocate nothing.
    /// </remarks>
    public readonly struct Vector3f : IEquatable<Vector3f>
    {
        public Vector3f(float x, float y, float z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public float X { get; }

        public float Y { get; }

        public float Z { get; }

        /// <summary>The origin <c>(0, 0, 0)</c>.</summary>
        public static Vector3f Zero => default;

        public bool Equals(Vector3f other)
        {
            return X == other.X && Y == other.Y && Z == other.Z;
        }

        public override bool Equals(object? obj)
        {
            return obj is Vector3f other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = 17;
                hash = (hash * 31) + X.GetHashCode();
                hash = (hash * 31) + Y.GetHashCode();
                hash = (hash * 31) + Z.GetHashCode();
                return hash;
            }
        }

        public static bool operator ==(Vector3f a, Vector3f b)
        {
            return a.Equals(b);
        }

        public static bool operator !=(Vector3f a, Vector3f b)
        {
            return !a.Equals(b);
        }

        public override string ToString()
        {
            return string.Format(CultureInfo.InvariantCulture, "({0}, {1}, {2})", X, Y, Z);
        }
    }
}

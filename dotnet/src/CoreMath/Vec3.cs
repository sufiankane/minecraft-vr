using System;

namespace Cubeglass.CoreMath
{
    /// <summary>
    /// A three-component vector of doubles in metres.
    /// </summary>
    /// <remarks>
    /// Right-handed, Y up, forward -Z, X right (ADR-0004). A value type: every
    /// operation is total and allocates nothing.
    /// </remarks>
    public readonly struct Vec3 : IEquatable<Vec3>
    {
        public Vec3(double x, double y, double z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public double X { get; }

        public double Y { get; }

        public double Z { get; }

        public static Vec3 operator +(Vec3 a, Vec3 b)
        {
            return new Vec3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        }

        public static Vec3 operator -(Vec3 a, Vec3 b)
        {
            return new Vec3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        }

        public static Vec3 operator -(Vec3 v)
        {
            return new Vec3(-v.X, -v.Y, -v.Z);
        }

        public static Vec3 operator *(Vec3 v, double scalar)
        {
            return new Vec3(v.X * scalar, v.Y * scalar, v.Z * scalar);
        }

        public static Vec3 operator *(double scalar, Vec3 v)
        {
            return v * scalar;
        }

        /// <summary>
        /// Divides every component by <paramref name="scalar"/>.
        /// Precondition: <paramref name="scalar"/> is non-zero.
        /// </summary>
        public static Vec3 operator /(Vec3 v, double scalar)
        {
            return new Vec3(v.X / scalar, v.Y / scalar, v.Z / scalar);
        }

        /// <summary>Dot product <c>a.x * b.x + a.y * b.y + a.z * b.z</c>.</summary>
        public static double Dot(Vec3 a, Vec3 b)
        {
            return (a.X * b.X) + (a.Y * b.Y) + (a.Z * b.Z);
        }

        /// <summary>Right-handed cross product.</summary>
        public static Vec3 Cross(Vec3 a, Vec3 b)
        {
            return new Vec3(
                (a.Y * b.Z) - (a.Z * b.Y),
                (a.Z * b.X) - (a.X * b.Z),
                (a.X * b.Y) - (a.Y * b.X));
        }

        /// <summary>Euclidean length <c>sqrt(Dot(v, v))</c>.</summary>
        public static double Length(Vec3 v)
        {
            return Math.Sqrt(Dot(v, v));
        }

        /// <summary>
        /// Unit vector in the direction of <paramref name="v"/>; the zero vector
        /// maps to the zero vector (no NaNs).
        /// </summary>
        public static Vec3 Normalized(Vec3 v)
        {
            double length = Length(v);
            if (length == 0.0)
            {
                return new Vec3(0.0, 0.0, 0.0);
            }

            return new Vec3(v.X / length, v.Y / length, v.Z / length);
        }

        /// <summary>
        /// True when every component of this vector is within
        /// <paramref name="tolerance"/> (inclusive) of <paramref name="other"/>.
        /// </summary>
        public bool NearlyEquals(Vec3 other, double tolerance)
        {
            return Math.Abs(X - other.X) <= tolerance
                && Math.Abs(Y - other.Y) <= tolerance
                && Math.Abs(Z - other.Z) <= tolerance;
        }

        public bool Equals(Vec3 other)
        {
            return X == other.X && Y == other.Y && Z == other.Z;
        }

        public override bool Equals(object? obj)
        {
            return obj is Vec3 other && Equals(other);
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

        public static bool operator ==(Vec3 a, Vec3 b)
        {
            return a.Equals(b);
        }

        public static bool operator !=(Vec3 a, Vec3 b)
        {
            return !a.Equals(b);
        }
    }
}

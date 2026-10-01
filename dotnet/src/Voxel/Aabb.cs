using System;
using Cubeglass.CoreMath;

namespace Cubeglass.Voxel
{
    /// <summary>
    /// An axis-aligned box in metres with half-open semantics: a point
    /// <c>p</c> is inside exactly when <c>Min &lt;= p &lt; Max</c> on every
    /// axis.
    /// </summary>
    /// <remarks>
    /// The constructor rejects a box whose minimum exceeds the maximum on any
    /// axis, so every box is a valid (possibly zero-extent) interval per axis.
    /// <see cref="Overlaps"/> uses the element-wise half-open test
    /// <c>Min &lt; other.Max &amp;&amp; other.Min &lt; Max</c>: boxes that
    /// merely touch on a face, edge or corner do not overlap. A box with zero
    /// extent on any axis is empty, so it contains no point and overlaps
    /// nothing. Element-wise bounds are also what <see cref="VoxelCollision"/>
    /// applies to cell cubes.
    /// </remarks>
    public readonly struct Aabb : IEquatable<Aabb>
    {
        /// <summary>Creates the box spanning <paramref name="min"/> to <paramref name="max"/>.</summary>
        /// <exception cref="ArgumentException">
        /// Any component of <paramref name="min"/> exceeds the matching
        /// component of <paramref name="max"/>, or either bound is NaN.
        /// </exception>
        public Aabb(Vec3 min, Vec3 max)
        {
            if (!(min.X <= max.X) || !(min.Y <= max.Y) || !(min.Z <= max.Z))
            {
                throw new ArgumentException(
                    $"Every Min component must be less than or equal to Max; got Min {min} and Max {max}.",
                    nameof(min));
            }

            Min = min;
            Max = max;
        }

        /// <summary>The inclusive lower corner.</summary>
        public Vec3 Min { get; }

        /// <summary>The exclusive upper corner.</summary>
        public Vec3 Max { get; }

        /// <summary>Whether <paramref name="point"/> lies in the half-open box.</summary>
        public bool Contains(Vec3 point)
        {
            return point.X >= Min.X && point.X < Max.X
                && point.Y >= Min.Y && point.Y < Max.Y
                && point.Z >= Min.Z && point.Z < Max.Z;
        }

        /// <summary>
        /// Whether this box and <paramref name="other"/> share any volume under
        /// half-open semantics; face, edge and corner contacts, and empty boxes,
        /// are not overlaps.
        /// </summary>
        public bool Overlaps(Aabb other)
        {
            return HasVolume() && other.HasVolume()
                && Min.X < other.Max.X && other.Min.X < Max.X
                && Min.Y < other.Max.Y && other.Min.Y < Max.Y
                && Min.Z < other.Max.Z && other.Min.Z < Max.Z;
        }

        private bool HasVolume()
        {
            return Min.X < Max.X && Min.Y < Max.Y && Min.Z < Max.Z;
        }

        public bool Equals(Aabb other)
        {
            return Min == other.Min && Max == other.Max;
        }

        public override bool Equals(object? obj)
        {
            return obj is Aabb other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return ((Min.GetHashCode() * 397) ^ Max.GetHashCode());
            }
        }

        public static bool operator ==(Aabb a, Aabb b)
        {
            return a.Equals(b);
        }

        public static bool operator !=(Aabb a, Aabb b)
        {
            return !a.Equals(b);
        }
    }
}

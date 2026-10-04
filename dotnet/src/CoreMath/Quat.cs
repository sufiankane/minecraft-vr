using System;

namespace Cubeglass.CoreMath
{
    /// <summary>
    /// A rotation stored as a unit quaternion <c>(w, x, y, z)</c> of doubles.
    /// </summary>
    /// <remarks>
    /// Right-handed, Y up, forward -Z (ADR-0004). Every instance is constructed
    /// through <see cref="FromComponents"/> or <see cref="FromAxisAngle"/>, so a
    /// live quaternion is unit and finite (degenerate input becomes
    /// <see cref="Identity"/>). All operations are total and allocate nothing;
    /// the Hamilton product is intentionally not renormalised.
    /// </remarks>
    public readonly struct Quat
    {
        private const double MinimumSquaredNorm = 1e-24;
        private const double LinearInterpolationDotThreshold = 0.9995;

        private Quat(double w, double x, double y, double z)
        {
            W = w;
            X = x;
            Y = y;
            Z = z;
        }

        /// <summary>The identity rotation <c>(1, 0, 0, 0)</c>.</summary>
        public static Quat Identity { get; } = new Quat(1.0, 0.0, 0.0, 0.0);

        /// <summary>Scalar part <c>w</c>.</summary>
        public double W { get; }

        /// <summary>Vector part <c>x</c>.</summary>
        public double X { get; }

        /// <summary>Vector part <c>y</c>.</summary>
        public double Y { get; }

        /// <summary>Vector part <c>z</c>.</summary>
        public double Z { get; }

        /// <summary>
        /// Builds a unit quaternion from raw <c>(w, x, y, z)</c> components. If
        /// the squared norm is below <c>1e-24</c> or any component is non-finite,
        /// returns <see cref="Identity"/>.
        /// </summary>
        /// <remarks>
        /// A huge finite component set whose squared norm overflows to infinity
        /// is degenerate by the same rule and also returns <see cref="Identity"/>.
        /// That overflow edge is deliberate, pinned by tests and mirrored by the
        /// C++ implementation (M6); it is not silently wrapped.
        /// </remarks>
        public static Quat FromComponents(double w, double x, double y, double z)
        {
            double squaredNorm = (w * w) + (x * x) + (y * y) + (z * z);
            if (!double.IsFinite(squaredNorm) || squaredNorm < MinimumSquaredNorm)
            {
                return Identity;
            }

            double inverseLength = 1.0 / Math.Sqrt(squaredNorm);
            return new Quat(w * inverseLength, x * inverseLength, y * inverseLength, z * inverseLength);
        }

        /// <summary>
        /// Builds a unit quaternion rotating <paramref name="radians"/> about
        /// <paramref name="axis"/> (right-hand rule). The axis is normalised
        /// first; a zero or non-finite axis, or non-finite
        /// <paramref name="radians"/>, yields <see cref="Identity"/>.
        /// </summary>
        /// <remarks>
        /// A huge finite axis whose squared length overflows to infinity is
        /// degenerate by the same rule and also yields <see cref="Identity"/>
        /// (the documented M6 overflow edge, mirrored by C++).
        /// </remarks>
        public static Quat FromAxisAngle(Vec3 axis, double radians)
        {
            double squaredAxisLength = (axis.X * axis.X) + (axis.Y * axis.Y) + (axis.Z * axis.Z);
            if (!double.IsFinite(radians) || !double.IsFinite(squaredAxisLength) || squaredAxisLength < MinimumSquaredNorm)
            {
                return Identity;
            }

            double inverseLength = 1.0 / Math.Sqrt(squaredAxisLength);
            double halfAngle = radians * 0.5;
            double sine = Math.Sin(halfAngle);
            return new Quat(
                Math.Cos(halfAngle),
                axis.X * inverseLength * sine,
                axis.Y * inverseLength * sine,
                axis.Z * inverseLength * sine);
        }

        /// <summary>Conjugate <c>(w, -x, -y, -z)</c>.</summary>
        public Quat Conjugate()
        {
            return new Quat(W, -X, -Y, -Z);
        }

        /// <summary>
        /// Inverse rotation. Equals <see cref="Conjugate"/> because the
        /// quaternion is unit.
        /// </summary>
        public Quat Inverse()
        {
            return Conjugate();
        }

        /// <summary>Dot product of the four components.</summary>
        public double Dot(Quat other)
        {
            return (W * other.W) + (X * other.X) + (Y * other.Y) + (Z * other.Z);
        }

        /// <summary>
        /// Rotates <paramref name="v"/> by this quaternion using
        /// <c>t = 2 * Cross(q_xyz, v); result = v + w * t + Cross(q_xyz, t)</c>
        /// in exactly that floating-point order (shared with C++).
        /// </summary>
        public Vec3 Rotate(Vec3 v)
        {
            Vec3 q = new Vec3(X, Y, Z);
            Vec3 t = 2.0 * Vec3.Cross(q, v);
            return v + (W * t) + Vec3.Cross(q, t);
        }

        /// <summary>Hamilton product; the result is not renormalised.</summary>
        public static Quat operator *(Quat a, Quat b)
        {
            return new Quat(
                (a.W * b.W) - (a.X * b.X) - (a.Y * b.Y) - (a.Z * b.Z),
                (a.W * b.X) + (a.X * b.W) + (a.Y * b.Z) - (a.Z * b.Y),
                (a.W * b.Y) - (a.X * b.Z) + (a.Y * b.W) + (a.Z * b.X),
                (a.W * b.Z) + (a.X * b.Y) - (a.Y * b.X) + (a.Z * b.W));
        }

        /// <summary>
        /// True when the squared norm is within <paramref name="tolerance"/>
        /// (inclusive) of 1.
        /// </summary>
        public bool IsNormalized(double tolerance = 1e-9)
        {
            double squaredNorm = (W * W) + (X * X) + (Y * Y) + (Z * Z);
            return Math.Abs(squaredNorm - 1.0) <= tolerance;
        }

        /// <summary>
        /// Returns <paramref name="q"/> scaled to unit length. Degenerate input
        /// (squared norm below <c>1e-24</c> or non-finite) yields
        /// <see cref="Identity"/>.
        /// </summary>
        public static Quat Normalize(Quat q)
        {
            double squaredNorm = q.Dot(q);
            if (!double.IsFinite(squaredNorm) || squaredNorm < MinimumSquaredNorm)
            {
                return Identity;
            }

            double inverseLength = 1.0 / Math.Sqrt(squaredNorm);
            return new Quat(q.W * inverseLength, q.X * inverseLength, q.Y * inverseLength, q.Z * inverseLength);
        }

        /// <summary>
        /// Normalised shortest-path spherical linear interpolation between
        /// <paramref name="a"/> and <paramref name="b"/>. A negative dot product
        /// flips <paramref name="b"/> so the shortest arc is taken, and a dot
        /// product above <c>0.9995</c> switches to a normalised linear
        /// interpolation to avoid dividing by a vanishing <c>sin(theta)</c>.
        /// The result is unit in every branch.
        /// </summary>
        public static Quat Slerp(Quat a, Quat b, double t)
        {
            double dot = a.Dot(b);
            double bw = b.W;
            double bx = b.X;
            double by = b.Y;
            double bz = b.Z;
            if (dot < 0.0)
            {
                dot = -dot;
                bw = -bw;
                bx = -bx;
                by = -by;
                bz = -bz;
            }

            if (dot > LinearInterpolationDotThreshold)
            {
                double oneMinusT = 1.0 - t;
                return Normalize(new Quat(
                    (oneMinusT * a.W) + (t * bw),
                    (oneMinusT * a.X) + (t * bx),
                    (oneMinusT * a.Y) + (t * by),
                    (oneMinusT * a.Z) + (t * bz)));
            }

            double theta = Math.Acos(dot);
            double sineTheta = Math.Sin(theta);
            double s1 = Math.Sin((1.0 - t) * theta) / sineTheta;
            double s2 = Math.Sin(t * theta) / sineTheta;
            return Normalize(new Quat(
                (s1 * a.W) + (s2 * bw),
                (s1 * a.X) + (s2 * bx),
                (s1 * a.Y) + (s2 * by),
                (s1 * a.Z) + (s2 * bz)));
        }
    }
}

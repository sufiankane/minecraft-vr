using System;

namespace Cubeglass.CoreMath
{
    /// <summary>
    /// A rigid transform: a rotation followed by a translation. Maps a point
    /// <c>v</c> to <c>Position + Rotation.Rotate(v)</c>.
    /// </summary>
    /// <remarks>
    /// Right-handed, Y up, forward -Z (ADR-0004). A value type: every operation
    /// is total and allocates nothing.
    /// </remarks>
    public readonly struct Pose
    {
        public Pose(Vec3 position, Quat rotation)
        {
            Position = position;
            Rotation = rotation;
        }

        /// <summary>Translation in metres.</summary>
        public Vec3 Position { get; }

        /// <summary>Unit rotation.</summary>
        public Quat Rotation { get; }

        /// <summary>
        /// Composes <paramref name="child"/> into <paramref name="parent"/>'s
        /// frame: the position is
        /// <c>parent.Position + parent.Rotation.Rotate(child.Position)</c> and
        /// the rotation is <c>parent.Rotation * child.Rotation</c>.
        /// </summary>
        public static Pose Compose(Pose parent, Pose child)
        {
            return new Pose(
                parent.Position + parent.Rotation.Rotate(child.Position),
                parent.Rotation * child.Rotation);
        }

        /// <summary>
        /// Inverse transform: the rotation is the conjugate and the position is
        /// the negated inverse rotation of the original position.
        /// </summary>
        public Pose Inverse()
        {
            Quat rotation = Rotation.Inverse();
            return new Pose(-rotation.Rotate(Position), rotation);
        }

        /// <summary>
        /// Maps <paramref name="point"/> from the local frame of this pose into
        /// its parent frame: <c>Position + Rotation.Rotate(point)</c>.
        /// </summary>
        public Vec3 TransformPoint(Vec3 point)
        {
            return Position + Rotation.Rotate(point);
        }

        /// <summary>
        /// Builds a pose from a VITURE SDK pose with layout
        /// <c>[px, py, pz, qw, qx, qy, qz]</c>. The position is widened to
        /// doubles and the quaternion is normalised through
        /// <see cref="Quat.FromComponents"/> (degenerate input becomes identity).
        /// </summary>
        /// <exception cref="ArgumentException">
        /// <paramref name="sdk"/> does not contain exactly 7 values. This is the
        /// only throwing operation in the S1 CoreMath surface.
        /// </exception>
        public static Pose FromSdk(ReadOnlySpan<float> sdk)
        {
            if (sdk.Length != 7)
            {
                throw new ArgumentException(
                    "SDK pose must contain exactly 7 values: [px, py, pz, qw, qx, qy, qz].",
                    nameof(sdk));
            }

            return new Pose(
                new Vec3(sdk[0], sdk[1], sdk[2]),
                Quat.FromComponents(sdk[3], sdk[4], sdk[5], sdk[6]));
        }
    }
}

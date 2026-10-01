namespace Cubeglass.CoreMath
{
    /// <summary>
    /// The single place the internal to Unity axis flips live (ADR-0004).
    /// </summary>
    /// <remarks>
    /// The S6 Unity adapter must call these overloads and only narrow the
    /// result to <c>float</c>; it must not re-derive the flip.
    /// </remarks>
    public static class UnityConvert
    {
        /// <summary>Maps an internal position to Unity space: <c>(x, y, -z)</c>.</summary>
        public static Vec3 ToUnity(Vec3 position)
        {
            return new Vec3(position.X, position.Y, -position.Z);
        }

        /// <summary>Maps an internal rotation to Unity space: <c>(w, -x, -y, z)</c>.</summary>
        public static Quat ToUnity(Quat rotation)
        {
            return Quat.FromComponents(rotation.W, -rotation.X, -rotation.Y, rotation.Z);
        }

        /// <summary>Maps an internal pose to Unity space, applying both flips.</summary>
        public static Pose ToUnity(Pose pose)
        {
            return new Pose(ToUnity(pose.Position), ToUnity(pose.Rotation));
        }
    }
}

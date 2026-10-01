namespace Cubeglass.Voxel
{
    /// <summary>
    /// An integer lattice cell or chunk coordinate on the three world axes.
    /// </summary>
    /// <remarks>
    /// Value type with structural equality. Floor behaviour lives in
    /// <see cref="ChunkMath"/>; this type only carries the three components.
    /// </remarks>
    public readonly record struct Int3(int X, int Y, int Z)
    {
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
    }
}

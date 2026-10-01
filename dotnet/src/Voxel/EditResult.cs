namespace Cubeglass.Voxel
{
    /// <summary>The outcome of <see cref="IWorld.Apply"/> (dossier section 5.9).</summary>
    public enum EditResult
    {
        /// <summary>The cell was loaded, <c>Expected</c> matched and the write happened.</summary>
        Applied = 0,

        /// <summary>The cell was unloaded or <c>Expected</c> did not match; nothing changed.</summary>
        Rejected = 1,
    }
}

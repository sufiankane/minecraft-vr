namespace Cubeglass.Placeholder
{
    /// <summary>
    /// The Unity-side mirror of the managed <c>Cubeglass.CoreMath.AbiVersion</c>
    /// and of <c>CG_ABI_VERSION</c> in <c>contracts/cg_types.h</c> (ABI 2, see
    /// ADR-0010 and the TD-068 M9 ruling). Kept in this placeholder package so
    /// the mirror is pinned by an EditMode test even before the managed
    /// assemblies are imported.
    /// </summary>
    public static class AbiVersion
    {
        public const int Value = 2;
    }
}

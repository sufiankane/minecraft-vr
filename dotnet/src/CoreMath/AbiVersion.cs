namespace Cubeglass.CoreMath
{
    /// <summary>
    /// The managed mirror of the C ABI version (<c>CG_ABI_VERSION</c> in
    /// <c>contracts/cg_types.h</c>, ADR-0010).
    /// </summary>
    /// <remarks>
    /// <c>AbiVersionTests.ValueTracksTheCAviVersionHeader</c> reads the header
    /// from the repository and fails when this constant drifts, so the managed
    /// handshake can never silently claim an older ABI than the contract
    /// registers. Bump both together with the ADR + baseline regeneration
    /// runbook in <c>docs/CONTRACTS.md</c> section 3.
    /// </remarks>
    public static class AbiVersion
    {
        public const int Value = 2;
    }
}

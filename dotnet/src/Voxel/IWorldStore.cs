using System.Threading.Tasks;

namespace Cubeglass.Voxel
{
    /// <summary>
    /// Persistence boundary for chunk deltas (dossier section 5.9).
    /// </summary>
    /// <remarks>
    /// The async signatures are type-only (ADR-0005): implementations live
    /// outside the pure module in S7 adapters, while
    /// <see cref="InMemoryWorldStore"/> is the pure S2 implementation.
    /// <see cref="System.Threading.CancellationToken"/> is written fully
    /// qualified because <c>System.Threading</c> itself stays forbidden in this
    /// module; only <c>System.Threading.Tasks</c> is allowed.
    /// </remarks>
    public interface IWorldStore
    {
        /// <summary>Saves the delta of chunk <paramref name="c"/>.</summary>
        ValueTask SaveAsync(ChunkCoord c, ChunkDelta delta, System.Threading.CancellationToken ct);

        /// <summary>Loads the delta of chunk <paramref name="c"/>, or null when none is saved.</summary>
        ValueTask<ChunkDelta?> LoadAsync(ChunkCoord c, System.Threading.CancellationToken ct);
    }
}

using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Cubeglass.Voxel
{
    /// <summary>
    /// The pure S2 <see cref="IWorldStore"/>: stores chunk deltas in memory
    /// (ADR-0005). The same contract tests will run against the S7 file store.
    /// </summary>
    public sealed class InMemoryWorldStore : IWorldStore
    {
        private readonly Dictionary<ChunkCoord, ChunkDelta> _deltas =
            new Dictionary<ChunkCoord, ChunkDelta>();

        public ValueTask SaveAsync(ChunkCoord c, ChunkDelta delta, System.Threading.CancellationToken ct)
        {
            if (delta is null)
            {
                throw new ArgumentNullException(nameof(delta));
            }

            ct.ThrowIfCancellationRequested();
            _deltas[c] = delta;
            return default;
        }

        public ValueTask<ChunkDelta?> LoadAsync(ChunkCoord c, System.Threading.CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return new ValueTask<ChunkDelta?>(_deltas.TryGetValue(c, out ChunkDelta? delta) ? delta : null);
        }
    }
}

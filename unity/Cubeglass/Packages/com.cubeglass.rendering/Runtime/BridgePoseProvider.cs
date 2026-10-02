using System;
using Cubeglass.Unity.Bridge;

namespace Cubeglass.Unity.Rendering
{
    /// <summary>
    /// Production <see cref="IPoseProvider"/>: reads the newest head sample
    /// straight from an open <see cref="BridgeClient"/> (5.12), which already
    /// keeps its last-good sample and reports <c>NotReady</c>/<c>Timeout</c>
    /// through <c>TryReadHead</c>.
    /// </summary>
    public sealed class BridgePoseProvider : IPoseProvider
    {
        private readonly BridgeClient client;

        public BridgePoseProvider(BridgeClient client)
        {
            this.client = client ?? throw new ArgumentNullException(nameof(client));
        }

        /// <summary>The wrapped bridge client.</summary>
        public BridgeClient Client
        {
            get { return client; }
        }

        /// <summary>Reads the newest head sample; false when none is available.</summary>
        public bool TryGetLatest(out BridgeHeadSample sample)
        {
            return client.TryReadHead(out sample);
        }
    }
}

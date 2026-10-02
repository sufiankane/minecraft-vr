using Cubeglass.Unity.Bridge;

namespace Cubeglass.Unity.Rendering
{
    /// <summary>
    /// Supplies the newest head sample to <see cref="LateLatchPose"/>.
    /// </summary>
    /// <remarks>
    /// The interface lives in this package so the production bridge adapter
    /// (<see cref="BridgePoseProvider"/>, here) and the scripted provider in
    /// <c>com.cubeglass.input</c> can both implement it without a package
    /// cycle: input depends on rendering, rendering depends on bridge.
    /// Implementations must be allocation-free: the late-latch path calls
    /// <see cref="TryGetLatest"/> once per rendered frame.
    /// </remarks>
    public interface IPoseProvider
    {
        /// <summary>
        /// Copies the newest head sample when one is available. Returns false
        /// when the bridge is absent/not ready, in which case the caller keeps
        /// its last pose; <paramref name="sample"/> is then unspecified.
        /// </summary>
        bool TryGetLatest(out BridgeHeadSample sample);
    }
}

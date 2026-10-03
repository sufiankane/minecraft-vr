namespace Cubeglass.Unity.Input
{
    /// <summary>
    /// The additive snap-turn edge channel (S7 Task 4a, ADR-0011). Snap turn is
    /// not part of the frozen S4 <see cref="Cubeglass.Gameplay.InputFrame"/>, so
    /// a provider exposes it as a one-shot edge that the bridge consumes
    /// explicitly after sampling; the bridge applies the discrete increment to
    /// the player heading.
    /// </summary>
    public interface ISnapInputSource
    {
        /// <summary>
        /// True exactly once per snap-turn press edge; reading clears it so a
        /// single device press produces a single snap.
        /// </summary>
        bool ConsumeSnapPressed();
    }
}

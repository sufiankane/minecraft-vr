namespace Cubeglass.Unity.Input
{
    /// <summary>
    /// The additive snap-turn edge channel (S7 Task 4a, ADR-0011). Snap turn is
    /// not part of the frozen S4 <see cref="Cubeglass.Gameplay.InputFrame"/>, so
    /// a provider exposes it as a one-shot signed edge that the bridge consumes
    /// explicitly after sampling; the bridge applies the discrete increment to
    /// the player heading.
    /// </summary>
    public interface ISnapInputSource
    {
        /// <summary>
        /// Returns the pending snap-turn direction exactly once: -1 left,
        /// +1 right, 0 when no press edge is pending. Reading clears the edge,
        /// so a single device press produces a single snap.
        /// </summary>
        int ConsumeSnapDirection();
    }
}

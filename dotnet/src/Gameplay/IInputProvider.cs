namespace Cubeglass.Gameplay
{
    /// <summary>
    /// A pure read of the latest input state (dossier section 5.11).
    /// </summary>
    public interface IInputProvider
    {
        /// <summary>
        /// Returns the input frame for <paramref name="timeSeconds"/>: a pure
        /// function of the provider's timeline, with no clocks and no side
        /// effects.
        /// </summary>
        InputFrame Sample(double timeSeconds);
    }
}

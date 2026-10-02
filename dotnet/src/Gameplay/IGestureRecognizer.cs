namespace Cubeglass.Gameplay
{
    /// <summary>
    /// Recognises hand gestures from joint frames (dossier section 5.11
    /// verbatim).
    /// </summary>
    /// <remarks>
    /// A pure state machine over <see cref="HandsInput"/>: the same input and
    /// <c>dt</c> series produce the same outputs, services hold no clocks and
    /// nothing is allocated on the hot path. <see cref="GestureRecognizer"/>
    /// records the additive conventions in ADR-0008.
    /// </remarks>
    public interface IGestureRecognizer
    {
        /// <summary>
        /// Advances the recogniser by <paramref name="dt"/> seconds.
        /// </summary>
        /// <param name="hands">The frame of hand tracking to consume.</param>
        /// <param name="dt">The fixed timestep in seconds.</param>
        /// <returns>The gesture state for this update.</returns>
        GestureOutput Update(in HandsInput hands, double dt);
    }
}

namespace Cubeglass.Gameplay
{
    /// <summary>
    /// The gesture state produced by a recogniser for one update (dossier
    /// section 5.11 names it without defining it; ADR-0008, R26).
    /// </summary>
    /// <remarks>
    /// The default value is the neutral output: no gesture active and zero
    /// strength. <see cref="PinchStrength"/> lies in [0, 1].
    /// </remarks>
    public readonly struct GestureOutput
    {
        public GestureOutput(bool pinching, bool fist, bool paletteFlick, float pinchStrength)
        {
            Pinching = pinching;
            Fist = fist;
            PaletteFlick = paletteFlick;
            PinchStrength = pinchStrength;
        }

        /// <summary>Whether the pinch gesture is active after hysteresis.</summary>
        public bool Pinching { get; }

        /// <summary>Whether the fist gesture is active.</summary>
        public bool Fist { get; }

        /// <summary>Whether a palette flick was detected in this update.</summary>
        public bool PaletteFlick { get; }

        /// <summary>How closed the pinch is, clamped to [0, 1].</summary>
        public float PinchStrength { get; }
    }
}

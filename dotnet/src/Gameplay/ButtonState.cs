namespace Cubeglass.Gameplay
{
    /// <summary>
    /// The edge/latch state of one button in an <see cref="InputFrame"/>
    /// (dossier section 5.11 names it without defining it; ADR-0008, R26).
    /// </summary>
    /// <remarks>
    /// <see cref="Up"/> and <see cref="Held"/> are steady states;
    /// <see cref="Pressed"/> is the frame a button goes down and
    /// <see cref="Released"/> the frame it comes up. Zero is
    /// <see cref="Up"/>, so a default frame carries no button edges.
    /// </remarks>
    public enum ButtonState
    {
        Up,
        Pressed,
        Held,
        Released
    }
}

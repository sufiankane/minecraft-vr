namespace Cubeglass.Gameplay
{
    /// <summary>
    /// How well the input provider is currently tracking the user (dossier
    /// section 5.11 names it without defining it; ADR-0008, R26).
    /// </summary>
    /// <remarks>
    /// Zero is <see cref="None"/>, so a default frame reports no tracking.
    /// Any loss longer than 200 ms cancels in-progress break or place
    /// (section 5.11).
    /// </remarks>
    public enum TrackingQuality
    {
        None,
        Degraded,
        Good
    }
}

using Cubeglass.Voxel;

namespace Cubeglass.Gameplay
{
    /// <summary>
    /// The observable outcome of one interaction update (dossier section 5.11
    /// names it without defining it; ADR-0008, R26).
    /// </summary>
    /// <remarks>
    /// The default value is idle: no break in progress, zero progress, no
    /// edit and no target. <see cref="BreakProgress"/> lies in [0, 1].
    /// </remarks>
    public readonly struct InteractionResult
    {
        public InteractionResult(bool breakInProgress, float breakProgress, bool edited, Int3? target)
        {
            BreakInProgress = breakInProgress;
            BreakProgress = breakProgress;
            Edited = edited;
            Target = target;
        }

        /// <summary>Whether a break is accumulating on <see cref="Target"/>.</summary>
        public bool BreakInProgress { get; }

        /// <summary>Break completion fraction, clamped to [0, 1].</summary>
        public float BreakProgress { get; }

        /// <summary>Whether this update applied an edit to the world.</summary>
        public bool Edited { get; }

        /// <summary>The targeted cell, or null when the ray hit nothing in reach.</summary>
        public Int3? Target { get; }
    }
}

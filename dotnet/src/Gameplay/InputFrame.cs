using Cubeglass.CoreMath;

namespace Cubeglass.Gameplay
{
#pragma warning disable CA1051 // Dossier section 5.11 freezes these as public fields.
#pragma warning disable CA1720 // The field name Pointer is frozen by dossier section 5.11.
    /// <summary>
    /// One frame of abstract input (dossier section 5.11 verbatim; the
    /// constructor and <see cref="Neutral"/> are additive per ADR-0008, R26).
    /// </summary>
    /// <remarks>
    /// The field order and types are exactly section 5.11. A default frame is
    /// the neutral frame: zero move and turn, no recentre, no pointer, no
    /// button edges, zero hotbar delta and <see cref="TrackingQuality.None"/>.
    /// <see cref="HotbarDelta"/> is a full <c>int</c> as frozen; providers must
    /// produce <c>-1</c>, <c>0</c> or <c>+1</c>.
    /// </remarks>
    public readonly struct InputFrame
    {
        /// <summary>Creates a frame with every field set explicitly.</summary>
        public InputFrame(
            Vector2f move,
            float turnSnap,
            bool recenterPressed,
            PointerRay? pointer,
            ButtonState primary,
            ButtonState secondary,
            int hotbarDelta,
            TrackingQuality quality)
        {
            Move = move;
            TurnSnap = turnSnap;
            RecenterPressed = recenterPressed;
            Pointer = pointer;
            Primary = primary;
            Secondary = secondary;
            HotbarDelta = hotbarDelta;
            Quality = quality;
        }

        public readonly Vector2f Move;

        public readonly float TurnSnap;

        public readonly bool RecenterPressed;

        public readonly PointerRay? Pointer;

        public readonly ButtonState Primary;

        public readonly ButtonState Secondary;

        public readonly int HotbarDelta;

        public readonly TrackingQuality Quality;

        /// <summary>The neutral frame; identical to <c>default(InputFrame)</c>.</summary>
        public static InputFrame Neutral => default;
    }
#pragma warning restore CA1720
#pragma warning restore CA1051
}

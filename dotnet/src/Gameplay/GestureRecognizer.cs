using System;
using Cubeglass.CoreMath;

namespace Cubeglass.Gameplay
{
    /// <summary>
    /// The S4 gesture recogniser over mock hands (dossier section 5.11;
    /// ADR-0008).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Hysteresis bands.</b> ADR-0008 pins two edges per gesture. Both
    /// measures are small when the hand is closed, so the state engages when
    /// the measure falls to the <b>closed</b> edge and releases when it rises
    /// to the <b>open</b> edge; between the edges the previous state holds.
    /// Reading the pair the other way round (engage at the open edge, release
    /// at the closed edge) would toggle the output on every frame for any
    /// measure between the two edges, so the latched band is the reading that
    /// satisfies the "oscillation cannot flip without crossing the opposite
    /// edge" rule. Pinch uses the 0.5/0.7 pair and fist the 0.4/0.6 pair.
    /// </para>
    /// <para>
    /// <b>Pinch.</b>
    /// <c>ratio = |ThumbTip - IndexTip| / |Wrist - MiddleTip|</c>. The pinch
    /// engages at <c>ratio &lt;= <see cref="PinchClosedRatio"/></c> and
    /// releases at <c>ratio &gt;= <see cref="PinchOpenRatio"/></c>. While
    /// engaged
    /// <c>PinchStrength = clamp((0.7 - ratio) / 0.2, 0, 1)</c>, otherwise 0.
    /// </para>
    /// <para>
    /// <b>Fist.</b>
    /// <c>ratio = max(|IndexTip - Wrist|, |MiddleTip - Wrist|,
    /// |RingTip - Wrist|, |LittleTip - Wrist|) /
    /// max(|Wrist - MiddleTip|, <see cref="NominalHandScale"/>)</c>. The fist
    /// engages at <c>ratio &lt;= <see cref="FistClosedRatio"/></c> and
    /// releases at <c>ratio &gt;= <see cref="FistOpenRatio"/></c>. The thumb
    /// tip is excluded because its pose is independent (a natural fist can
    /// leave it clear) and it already drives the pinch measure; the middle
    /// tip anchors the scale because there are no palm joints in the S4
    /// frame, and the denominator is floored by
    /// <see cref="NominalHandScale"/> so the curled middle finger of a fist
    /// cannot collapse the measure being tested. S13 replaces the nominal
    /// floor with the per-user calibrated hand length.
    /// </para>
    /// <para>
    /// <b>Palette flick.</b> A pinch release arms a
    /// <see cref="PaletteFlickWindowSeconds"/> window (inclusive). A re-pinch
    /// inside the window reports <c>PaletteFlick</c> true on exactly that
    /// frame; the flick frame also reports the new pinch and its strength.
    /// The window freezes while tracking is held and is cleared by a
    /// tracking-loss clear.
    /// </para>
    /// <para>
    /// <b>Tracking loss.</b> A frame is a loss frame when
    /// <c>Tracked == false</c> or when both hands are absent. Accumulated
    /// loss at or below <see cref="TrackingLossTimeoutSeconds"/> holds the
    /// last outputs (<c>Pinching</c>, <c>Fist</c>, <c>PinchStrength</c>) and
    /// freezes every timer; the first frame strictly past the timeout clears
    /// all outputs and timers once. Recovery evaluates from the cleared
    /// state.
    /// </para>
    /// <para>
    /// <b>Hand selection.</b> The left hand drives a frame whenever it is
    /// present, otherwise the right; the latched state carries across a hand
    /// switch. A frame whose joints are non-finite, or whose pinch scale is
    /// zero or non-finite, holds the state without advancing timers.
    /// </para>
    /// <para>
    /// <b>Purity.</b> Deterministic for identical input and allocation-free;
    /// a negative, NaN or infinite <c>dt</c> throws
    /// <see cref="ArgumentOutOfRangeException"/> and <c>dt == 0</c> still
    /// evaluates the current pose without advancing any timer.
    /// </para>
    /// </remarks>
    public sealed class GestureRecognizer : IGestureRecognizer
    {
        /// <summary>Pinch engages when the normalised thumb-index ratio falls to this closed edge (ADR-0008's 0.5).</summary>
        public const float PinchClosedRatio = 0.5f;

        /// <summary>Pinch releases when the normalised thumb-index ratio rises to this open edge (ADR-0008's 0.7).</summary>
        public const float PinchOpenRatio = 0.7f;

        /// <summary>Fist engages when the normalised four-finger reach falls to this closed edge (ADR-0008's 0.4).</summary>
        public const float FistClosedRatio = 0.4f;

        /// <summary>Fist releases when the normalised four-finger reach rises to this open edge (ADR-0008's 0.6).</summary>
        public const float FistOpenRatio = 0.6f;

        /// <summary>A pinch release stays eligible for a palette flick for this long, in seconds.</summary>
        public const double PaletteFlickWindowSeconds = 0.25;

        /// <summary>Tracking loss longer than this clears outputs and timers, in seconds.</summary>
        public const double TrackingLossTimeoutSeconds = 0.2;

        /// <summary>Reference wrist-to-middle-tip length that floors the fist scale, in metres.</summary>
        public const float NominalHandScale = 0.12f;

        private bool _pinching;
        private bool _fist;
        private float _pinchStrength;
        private bool _awaitingRepinch;
        private double _secondsSinceRelease;
        private double _trackingLossSeconds;
        private bool _trackingLost;

        /// <summary>Whether the most recent frame is past the 200 ms tracking-loss window.</summary>
        public bool TrackingLost => _trackingLost;

        /// <inheritdoc />
        public GestureOutput Update(in HandsInput hands, double dt)
        {
            if (!(dt >= 0.0) || double.IsInfinity(dt))
            {
                throw new ArgumentOutOfRangeException(nameof(dt), dt, "dt must be finite and non-negative.");
            }

            if (!hands.Tracked || (!hands.Left.HasValue && !hands.Right.HasValue))
            {
                _trackingLossSeconds += dt;
                if (_trackingLossSeconds > TrackingLossTimeoutSeconds)
                {
                    _trackingLost = true;
                    Clear();
                }

                return Held();
            }

            _trackingLossSeconds = 0.0;
            _trackingLost = false;

            HandFrame hand = hands.Left ?? hands.Right.GetValueOrDefault();
            float scale = Distance(hand.Wrist, hand.MiddleTip);
            if (!IsFinite(scale) || scale <= 0f || !IsFinite(hand))
            {
                return Held();
            }

            float pinchRatio = Distance(hand.ThumbTip, hand.IndexTip) / scale;
            float fistRatio = FistRatio(hand, scale);

            if (_awaitingRepinch)
            {
                _secondsSinceRelease += dt;
                if (_secondsSinceRelease > PaletteFlickWindowSeconds)
                {
                    _awaitingRepinch = false;
                }
            }

            bool flick = false;
            if (_pinching)
            {
                if (pinchRatio >= PinchOpenRatio)
                {
                    _pinching = false;
                    _pinchStrength = 0f;
                    _awaitingRepinch = true;
                    _secondsSinceRelease = 0.0;
                }
                else
                {
                    _pinchStrength = Clamp01((PinchOpenRatio - pinchRatio) / (PinchOpenRatio - PinchClosedRatio));
                }
            }
            else if (pinchRatio <= PinchClosedRatio)
            {
                _pinching = true;
                _pinchStrength = Clamp01((PinchOpenRatio - pinchRatio) / (PinchOpenRatio - PinchClosedRatio));
                if (_awaitingRepinch)
                {
                    flick = true;
                    _awaitingRepinch = false;
                    _secondsSinceRelease = 0.0;
                }
            }
            else
            {
                _pinchStrength = 0f;
            }

            if (_fist)
            {
                if (fistRatio >= FistOpenRatio)
                {
                    _fist = false;
                }
            }
            else if (fistRatio <= FistClosedRatio)
            {
                _fist = true;
            }

            return new GestureOutput(_pinching, _fist, flick, _pinchStrength);
        }

        private static float FistRatio(in HandFrame hand, float scale)
        {
            float reach = MathF.Max(
                MathF.Max(Distance(hand.Wrist, hand.IndexTip), Distance(hand.Wrist, hand.MiddleTip)),
                MathF.Max(Distance(hand.Wrist, hand.RingTip), Distance(hand.Wrist, hand.LittleTip)));
            return reach / MathF.Max(scale, NominalHandScale);
        }

        private static float Distance(in Vector3f a, in Vector3f b)
        {
            float dx = a.X - b.X;
            float dy = a.Y - b.Y;
            float dz = a.Z - b.Z;
            return MathF.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
        }

        private static bool IsFinite(in HandFrame hand)
        {
            return IsFinite(hand.Wrist)
                && IsFinite(hand.ThumbTip)
                && IsFinite(hand.IndexTip)
                && IsFinite(hand.MiddleTip)
                && IsFinite(hand.RingTip)
                && IsFinite(hand.LittleTip);
        }

        private static bool IsFinite(in Vector3f value)
        {
            return IsFinite(value.X) && IsFinite(value.Y) && IsFinite(value.Z);
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static float Clamp01(float value)
        {
            if (value < 0f)
            {
                return 0f;
            }

            return value > 1f ? 1f : value;
        }

        private GestureOutput Held()
        {
            return new GestureOutput(_pinching, _fist, false, _pinchStrength);
        }

        private void Clear()
        {
            _pinching = false;
            _fist = false;
            _pinchStrength = 0f;
            _awaitingRepinch = false;
            _secondsSinceRelease = 0.0;
        }
    }
}

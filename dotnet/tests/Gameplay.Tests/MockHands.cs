using Cubeglass.CoreMath;

namespace Cubeglass.Gameplay.Tests
{
    /// <summary>
    /// Builds mock hand joint sets for gesture tests with a small DSL
    /// (task 3 brief): <see cref="Open"/>, <see cref="Pinch"/>,
    /// <see cref="Fist"/>, <see cref="Move"/> and <see cref="Override"/>.
    /// </summary>
    /// <remarks>
    /// The wrist sits at the origin, forward is -Z (ADR-0004) and the middle
    /// finger is the scale reference: <c>|wrist - middleTip| = Scale</c>, the
    /// nominal 0.12 m hand length of ADR-0008. <see cref="Pinch"/> places the
    /// thumb and index tips <c>(0.7 - 0.2 * strength) * Scale</c> apart, the
    /// inverse of the recogniser's strength map, so a pinch pose with
    /// <c>strength &gt;= 1.05</c> is decisively closed and <c>0.5</c> sits
    /// in the held band; <see cref="Fist"/> poses the four finger tips at
    /// about 0.30 of the scale with the thumb held clear of the index tip so
    /// only the fist recognises.
    /// </remarks>
    internal static class MockHands
    {
        /// <summary>The reference wrist-to-middle-tip length in metres.</summary>
        public const float Scale = 0.12f;

        /// <summary>An open hand: thumb clearly away from the index tip, middle tip at the scale.</summary>
        public static HandFrame Open()
        {
            return new HandFrame(
                new Vector3f(0f, 0f, 0f),
                new Vector3f(0.09f, 0f, -0.03f),
                new Vector3f(0.035f, 0.005f, -0.105f),
                new Vector3f(0f, 0f, -Scale),
                new Vector3f(-0.035f, 0.005f, -0.105f),
                new Vector3f(-0.06f, 0f, -0.085f));
        }

        /// <summary>A pinch whose thumb-index separation maps to <paramref name="strength"/>.</summary>
        public static HandFrame Pinch(float strength)
        {
            float separation = (0.7f - (0.2f * strength)) * Scale;
            return new HandFrame(
                new Vector3f(0f, 0f, 0f),
                new Vector3f(separation, 0f, -0.06f),
                new Vector3f(0f, 0f, -0.06f),
                new Vector3f(0f, 0f, -Scale),
                new Vector3f(-0.035f, 0.005f, -0.105f),
                new Vector3f(-0.06f, 0f, -0.085f));
        }

        /// <summary>A closed hand: the four finger tips bunch near the wrist, thumb clear of the index.</summary>
        public static HandFrame Fist()
        {
            return new HandFrame(
                new Vector3f(0f, 0f, 0f),
                new Vector3f(0.05f, 0f, -0.06f),
                new Vector3f(0.012f, 0.008f, -0.032f),
                new Vector3f(0f, 0.01f, -0.035f),
                new Vector3f(-0.012f, 0.008f, -0.032f),
                new Vector3f(-0.02f, 0.005f, -0.028f));
        }

        /// <summary>Translates every joint of <paramref name="hand"/>; all measures are translation invariant.</summary>
        public static HandFrame Move(HandFrame hand, float x, float y, float z)
        {
            return new HandFrame(
                Translate(hand.Wrist, x, y, z),
                Translate(hand.ThumbTip, x, y, z),
                Translate(hand.IndexTip, x, y, z),
                Translate(hand.MiddleTip, x, y, z),
                Translate(hand.RingTip, x, y, z),
                Translate(hand.LittleTip, x, y, z));
        }

        /// <summary>Replaces the named joints of <paramref name="hand"/>; null keeps the pose.</summary>
        public static HandFrame Override(
            HandFrame hand,
            Vector3f? wrist = null,
            Vector3f? thumbTip = null,
            Vector3f? indexTip = null,
            Vector3f? middleTip = null,
            Vector3f? ringTip = null,
            Vector3f? littleTip = null)
        {
            return new HandFrame(
                wrist ?? hand.Wrist,
                thumbTip ?? hand.ThumbTip,
                indexTip ?? hand.IndexTip,
                middleTip ?? hand.MiddleTip,
                ringTip ?? hand.RingTip,
                littleTip ?? hand.LittleTip);
        }

        /// <summary>Tracked input with only the left hand.</summary>
        public static HandsInput Left(HandFrame hand)
        {
            return new HandsInput(true, hand, null);
        }

        /// <summary>Tracked input with only the right hand.</summary>
        public static HandsInput Right(HandFrame hand)
        {
            return new HandsInput(true, null, hand);
        }

        /// <summary>Tracked input with both hands (either may be absent).</summary>
        public static HandsInput Both(HandFrame? left, HandFrame? right)
        {
            return new HandsInput(true, left, right);
        }

        /// <summary>Untracked input with no hands.</summary>
        public static HandsInput Untracked()
        {
            return new HandsInput(false, null, null);
        }

        private static Vector3f Translate(Vector3f value, float x, float y, float z)
        {
            return new Vector3f(value.X + x, value.Y + y, value.Z + z);
        }
    }
}

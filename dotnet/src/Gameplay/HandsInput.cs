using Cubeglass.CoreMath;

namespace Cubeglass.Gameplay
{
    /// <summary>
    /// The tracked joints of one hand, in world space (dossier section 5.11
    /// names <c>HandsInput</c> without defining this shape; ADR-0008, R26).
    /// </summary>
    /// <remarks>
    /// The minimal S4 joint set; S11 extends it with the SDK's full skeleton.
    /// </remarks>
    public readonly struct HandFrame
    {
        public HandFrame(
            Vector3f wrist,
            Vector3f thumbTip,
            Vector3f indexTip,
            Vector3f middleTip,
            Vector3f ringTip,
            Vector3f littleTip)
        {
            Wrist = wrist;
            ThumbTip = thumbTip;
            IndexTip = indexTip;
            MiddleTip = middleTip;
            RingTip = ringTip;
            LittleTip = littleTip;
        }

        public Vector3f Wrist { get; }

        public Vector3f ThumbTip { get; }

        public Vector3f IndexTip { get; }

        public Vector3f MiddleTip { get; }

        public Vector3f RingTip { get; }

        public Vector3f LittleTip { get; }
    }

    /// <summary>
    /// One frame of hand tracking for <see cref="IGestureRecognizer"/>
    /// (dossier section 5.11 names <c>HandsInput</c> without defining it;
    /// ADR-0008, R26).
    /// </summary>
    /// <remarks>
    /// The minimal S4 shape, extended by S11. The default value is untracked
    /// with both hands absent.
    /// </remarks>
    public readonly struct HandsInput
    {
        public HandsInput(bool tracked, HandFrame? left, HandFrame? right)
        {
            Tracked = tracked;
            Left = left;
            Right = right;
        }

        /// <summary>Whether the provider currently tracks the hands at all.</summary>
        public bool Tracked { get; }

        /// <summary>The left hand joints, or null when not tracked.</summary>
        public HandFrame? Left { get; }

        /// <summary>The right hand joints, or null when not tracked.</summary>
        public HandFrame? Right { get; }
    }
}

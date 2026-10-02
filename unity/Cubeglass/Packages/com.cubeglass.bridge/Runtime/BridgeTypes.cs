using System.Runtime.InteropServices;

namespace Cubeglass.Unity.Bridge
{
    /// <summary>Mirrors <c>cg_status</c> from <c>contracts/cg_types.h</c>.</summary>
    public enum BridgeStatus
    {
        Ok = 0,
        InvalidArg = 1,
        NotReady = 2,
        Device = 3,
        Timeout = 4,
        Unsupported = 5,
        Internal = 6,
    }

    /// <summary>Mirrors <c>cg_track_state</c> from <c>contracts/cg_types.h</c>.</summary>
    public enum TrackState
    {
        Stable = 0,
        Unstable = 1,
        Lost = 2,
    }

    /// <summary>Mirrors <c>cg_vec3</c> (12 bytes); field order pinned by the C ABI.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 12)]
    public struct BridgeVec3
    {
        [FieldOffset(0)] public float X;
        [FieldOffset(4)] public float Y;
        [FieldOffset(8)] public float Z;
    }

    /// <summary>Mirrors <c>cg_quat</c> (16 bytes); C order is <c>w, x, y, z</c>.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 16)]
    public struct BridgeQuat
    {
        [FieldOffset(0)] public float W;
        [FieldOffset(4)] public float X;
        [FieldOffset(8)] public float Y;
        [FieldOffset(12)] public float Z;
    }

    /// <summary>Mirrors <c>cg_pose</c> (28 bytes): position then rotation.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 28)]
    public struct BridgePose
    {
        /// <summary>Position in metres. FieldOffset 0.</summary>
        [FieldOffset(0)] public BridgeVec3 Position;

        /// <summary>Unit quaternion. FieldOffset 12.</summary>
        [FieldOffset(12)] public BridgeQuat Rotation;
    }

    /// <summary>
    /// Mirrors <c>cg_head_sample</c> (48 bytes): the C struct's 44 payload bytes
    /// plus 4 bytes of alignment padding, so <c>Size = 48</c> matches
    /// <c>sizeof(cg_head_sample)</c>.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 48)]
    public struct BridgeHeadSample
    {
        /// <summary>HostTime (monotonic nanoseconds) the sample refers to. FieldOffset 0.</summary>
        [FieldOffset(0)] public long HostTime;

        /// <summary>Head pose: position at 8, rotation at 20 (bytes 8..35).</summary>
        [FieldOffset(8)] public BridgePose Pose;

        /// <summary>Tracking state. FieldOffset 36.</summary>
        [FieldOffset(36)] public TrackState State;

        /// <summary>Writer seqlock generation. FieldOffset 40.</summary>
        [FieldOffset(40)] public uint Sequence;

        // Bytes 44..47 are C padding before the next 8-byte-aligned record.
    }

    /// <summary>
    /// Mirrors <c>cg_hand</c> (272 bytes): flags, confidence, the 21-joint array
    /// in head space (metres) and the wrist velocity. Each joint <c>i</c> sits at
    /// byte <c>8 + 12 * i</c>.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 272)]
    public struct BridgeHand
    {
        /// <summary>0/1. FieldOffset 0.</summary>
        [FieldOffset(0)] public byte Present;

        /// <summary>0 left, 1 right. FieldOffset 1.</summary>
        [FieldOffset(1)] public byte Handedness;

        /// <summary>Reserved byte 0. FieldOffset 2.</summary>
        [FieldOffset(2)] public byte Reserved0;

        /// <summary>Reserved byte 1. FieldOffset 3.</summary>
        [FieldOffset(3)] public byte Reserved1;

        /// <summary>Tracking confidence, 0..1. FieldOffset 4.</summary>
        [FieldOffset(4)] public float Confidence;

        /// <summary>Joint 0, head space. FieldOffset 8.</summary>
        [FieldOffset(8)] public BridgeVec3 Joint0;

        /// <summary>Joint 1. FieldOffset 20.</summary>
        [FieldOffset(20)] public BridgeVec3 Joint1;

        /// <summary>Joint 2. FieldOffset 32.</summary>
        [FieldOffset(32)] public BridgeVec3 Joint2;

        /// <summary>Joint 3. FieldOffset 44.</summary>
        [FieldOffset(44)] public BridgeVec3 Joint3;

        /// <summary>Joint 4. FieldOffset 56.</summary>
        [FieldOffset(56)] public BridgeVec3 Joint4;

        /// <summary>Joint 5. FieldOffset 68.</summary>
        [FieldOffset(68)] public BridgeVec3 Joint5;

        /// <summary>Joint 6. FieldOffset 80.</summary>
        [FieldOffset(80)] public BridgeVec3 Joint6;

        /// <summary>Joint 7. FieldOffset 92.</summary>
        [FieldOffset(92)] public BridgeVec3 Joint7;

        /// <summary>Joint 8. FieldOffset 104.</summary>
        [FieldOffset(104)] public BridgeVec3 Joint8;

        /// <summary>Joint 9. FieldOffset 116.</summary>
        [FieldOffset(116)] public BridgeVec3 Joint9;

        /// <summary>Joint 10. FieldOffset 128.</summary>
        [FieldOffset(128)] public BridgeVec3 Joint10;

        /// <summary>Joint 11. FieldOffset 140.</summary>
        [FieldOffset(140)] public BridgeVec3 Joint11;

        /// <summary>Joint 12. FieldOffset 152.</summary>
        [FieldOffset(152)] public BridgeVec3 Joint12;

        /// <summary>Joint 13. FieldOffset 164.</summary>
        [FieldOffset(164)] public BridgeVec3 Joint13;

        /// <summary>Joint 14. FieldOffset 176.</summary>
        [FieldOffset(176)] public BridgeVec3 Joint14;

        /// <summary>Joint 15. FieldOffset 188.</summary>
        [FieldOffset(188)] public BridgeVec3 Joint15;

        /// <summary>Joint 16. FieldOffset 200.</summary>
        [FieldOffset(200)] public BridgeVec3 Joint16;

        /// <summary>Joint 17. FieldOffset 212.</summary>
        [FieldOffset(212)] public BridgeVec3 Joint17;

        /// <summary>Joint 18. FieldOffset 224.</summary>
        [FieldOffset(224)] public BridgeVec3 Joint18;

        /// <summary>Joint 19. FieldOffset 236.</summary>
        [FieldOffset(236)] public BridgeVec3 Joint19;

        /// <summary>Joint 20 (wrist). FieldOffset 248.</summary>
        [FieldOffset(248)] public BridgeVec3 Joint20;

        /// <summary>Wrist velocity in m/s. FieldOffset 260.</summary>
        [FieldOffset(260)] public BridgeVec3 Velocity;
    }

    /// <summary>
    /// Mirrors <c>cg_hand_frame</c> (576 bytes): the two 272-byte hands end at
    /// byte 572 and the C struct pads to its 8-byte alignment (R47), so
    /// <c>Size = 576</c> matches <c>sizeof(cg_hand_frame)</c>.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 576)]
    public struct BridgeHandFrame
    {
        /// <summary>Camera frame time mapped to HostTime. FieldOffset 0.</summary>
        [FieldOffset(0)] public long CaptureTime;

        /// <summary>FieldOffset 8.</summary>
        [FieldOffset(8)] public long PublishTime;

        /// <summary>The time the joints are extrapolated to. FieldOffset 16.</summary>
        [FieldOffset(16)] public long PredictedFor;

        /// <summary>Writer seqlock generation. FieldOffset 24.</summary>
        [FieldOffset(24)] public uint Sequence;

        /// <summary>hands[0]. FieldOffset 28.</summary>
        [FieldOffset(28)] public BridgeHand Hand0;

        /// <summary>hands[1]. FieldOffset 300.</summary>
        [FieldOffset(300)] public BridgeHand Hand1;

        // Bytes 572..575 are C tail padding.
    }
}

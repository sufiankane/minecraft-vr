using System;
using Cubeglass.CoreMath;
using UnityEngine;

namespace Cubeglass.Unity.Rendering
{
    /// <summary>
    /// The body-level player transform (S7 Task 4a, ADR-0011). The scene is
    /// built in Unity space (R52), so its world position is
    /// <c>PlayerState.Position</c> routed through the single ADR-0004
    /// <see cref="UnityConvert"/> mirror, and its yaw is
    /// <c>PlayerState.YawRadians</c> converted the same way (internal yaw 90°
    /// becomes Unity −90°).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The head level (the stereo rig carrying <see cref="StereoRig"/> and
    /// <see cref="LateLatchPose"/>) is a child at
    /// <c>(0, <see cref="EyeHeightMeters"/>, 0)</c> in the root's local frame.
    /// The late latch gives it head-relative rotation only; the root owns the
    /// position and the body yaw, so the head can never overwrite the turn
    /// (ADR-0011). The rig follows the player, never the reverse: nothing here
    /// reads back into <c>PlayerState</c>.
    /// </para>
    /// <para>
    /// The root is expected to be a top-level scene object; it writes
    /// <c>transform.position</c> and a yaw-only local rotation.
    /// </para>
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class PlayerRoot : MonoBehaviour
    {
        /// <summary>
        /// The ADR-0008 eye height above the feet centre, matching
        /// <c>InteractionService.EyeHeight</c> and <c>PlayerState.BodyHeight</c>.
        /// </summary>
        public const float EyeHeightMeters = 0.9f;

        [SerializeField] private Transform head;

        private bool hasPose;

        /// <summary>The head child (the stereo rig); defaults to the first child.</summary>
        public Transform Head
        {
            get { return head; }
            set { head = value; }
        }

        /// <summary>True after at least one <see cref="SetPlayerPose"/> call.</summary>
        public bool HasPose
        {
            get { return hasPose; }
        }

        /// <summary>The last internal position applied; default before the first pose.</summary>
        public Vec3 PositionInternal { get; private set; }

        /// <summary>The last internal yaw applied, in radians.</summary>
        public float YawRadians { get; private set; }

        private void Awake()
        {
            if (head == null && transform.childCount > 0)
            {
                head = transform.GetChild(0);
            }

            ApplyHeadOffset();
        }

        /// <summary>
        /// Applies the player's body pose: position and yaw converted exactly
        /// once through <see cref="UnityConvert"/>, and the fixed eye height on
        /// the head child.
        /// </summary>
        public void SetPlayerPose(Vec3 internalPosition, float yawRadians)
        {
            PositionInternal = internalPosition;
            YawRadians = yawRadians;
            hasPose = true;

            Vec3 unityPosition = UnityConvert.ToUnity(internalPosition);
            transform.position = new Vector3(
                (float)unityPosition.X,
                (float)unityPosition.Y,
                (float)unityPosition.Z);
            transform.localRotation = Heading(yawRadians);
            ApplyHeadOffset();
        }

        /// <summary>Pins the head child to the eye height above the feet centre.</summary>
        public void ApplyHeadOffset()
        {
            if (head != null)
            {
                head.localPosition = new Vector3(0f, EyeHeightMeters, 0f);
            }
        }

        /// <summary>
        /// Converts an internal body yaw to a Unity yaw-only rotation through
        /// the single <see cref="UnityConvert"/> source (ADR-0004).
        /// </summary>
        public static Quaternion Heading(float yawRadians)
        {
            double half = yawRadians * 0.5;
            Quat internalYaw = Quat.FromComponents(Math.Cos(half), 0.0, Math.Sin(half), 0.0);
            Quat unity = UnityConvert.ToUnity(internalYaw);
            return new Quaternion((float)unity.X, (float)unity.Y, (float)unity.Z, (float)unity.W);
        }
    }
}

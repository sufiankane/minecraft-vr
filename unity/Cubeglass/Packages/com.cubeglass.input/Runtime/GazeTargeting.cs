using Cubeglass.CoreMath;
using Cubeglass.Gameplay;
using UnityEngine;

namespace Cubeglass.Unity.Input
{
    /// <summary>
    /// Builds the gaze <see cref="PointerRay"/> from the active stereo camera
    /// (S7 Task 3): the midpoint of the two eye cameras as origin, their
    /// averaged forward direction, limited by the caller to
    /// <see cref="DefaultReach"/> metres.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The eye cameras live in Unity space, so their positions and directions
    /// are routed through the single <c>Cubeglass.CoreMath.UnityConvert</c>
    /// flip (ADR-0004) into the internal right-handed frame the voxel world
    /// and <c>DdaRaycaster</c> use: identity rotation gives the ADR forward
    /// <c>(0, 0, -1)</c>, positive internal yaw turns toward -X and positive
    /// internal pitch looks up. The conversion is an involution, so the same
    /// call serves both directions.
    /// </para>
    /// <para>
    /// <b>Reach.</b> A <see cref="PointerRay"/> is unbounded; the reach limit
    /// is applied by the interaction service (ADR-0008 pins it at 5.0 m).
    /// <see cref="DefaultReach"/> is the matching value callers pass when they
    /// cast the returned ray themselves.
    /// </para>
    /// </remarks>
    public static class GazeTargeting
    {
        /// <summary>ADR-0008 interaction reach in metres.</summary>
        public const float DefaultReach = 5f;

        /// <summary>
        /// Builds the pointer ray from the midpoint of two eye cameras. A null
        /// eye falls back to the other; both null, a non-finite pose or a
        /// degenerate direction return false and leave <paramref name="ray"/>
        /// default.
        /// </summary>
        public static bool TryBuildPointerRay(Camera leftEye, Camera rightEye, out PointerRay ray)
        {
            ray = default;

            Camera fallback = leftEye != null ? leftEye : rightEye;
            if (fallback == null)
            {
                return false;
            }

            Vector3 origin;
            Vector3 forward;
            if (leftEye != null && rightEye != null)
            {
                origin = 0.5f * (leftEye.transform.position + rightEye.transform.position);
                Vector3 summed = leftEye.transform.forward + rightEye.transform.forward;
                forward = summed.sqrMagnitude > 0f ? summed.normalized : fallback.transform.forward;
            }
            else
            {
                origin = fallback.transform.position;
                forward = fallback.transform.forward;
            }

            return TryBuildInternalRay(origin, forward, out ray);
        }

        /// <summary>
        /// Builds the pointer ray from a <see cref="Cubeglass.Unity.Rendering.StereoRig"/>'s
        /// eye cameras; false when the rig has no camera yet.
        /// </summary>
        public static bool TryBuildPointerRay(
            Cubeglass.Unity.Rendering.StereoRig rig,
            out PointerRay ray)
        {
            ray = default;
            if (rig == null)
            {
                return false;
            }

            return TryBuildPointerRay(rig.LeftCamera, rig.RightCamera, out ray);
        }

        private static bool TryBuildInternalRay(Vector3 origin, Vector3 forward, out PointerRay ray)
        {
            ray = default;
            if (!IsFinite(origin) || !IsFinite(forward))
            {
                return false;
            }

            // ADR-0004: (x, y, z) -> (x, y, -z). UnityConvert is its own
            // inverse, so it is the one recorded implementation to call.
            Vec3 internalOrigin = UnityConvert.ToUnity(new Vec3(origin.x, origin.y, origin.z));
            Vec3 internalDirection = UnityConvert.ToUnity(new Vec3(forward.x, forward.y, forward.z));

            var candidate = new PointerRay(internalOrigin, internalDirection);
            if (!candidate.TryToRay(out _))
            {
                return false;
            }

            ray = candidate;
            return true;
        }

        private static bool IsFinite(Vector3 value)
        {
            return !float.IsNaN(value.x) && !float.IsInfinity(value.x)
                && !float.IsNaN(value.y) && !float.IsInfinity(value.y)
                && !float.IsNaN(value.z) && !float.IsInfinity(value.z);
        }
    }
}

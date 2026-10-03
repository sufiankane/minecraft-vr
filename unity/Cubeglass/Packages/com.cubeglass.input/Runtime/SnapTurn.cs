using UnityEngine;

namespace Cubeglass.Unity.Input
{
    /// <summary>
    /// Comfort snap-turn configuration and accumulator (S7 Task 3, reworked in
    /// Task 4a). The component is edge-agnostic: the input provider emits the
    /// snap press through <see cref="ISnapInputSource"/> and
    /// <see cref="GameplayBridge"/> consumes it, asks this component for the
    /// increment and applies it to the player heading.
    /// </summary>
    /// <remarks>
    /// The snap lands in <c>PlayerState.YawRadians</c> (as a Unity-yaw
    /// direction) before <c>PlayerController.Step</c>, so the movement basis
    /// follows the turn; <see cref="Cubeglass.Unity.Rendering.PlayerRoot"/>
    /// composes its rotation from that state and
    /// <see cref="Cubeglass.Unity.Rendering.LateLatchPose"/> only writes the
    /// head child, so a snap can never be overwritten by the head pose
    /// (ADR-0011). Smooth turning is deliberately absent in S7.
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class SnapTurn : MonoBehaviour
    {
        /// <summary>Default snap increment in degrees.</summary>
        public const float DefaultIncrementDegrees = 45f;

        [SerializeField] private float incrementDegrees = DefaultIncrementDegrees;
        [SerializeField] private bool snapEnabled = true;

        /// <summary>The increment applied by <see cref="ApplyIncrement"/>.</summary>
        public float IncrementDegrees
        {
            get { return incrementDegrees; }
            set { incrementDegrees = value; }
        }

        /// <summary>When false, <see cref="ApplyIncrement"/> returns zero.</summary>
        public bool SnapEnabled
        {
            get { return snapEnabled; }
            set { snapEnabled = value; }
        }

        /// <summary>Total degrees applied since the last reset.</summary>
        public float AccumulatedDegrees { get; private set; }

        /// <summary>
        /// Applies <c>direction * IncrementDegrees</c>, accumulates it and
        /// returns the Unity-yaw degrees to subtract from the internal heading
        /// (positive is the Unity +Y direction, i.e. turn right). Zero,
        /// non-finite and disabled calls return zero and change nothing.
        /// </summary>
        public float ApplyIncrement(float direction)
        {
            if (!snapEnabled || direction == 0f || float.IsNaN(direction) || float.IsInfinity(direction))
            {
                return 0f;
            }

            float degrees = direction * incrementDegrees;
            if (degrees == 0f || float.IsNaN(degrees) || float.IsInfinity(degrees))
            {
                return 0f;
            }

            AccumulatedDegrees += degrees;
            return degrees;
        }

        /// <summary>Resets <see cref="AccumulatedDegrees"/> without turning.</summary>
        public void ResetAccumulated()
        {
            AccumulatedDegrees = 0f;
        }
    }
}

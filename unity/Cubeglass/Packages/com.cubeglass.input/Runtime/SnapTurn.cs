using UnityEngine;

namespace Cubeglass.Unity.Input
{
    /// <summary>
    /// Applies discrete comfort snap turns to the rig yaw (S7 Task 3,
    /// default 45 degrees). The component is edge-agnostic: the input mapping
    /// emits the increment on exactly the snap-turn press edge, and
    /// <see cref="GameplayBridge"/> forwards that single value here, so a held
    /// button snaps once.
    /// </summary>
    /// <remarks>
    /// <see cref="Apply"/> pre-multiplies a yaw-only rotation in parent space,
    /// leaving pitch and roll untouched; <see cref="AccumulatedDegrees"/>
    /// records the total applied for diagnostics. Smooth turning is
    /// deliberately absent in S7.
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

        /// <summary>When false, <see cref="Apply"/> is a no-op.</summary>
        public bool SnapEnabled
        {
            get { return snapEnabled; }
            set { snapEnabled = value; }
        }

        /// <summary>Total degrees applied since the last reset.</summary>
        public float AccumulatedDegrees { get; private set; }

        /// <summary>
        /// Rotates this transform's yaw only by <paramref name="degrees"/>
        /// (positive is the Unity +Y direction). Zero, non-finite and disabled
        /// calls are no-ops.
        /// </summary>
        public void Apply(float degrees)
        {
            if (!snapEnabled || degrees == 0f || float.IsNaN(degrees) || float.IsInfinity(degrees))
            {
                return;
            }

            transform.localRotation = Quaternion.Euler(0f, degrees, 0f) * transform.localRotation;
            AccumulatedDegrees += degrees;
        }

        /// <summary>Applies <c>direction * IncrementDegrees</c>.</summary>
        public void ApplyIncrement(float direction)
        {
            Apply(direction * incrementDegrees);
        }

        /// <summary>Resets <see cref="AccumulatedDegrees"/> without moving the rig.</summary>
        public void ResetAccumulated()
        {
            AccumulatedDegrees = 0f;
        }
    }
}

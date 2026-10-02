using Cubeglass.CoreMath;
using Cubeglass.Voxel;

namespace Cubeglass.Gameplay
{
    /// <summary>
    /// A world-space pointer ray carried by an <see cref="InputFrame"/>
    /// (dossier section 5.11 names it without defining it; ADR-0008, R26).
    /// </summary>
    /// <remarks>
    /// The direction need not be unit; <see cref="TryToRay"/> normalises it.
    /// </remarks>
    public readonly record struct PointerRay(Vec3 Origin, Vec3 Direction)
    {
        /// <summary>
        /// Converts to a <see cref="Ray"/> with a normalised direction.
        /// </summary>
        /// <param name="ray">
        /// The converted ray, or <c>default</c> when the conversion fails.
        /// </param>
        /// <returns>
        /// False when <see cref="Origin"/> is non-finite, or when
        /// <see cref="Direction"/> is zero, non-finite, or degenerates during
        /// normalisation (its squared length underflows to zero, or its length
        /// overflows so the normalised vector is zero).
        /// </returns>
        public bool TryToRay(out Ray ray)
        {
            ray = default;

            if (!IsFinite(Origin.X) || !IsFinite(Origin.Y) || !IsFinite(Origin.Z))
            {
                return false;
            }

            Vec3 direction = Vec3.Normalized(Direction);
            if (!IsFinite(direction.X) || !IsFinite(direction.Y) || !IsFinite(direction.Z)
                || (direction.X == 0.0 && direction.Y == 0.0 && direction.Z == 0.0))
            {
                return false;
            }

            ray = new Ray(Origin, direction);
            return true;
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }
}

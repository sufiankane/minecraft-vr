using Cubeglass.CoreMath;
using Cubeglass.Voxel;

namespace Cubeglass.Gameplay
{
    /// <summary>
    /// The mutable state of the local player (dossier section 5.11 names it
    /// without defining it; ADR-0008, R27).
    /// </summary>
    /// <remarks>
    /// A sealed class with settable properties, not a struct: section 5.11
    /// passes <c>PlayerState</c> by value into
    /// <see cref="IInteractionService.Update"/>, so reference semantics are
    /// required for hotbar and recentre changes to persist (R27).
    /// <see cref="Position"/> is the feet centre; <see cref="Body"/> derives
    /// the collision box from it on every access.
    /// </remarks>
    public sealed class PlayerState
    {
        /// <summary>Half extent of the body on the X axis, in metres.</summary>
        public const double HalfWidth = 0.3;

        /// <summary>Body height above the feet, in metres.</summary>
        public const double BodyHeight = 0.9;

        /// <summary>Half extent of the body on the Z axis, in metres.</summary>
        public const double HalfDepth = 0.3;

        /// <summary>The feet centre position in metres.</summary>
        public Vec3 Position { get; set; }

        /// <summary>The current velocity in metres per second.</summary>
        public Vec3 Velocity { get; set; }

        /// <summary>Yaw in radians; zero faces -Z, positive turns toward -X.</summary>
        public float YawRadians { get; set; }

        /// <summary>Pitch in radians; positive looks up, clamped to +/-89 degrees.</summary>
        public float PitchRadians { get; set; }

        /// <summary>Whether the feet rested on a solid cell after the last step.</summary>
        public bool OnGround { get; set; }

        /// <summary>The selected hotbar slot, normally in [0, Hotbar.SlotCount).</summary>
        public int HotbarIndex { get; set; }

        /// <summary>
        /// The player collision box: half extents 0.3 x 0.9 x 0.3 with the
        /// feet centre at <see cref="Position"/>.
        /// </summary>
        public Aabb Body => new Aabb(
            new Vec3(Position.X - HalfWidth, Position.Y, Position.Z - HalfDepth),
            new Vec3(Position.X + HalfWidth, Position.Y + BodyHeight, Position.Z + HalfDepth));
    }
}

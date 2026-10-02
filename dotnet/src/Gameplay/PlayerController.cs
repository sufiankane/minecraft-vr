using System;
using Cubeglass.CoreMath;
using Cubeglass.Voxel;

namespace Cubeglass.Gameplay
{
    /// <summary>
    /// The S4 fixed-timestep player movement integrator (ADR-0008).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Model.</b> <see cref="Step"/> consumes only <see cref="InputFrame.Move"/>
    /// and <see cref="InputFrame.TurnSnap"/>; pointer, buttons, hotbar delta,
    /// recentre and quality are consumed by the interaction and gesture
    /// services. Movement has no acceleration: horizontal velocity is replaced
    /// from the frame every step at <see cref="WalkSpeed"/> m/s, scaled by
    /// <c>min(|Move|, 1)</c>. Yaw is right-handed about +Y with zero facing -Z
    /// and positive turning toward -X; <see cref="InputFrame.TurnSnap"/> is
    /// degrees per second. Pitch has no input source in S4 and is clamped to
    /// +/-89 degrees every step.
    /// </para>
    /// <para>
    /// <b>Gravity and collision.</b> Gravity is -25 m/s^2 capped at
    /// <see cref="TerminalFallSpeed"/>; the vertical displacement uses the
    /// average of the pre- and post-gravity velocities so splitting a step is
    /// frame-invariant. Collision is resolved per axis in X, then Z, then Y:
    /// the full displacement is attempted and, when the resulting body
    /// overlaps a solid cell (<see cref="VoxelCollision.Overlaps"/>; unloaded
    /// cells read as air), that axis is restored to its exact pre-move
    /// coordinate and its velocity component is zeroed. Landing zeroes
    /// <see cref="PlayerState.Velocity"/>'s Y component and sets
    /// <see cref="PlayerState.OnGround"/>; there is no contact snap, so the
    /// resting height may be up to one step above the surface. S4 does not
    /// sweep: the caller must keep per-step displacement below one cell
    /// (4.5 m/s at 60 Hz is 0.075 m).
    /// </para>
    /// <para>
    /// <b>Edge rules.</b> A null player or world throws
    /// <see cref="ArgumentNullException"/>; a negative, NaN or infinite
    /// <paramref name="dt"/> throws <see cref="ArgumentOutOfRangeException"/>.
    /// <c>dt == 0</c> is a physics no-op that still clamps pitch. The method
    /// allocates nothing.
    /// </para>
    /// </remarks>
    public static class PlayerController
    {
        /// <summary>Horizontal walk speed in metres per second.</summary>
        public const double WalkSpeed = 4.5;

        /// <summary>Gravity acceleration in metres per second squared.</summary>
        public const double Gravity = -25.0;

        /// <summary>Fastest downward speed in metres per second.</summary>
        public const double TerminalFallSpeed = -40.0;

        /// <summary>The pitch clamp, +/-89 degrees in radians.</summary>
        public const double MaxPitchRadians = 89.0 * (Math.PI / 180.0);

        private const double DegreesToRadians = Math.PI / 180.0;

        /// <summary>
        /// Advances <paramref name="player"/> by <paramref name="dt"/> seconds
        /// under <paramref name="input"/>, resolving collisions against
        /// <paramref name="world"/>.
        /// </summary>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="player"/> or <paramref name="world"/> is null.
        /// </exception>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="dt"/> is negative, NaN or infinite.
        /// </exception>
        public static void Step(PlayerState player, in InputFrame input, IWorld world, double dt)
        {
            if (player is null)
            {
                throw new ArgumentNullException(nameof(player));
            }

            if (world is null)
            {
                throw new ArgumentNullException(nameof(world));
            }

            if (!(dt >= 0.0) || double.IsInfinity(dt))
            {
                throw new ArgumentOutOfRangeException(nameof(dt), dt, "dt must be finite and non-negative.");
            }

            ClampPitch(player);

            if (dt == 0.0)
            {
                return;
            }

            if (input.TurnSnap != 0f)
            {
                player.YawRadians = (float)(player.YawRadians + (input.TurnSnap * DegreesToRadians * dt));
            }

            double strafe = input.Move.X;
            double forward = input.Move.Y;
            double moveLength = Math.Sqrt((strafe * strafe) + (forward * forward));
            double cos = Math.Cos(player.YawRadians);
            double sin = Math.Sin(player.YawRadians);
            double scale = moveLength > 1.0 ? WalkSpeed / moveLength : WalkSpeed;
            double velocityX = ((strafe * cos) - (forward * sin)) * scale;
            double velocityZ = ((-strafe * sin) - (forward * cos)) * scale;

            double velocityY0 = player.Velocity.Y;
            double velocityY = velocityY0 + (Gravity * dt);
            if (velocityY < TerminalFallSpeed)
            {
                velocityY = TerminalFallSpeed;
            }

            double displacementY = 0.5 * (velocityY0 + velocityY) * dt;

            velocityX = MoveX(player, world, velocityX, dt);
            velocityZ = MoveZ(player, world, velocityZ, dt);
            velocityY = MoveY(player, world, velocityY, displacementY);

            player.Velocity = new Vec3(velocityX, velocityY, velocityZ);
        }

        private static double MoveX(PlayerState player, IWorld world, double velocity, double dt)
        {
            if (velocity == 0.0)
            {
                return 0.0;
            }

            double start = player.Position.X;
            double displacement = velocity * dt;

            player.Position = new Vec3(start + displacement, player.Position.Y, player.Position.Z);
            if (VoxelCollision.Overlaps(world, player.Body))
            {
                player.Position = new Vec3(start, player.Position.Y, player.Position.Z);
                return 0.0;
            }

            return velocity;
        }

        private static double MoveZ(PlayerState player, IWorld world, double velocity, double dt)
        {
            if (velocity == 0.0)
            {
                return 0.0;
            }

            double start = player.Position.Z;
            double displacement = velocity * dt;

            player.Position = new Vec3(player.Position.X, player.Position.Y, start + displacement);
            if (VoxelCollision.Overlaps(world, player.Body))
            {
                player.Position = new Vec3(player.Position.X, player.Position.Y, start);
                return 0.0;
            }

            return velocity;
        }

        private static double MoveY(PlayerState player, IWorld world, double velocity, double displacement)
        {
            double start = player.Position.Y;
            player.Position = new Vec3(player.Position.X, start + displacement, player.Position.Z);
            if (VoxelCollision.Overlaps(world, player.Body))
            {
                player.Position = new Vec3(player.Position.X, start, player.Position.Z);
                player.OnGround = velocity <= 0.0;
                return 0.0;
            }

            player.OnGround = false;
            return velocity;
        }

        private static void ClampPitch(PlayerState player)
        {
            float pitch = player.PitchRadians;
            if (pitch > MaxPitchRadians)
            {
                pitch = (float)MaxPitchRadians;
            }
            else if (pitch < -MaxPitchRadians)
            {
                pitch = (float)-MaxPitchRadians;
            }

            player.PitchRadians = pitch;
        }
    }
}

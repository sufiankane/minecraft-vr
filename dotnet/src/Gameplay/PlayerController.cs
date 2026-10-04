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
    /// <b>State sanitisation.</b> A corrupt input frame or a corrupt player
    /// state cannot poison the step: NaN <see cref="InputFrame.Move"/>
    /// components map to zero, positive/negative infinity clamps to the
    /// documented range end (+/-1), and a NaN or infinite
    /// <see cref="InputFrame.TurnSnap"/> maps to zero or the finite float range
    /// end respectively. A non-finite <see cref="PlayerState.YawRadians"/>
    /// resets to zero before use and the yaw update is clamped to the finite
    /// float range. On <see cref="PlayerState.Position"/> and
    /// <see cref="PlayerState.Velocity"/>, a NaN component resets to zero and an
    /// infinite component clamps to the finite range end; a NaN
    /// <see cref="PlayerState.PitchRadians"/> resets to zero and an infinite
    /// one clamps to +/- <see cref="MaxPitchRadians"/>. An axis displacement
    /// that would leave the finite range is refused outright (the axis keeps
    /// its coordinate and the velocity component is zeroed, exactly like a
    /// blocked move), so for any finite <paramref name="dt"/> the step leaves a
    /// finite player state.
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

            SanitiseState(player);

            if (dt == 0.0)
            {
                return;
            }

            float yaw = player.YawRadians;
            if (!float.IsFinite(yaw))
            {
                yaw = 0f;
            }

            if (input.TurnSnap != 0f)
            {
                yaw = ClampToFloatRange(yaw + (SanitiseTurnSnap(input.TurnSnap) * DegreesToRadians * dt));
            }

            player.YawRadians = yaw;

            double strafe = SanitiseMoveComponent(input.Move.X);
            double forward = SanitiseMoveComponent(input.Move.Y);
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
            double next = start + (velocity * dt);
            if (!double.IsFinite(next))
            {
                return 0.0;
            }

            player.Position = new Vec3(next, player.Position.Y, player.Position.Z);
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
            double next = start + (velocity * dt);
            if (!double.IsFinite(next))
            {
                return 0.0;
            }

            player.Position = new Vec3(player.Position.X, player.Position.Y, next);
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
            double next = start + displacement;
            if (!double.IsFinite(next))
            {
                player.OnGround = displacement <= 0.0;
                return 0.0;
            }

            player.Position = new Vec3(player.Position.X, next, player.Position.Z);
            if (VoxelCollision.Overlaps(world, player.Body))
            {
                player.Position = new Vec3(player.Position.X, start, player.Position.Z);
                player.OnGround = displacement <= 0.0;
                return 0.0;
            }

            player.OnGround = false;
            return velocity;
        }

        private static double SanitiseMoveComponent(float component)
        {
            if (float.IsNaN(component))
            {
                return 0.0;
            }

            if (float.IsPositiveInfinity(component))
            {
                return 1.0;
            }

            if (float.IsNegativeInfinity(component))
            {
                return -1.0;
            }

            return component;
        }

        private static double SanitiseTurnSnap(float turnSnap)
        {
            if (float.IsNaN(turnSnap))
            {
                return 0.0;
            }

            if (float.IsPositiveInfinity(turnSnap))
            {
                return float.MaxValue;
            }

            if (float.IsNegativeInfinity(turnSnap))
            {
                return float.MinValue;
            }

            return turnSnap;
        }

        private static float ClampToFloatRange(double value)
        {
            if (value >= float.MaxValue)
            {
                return float.MaxValue;
            }

            if (value <= float.MinValue)
            {
                return float.MinValue;
            }

            return (float)value;
        }

        private static void SanitiseState(PlayerState player)
        {
            player.Position = SanitiseVec3(player.Position);
            player.Velocity = SanitiseVec3(player.Velocity);
            ClampPitch(player);
        }

        private static Vec3 SanitiseVec3(Vec3 value)
        {
            return new Vec3(
                SanitiseCoordinate(value.X),
                SanitiseCoordinate(value.Y),
                SanitiseCoordinate(value.Z));
        }

        private static double SanitiseCoordinate(double value)
        {
            if (double.IsNaN(value))
            {
                return 0.0;
            }

            if (double.IsPositiveInfinity(value))
            {
                return double.MaxValue;
            }

            if (double.IsNegativeInfinity(value))
            {
                return double.MinValue;
            }

            return value;
        }

        private static void ClampPitch(PlayerState player)
        {
            float pitch = player.PitchRadians;
            if (float.IsNaN(pitch))
            {
                pitch = 0f;
            }
            else if (pitch > MaxPitchRadians)
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

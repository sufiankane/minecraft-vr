using System;
using Cubeglass.CoreMath;
using Cubeglass.Voxel;
using NUnit.Framework;

namespace Cubeglass.Gameplay.Tests
{
    // Player physics contract (ADR-0008): feet-centre body, yaw-relative move
    // at 4.5 m/s with no acceleration, gravity -25 m/s^2 capped at terminal
    // -40 m/s, and collision resolved per axis in X -> Z -> Y order by
    // reverting the axis move that would overlap a solid cell.
    [TestFixture]
    public sealed class PlayerControllerTests
    {
        private const double Dt = 0.02;

        [Test]
        public void BodyDerivesFromTheFeetCentrePosition()
        {
            PlayerState player = Player(1, 2, 3);

            Assert.That(player.Body.Min.NearlyEquals(new Vec3(0.7, 2.0, 2.7), 1e-12), Is.True);
            Assert.That(player.Body.Max.NearlyEquals(new Vec3(1.3, 2.9, 3.3), 1e-12), Is.True);
            Assert.That(PlayerState.HalfWidth, Is.EqualTo(0.3));
            Assert.That(PlayerState.BodyHeight, Is.EqualTo(0.9));
            Assert.That(PlayerState.HalfDepth, Is.EqualTo(0.3));
        }

        [Test]
        public void FallsUnderGravityAndRestsOnTheFloor()
        {
            IWorld world = TestWorlds.CreateFloor();
            PlayerState player = Player(8, 2, 8);

            Run(player, default, world, 1.0 / 60.0, 600);

            Assert.That(player.OnGround, Is.True);
            Assert.That(player.Velocity.Y, Is.EqualTo(0.0));
            Assert.That(player.Position.Y, Is.GreaterThanOrEqualTo(1.0));
            Assert.That(player.Position.Y, Is.LessThanOrEqualTo(1.2));

            double restingY = player.Position.Y;
            Run(player, default, world, 1.0 / 60.0, 10);

            Assert.That(player.Position.Y, Is.EqualTo(restingY), "resting height must not creep");
            Assert.That(player.OnGround, Is.True);
        }

        [Test]
        public void OnGroundIsFalseWhileFalling()
        {
            IWorld world = TestWorlds.CreateFloor();
            PlayerState player = Player(8, 5, 8);

            PlayerController.Step(player, in Frames.Neutral, world, Dt);

            Assert.That(player.OnGround, Is.False);
            Assert.That(player.Velocity.Y, Is.LessThan(0.0));
        }

        [Test]
        public void StopsAtAWallWithoutTunnelling()
        {
            IWorld world = TestWorlds.CreateFloorWithWallX(12);
            PlayerState player = Player(2, 1, 8);
            InputFrame input = Move(1f, 0f);

            for (int i = 0; i < 300; i++)
            {
                PlayerController.Step(player, in input, world, Dt);
                Assert.That(VoxelCollision.Overlaps(world, player.Body), Is.False, $"overlap at step {i}");
            }

            Assert.That(player.Position.X, Is.GreaterThan(2.0), "the player must have moved");
            Assert.That(player.Position.X, Is.LessThan(12.0), "the player must not pass the wall");
            Assert.That(player.Body.Max.X, Is.LessThanOrEqualTo(12.0));
            Assert.That(player.Velocity.X, Is.EqualTo(0.0), "a blocked axis zeroes its velocity");
            Assert.That(player.OnGround, Is.True);
        }

        [Test]
        public void SlidesAlongAWallWhenMovingDiagonally()
        {
            IWorld world = TestWorlds.CreateFloorWithWallX(12);
            PlayerState player = Player(11, 1, 8);
            InputFrame input = Move(1f, 1f);

            Run(player, input, world, Dt, 12);
            Assert.That(player.Body.Max.X, Is.LessThanOrEqualTo(12.0));

            double pinnedX = player.Position.X;
            double zBefore = player.Position.Z;
            Run(player, input, world, Dt, 20);

            Assert.That(player.Position.X, Is.EqualTo(pinnedX), "the wall axis must stay pinned");
            Assert.That(player.Position.Z, Is.LessThan(zBefore), "the free axis must keep sliding");
            Assert.That(player.Position.Z, Is.LessThan(7.0));
            Assert.That(player.Body.Max.X, Is.LessThanOrEqualTo(12.0));
            Assert.That(player.OnGround, Is.True);
        }

        [Test]
        public void CeilingStopsAnUpwardMoveAndClearsVerticalVelocity()
        {
            var world = TestWorlds.CreateFloor();
            Assert.That(
                world.Apply(new EditCommand(new Int3(8, 2, 8), BlockId.Air, TestWorlds.Stone, 1)),
                Is.EqualTo(EditResult.Applied));

            PlayerState player = Player(8, 1, 8);
            player.Velocity = new Vec3(0, 5, 0);

            PlayerController.Step(player, in Frames.Neutral, world, Dt);
            Assert.That(player.OnGround, Is.False);

            PlayerController.Step(player, in Frames.Neutral, world, Dt);

            Assert.That(player.Position.Y, Is.EqualTo(1.095).Within(1e-12));
            Assert.That(player.Velocity.Y, Is.EqualTo(0.0));
            Assert.That(player.OnGround, Is.False);
        }

        [Test]
        public void BlockedUpwardMoveWithNonPositivePostGravityVelocityKeepsOnGroundFalse()
        {
            var world = TestWorlds.CreateFloor();
            Assert.That(
                world.Apply(new EditCommand(new Int3(8, 2, 8), BlockId.Air, TestWorlds.Stone, 1)),
                Is.EqualTo(EditResult.Applied));

            // v0 = +1.5 m/s at dt = 0.1 s and gravity -25 m/s^2 leave the
            // post-gravity velocity at -1.0 m/s while the trapezoidal
            // displacement is still +0.025 m; the ceiling blocks that upward
            // move. OnGround must follow the displacement direction, not the
            // post-gravity velocity sign.
            PlayerState player = Player(8, 1.09, 8);
            player.Velocity = new Vec3(0, 1.5, 0);

            PlayerController.Step(player, in Frames.Neutral, world, 0.1);

            Assert.That(player.Position.Y, Is.EqualTo(1.09).Within(1e-12), "the blocked move reverts to its exact start");
            Assert.That(player.Velocity.Y, Is.EqualTo(0.0), "a blocked axis zeroes its velocity");
            Assert.That(player.OnGround, Is.False, "an upward displacement stopped by a ceiling is not a landing");
        }

        [Test]
        public void FrameSplitInvarianceHoldsWithinOneMillimetre()
        {
            IWorld world = TestWorlds.CreateEmpty();
            PlayerState single = Player(0, 10, 0);
            PlayerState split = Player(0, 10, 0);
            InputFrame input = Move(0f, 1f, 30f);

            PlayerController.Step(single, in input, world, 0.02);
            PlayerController.Step(split, in input, world, 0.01);
            PlayerController.Step(split, in input, world, 0.01);

            Assert.That(single.Position.NearlyEquals(split.Position, 1e-3), Is.True);
            Assert.That(single.Velocity.NearlyEquals(split.Velocity, 1e-3), Is.True);
            Assert.That(single.YawRadians, Is.EqualTo(split.YawRadians).Within(1e-3));
        }

        [Test]
        public void SameStartAndInputsProduceIdenticalState()
        {
            var script = new (double, InputFrame)[]
            {
                (0.0, Move(0f, 1f, 30f)),
                (0.4, Move(1f, 1f, 0f)),
                (0.8, Move(0f, 0f, -90f)),
                (1.2, Move(-1f, 0f, 45f)),
                (1.6, default),
            };

            PlayerState first = RunScript(new ScriptedInputProvider(script));
            PlayerState second = RunScript(new ScriptedInputProvider(script));

            Assert.That(Hash(second), Is.EqualTo(Hash(first)));
            Assert.That(second.Position, Is.EqualTo(first.Position));
            Assert.That(second.Velocity, Is.EqualTo(first.Velocity));
            Assert.That(second.YawRadians, Is.EqualTo(first.YawRadians));
            Assert.That(second.PitchRadians, Is.EqualTo(first.PitchRadians));
            Assert.That(second.OnGround, Is.EqualTo(first.OnGround));
        }

        [Test]
        public void DifferentInputsChangeTheStateHash()
        {
            var moving = new ScriptedInputProvider(
                new[] { (0.0, Move(0f, 1f, 0f)), (0.5, Move(0f, 1f, 0f)) });
            var still = new ScriptedInputProvider(Array.Empty<(double, InputFrame)>());

            Assert.That(Hash(RunScript(moving)), Is.Not.EqualTo(Hash(RunScript(still))));
        }

        [Test]
        public void TurnSnapTurnsYawAtDegreesPerSecond()
        {
            IWorld world = TestWorlds.CreateEmpty();
            PlayerState player = Player(0, 10, 0);
            InputFrame input = Move(0f, 0f, 90f);

            PlayerController.Step(player, in input, world, 1.0);

            Assert.That(player.YawRadians, Is.EqualTo((float)(Math.PI / 2.0)).Within(1e-5));
        }

        [Test]
        public void MoveDirectionIsYawRelative()
        {
            IWorld world = TestWorlds.CreateEmpty();
            float quarterTurn = (float)(Math.PI / 2.0);

            PlayerState forward = Player(0, 10, 0);
            forward.YawRadians = quarterTurn;
            InputFrame forwardInput = Move(0f, 1f);
            PlayerController.Step(forward, in forwardInput, world, 1.0);
            Assert.That(forward.Position.X, Is.EqualTo(-4.5).Within(1e-5), "yaw +90 turns forward to -X");
            Assert.That(forward.Position.Z, Is.EqualTo(0.0).Within(1e-5));

            PlayerState strafe = Player(0, 10, 0);
            strafe.YawRadians = quarterTurn;
            InputFrame strafeInput = Move(1f, 0f);
            PlayerController.Step(strafe, in strafeInput, world, 1.0);
            Assert.That(strafe.Position.Z, Is.EqualTo(-4.5).Within(1e-5), "yaw +90 turns right to -Z");
            Assert.That(strafe.Position.X, Is.EqualTo(0.0).Within(1e-5));
        }

        [Test]
        public void WalkSpeedIsFourPointFiveAndTheMoveVectorIsClamped()
        {
            IWorld world = TestWorlds.CreateEmpty();

            PlayerState forward = Player(0, 10, 0);
            InputFrame forwardInput = Move(0f, 1f);
            PlayerController.Step(forward, in forwardInput, world, Dt);
            Assert.That(HorizontalSpeed(forward), Is.EqualTo(4.5).Within(1e-12));
            Assert.That(forward.Position.Z, Is.EqualTo(-4.5 * Dt).Within(1e-12));

            PlayerState diagonal = Player(0, 10, 0);
            InputFrame diagonalInput = Move(1f, 1f);
            PlayerController.Step(diagonal, in diagonalInput, world, Dt);
            Assert.That(HorizontalSpeed(diagonal), Is.EqualTo(4.5).Within(1e-12), "a diagonal is clamped to walk speed");

            PlayerState half = Player(0, 10, 0);
            InputFrame halfInput = Move(0.5f, 0f);
            PlayerController.Step(half, in halfInput, world, Dt);
            Assert.That(HorizontalSpeed(half), Is.EqualTo(2.25).Within(1e-12), "partial input scales the speed");

            PlayerState still = Player(0, 10, 0);
            PlayerController.Step(still, in Frames.Neutral, world, Dt);
            Assert.That(still.Velocity.X, Is.EqualTo(0.0));
            Assert.That(still.Velocity.Z, Is.EqualTo(0.0));
        }

        [Test]
        public void TerminalFallSpeedClampsDownwardVelocity()
        {
            IWorld world = TestWorlds.CreateEmpty();
            PlayerState player = Player(0, 1000, 0);

            Run(player, default, world, 0.5, 10);

            Assert.That(player.Velocity.Y, Is.EqualTo(-40.0));
        }

        [Test]
        public void PitchClampsToEightyNineDegrees()
        {
            IWorld world = TestWorlds.CreateEmpty();

            Assert.That(
                PlayerController.MaxPitchRadians,
                Is.EqualTo(89.0 * Math.PI / 180.0).Within(1e-12));

            PlayerState up = Player(0, 10, 0);
            up.PitchRadians = 2.0f;
            PlayerController.Step(up, in Frames.Neutral, world, Dt);
            Assert.That(up.PitchRadians, Is.EqualTo((float)PlayerController.MaxPitchRadians));

            PlayerState down = Player(0, 10, 0);
            down.PitchRadians = -2.0f;
            PlayerController.Step(down, in Frames.Neutral, world, Dt);
            Assert.That(down.PitchRadians, Is.EqualTo((float)-PlayerController.MaxPitchRadians));

            PlayerState inside = Player(0, 10, 0);
            inside.PitchRadians = 0.5f;
            PlayerController.Step(inside, in Frames.Neutral, world, Dt);
            Assert.That(inside.PitchRadians, Is.EqualTo(0.5f));
        }

        [Test]
        public void ZeroDtIsAPhysicsNoOpButStillClampsPitch()
        {
            IWorld world = TestWorlds.CreateEmpty();
            PlayerState player = Player(0, 10, 0);
            player.Velocity = new Vec3(1, 2, 3);
            player.OnGround = true;
            player.PitchRadians = 2.0f;

            PlayerController.Step(player, in Frames.Neutral, world, 0.0);

            Assert.That(player.Position, Is.EqualTo(new Vec3(0, 10, 0)));
            Assert.That(player.Velocity, Is.EqualTo(new Vec3(1, 2, 3)));
            Assert.That(player.OnGround, Is.True);
            Assert.That(player.PitchRadians, Is.EqualTo((float)PlayerController.MaxPitchRadians));
        }

        [Test]
        public void NegativeOrNonFiniteDtIsRejected()
        {
            IWorld world = TestWorlds.CreateEmpty();
            PlayerState player = Player(0, 10, 0);

            Assert.Throws<ArgumentOutOfRangeException>(() => StepWith(player, world, -0.01));
            Assert.Throws<ArgumentOutOfRangeException>(() => StepWith(player, world, double.NaN));
            Assert.Throws<ArgumentOutOfRangeException>(() => StepWith(player, world, double.PositiveInfinity));
            Assert.Throws<ArgumentOutOfRangeException>(() => StepWith(player, world, double.NegativeInfinity));
        }

        [Test]
        public void NullPlayerOrWorldIsRejected()
        {
            IWorld world = TestWorlds.CreateEmpty();
            PlayerState player = Player(0, 10, 0);

            Assert.Throws<ArgumentNullException>(() => StepWith(null!, world, Dt));
            Assert.Throws<ArgumentNullException>(() => StepWith(player, null!, Dt));
        }

        [Test]
        public void StepAllocatesNothing()
        {
            IWorld world = TestWorlds.CreateFloor();
            PlayerState player = Player(8, 1, 8);
            InputFrame[] inputs = { Move(1f, 0f), Move(-1f, 0f) };

            for (int i = 0; i < 1_000; i++)
            {
                PlayerController.Step(player, in inputs[i & 1], world, Dt);
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100_000; i++)
            {
                PlayerController.Step(player, in inputs[i & 1], world, Dt);
            }

            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.That(allocated, Is.EqualTo(0L), $"PlayerController.Step allocated {allocated} bytes");
        }

        private static void StepWith(PlayerState player, IWorld world, double dt)
        {
            PlayerController.Step(player, in Frames.Neutral, world, dt);
        }

        private static PlayerState RunScript(ScriptedInputProvider provider)
        {
            IWorld world = TestWorlds.CreateFloor();
            PlayerState player = Player(8, 1, 8);
            const double step = 1.0 / 60.0;

            for (int i = 0; i < 120; i++)
            {
                InputFrame frame = provider.Sample(i * step);
                PlayerController.Step(player, in frame, world, step);
            }

            return player;
        }

        private static ulong Hash(PlayerState player)
        {
            ulong hash = 14695981039346656037UL;
            hash = Mix(hash, (ulong)BitConverter.DoubleToInt64Bits(player.Position.X));
            hash = Mix(hash, (ulong)BitConverter.DoubleToInt64Bits(player.Position.Y));
            hash = Mix(hash, (ulong)BitConverter.DoubleToInt64Bits(player.Position.Z));
            hash = Mix(hash, (ulong)BitConverter.DoubleToInt64Bits(player.Velocity.X));
            hash = Mix(hash, (ulong)BitConverter.DoubleToInt64Bits(player.Velocity.Y));
            hash = Mix(hash, (ulong)BitConverter.DoubleToInt64Bits(player.Velocity.Z));
            hash = Mix(hash, (ulong)(uint)BitConverter.SingleToInt32Bits(player.YawRadians));
            hash = Mix(hash, (ulong)(uint)BitConverter.SingleToInt32Bits(player.PitchRadians));
            hash = Mix(hash, player.OnGround ? 1UL : 0UL);
            hash = Mix(hash, (ulong)(uint)player.HotbarIndex);
            return hash;
        }

        private static ulong Mix(ulong hash, ulong value)
        {
            return (hash ^ value) * 1099511628211UL;
        }

        private static PlayerState Player(double x, double y, double z)
        {
            return new PlayerState { Position = new Vec3(x, y, z) };
        }

        private static InputFrame Move(float strafe, float forward, float turnSnap = 0f)
        {
            return new InputFrame(
                new Vector2f(strafe, forward),
                turnSnap,
                false,
                null,
                ButtonState.Up,
                ButtonState.Up,
                0,
                TrackingQuality.Good);
        }

        private static void Run(PlayerState player, InputFrame input, IWorld world, double dt, int steps)
        {
            for (int i = 0; i < steps; i++)
            {
                PlayerController.Step(player, in input, world, dt);
            }
        }

        private static double HorizontalSpeed(PlayerState player)
        {
            return Math.Sqrt(
                (player.Velocity.X * player.Velocity.X) + (player.Velocity.Z * player.Velocity.Z));
        }

        private static class Frames
        {
            // A field, not a property: it must be passable by `in`.
#pragma warning disable CA1805 // The neutral frame is the default value by definition (ADR-0008).
            internal static readonly InputFrame Neutral = default;
#pragma warning restore CA1805
        }
    }
}

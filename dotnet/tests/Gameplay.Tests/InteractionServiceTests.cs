using System;
using Cubeglass.CoreMath;
using Cubeglass.Voxel;
using FsCheck;
using FsCheck.Fluent;
using NUnit.Framework;

namespace Cubeglass.Gameplay.Tests
{
    // The interaction service contract (dossier 5.11, ADR-0008): target
    // selection through a pointer or the yaw/pitch view ray, hold-to-break at
    // max(0.05, hardness), edge-triggered placement gated by CanPlace, hotbar
    // cycling on the mutable PlayerState, the 200 ms tracking-loss cancel and
    // the recentre request. Every edit goes through IWorld.Apply.
    [TestFixture]
    public sealed class InteractionServiceTests
    {
        private const double Dt = 0.1;

        private static readonly BlockId Stone = new BlockId(1);
        private static readonly BlockId Dirt = new BlockId(2);

#pragma warning disable CA1805 // The neutral frame is the default value by definition (ADR-0008).
        private static readonly InputFrame Neutral = default;
#pragma warning restore CA1805

        [Test]
        public void ConstructorRejectsNullArguments()
        {
            Assert.Throws<ArgumentNullException>(() => new InteractionService(null!, BlockRegistry.Default));
            Assert.Throws<ArgumentNullException>(() => new InteractionService(new DdaRaycaster(), null!));
        }

        [Test]
        public void UpdateRejectsNullPlayerOrWorld()
        {
            InteractionService service = NewService();
            World world = FloorWorld();
            PlayerState player = Player(8, 1, 8);

            Assert.Throws<ArgumentNullException>(() => service.Update(in Neutral, world, null!, Dt));
            Assert.Throws<ArgumentNullException>(() => service.Update(in Neutral, null!, player, Dt));
        }

        [Test]
        public void UpdateRejectsNegativeOrNonFiniteDt()
        {
            InteractionService service = NewService();
            World world = FloorWorld();
            PlayerState player = Player(8, 1, 8);

            Assert.Throws<ArgumentOutOfRangeException>(() => UpdateWith(service, world, player, -0.01));
            Assert.Throws<ArgumentOutOfRangeException>(() => UpdateWith(service, world, player, double.NaN));
            Assert.Throws<ArgumentOutOfRangeException>(() => UpdateWith(service, world, player, double.PositiveInfinity));
            Assert.Throws<ArgumentOutOfRangeException>(() => UpdateWith(service, world, player, double.NegativeInfinity));
        }

        [Test]
        public void ZeroDtStillProcessesPlacementAndRecentre()
        {
            InteractionService service = NewService();
            World world = FloorWorld();
            PlayerState player = Player(8, 1, 8, yaw: 1f);
            InputFrame frame = Frame(
                secondary: ButtonState.Pressed,
                pointer: Down(new Int3(2, 0, 2)),
                recenter: true);

            InteractionResult result = service.Update(in frame, world, player, 0.0);

            Assert.That(result.Edited, Is.True);
            Assert.That(world.Get(new Int3(2, 1, 2)), Is.EqualTo(Stone));
            Assert.That(player.YawRadians, Is.EqualTo(0f));
            Assert.That(service.Recentered, Is.True);
        }

        [Test]
        public void NeutralFrameIsIdleAndDoesNotEdit()
        {
            InteractionService service = NewService();
            World world = FloorWorld();
            PlayerState player = Player(8, 1, 8);
            ulong before = Hash(world);

            InteractionResult result = service.Update(in Neutral, world, player, Dt);

            Assert.That(result.BreakInProgress, Is.False);
            Assert.That(result.BreakProgress, Is.EqualTo(0f));
            Assert.That(result.Edited, Is.False);
            Assert.That(result.Target.HasValue, Is.False);
            Assert.That(service.State, Is.EqualTo(InteractionState.Idle));
            Assert.That(service.TrackingLost, Is.False);
            Assert.That(service.Recentered, Is.False);
            Assert.That(Hash(world), Is.EqualTo(before));
        }

        [Test]
        public void PointerHitIsTheTargetWithinReach()
        {
            InteractionService service = NewService();
            World world = FloorWorld();
            Set(world, new Int3(2, 1, 4), Stone);
            PlayerState player = Player(8, 1, 8);

            InteractionResult result = service.Update(
                Frame(pointer: Down(new Int3(2, 1, 4))), world, player, Dt);

            Assert.That(result.Target.GetValueOrDefault(), Is.EqualTo(new Int3(2, 1, 4)));
            Assert.That(result.Edited, Is.False);
        }

        [Test]
        public void ReachIsInclusiveAtFiveMetres()
        {
            InteractionService service = NewService();
            World world = FloorWorld();
            Set(world, new Int3(4, 1, 0), Stone);
            PlayerState player = Player(8, 1, 8);

            var exactlyFive = new PointerRay(new Vec3(4.5, 1.5, 6.0), new Vec3(0, 0, -1));
            InteractionResult hit = service.Update(Frame(pointer: exactlyFive), world, player, Dt);
            Assert.That(hit.Target.GetValueOrDefault(), Is.EqualTo(new Int3(4, 1, 0)));

            var tooFar = new PointerRay(new Vec3(4.5, 1.5, 6.25), new Vec3(0, 0, -1));
            InteractionResult miss = service.Update(Frame(pointer: tooFar), world, player, Dt);
            Assert.That(miss.Target.HasValue, Is.False);
        }

        [Test]
        public void ViewRayFollowsYawAndClampsPitch()
        {
            InteractionService service = NewService();
            World world = FloorWorld();
            Set(world, new Int3(8, 1, 4), Stone);
            Set(world, new Int3(4, 1, 8), Stone);
            float quarterTurn = (float)(Math.PI / 2.0);
            InputFrame good = Frame();

            PlayerState forward = Player(8, 1, 8);
            InteractionResult ahead = service.Update(in good, world, forward, Dt);
            Assert.That(ahead.Target.GetValueOrDefault(), Is.EqualTo(new Int3(8, 1, 4)), "yaw 0 must face -Z");

            PlayerState turned = Player(8, 1, 8, yaw: quarterTurn);
            InteractionResult left = service.Update(in good, world, turned, Dt);
            Assert.That(left.Target.GetValueOrDefault(), Is.EqualTo(new Int3(4, 1, 8)), "yaw +90 must face -X");

            PlayerState steep = Player(8, 1, 8, pitch: -2f);
            InteractionResult clamped = service.Update(in good, world, steep, Dt);
            Assert.That(
                clamped.Target.GetValueOrDefault(),
                Is.EqualTo(new Int3(8, 0, 7)),
                "pitch below -89 degrees must clamp and land just in front, not look backwards");

            World above = FloorWorld();
            Set(above, new Int3(8, 4, 8), Stone);
            PlayerState lookingBackwards = Player(8, 1, 8, pitch: 2f);
            InteractionResult up = service.Update(in good, above, lookingBackwards, Dt);
            Assert.That(
                up.Target.HasValue,
                Is.False,
                "pitch above +89 degrees must clamp instead of looking backwards at (8,4,8)");
        }

        [Test]
        public void PointerIsPreferredAndInvalidPointerFallsBackToViewRay()
        {
            InteractionService service = NewService();
            World world = FloorWorld();
            Set(world, new Int3(8, 1, 4), Stone);
            Set(world, new Int3(2, 1, 4), Stone);
            PlayerState player = Player(8, 1, 8);

            InteractionResult pointer = service.Update(
                Frame(pointer: Down(new Int3(2, 1, 4))), world, player, Dt);
            Assert.That(pointer.Target.GetValueOrDefault(), Is.EqualTo(new Int3(2, 1, 4)));

            var invalid = new PointerRay(new Vec3(double.NaN, 0, 0), new Vec3(0, -1, 0));
            InteractionResult fallback = service.Update(Frame(pointer: invalid), world, player, Dt);
            Assert.That(fallback.Target.GetValueOrDefault(), Is.EqualTo(new Int3(8, 1, 4)));
        }

        [Test]
        public void PressedFrameDoesNotAccumulateUntilHeld()
        {
            InteractionService service = NewService();
            World world = FloorWorld();
            Set(world, new Int3(2, 1, 4), Stone);
            PlayerState player = Player(8, 1, 8);

            InteractionResult pressed = service.Update(
                Frame(primary: ButtonState.Pressed, pointer: Down(new Int3(2, 1, 4))), world, player, Dt);

            Assert.That(pressed.BreakInProgress, Is.False);
            Assert.That(pressed.BreakProgress, Is.EqualTo(0f));
            Assert.That(pressed.Edited, Is.False);
            Assert.That(service.State, Is.EqualTo(InteractionState.Idle));
        }

        [Test]
        public void HoldingPrimaryToHardnessBreaksOnceAndResetsOnTheNewTarget()
        {
            InteractionService service = NewService();
            World world = FloorWorld();
            Set(world, new Int3(2, 1, 4), Dirt);
            PlayerState player = Player(8, 1, 8);
            PointerRay pointer = Down(new Int3(2, 1, 4));

            InteractionResult pressed = service.Update(
                Frame(primary: ButtonState.Pressed, pointer: pointer), world, player, Dt);
            Assert.That(pressed.Edited, Is.False, "the pressed frame only arms nothing: Held accumulates");

            InteractionResult firstHeld = service.Update(
                Frame(primary: ButtonState.Held, pointer: pointer), world, player, Dt);
            Assert.That(firstHeld.BreakInProgress, Is.True);
            Assert.That(firstHeld.BreakProgress, Is.EqualTo(0.2f).Within(1e-6f));
            Assert.That(service.State, Is.EqualTo(InteractionState.Breaking));

            for (int step = 0; step < 3; step++)
            {
                InteractionResult held = service.Update(
                    Frame(primary: ButtonState.Held, pointer: pointer), world, player, Dt);
                Assert.That(held.Edited, Is.False, $"no edit before hardness at step {step + 2}");
            }

            InteractionResult complete = service.Update(
                Frame(primary: ButtonState.Held, pointer: pointer), world, player, Dt);
            Assert.That(complete.Edited, Is.True);
            Assert.That(complete.BreakInProgress, Is.True, "the completing frame still reports the break");
            Assert.That(complete.BreakProgress, Is.EqualTo(1f));
            Assert.That(complete.Target.GetValueOrDefault(), Is.EqualTo(new Int3(2, 1, 4)));
            Assert.That(world.Get(new Int3(2, 1, 4)), Is.EqualTo(BlockId.Air));

            InteractionResult next = service.Update(
                Frame(primary: ButtonState.Held, pointer: pointer), world, player, Dt);
            Assert.That(next.Target.GetValueOrDefault(), Is.EqualTo(new Int3(2, 0, 4)), "the ray reaches the floor");
            Assert.That(next.Edited, Is.False);
            Assert.That(next.BreakProgress, Is.EqualTo(0.1f / 1.5f).Within(1e-6f), "a new target starts from zero");
        }

        [Test]
        public void ReleaseResetsAccumulatedProgress()
        {
            InteractionService service = NewService();
            World world = FloorWorld();
            Set(world, new Int3(2, 1, 4), Stone);
            PlayerState player = Player(8, 1, 8);
            PointerRay pointer = Down(new Int3(2, 1, 4));

            RunHeld(service, world, player, pointer, 3);
            InteractionResult released = service.Update(
                Frame(primary: ButtonState.Up, pointer: pointer), world, player, Dt);

            Assert.That(released.BreakInProgress, Is.False);
            Assert.That(released.BreakProgress, Is.EqualTo(0f));
            Assert.That(service.State, Is.EqualTo(InteractionState.Idle));

            InteractionResult restarted = service.Update(
                Frame(primary: ButtonState.Held, pointer: pointer), world, player, Dt);
            Assert.That(restarted.BreakProgress, Is.EqualTo(0.1f / 1.5f).Within(1e-6f));
        }

        [Test]
        public void TargetChangeResetsAccumulatedProgress()
        {
            InteractionService service = NewService();
            World world = FloorWorld();
            Set(world, new Int3(2, 1, 4), Stone);
            Set(world, new Int3(2, 1, 6), Stone);
            PlayerState player = Player(8, 1, 8);

            RunHeld(service, world, player, Down(new Int3(2, 1, 4)), 3);
            InteractionResult moved = service.Update(
                Frame(primary: ButtonState.Held, pointer: Down(new Int3(2, 1, 6))), world, player, Dt);

            Assert.That(moved.Target.GetValueOrDefault(), Is.EqualTo(new Int3(2, 1, 6)));
            Assert.That(moved.BreakProgress, Is.EqualTo(0.1f / 1.5f).Within(1e-6f));
            Assert.That(world.Get(new Int3(2, 1, 4)), Is.EqualTo(Stone), "the abandoned target is untouched");
        }

        [Test]
        public void BreakTimeUsesRegistryHardnessWithAFiftyMillisecondFloor()
        {
            var registry = BlockRegistry.Parse(HardnessRegistryJson);
            var service = new InteractionService(new DdaRaycaster(), registry);
            World world = FloorWorld();
            Set(world, new Int3(2, 1, 4), new BlockId(1));
            Set(world, new Int3(4, 1, 4), new BlockId(2));
            PlayerState player = Player(8, 1, 8);
            const double floorDt = 0.025;

            InteractionResult instantOne = service.Update(
                Frame(primary: ButtonState.Held, pointer: Down(new Int3(2, 1, 4))), world, player, floorDt);
            Assert.That(instantOne.Edited, Is.False, "0.025 s is below the 0.05 s floor");
            Assert.That(instantOne.BreakProgress, Is.EqualTo(0.5f).Within(1e-6f));

            InteractionResult instantTwo = service.Update(
                Frame(primary: ButtonState.Held, pointer: Down(new Int3(2, 1, 4))), world, player, floorDt);
            Assert.That(instantTwo.Edited, Is.True, "0.05 s completes a hardness-0 block at the 0.05 s floor");

            const double quickDt = 0.05;
            InteractionResult quick = default;
            for (int step = 0; step < 4; step++)
            {
                quick = service.Update(
                    Frame(primary: ButtonState.Held, pointer: Down(new Int3(4, 1, 4))), world, player, quickDt);
            }

            Assert.That(quick.Edited, Is.False, "four 0.05 s frames stay below a 0.25 s hardness");
            quick = service.Update(
                Frame(primary: ButtonState.Held, pointer: Down(new Int3(4, 1, 4))), world, player, quickDt);
            Assert.That(quick.Edited, Is.True, "the fifth 0.05 s frame completes a 0.25 s hardness");
        }

        [Test]
        public void PlacementOnAPressedEdgeWritesTheSelectedBlock()
        {
            InteractionService service = NewService();
            World world = FloorWorld();
            PlayerState player = Player(8, 1, 8);

            InteractionResult result = service.Update(
                Frame(secondary: ButtonState.Pressed, pointer: Down(new Int3(2, 0, 2))), world, player, Dt);

            Assert.That(result.Edited, Is.True);
            Assert.That(result.Target.GetValueOrDefault(), Is.EqualTo(new Int3(2, 0, 2)), "the target is the hit cell");
            Assert.That(world.Get(new Int3(2, 1, 2)), Is.EqualTo(Stone), "placement goes on the hit face");
            Assert.That(service.State, Is.EqualTo(InteractionState.Placing), "an accepted placement is Placing");
        }

        [Test]
        public void PlacementDoesNotRepeatWhileHeldAndRejectsAnOccupiedCell()
        {
            InteractionService service = NewService();
            World world = FloorWorld();
            PlayerState player = Player(8, 1, 8);
            PointerRay pointer = Down(new Int3(2, 0, 2));

            Assert.That(
                service.Update(Frame(secondary: ButtonState.Pressed, pointer: pointer), world, player, Dt).Edited,
                Is.True);
            Assert.That(
                service.Update(Frame(secondary: ButtonState.Held, pointer: pointer), world, player, Dt).Edited,
                Is.False,
                "Held is not a placement edge");

            var insideThePlacedBlock = new PointerRay(new Vec3(2.5, 1.5, 2.5), new Vec3(0, -1, 0));
            Assert.That(
                service.Update(
                    Frame(secondary: ButtonState.Pressed, pointer: insideThePlacedBlock), world, player, Dt).Edited,
                Is.False,
                "the placement cell already holds the expected block");
            Assert.That(service.State, Is.EqualTo(InteractionState.Idle), "a rejected placement stays Idle");
        }

        [Test]
        public void PlacementInsideThePlayerIsRejectedWithoutAnEdit()
        {
            InteractionService service = NewService();
            World world = FloorWorld();
            PlayerState player = Player(8, 1, 8);
            ulong before = Hash(world);
            PointerRay downThroughTheFeet = Down(new Int3(8, 0, 8));

            InteractionResult result = service.Update(
                Frame(secondary: ButtonState.Pressed, pointer: downThroughTheFeet), world, player, Dt);

            Assert.That(result.Target.GetValueOrDefault(), Is.EqualTo(new Int3(8, 0, 8)));
            Assert.That(result.Edited, Is.False, "the placement cell overlaps the player body");
            Assert.That(service.State, Is.EqualTo(InteractionState.Idle));
            Assert.That(Hash(world), Is.EqualTo(before));
        }

        [Test]
        public void PlacementIntoASolidCellIsRejectedWithoutAnEdit()
        {
            InteractionService service = NewService();
            World world = FloorWorld();
            Set(world, new Int3(2, 1, 4), Stone);
            PlayerState player = Player(8, 1, 8);
            ulong before = Hash(world);

            var insideSolid = new PointerRay(new Vec3(2.5, 1.5, 4.5), new Vec3(0, 0, -1));
            InteractionResult result = service.Update(
                Frame(secondary: ButtonState.Pressed, pointer: insideSolid), world, player, Dt);

            Assert.That(result.Target.GetValueOrDefault(), Is.EqualTo(new Int3(2, 1, 4)), "an inside hit targets the cell");
            Assert.That(result.Edited, Is.False, "expected Air does not match the solid target cell");
            Assert.That(service.State, Is.EqualTo(InteractionState.Idle));
            Assert.That(Hash(world), Is.EqualTo(before));
        }

        [Test]
        public void HotbarDeltaCyclesAndWrapsOnThePlayerState()
        {
            InteractionService service = NewService();
            World world = FloorWorld();
            PlayerState player = Player(8, 1, 8);

            service.Update(Frame(hotbarDelta: 1), world, player, Dt);
            Assert.That(player.HotbarIndex, Is.EqualTo(1));

            service.Update(Frame(hotbarDelta: -1), world, player, Dt);
            Assert.That(player.HotbarIndex, Is.EqualTo(0));

            service.Update(Frame(hotbarDelta: -1), world, player, Dt);
            Assert.That(player.HotbarIndex, Is.EqualTo(8), "cycling wraps below slot 0");

            service.Update(Frame(hotbarDelta: 10), world, player, Dt);
            Assert.That(player.HotbarIndex, Is.EqualTo(0), "large deltas wrap");

            PlayerState externallyMoved = Player(8, 1, 8, hotbar: 3);
            service.Update(in Neutral, world, externallyMoved, Dt);
            Assert.That(externallyMoved.HotbarIndex, Is.EqualTo(3), "an external index is preserved");
        }

        [Test]
        public void PlacementUsesTheCycledSelection()
        {
            InteractionService service = NewService();
            World world = FloorWorld();
            PlayerState player = Player(8, 1, 8);

            service.Update(Frame(hotbarDelta: 1), world, player, Dt);
            InteractionResult result = service.Update(
                Frame(secondary: ButtonState.Pressed, pointer: Down(new Int3(2, 0, 2))), world, player, Dt);

            Assert.That(result.Edited, Is.True);
            Assert.That(world.Get(new Int3(2, 1, 2)), Is.EqualTo(Dirt), "slot 1 is Dirt");
        }

        [Test]
        public void HotbarCycleMidBreakKeepsTheTarget()
        {
            InteractionService service = NewService();
            World world = FloorWorld();
            Set(world, new Int3(2, 1, 4), Dirt);
            PlayerState player = Player(8, 1, 8);
            PointerRay pointer = Down(new Int3(2, 1, 4));

            InteractionResult result = default;
            for (int step = 0; step < 5; step++)
            {
                result = service.Update(
                    Frame(primary: ButtonState.Held, pointer: pointer, hotbarDelta: step < 3 ? 1 : 0),
                    world,
                    player,
                    Dt);
            }

            Assert.That(result.Edited, Is.True);
            Assert.That(result.Target.GetValueOrDefault(), Is.EqualTo(new Int3(2, 1, 4)));
            Assert.That(world.Get(new Int3(2, 1, 4)), Is.EqualTo(BlockId.Air));
            Assert.That(player.HotbarIndex, Is.EqualTo(3), "cycling during a break only moves the selection");
        }

        [Test]
        public void ShortTrackingLossPreservesProgress()
        {
            InteractionService service = NewService();
            World world = FloorWorld();
            Set(world, new Int3(2, 1, 4), Dirt);
            PlayerState player = Player(8, 1, 8);
            PointerRay pointer = Down(new Int3(2, 1, 4));

            InteractionResult result = default;
            for (int step = 0; step < 5; step++)
            {
                TrackingQuality quality = step < 2 || step > 3 ? TrackingQuality.Good : TrackingQuality.None;
                result = service.Update(
                    Frame(primary: ButtonState.Held, pointer: pointer, quality: quality), world, player, Dt);
            }

            Assert.That(result.Edited, Is.True, "loss at or below 200 ms does not cancel the break");
            Assert.That(world.Get(new Int3(2, 1, 4)), Is.EqualTo(BlockId.Air));
        }

        [Test]
        public void TrackingLossBeyondTwoHundredMillisecondsCancelsAtTheBoundaryWithoutAnEdit()
        {
            InteractionService service = NewService();
            World world = FloorWorld();
            Set(world, new Int3(2, 1, 4), Dirt);
            PlayerState player = Player(8, 1, 8);
            PointerRay pointer = Down(new Int3(2, 1, 4));
            ulong before = Hash(world);

            // Two good Held frames (0.2 s), then None: 0.1 s, 0.2 s (still
            // within the window) and 0.30000000000000004 s crosses the
            // boundary on step 4 with the break one frame from completion.
            for (int step = 0; step < 2; step++)
            {
                service.Update(Frame(primary: ButtonState.Held, pointer: pointer), world, player, Dt);
            }

            InteractionResult atPointOne = service.Update(
                Frame(primary: ButtonState.Held, pointer: pointer, quality: TrackingQuality.None), world, player, Dt);
            Assert.That(atPointOne.BreakProgress, Is.EqualTo(0.6f).Within(1e-6f));

            InteractionResult atPointTwo = service.Update(
                Frame(primary: ButtonState.Held, pointer: pointer, quality: TrackingQuality.None), world, player, Dt);
            Assert.That(atPointTwo.BreakProgress, Is.EqualTo(0.8f).Within(1e-6f));
            Assert.That(atPointTwo.Edited, Is.False);

            InteractionResult boundary = service.Update(
                Frame(primary: ButtonState.Held, pointer: pointer, quality: TrackingQuality.None), world, player, Dt);
            Assert.That(boundary.Edited, Is.False, "the boundary frame must not apply an edit");
            Assert.That(boundary.BreakInProgress, Is.False);
            Assert.That(boundary.BreakProgress, Is.EqualTo(0f));
            Assert.That(boundary.Target.HasValue, Is.False, "pointer input is ignored once tracking is lost");
            Assert.That(service.State, Is.EqualTo(InteractionState.Idle));
            Assert.That(service.TrackingLost, Is.True);
            Assert.That(Hash(world), Is.EqualTo(before));

            InteractionResult stillLost = service.Update(
                Frame(primary: ButtonState.Held, pointer: pointer, quality: TrackingQuality.None), world, player, Dt);
            Assert.That(stillLost.Edited, Is.False);
            Assert.That(Hash(world), Is.EqualTo(before));

            InteractionResult recovery = service.Update(
                Frame(primary: ButtonState.Held, pointer: pointer), world, player, Dt);
            Assert.That(service.TrackingLost, Is.False);
            Assert.That(recovery.BreakInProgress, Is.True);
            Assert.That(recovery.BreakProgress, Is.EqualTo(0.2f).Within(1e-6f), "recovery re-arms from zero");

            for (int step = 0; step < 4; step++)
            {
                recovery = service.Update(Frame(primary: ButtonState.Held, pointer: pointer), world, player, Dt);
            }

            Assert.That(recovery.Edited, Is.True, "the re-armed break completes after the full hardness");
            Assert.That(world.Get(new Int3(2, 1, 4)), Is.EqualTo(BlockId.Air));
        }

        [Test]
        public void SustainedTrackingLossIgnoresPointerAndButtonsAndRecoveryRearms()
        {
            InteractionService service = NewService();
            World world = FloorWorld();
            PlayerState player = Player(8, 1, 8);
            ulong before = Hash(world);
            const double longDt = 0.3;
            PointerRay pointer = Down(new Int3(2, 0, 2));

            InteractionResult lost = service.Update(
                Frame(
                    primary: ButtonState.Held,
                    secondary: ButtonState.Pressed,
                    pointer: pointer,
                    quality: TrackingQuality.None),
                world,
                player,
                longDt);

            Assert.That(lost.Edited, Is.False);
            Assert.That(lost.Target.HasValue, Is.False);
            Assert.That(Hash(world), Is.EqualTo(before));

            InteractionResult recovered = service.Update(
                Frame(secondary: ButtonState.Pressed, pointer: pointer), world, player, Dt);
            Assert.That(recovered.Edited, Is.True, "recovery accepts edges again");
            Assert.That(world.Get(new Int3(2, 1, 2)), Is.EqualTo(Stone));
        }

        [Test]
        public void RecenterSetsYawToZeroOnTheEdge()
        {
            InteractionService service = NewService();
            World world = FloorWorld();
            PlayerState player = Player(8, 1, 8, yaw: 1.25f);

            InteractionResult first = service.Update(Frame(recenter: true), world, player, Dt);
            Assert.That(player.YawRadians, Is.EqualTo(0f));
            Assert.That(service.Recentered, Is.True);
            Assert.That(first.Edited, Is.False, "a recentre is not a world edit");

            InteractionResult second = service.Update(in Neutral, world, player, Dt);
            Assert.That(player.YawRadians, Is.EqualTo(0f));
            Assert.That(service.Recentered, Is.False);
            Assert.That(second.Edited, Is.False);
        }

        [Test]
        public void SameScriptProducesIdenticalResultsAndWorldHash()
        {
            InputFrame[] script =
            {
                Frame(primary: ButtonState.Held, pointer: Down(new Int3(2, 1, 4))),
                Frame(primary: ButtonState.Held, pointer: Down(new Int3(2, 1, 4)), hotbarDelta: 1),
                Frame(secondary: ButtonState.Pressed, pointer: Down(new Int3(4, 0, 4))),
                Frame(primary: ButtonState.Held, pointer: Down(new Int3(2, 1, 4)), recenter: true),
                Frame(primary: ButtonState.Held, pointer: Down(new Int3(2, 1, 4)), quality: TrackingQuality.None),
            };

            (ulong Hash, float Yaw, int Hotbar, InteractionResult[] Results) first = RunScript(script);
            (ulong Hash, float Yaw, int Hotbar, InteractionResult[] Results) second = RunScript(script);

            Assert.That(second.Hash, Is.EqualTo(first.Hash));
            Assert.That(second.Yaw, Is.EqualTo(first.Yaw));
            Assert.That(second.Hotbar, Is.EqualTo(first.Hotbar));
            Assert.That(second.Results.Length, Is.EqualTo(first.Results.Length));
            for (int i = 0; i < first.Results.Length; i++)
            {
                Assert.That(second.Results[i].Edited, Is.EqualTo(first.Results[i].Edited), $"step {i} Edited");
                Assert.That(
                    second.Results[i].BreakProgress,
                    Is.EqualTo(first.Results[i].BreakProgress),
                    $"step {i} BreakProgress");
                Assert.That(
                    second.Results[i].Target.GetValueOrDefault(),
                    Is.EqualTo(first.Results[i].Target.GetValueOrDefault()),
                    $"step {i} Target");
            }
        }

        [Test]
        public void UpdateAllocatesNothing()
        {
            InteractionService service = NewService();
            World world = FloorWorld();
            Set(world, new Int3(2, 1, 4), Dirt);
            PlayerState player = Player(8, 1, 8);
            InputFrame held = Frame(primary: ButtonState.Held, pointer: Down(new Int3(2, 1, 4)));

            for (int i = 0; i < 1_000; i++)
            {
                service.Update(in held, world, player, Dt);
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100_000; i++)
            {
                service.Update(in held, world, player, Dt);
            }

            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.That(allocated, Is.EqualTo(0L), $"InteractionService.Update allocated {allocated} bytes");
        }

        [Test]
        public void RandomInputSequencesNeverEditDuringSustainedTrackingLossAndReplayThroughAcceptedCommands()
        {
            Arbitrary<int[]> scripts = Arb.Array(Arb.From(Gen.Choose(0, 255)));
            Property property = Prop.ForAll(
                scripts,
                (int[] script) =>
                {
                    RunRandomScriptProperty(script);
                    return Prop.ToProperty(true);
                });

            Check.One(Config.QuickThrowOnFailure, property);
        }

        // ----- helpers -----------------------------------------------------

        private static InteractionService NewService()
        {
            return new InteractionService(new DdaRaycaster(), BlockRegistry.Default);
        }

        private static void UpdateWith(InteractionService service, IWorld world, PlayerState player, double dt)
        {
            service.Update(in Neutral, world, player, dt);
        }

        private static PlayerState Player(double x, double y, double z, float yaw = 0f, float pitch = 0f, int hotbar = 0)
        {
            return new PlayerState
            {
                Position = new Vec3(x, y, z),
                YawRadians = yaw,
                PitchRadians = pitch,
                HotbarIndex = hotbar,
            };
        }

        private static InputFrame Frame(
            ButtonState primary = ButtonState.Up,
            ButtonState secondary = ButtonState.Up,
            PointerRay? pointer = null,
            int hotbarDelta = 0,
            bool recenter = false,
            TrackingQuality quality = TrackingQuality.Good)
        {
            return new InputFrame(default, 0f, recenter, pointer, primary, secondary, hotbarDelta, quality);
        }

        private static PointerRay Down(Int3 cell, double above = 2.0)
        {
            return new PointerRay(
                new Vec3(cell.X + 0.5, cell.Y + above, cell.Z + 0.5),
                new Vec3(0, -1, 0));
        }

        private static World FloorWorld()
        {
            return WorldWith((x, z) => new Int3(x, 0, z), Stone);
        }

        private static World WorldWith(Func<int, int, Int3> cell, BlockId block)
        {
            var world = new World();
            world.LoadChunk(new Chunk(new ChunkCoord(0, 0, 0)));
            for (int z = 0; z < ChunkMath.ChunkSize; z++)
            {
                for (int x = 0; x < ChunkMath.ChunkSize; x++)
                {
                    Set(world, cell(x, z), block);
                }
            }

            return world;
        }

        private static void Set(IWorld world, Int3 cell, BlockId block)
        {
            EditResult result = world.Apply(new EditCommand(cell, BlockId.Air, block, 0));
            Assert.That(result, Is.EqualTo(EditResult.Applied), $"setup write at {cell} was rejected");
        }

        private static void RunHeld(
            InteractionService service, IWorld world, PlayerState player, PointerRay pointer, int steps)
        {
            for (int i = 0; i < steps; i++)
            {
                service.Update(Frame(primary: ButtonState.Held, pointer: pointer), world, player, Dt);
            }
        }

        private static ulong Hash(IWorld world)
        {
            return ScenarioRunner.Hash(world, new Int3(0, 0, 0), new Int3(15, 5, 15));
        }

        private static (ulong Hash, float Yaw, int Hotbar, InteractionResult[] Results) RunScript(InputFrame[] script)
        {
            InteractionService service = NewService();
            World world = FloorWorld();
            Set(world, new Int3(2, 1, 4), Dirt);
            PlayerState player = Player(8, 1, 8);
            var results = new InteractionResult[script.Length];
            for (int i = 0; i < script.Length; i++)
            {
                results[i] = service.Update(in script[i], world, player, Dt);
            }

            return (Hash(world), player.YawRadians, player.HotbarIndex, results);
        }

        private static void RunRandomScriptProperty(int[] script)
        {
            const double dt = 0.1;
            int steps = Math.Min(script.Length, 40);
            World world = PropertyWorld();
            World model = PropertyWorld();
            var service = new InteractionService(new DdaRaycaster(), BlockRegistry.Default);
            var player = new PlayerState { Position = new Vec3(4, 1, 4), HotbarIndex = 0 };
            var min = new Int3(0, 0, 0);
            var max = new Int3(7, 5, 7);
            double loss = 0.0;

            for (int step = 0; step < steps; step++)
            {
                InputFrame frame = PropertyFrame(script[step]);
                bool none = frame.Quality == TrackingQuality.None;
                double lossAfter = none ? loss + dt : 0.0;
                InteractionResult result = service.Update(in frame, world, player, dt);

                if (none && lossAfter > 0.2 && result.Edited)
                {
                    throw new InvalidOperationException(
                        $"an edit was applied after {lossAfter} s of tracking loss at step {step}");
                }

                bool changed = false;
                for (int y = min.Y; y <= max.Y; y++)
                {
                    for (int z = min.Z; z <= max.Z; z++)
                    {
                        for (int x = min.X; x <= max.X; x++)
                        {
                            var cell = new Int3(x, y, z);
                            BlockId before = model.Get(cell);
                            BlockId after = world.Get(cell);
                            if (before == after)
                            {
                                continue;
                            }

                            if (model.Apply(new EditCommand(cell, before, after, 0)) != EditResult.Applied)
                            {
                                throw new InvalidOperationException(
                                    $"the accepted-command model rejected the observed edit at {cell} (step {step})");
                            }

                            changed = true;
                        }
                    }
                }

                if (!result.Edited && changed)
                {
                    throw new InvalidOperationException($"the world changed without Edited at step {step}");
                }

                if (result.Edited && !changed && !IsAirPlacementNoOp(script[step], player.HotbarIndex))
                {
                    throw new InvalidOperationException($"Edited was reported with no world change at step {step}");
                }

                loss = lossAfter;
            }

            if (ScenarioRunner.Hash(world, min, max) != ScenarioRunner.Hash(model, min, max))
            {
                throw new InvalidOperationException("the accepted-command model does not reproduce the final world hash");
            }
        }

        private static InputFrame PropertyFrame(int value)
        {
            ButtonState primary = ((value >> 4) & 1) == 0 ? ButtonState.Held : ButtonState.Up;
            ButtonState secondary = ((value >> 5) & 1) == 0 ? ButtonState.Pressed : ButtonState.Up;
            TrackingQuality quality = ((value >> 6) & 3) switch
            {
                0 => TrackingQuality.None,
                1 => TrackingQuality.Degraded,
                _ => TrackingQuality.Good,
            };

            return new InputFrame(
                default,
                0f,
                false,
                PointerOf(value),
                primary,
                secondary,
                (value % 3) - 1,
                quality);
        }

        private static PointerRay? PointerOf(int value)
        {
            switch ((value / 3) % 8)
            {
                case 0:
                    return Down(new Int3(4, 0, 4));
                case 1:
                    return Down(new Int3(1, 0, 1));
                case 2:
                    return Down(new Int3(6, 0, 6));
                case 3:
                    return new PointerRay(new Vec3(2.5, 1.5, 7.5), new Vec3(0, 0, -1));
                case 4:
                    return new PointerRay(new Vec3(2.5, 1.5, 0.5), new Vec3(0, 0, 1));
                case 5:
                    return null;
                case 6:
                    return new PointerRay(new Vec3(double.NaN, 0, 0), new Vec3(0, -1, 0));
                default:
                    return new PointerRay(new Vec3(4.5, 3.0, 4.5), new Vec3(0, 1, 0));
            }
        }

        private static bool IsAirPlacementNoOp(int value, int hotbarIndex)
        {
            bool secondaryPressed = ((value >> 5) & 1) == 0;
            return secondaryPressed && DefaultHotbarBlock(hotbarIndex) == BlockId.Air;
        }

        private static BlockId DefaultHotbarBlock(int index)
        {
            switch (Hotbar.WrapIndex(index))
            {
                case 0:
                    return new BlockId(1);
                case 1:
                    return new BlockId(2);
                case 2:
                    return new BlockId(3);
                case 3:
                    return new BlockId(4);
                case 4:
                    return new BlockId(5);
                default:
                    return BlockId.Air;
            }
        }

        private static World PropertyWorld()
        {
            var world = new World();
            world.LoadChunk(new Chunk(new ChunkCoord(0, 0, 0)));
            for (int z = 0; z < 8; z++)
            {
                for (int x = 0; x < 8; x++)
                {
                    Set(world, new Int3(x, 0, z), Dirt);
                }
            }

            Set(world, new Int3(2, 1, 3), Stone);
            Set(world, new Int3(2, 2, 3), Stone);
            return world;
        }

        private const string HardnessRegistryJson =
            "[{\"Id\":0,\"Name\":\"Air\",\"Solid\":false,\"Opaque\":false,\"Hardness\":0.0," +
            "\"AtlasIndexTop\":0,\"AtlasIndexFront\":0,\"AtlasIndexSide\":0}," +
            "{\"Id\":1,\"Name\":\"Instant\",\"Solid\":true,\"Opaque\":true,\"Hardness\":0.0," +
            "\"AtlasIndexTop\":1,\"AtlasIndexFront\":1,\"AtlasIndexSide\":1}," +
            "{\"Id\":2,\"Name\":\"Quick\",\"Solid\":true,\"Opaque\":true,\"Hardness\":0.25," +
            "\"AtlasIndexTop\":2,\"AtlasIndexFront\":2,\"AtlasIndexSide\":2}]";
    }

    [TestFixture]
    public sealed class BreakStateTests
    {
        [Test]
        public void AccumulateAddsTimeAndKeepsIdentity()
        {
            var state = new BreakState(new Int3(1, 2, 3), 0.25, 1.5);

            BreakState next = state.Accumulate(0.5);

            Assert.That(next.Cell, Is.EqualTo(new Int3(1, 2, 3)));
            Assert.That(next.AccumulatedSeconds, Is.EqualTo(0.75));
            Assert.That(next.RequiredSeconds, Is.EqualTo(1.5));
            Assert.That(state.AccumulatedSeconds, Is.EqualTo(0.25), "BreakState is a value");
        }

        [Test]
        public void ProgressIsTheFractionClampedToOne()
        {
            Assert.That(new BreakState(Int3.Zero, 0.0, 2.0).Progress, Is.EqualTo(0f));
            Assert.That(new BreakState(Int3.Zero, 1.0, 2.0).Progress, Is.EqualTo(0.5f).Within(1e-6f));
            Assert.That(new BreakState(Int3.Zero, 3.0, 2.0).Progress, Is.EqualTo(1f));
            Assert.That(new BreakState(Int3.Zero, 2.0, 2.0).Progress, Is.EqualTo(1f));
            Assert.That(new BreakState(Int3.Zero, 2.0, 2.0).IsComplete, Is.True);
        }

        [Test]
        public void ConstructorAndAccumulateRejectInvalidTimes()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new BreakState(Int3.Zero, 0.0, 0.0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new BreakState(Int3.Zero, 0.0, -1.0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new BreakState(Int3.Zero, -0.1, 1.0));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new BreakState(Int3.Zero, 0.0, 1.0).Accumulate(double.NaN));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new BreakState(Int3.Zero, 0.0, 1.0).Accumulate(-0.1));
        }
    }

    [TestFixture]
    public sealed class InteractionStateTests
    {
        [Test]
        public void DefaultsToIdleAndNamesTheThreeStates()
        {
            Assert.That((int)InteractionState.Idle, Is.EqualTo(0));
            Assert.That((int)InteractionState.Breaking, Is.EqualTo(1));
            Assert.That((int)InteractionState.Placing, Is.EqualTo(2));
            Assert.That(default(InteractionState), Is.EqualTo(InteractionState.Idle));
        }
    }
}

using System;
using Cubeglass.CoreMath;
using Cubeglass.Voxel;

namespace Cubeglass.Gameplay
{
    /// <summary>
    /// The S4 interaction service (dossier section 5.11; ADR-0008).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Target.</b> The frame's <see cref="InputFrame.Pointer"/> is cast when
    /// it is present and finite; otherwise the player's view ray is derived
    /// from <see cref="PlayerState.YawRadians"/> and
    /// <see cref="PlayerState.PitchRadians"/> (forward is -Z at yaw zero,
    /// positive pitch looks up, pitch is clamped to
    /// <see cref="PlayerController.MaxPitchRadians"/>). The ray origin is the
    /// eye at <see cref="EyeHeight"/> above the feet centre and the cast is
    /// limited to <see cref="Reach"/> metres.
    /// </para>
    /// <para>
    /// <b>Break.</b> While <see cref="InputFrame.Primary"/> is
    /// <see cref="ButtonState.Held"/> and the ray hits a solid cell, <c>dt</c>
    /// accumulates per target cell in a <see cref="BreakState"/>. At
    /// <c>max(0.05, hardness)</c> seconds the service applies
    /// <c>EditCommand(cell, expected: hit block, new: Air)</c> and resets.
    /// Progress is discarded on button release, target change and tracking
    /// loss. The completion frame reports <c>BreakInProgress</c> and progress
    /// one so a HUD can show the final tick.
    /// </para>
    /// <para>
    /// <b>Place.</b> A <see cref="ButtonState.Pressed"/> edge on
    /// <see cref="InputFrame.Secondary"/> applies
    /// <c>EditCommand(cell + normal, expected: Air, new: hotbar.Selected)</c>
    /// when <see cref="VoxelCollision.CanPlace"/> accepts the cell against the
    /// player body. A rejected placement (no solid target, overlap, unloaded
    /// cell or a mismatched expectation) produces no edit and leaves the state
    /// <see cref="InteractionState.Idle"/>.
    /// </para>
    /// <para>
    /// <b>Hotbar and recentre.</b> <see cref="InputFrame.HotbarDelta"/> cycles
    /// the service hotbar in lockstep with <see cref="PlayerState.HotbarIndex"/>
    /// (both wrapped by <see cref="Hotbar.WrapIndex"/>); cycling mid-break
    /// keeps the target. <see cref="InputFrame.RecenterPressed"/> sets
    /// <see cref="PlayerState.YawRadians"/> to zero for that update and is
    /// recorded in <see cref="Recentered"/>.
    /// </para>
    /// <para>
    /// <b>Tracking loss.</b> <see cref="TrackingQuality.None"/> starts a loss
    /// timer. Losses at or below 200 ms change nothing; the first frame past
    /// 200 ms (<see cref="TrackingLossTimeoutSeconds"/>) cancels any break
    /// exactly once, ignores pointer and buttons, and reports an idle result.
    /// A non-<see cref="TrackingQuality.None"/> frame recovers, clearing the
    /// timer and re-arming the state machine. Hotbar cycling and recentre
    /// still apply during loss because they are not world edits.
    /// </para>
    /// <para>
    /// <b>Purity.</b> <see cref="IWorld.Apply"/> is the only world side effect.
    /// The update is deterministic for identical inputs and allocates nothing;
    /// null players or worlds throw <see cref="ArgumentNullException"/> and a
    /// negative, NaN or infinite <c>dt</c> throws
    /// <see cref="ArgumentOutOfRangeException"/>. The edit tick is zero because
    /// S4 services have no clock.
    /// </para>
    /// </remarks>
    public sealed class InteractionService : IInteractionService
    {
        /// <summary>Maximum interaction distance in metres; inclusive at the boundary.</summary>
        public const float Reach = 5.0f;

        /// <summary>Tracking loss longer than this cancels break and place, in seconds.</summary>
        public const double TrackingLossTimeoutSeconds = 0.2;

        /// <summary>Minimum break time in seconds, whatever a block's hardness says.</summary>
        public const double MinimumBreakSeconds = 0.05;

        /// <summary>View-ray origin height above the feet centre, in metres.</summary>
        public const double EyeHeight = PlayerState.BodyHeight;

        private readonly DdaRaycaster _raycaster;
        private readonly IBlockRegistry _blocks;
        private readonly Hotbar _hotbar = new Hotbar();

        private BreakState? _break;
        private double _trackingLossSeconds;
        private bool _trackingLost;

        /// <summary>Creates a service over an explicit raycaster and block registry.</summary>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="raycaster"/> or <paramref name="blocks"/> is null.
        /// </exception>
        public InteractionService(DdaRaycaster raycaster, IBlockRegistry blocks)
        {
            _raycaster = raycaster ?? throw new ArgumentNullException(nameof(raycaster));
            _blocks = blocks ?? throw new ArgumentNullException(nameof(blocks));
        }

        /// <summary>Creates a service with the shipped raycaster and block registry.</summary>
        public InteractionService()
            : this(new DdaRaycaster(), BlockRegistry.Default)
        {
        }

        /// <summary>The state from the most recent update.</summary>
        public InteractionState State { get; private set; }

        /// <summary>Whether the most recent frame was past the 200 ms tracking-loss window.</summary>
        public bool TrackingLost => _trackingLost;

        /// <summary>Whether the most recent update applied a recentre request.</summary>
        public bool Recentered { get; private set; }

        /// <inheritdoc />
        public InteractionResult Update(in InputFrame input, IWorld world, PlayerState player, double dt)
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

            State = InteractionState.Idle;
            Recentered = false;

            if (input.RecenterPressed)
            {
                player.YawRadians = 0f;
                Recentered = true;
            }

            SyncHotbar(player);
            _hotbar.Cycle(Hotbar.WrapIndex(input.HotbarDelta));
            player.HotbarIndex = _hotbar.SelectedIndex;

            _trackingLossSeconds = input.Quality == TrackingQuality.None ? _trackingLossSeconds + dt : 0.0;
            if (_trackingLossSeconds > TrackingLossTimeoutSeconds)
            {
                if (!_trackingLost)
                {
                    _trackingLost = true;
                    _break = null;
                }

                return default;
            }

            _trackingLost = false;

            RayHit? hit = CastTarget(in input, world, player);
            Int3? target = hit.HasValue ? hit.Value.Cell : (Int3?)null;
            bool breaking = false;
            float progress = 0f;
            bool edited = false;

            if (hit.HasValue)
            {
                if (input.Primary == ButtonState.Held)
                {
                    edited |= AccumulateBreak(world, target!.Value, hit.Value.Block, dt, out breaking, out progress);
                }
                else
                {
                    _break = null;
                }
            }
            else
            {
                _break = null;
            }

            bool placed = false;
            if (hit.HasValue && input.Secondary == ButtonState.Pressed)
            {
                Int3 placeCell = target!.Value + hit.Value.Normal;
                if (VoxelCollision.CanPlace(world, placeCell, player.Body)
                    && world.Apply(new EditCommand(placeCell, BlockId.Air, _hotbar.Selected, 0)) == EditResult.Applied)
                {
                    edited = true;
                    placed = true;
                }
            }

            State = breaking
                ? InteractionState.Breaking
                : placed ? InteractionState.Placing : InteractionState.Idle;
            return new InteractionResult(breaking, progress, edited, target);
        }

        private bool AccumulateBreak(
            IWorld world,
            Int3 target,
            BlockId block,
            double dt,
            out bool breaking,
            out float progress)
        {
            double required = Math.Max(MinimumBreakSeconds, _blocks.Get(block).Hardness);
            if (!_break.HasValue || _break.Value.Cell != target || _break.Value.RequiredSeconds != required)
            {
                _break = new BreakState(target, 0.0, required);
            }

            BreakState current = _break.Value.Accumulate(dt);
            if (!current.IsComplete)
            {
                _break = current;
                breaking = true;
                progress = current.Progress;
                return false;
            }

            _break = null;
            breaking = true;
            progress = 1f;
            return world.Apply(new EditCommand(target, block, BlockId.Air, 0)) == EditResult.Applied;
        }

        private RayHit? CastTarget(in InputFrame input, IWorld world, PlayerState player)
        {
            if (input.Pointer.HasValue && input.Pointer.Value.TryToRay(out Ray pointerRay))
            {
                return _raycaster.Cast(world, pointerRay, Reach);
            }

            double pitch = player.PitchRadians;
            if (pitch > PlayerController.MaxPitchRadians)
            {
                pitch = PlayerController.MaxPitchRadians;
            }
            else if (pitch < -PlayerController.MaxPitchRadians)
            {
                pitch = -PlayerController.MaxPitchRadians;
            }

            double yaw = player.YawRadians;
            double cosPitch = Math.Cos(pitch);
            var direction = new Vec3(
                -Math.Sin(yaw) * cosPitch,
                Math.Sin(pitch),
                -Math.Cos(yaw) * cosPitch);
            var eye = new Vec3(player.Position.X, player.Position.Y + EyeHeight, player.Position.Z);
            return _raycaster.Cast(world, new Ray(eye, direction), Reach);
        }

        private void SyncHotbar(PlayerState player)
        {
            int wrapped = Hotbar.WrapIndex(player.HotbarIndex);
            int delta = wrapped - _hotbar.SelectedIndex;
            if (delta != 0)
            {
                _hotbar.Cycle(delta);
            }
        }
    }
}

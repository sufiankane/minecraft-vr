using Cubeglass.Voxel;

namespace Cubeglass.Gameplay
{
    /// <summary>
    /// Turns input frames into world edits (dossier section 5.11 verbatim).
    /// </summary>
    /// <remarks>
    /// <see cref="IWorld.Apply"/> is the only world side effect and the
    /// implementation is a pure function of <c>(input, world, player, dt)</c>:
    /// no clocks, no allocation on the hot path and no engine types. Tracking
    /// loss longer than 200 ms cancels in-progress break and place
    /// (section 5.11).
    /// </remarks>
    public interface IInteractionService
    {
        /// <summary>
        /// Advances interaction by <paramref name="dt"/> seconds.
        /// </summary>
        /// <param name="input">The frame to consume.</param>
        /// <param name="world">The world to raycast and edit.</param>
        /// <param name="player">The mutable player state; hotbar, recentre and yaw changes persist (R27).</param>
        /// <param name="dt">The fixed timestep in seconds.</param>
        /// <returns>The observable outcome of this update.</returns>
        InteractionResult Update(in InputFrame input, IWorld world, PlayerState player, double dt);
    }
}

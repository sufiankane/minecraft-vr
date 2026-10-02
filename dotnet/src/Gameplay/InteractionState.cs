namespace Cubeglass.Gameplay
{
    /// <summary>
    /// The explicit interaction state machine (ADR-0008; S4 work item 3).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="InteractionService"/> is always in exactly one of these
    /// states. The transition table is:
    /// </para>
    /// <list type="table">
    /// <item>
    ///   <term>Idle -> Breaking</term>
    ///   <description>
    ///   <see cref="InputFrame.Primary"/> is <see cref="ButtonState.Held"/> and
    ///   the ray hits a solid cell. The accumulated time starts at zero for
    ///   that target. A <see cref="ButtonState.Pressed"/> frame is not yet a
    ///   hold and does not start a break.
    ///   </description>
    /// </item>
    /// <item>
    ///   <term>Breaking -> Breaking</term>
    ///   <description>
    ///   <see cref="InputFrame.Primary"/> is still <see cref="ButtonState.Held"/>
    ///   and the target cell is the same; <c>dt</c> accumulates. A changed
    ///   target cell keeps the state but restarts the accumulation at zero.
    ///   </description>
    /// </item>
    /// <item>
    ///   <term>Breaking -> Idle</term>
    ///   <description>
    ///   <see cref="InputFrame.Primary"/> is not
    ///   <see cref="ButtonState.Held"/>, or the ray no longer hits a solid
    ///   cell, or tracking loss exceeds 200 ms, or the accumulated time
    ///   reached <c>max(0.05, hardness)</c> and the edit was applied.
    ///   Progress is discarded.
    ///   </description>
    /// </item>
    /// <item>
    ///   <term>Idle -> Placing</term>
    ///   <description>
    ///   <see cref="InputFrame.Secondary"/> is
    ///   <see cref="ButtonState.Pressed"/> (an edge) and the placement command
    ///   was accepted. A rejected placement (no solid target, no
    ///   <see cref="Cubeglass.Voxel.VoxelCollision.CanPlace"/> or a mismatched
    ///   expectation) stays <see cref="Idle"/>.
    ///   </description>
    /// </item>
    /// <item>
    ///   <term>Placing -> Idle</term>
    ///   <description>
    ///   Placement is one-shot: the state is recomputed at the start of the
    ///   next update, and an update with no accepted placement reports
    ///   <see cref="Idle"/>.
    ///   </description>
    /// </item>
    /// </list>
    /// <para>
    /// Break and place are evaluated independently: a frame that holds Primary
    /// and presses Secondary may accumulate a break and place a block in the
    /// same update. In that case <see cref="Breaking"/> takes precedence in the
    /// reported state.
    /// </para>
    /// </remarks>
    public enum InteractionState
    {
        /// <summary>Nothing is accumulating and no placement was accepted.</summary>
        Idle = 0,

        /// <summary>A hold-to-break is accumulating on a target cell.</summary>
        Breaking = 1,

        /// <summary>This update accepted a <see cref="ButtonState.Pressed"/> placement edge.</summary>
        Placing = 2,
    }
}

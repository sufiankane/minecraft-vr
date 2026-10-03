# 0011. Unity input mapping and rig composition

- Status: accepted
- Date: 2026-10-03
- Deciders: Sufyan Khan (owner)
- Consulted: Stage S7 build agents
- Informed: all later-stage builders and reviewers

## Context and problem statement

S7 Tasks 2 and 3 delivered the Unity adapters with three spaces that disagreed:
chunk views uploaded the voxel lattice unchanged, `LateLatchPose` converted the
head pose through ADR-0004's Z mirror, and `GazeTargeting` converted the camera
ray back to the internal frame. The scene only lined up because the fixtures
placed the camera at the flips of the internal eye position. On the same rig,
`SnapTurn` rotated the rig transform but `LateLatchPose` rewrote that transform
from the pose each pre-cull, so a snap survived only until the next sample;
`GameplayBridge.SyncPlayerYawFromRig` read the yaw back from the rig, so the
head pose drove movement. The Unity providers emitted `TurnSnap` in degrees
per frame (a stick rate multiplied by `dt`, or a discrete snap increment), which
is not the S4 degrees-per-second contract, and the bridge consumed the snap by
zeroing `TurnSnap` before `PlayerController.Step`.

Ruling R52 (S7 Task 4a) fixes the direction once: **the Unity scene is built in
Unity space**; everything crossing between internal space and Unity is converted
exactly once at the adapter boundary, reusing the ADR-0004 `UnityConvert`
formulas. This ADR records R52's consequences for rendering, the player rig,
input mapping and recentre.

## Decision drivers

- One conversion source (ADR-0004): no adapter re-derives the flip, and the
  mesh path may not copy lattice coordinates as if they were Unity coordinates.
- `InputFrame` is frozen (ADR-0008): `TurnSnap` is degrees per second, and snap
  turn has no field in the frame.
- The head pose is head-relative input for rendering; S4 has no head input on
  the movement path.
- A comfort turn must survive the late latch, and recentre must return the view
  to the player's forward.
- Rendered geometry and the gaze/shot ray must agree, so what the player sees
  in front is what the raycast hits in front.

## Considered options

- **Unity-space scene with one conversion at the boundary (R52).** Chosen. The
  alternative from Task 2/3 — render the lattice unchanged and convert only the
  rig — leaves two mirrored spaces and couples the result to fixture placement.
- **Convert every mesh vertex and keep the ADR-0007 index order.** Rejected: a
  mirror reverses orientation, so unchanged indices render the world
  inside-out; the winding flip is part of the same conversion.
- **Keep the rig as the yaw source and let the head drive movement.** Rejected:
  a 3DoF head-relative pose is not a body heading, and it made snap turn
  unrepresentable in `PlayerState`.
- **Apply snap turn as a rig-local rotation above the late latch.** Rejected:
  it splits the heading between two transforms and movement would not follow
  the turn. The heading is `PlayerState.YawRadians`; the root composes from it.
- **Emit per-frame degrees and let the bridge special-case them.** Rejected:
  `TurnSnap` must stay the S4 rate so a pure `ScriptedInputProvider` and the
  Unity providers produce the same frame.

## Decision outcome

Chosen options: Unity-space rendering with the mirror plus a winding flip at the
mesh adapter, a `PlayerRoot` body transform that the rig follows, S4
degrees-per-second `TurnSnap` plus a separate snap edge, and a recentre that
zeroes the heading and clears the local head offset.

### R52: Unity-space rendering and the mesh mirror

- Chunk mesh positions and normals are converted once each through
  `Cubeglass.CoreMath.UnityConvert` (`z -> -z`), and each triangle's winding is
  flipped (`i0, i2, i1`) because the mirror inverts orientation. With the flip,
  `cross(v1 - v0, v2 - v0)` in Unity space again equals the converted outward
  normal, so Unity's clockwise-when-visible rule puts the front face on the
  outside. The winding flip and the conversion must always change together.
- The chunk view GameObject is placed at the converted chunk origin
  (`UnityConvert` of `chunk * 16`), so the mirrored local geometry and the world
  placement agree.
- `StreamingRuntime` samples a Unity-space transform and converts the position
  back to the internal frame exactly once before the scheduler sees it; the
  mirror is its own inverse, so the same formula is used in both directions.
- EditMode tests pin a single block's six faces by geometric cross products in
  Unity space, and a PlayMode test pins that a gaze ray from a camera in front
  of a rendered block hits the same cell in the internal world (the
  mirrored-world regression test).

### Rig composition model

```
PlayerRoot                       world position = UnityConvert(PlayerState.Position)
                                 yaw            = UnityConvert(PlayerState.YawRadians)
  └── Head (StereoRig + LateLatchPose)
        local position = (0, 0.9, 0)                (PlayerRoot.EyeHeightMeters)
        local rotation = head-relative recentred yaw/pitch/roll, converted once
          ├── LeftEye  (-IPD/2, 0, 0)
          └── RightEye (+IPD/2, 0, 0)
```

- `PlayerRoot` is the body level. It is written by `GameplayBridge` from
  `PlayerState` every tick; nothing reads the rig back into `PlayerState`
  (`SyncPlayerYawFromRig` is deleted, not inverted in place).
- `LateLatchPose` writes only the head's local rotation, never a position. A
  sample's absolute position is ignored; the eye height is the fixed body
  offset and the eye cameras add only the IPD offsets.
- The applied rotation is the sample rotation relative to a recentre baseline
  (initially identity), so the head contributes yaw/pitch/roll as a head offset
  from the player's forward.

### Input mapping table

Both backends map to the same `InputFrame`; device-right look is a negative
`TurnSnap` because positive internal yaw turns toward -X, which is Unity left
(ADR-0004).

| Action | Gamepad | Keyboard/mouse | Output |
| --- | --- | --- | --- |
| Move | left stick | WASD | `InputFrame.Move`, radial deadzone then clamped to the unit disc |
| Continuous turn | right stick X | mouse X delta | `InputFrame.TurnSnap` in **degrees per second** (`-look.X * rate`; mouse delta / frame dt) |
| Break | X | left mouse | `Primary` edge machine (hold) |
| Place | A | right mouse | `Secondary` edge machine (edge) |
| Recentre | Y | R | `RecenterPressed` one-frame edge |
| Hotbar | RB/LB | E/Q | `HotbarDelta` +/-1 on the press edge |
| Snap turn | B | F | `ISnapInputSource.ConsumeSnapPressed()` one-shot edge, **not** an `InputFrame` field |

### Turn semantics

- `TurnSnap` is the S4 rate; `PlayerController.Step` integrates it
  (`YawRadians += TurnSnap * pi/180 * dt`). The bridge never rewrites it, so
  continuous turn works with snap disabled and a pure `ScriptedInputProvider`
  at 45 deg/s for one second produces a 45 degree heading change.
- The snap edge is provider-additive (`ISnapInputSource`) and is consumed by
  the bridge before `Step`. `SnapTurn.ApplyIncrement(1)` returns the Unity-yaw
  degrees (default +45, turn right); the bridge subtracts them from
  `PlayerState.YawRadians` and records `LastSnapDegrees`. Applying it to the
  heading (rather than a rig transform) is what makes the late latch unable to
  overwrite it and makes movement follow the turn.

### Recentre semantics

On `InputFrame.RecenterPressed`:

1. `InteractionService` sets `PlayerState.YawRadians = 0` (FR-08, S4).
2. `GameplayBridge` calls `LateLatchPose.Recentre()`, which clears the head
   offset: the rig returns to the player's forward immediately and the next
   sample is measured from the recentred baseline.
3. When the pose provider implements `IRecenterablePoseProvider`, the source is
   recentred as well (the scripted provider zeroes its base pose; the native
   bridge's `reset_origin_carina` command path is wired when a command word is
   pinned). Otherwise the current sample rotation becomes the local baseline
   (local offset reset), which clears the offset without source support.

The net effect is heading zero plus head offset zero, so the view faces the
player's forward (internal -Z, Unity +Z).

### Update order

`GameplayBridge.Update` (input → snap → `PlayerController.Step` → interaction →
recentre → `PlayerRoot` pose), then `WorldUi.LateUpdate` re-anchors the hotbar
on the applied body pose (body-relative, not head-locked), then `OnGUI` draws.
`LateLatchPose` applies the head rotation at `Camera.onPreCull`, after both.

## Consequences

- Good: one conversion each way; a mirrored world cannot silently pass because
  the cross-product and gaze tests share one camera placement.
- Good: the heading has a single owner (`PlayerState`), so snap, continuous
  turn and recentre compose and movement follows the body.
- Good: `TurnSnap` keeps the S4 unit, so scripted and device providers are
  interchangeable.
- Bad: `ChunkViewManager` dirties the Chebyshev-1 neighbourhood on
  `World.ChunkChanged` because the event carries the chunk, not the edit cell;
  this is a superset of `ChunkEditPropagation.GetAffectedChunks` and remeshes
  unchanged neighbours.
- Bad: recentre clears yaw/pitch/roll of the head offset in the S7 slice; the
  native source's yaw-only `reset_origin_carina` behaviour is preserved as an
  option when that path is pinned.

## Confirmation

- EditMode: `ChunkViewEditModeTests` six-face cross products after the mirror;
  `LateLatchPoseTests` head-relative rotation, ignored position and recentre
  baseline; `PlayerRootTests` conversion and heading; `InputMappingTests`
  degrees-per-second turn and separate snap edge.
- PlayMode: gaze ray hits the rendered surface in front; dirty remesh after
  break/place; 45 deg/s scripted turn yields 45 degrees; snap edge survives
  pose ticks; recentre clears heading and head offset.
- `LEDGER`/review: no adapter contains a second Z-flip; the mesh path's mirror
  and winding flip appear in the same function.

## Links

- Dossier sections: 2.1 (F-04, U-09), 3.6, 5.11, 6 (S7).
- Related ADRs: [ADR-0004](0004-coordinate-unit-time-conventions.md),
  [ADR-0007](0007-meshing-budget-winding-and-atlas.md),
  [ADR-0008](0008-gameplay-contracts-and-tuning.md),
  [ADR-0009](0009-glasses-adapter-pose-semantics.md),
  [ADR-0010](0010-unity-version-pipeline-and-bridge-layout.md).

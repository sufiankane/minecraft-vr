# 0008. Gameplay contracts, support types, player physics and tuning

- Status: accepted
- Date: 2026-10-02
- Deciders: Sufyan Khan (owner)
- Consulted: Stage S4 build agents
- Informed: all later-stage builders and reviewers

## Context and problem statement

Dossier section 5.11 freezes the input and gameplay contracts for
`Cubeglass.Gameplay` verbatim:

```csharp
public interface IInputProvider {                   // gamepad, keyboard/mouse, hands
    InputFrame Sample(double timeSeconds);          // pure read of latest state
}
public readonly struct InputFrame {
    public Vector2f Move;  public float TurnSnap;  public bool RecenterPressed;
    public PointerRay? Pointer;                     // world-space ray, null if none
    public ButtonState Primary;                     // break
    public ButtonState Secondary;                   // place
    public int HotbarDelta;                         // -1, 0, +1
    public TrackingQuality Quality; }

public interface IGestureRecognizer {               // pure state machine
    GestureOutput Update(in HandsInput hands, double dt);   // pinch, fist, palette flick, with hysteresis
}
public interface IInteractionService {              // turns input frames into world edits
    InteractionResult Update(in InputFrame input, IWorld world, PlayerState player, double dt);
}
```

The declaration names seven types it never defines (`PointerRay`,
`ButtonState`, `TrackingQuality`, `HandsInput`, `GestureOutput`,
`InteractionResult`, `PlayerState`) and leaves every tuning constant and
physical convention open: walk speed, gravity, terminal velocity, the
yaw/pitch reference frame, the player AABB, the collision axis order, the
break-time formula, gesture hysteresis edges, hotbar contents and the
tracking-loss timeout. Left unrecorded, the four S4 work items would guess
differently and the S5 device adapters could not bridge to the module. This ADR
pins them. `Cubeglass.Gameplay` is netstandard2.1, C# 10, references exactly
`Cubeglass.CoreMath` and `Cubeglass.Voxel` (`contracts/layers.json`), holds no
engine, file-system or thread types, and makes `IWorld.Apply` its only world
side effect. The section 5.11 declaration text is unchanged, so no contract
version bump is required; any later change to these conventions needs a new
ADR.

## Decision drivers

- Section 5.11 is frozen; the missing pieces must be additive and recorded,
  not edits to the quoted contract.
- Determinism: the same start state, inputs and `dt` series must produce the
  same state, and splitting a step must not change the result beyond 1e-3
  (fixed timestep supplied by the caller, no clocks inside services).
- `PlayerController.Step` and (in Task 2) `IInteractionService.Update` must
  allocate nothing on their hot paths.
- Every constant must be testable without hardware or an engine.
- Tracking loss longer than 200 ms cancels in-progress break and place
  (section 5.11).

## Considered options

- **One recorded decision per item, with the support types in
  `Cubeglass.Gameplay`** (chosen): the module that owns the contracts owns the
  additive shapes, and a single ADR gives every later consumer one reference.
- **Define the support types in `Cubeglass.Voxel` or `Cubeglass.CoreMath`.**
  Rejected: they are input/gameplay concerns; CoreMath is C# 9 and Voxel knows
  nothing about players or pointers.
- **`PlayerState` as a mutable `struct`.** Rejected (R27): section 5.11 passes
  `PlayerState` by value into `IInteractionService.Update`, so a struct would
  silently discard hotbar and recentre updates; a sealed class gives the
  documented reference semantics.
- **Swept or step-up collision.** Rejected: S4 has no step-up requirement and
  a per-axis try/revert is exact and allocation-free; a step larger than the
  obstacle is a caller error, documented below.
- **Acceleration-based movement.** Rejected: S4 is keyboard-only and pins the
  direct velocity model; inertia can arrive later behind a new ADR.
- **Auto-repeat for break/place.** Rejected: hold-to-break already produces
  one edit per completion and place is edge-triggered; repeat rates are
  deferred until a device needs them.

## Decision outcome

Chosen option: "one recorded decision per item, with the support types in
`Cubeglass.Gameplay`", because it keeps section 5.11 untouched while giving
every shape, constant and physical rule a single reviewable home and pure
tests.

### Support types (R26)

Additive to 5.11; `Cubeglass.Gameplay` sets `LangVersion 10.0` and carries an
internal `IsExternalInit` polyfill so `PointerRay` can be a
`readonly record struct` (the plan's Files list omits the polyfill; it mirrors
`Cubeglass.Voxel`'s).

- `enum ButtonState { Up, Pressed, Held, Released }`: `Up`/`Held` are steady
  states, `Pressed` is the frame a button goes down, `Released` the frame it
  comes up. Zero is `Up`, so default frames carry no edges.
- `enum TrackingQuality { None, Degraded, Good }`: zero is `None` (no
  tracking information), matching the neutral default frame.
- `readonly record struct PointerRay(Vec3 Origin, Vec3 Direction)` with
  `bool TryToRay(out Ray ray)`. The conversion fails (and leaves the default
  ray) when the origin is non-finite or when `Direction` is zero, non-finite,
  or degenerates during normalisation (underflow to a zero length, overflow to
  a zero normalised vector); otherwise the emitted `Ray` has a unit direction
  and the unchanged origin.
- `readonly struct InputFrame` with the eight 5.11 fields in 5.11's order
  (`Move`, `TurnSnap`, `RecenterPressed`, `Pointer`, `Primary`, `Secondary`,
  `HotbarDelta`, `Quality`) plus an all-argument constructor and
  `static InputFrame Neutral => default`. A default frame is the neutral
  frame: zero move, zero turn, no buttons, null pointer, zero hotbar delta and
  `Quality == None`. `HotbarDelta` is a full `int` field as frozen; providers
  must produce -1, 0 or +1.
- `readonly struct HandFrame` with `Wrist`, `ThumbTip`, `IndexTip`,
  `MiddleTip`, `RingTip`, `LittleTip` (`Vector3f` joints) and
  `readonly struct HandsInput(bool Tracked, HandFrame? Left, HandFrame? Right)`
  (the minimal S4 shape; S11 extends it). Defaults are untracked with null
  hands.
- `readonly struct GestureOutput` with `Pinching`, `Fist`, `PaletteFlick`,
  `PinchStrength`; default is all false / 0, `PinchStrength` in [0, 1].
- `readonly struct InteractionResult` with `BreakInProgress`,
  `BreakProgress` in [0, 1], `Edited` and `Int3? Target`; default is idle
  (false / 0 / false / null).
- `sealed class PlayerState` (R27) with mutable `Vec3 Position` (feet
  centre), `Vec3 Velocity`, `float YawRadians`, `float PitchRadians`,
  `bool OnGround`, `int HotbarIndex`, and derived `Aabb Body`.

### PlayerState reference semantics and the player body (R27)

`PlayerState` is a sealed class with settable properties so the by-value
parameter in 5.11's `IInteractionService.Update` still persists hotbar and
recentre changes. `Body` is derived on every access: with
`p = Position` the box is
`Min = p + (-0.3, 0, -0.3)`, `Max = p + (0.3, 0.9, 0.3)` — a 0.6 x 0.9 x 0.6
box whose feet centre is `Position` and whose top is `Position.Y + 0.9`.
`HalfWidth = 0.3`, `BodyHeight = 0.9` and `HalfDepth = 0.3` are public
constants.

### Tuning constants

| Item | Value | Source / consumer |
| --- | --- | --- |
| Walk speed | 4.5 m/s | `PlayerController.WalkSpeed` |
| Gravity | -25 m/s^2 | `PlayerController.Gravity` |
| Terminal fall speed | -40 m/s | `PlayerController.TerminalFallSpeed` |
| Turn snap | degrees/second applied to yaw; keyboard-only in S4 (pointer/mouse later) | `InputFrame.TurnSnap` |
| Pitch clamp | +/-89 degrees (1.5533430342749532 rad) | `PlayerController.MaxPitchRadians` |
| Reach | 5.0 m | Task 2 target selection |
| View-ray eye height | 0.9 m above the feet centre (`PlayerState.BodyHeight`) | Task 2 fallback targeting |
| Edit tick | 0 (clockless S4; S7 supplies ticks) | Task 2 `EditCommand.Tick` |
| Break time | `seconds = max(0.05, hardness)` from `BlockDefinition.Hardness` (stone 1.5 s, wood 2.0 s) | Task 2 (R30) |
| Break repeat | none in S4: hold accumulates to one edit at completion; release, target change or a >200 ms tracking loss resets progress | Task 2 |
| Place repeat | none in S4: `Secondary == Pressed` edge only | Task 2 |
| Hotbar | 9 slots; default Stone, Dirt, Grass, Sand, Wood in slots 0..4 and air in 5..8; cycling wraps at both ends | `Hotbar` |
| Pinch hysteresis band | latched band 0.5/0.7: engage `ratio <= 0.5` at the closed edge, release `ratio >= 0.7` at the open edge (R32) | Task 3 |
| Fist hysteresis band | latched band 0.4/0.6: engage `ratio <= 0.4` at the closed edge, release `ratio >= 0.6` at the open edge (R32) | Task 3 |
| Palette flick window | 250 ms | Task 3 |
| Tracking-loss timeout | 200 ms; longer cancels in-progress break/place | section 5.11, Tasks 2/3 |

### Movement model

`Move` is `(strafe, forward)` in [-1, 1]^2. With yaw `y` (right-handed, Y up,
zero forward = -Z; positive yaw turns counter-clockwise seen from above, i.e.
toward -X):

- forward `F = (-sin y, 0, -cos y)`, right `R = (cos y, 0, -sin y)`;
- desired direction `D = strafe * R + forward * F`; with `m = |Move|` the
  horizontal velocity is `D * WalkSpeed` when `m <= 1` and
  `D / m * WalkSpeed` when `m > 1`, i.e. `|velocity| = min(m, 1) * 4.5` m/s;
- there is no acceleration or damping in S4: `Velocity.X`/`Velocity.Z` are
  replaced from the frame every step;
- `TurnSnap` is degrees/second: `YawRadians += TurnSnap * (pi/180) * dt`;
  pitch has no input source in S4 and is clamped to +/-89 degrees every step.

Gravity integrates in two parts so that splitting a step is frame-invariant:
with `v0 = Velocity.Y`, `v1 = max(v0 - 25 * dt, -40)` and
`dy = (v0 + v1) / 2 * dt` (trapezoidal integration, exact for constant
acceleration). Horizontal displacement is `v * dt`.

`Step` rejects a null player or world with `ArgumentNullException` and a
negative, NaN or infinite `dt` with `ArgumentOutOfRangeException`. `dt == 0`
is a physics no-op that still clamps pitch; `OnGround` is left unchanged.

### Collision resolution

The player body is moved and resolved one axis at a time in **X, then Z, then
Y** order. For each axis the full step displacement is attempted; if the
resulting `Body` overlaps any solid cell (`VoxelCollision.Overlaps`; unloaded
cells read as air), the axis coordinate is restored to its exact pre-move
double and that axis's velocity component is zeroed. Blocked X or Z produces
sliding along the untouched axes; blocked Y zeroes `Velocity.Y`, and
`OnGround` becomes true only when the reverted move was downward (an upward
move stopped by a ceiling leaves `OnGround` false). A Y move that is not
blocked clears `OnGround`. Landing therefore zeroes the fall velocity and
leaves the feet at the last non-overlapping coordinate; there is no contact
snap, so the resting height may be up to one step above the surface. S4 does
not sweep: a caller must keep per-step displacement below one cell (4.5 m/s
at 60 Hz is 0.075 m).

### Scripted input and scenario assets (R28)

`ScriptedInputProvider` copies the caller's timeline at construction, requires
strictly increasing finite frame times (throwing `ArgumentException` on a
duplicate, decreasing or non-finite time and `ArgumentNullException` on a null
list), and is therefore pure over later source mutations. `Sample(t)` returns
the frame of the latest entry with `time <= t`; a time before the first entry
returns the first frame, and an empty timeline returns the neutral frame.
Scenario JSON scripts remain test assets parsed by `Gameplay.Tests` (R28);
`Cubeglass.Gameplay` stays free of `System.IO` and JSON dependencies.

### Recentre (R29)

In S4 the interaction service handles `InputFrame.RecenterPressed` by setting
`PlayerState.YawRadians = 0` (dossier FR-08 heading reset) and reporting it in
its state; wiring the VITURE `reset_origin_carina` path is S5. `PlayerController`
does not consume the recentre flag.

### Interaction targeting and edits (Task 2)

The interaction service targets the cell hit by the frame's `Pointer` when it
is present and finite; otherwise it casts the view ray. The view-ray origin is
the eye at `PlayerState.Position + (0, 0.9, 0)` — the recorded `BodyHeight`
above the feet centre, because the dossier defines no separate eye height — and
its unit direction follows ADR-0004's right-handed, Y-up axes with the same
forward convention as `PlayerController`:

    direction = (-sin(yaw) * cos(pitch), sin(pitch), -cos(yaw) * cos(pitch))

so yaw 0 at pitch 0 faces -Z, positive yaw turns toward -X, positive pitch
looks up, and pitch is clamped to +/-89 degrees
(`PlayerController.MaxPitchRadians`) before the ray is built. Both the pointer
and the view ray are limited to the 5.0 m reach.

Edits are clockless in S4: the service issues
`EditCommand(cell, expected, new, tick: 0)` for breaks and placements. Logical
ticks arrive with a later journaling stage (S7); until then `Tick` is always 0
and is not interpreted by `IWorld.Apply`.

### Gesture recogniser (Task 3)

`GestureRecognizer` is a pure, allocation-free hysteresis state machine over
`HandsInput`. It selects one hand per frame: `Left` when present, otherwise
`Right`, and the latched state carries across a hand switch. A frame is a
tracking-loss frame when `Tracked == false` or when both hands are absent.
Loss at or below 200 ms holds the last outputs (`Pinching`, `Fist`,
`PinchStrength`) and freezes every timer; the first frame strictly past
200 ms clears all outputs and timers once, and recovery evaluates from the
cleared state. The additive `GestureRecognizer.TrackingLost` property
(mirroring `InteractionService.TrackingLost`) reports whether the most
recent frame is past the window. Negative, NaN or infinite `dt` throws
`ArgumentOutOfRangeException`; `dt == 0` still evaluates the pose without
advancing a timer.

The ADR-0008 table pins two edges per gesture as ratios of a measure that is
small when the hand is closed. The state engages when the measure falls to
the **closed** edge and releases when it rises to the **open** edge; between
the edges the previous state holds. That latched band is the only reading of
the pair under which boundary oscillation cannot toggle the output without
crossing the opposite edge (reading it as "engage at the open edge, release
at the closed edge" toggles on every frame inside the band and makes light
gestures unholdable).

- **Pinch**: `ratio = |ThumbTip - IndexTip| / |Wrist - MiddleTip|`. A zero or
  non-finite scale holds the state. Engage `ratio <= 0.5`, release
  `ratio >= 0.7`. While engaged
  `PinchStrength = clamp((0.7 - ratio) / 0.2, 0, 1)`, otherwise 0. The pair
  is the `[0.5, 0.7]` band; the strength map spans it.
- **Fist**: `ratio = max(|IndexTip - Wrist|, |MiddleTip - Wrist|,
  |RingTip - Wrist|, |LittleTip - Wrist|) / max(|Wrist - MiddleTip|,
  NominalHandScale)` with `NominalHandScale = 0.12 m`. Engage
  `ratio <= 0.4`, release `ratio >= 0.6` (the `[0.4, 0.6]` band). The thumb
  tip is excluded because its pose is independent (a natural fist can leave
  it clear) and it already drives pinch; the denominator is floored by the
  nominal hand length so the curled middle finger of a fist cannot collapse
  the measure being tested. S13 replaces the nominal floor with the per-user
  calibrated hand length.
- **Palette flick**: a pinch release arms a 250 ms window (inclusive); a
  re-pinch inside it reports `PaletteFlick = true` on exactly that frame and
  the frame also reports the new pinch. The window freezes during a
  tracking-loss hold and is cleared by a tracking-loss clear. Pinch, fist and
  flick hysteresis are independent.

### Hotbar

`Hotbar` has `SlotCount = 9`, `SelectedIndex` (get), `Selected` (the
`BlockId` at the selection), `Cycle(int delta)` which wraps,
`Get(int slot)` which throws `ArgumentOutOfRangeException` outside
`[0, 9)`, and `static int WrapIndex(int)` so Task 2 can fold
`PlayerState.HotbarIndex + input.HotbarDelta` without duplicating the wrap.
Contents are the ADR-0006 ids 1..5 for the default five blocks; `Set` is not
part of S4.

## Consequences

- Good: all four S4 work items and the S5 adapters share one written
  reference for shapes, constants and physical conventions.
- Good: `Step` is deterministic, allocation-free and frame-split invariant to
  within 1e-3; its hot path uses only value types and `IWorld` lookups.
- Good: 5.11's declaration text is untouched; the additive types are listed
  here so later stages can rely on their exact shape.
- Bad: per-axis try/revert without a sweep means a single step larger than an
  obstacle can tunnel; the caller's fixed timestep keeps steps small and the
  limit is documented.
- Bad: the player can rest up to one step above a surface because landing does
  not snap to contact; determinism and frame-split invariance are worth the
  visual tolerance in S4.
- Bad: `Cubeglass.Gameplay` is C# 10 while `Cubeglass.CoreMath` stays C# 9,
  and it carries a second `IsExternalInit` polyfill (each assembly needs its
  own); the split mirrors ADR-0007's R21.
- Bad: no auto-repeat means a held place button places once; a later device
  stage can add repeat behind a new ADR.

## Confirmation

- `dotnet/tests/Gameplay.Tests/ScriptedProviderTests.cs` pins the neutral
  default frame, constructor field order, exact/between/earlier/after
  sampling, empty-timeline neutrality, strict-time and null rejection, source
  copy isolation and `PointerRay.TryToRay` normalisation plus degenerate,
  non-finite, underflow and overflow rejection; `SupportTypeTests` pins the
  defaults and value flow of `GestureOutput`, `InteractionResult`,
  `HandsInput` and `HandFrame`.
- `dotnet/tests/Gameplay.Tests/HotbarTests.cs` pins the default five slots,
  air tail, selection, forward/backward wrapping, large deltas, `WrapIndex`
  and out-of-range rejection.
- `dotnet/tests/Gameplay.Tests/PlayerControllerTests.cs` pins the feet-centre
  body, gravity-to-rest with a stable resting Y and `OnGround`, wall stop
  without overlap, diagonal wall sliding, ceiling rejection, frame-split
  invariance within 1e-3, identical state hashes for identical scripts,
  yaw-relative movement and speed clamping, terminal fall speed, pitch clamp,
  `dt` edge rules, null rejection and zero allocation over 100,000 steps.
- `TestWorlds.cs` builds its fixtures through the public `IWorld.Apply` path so
  the tests do not depend on Voxel internals.
- `dotnet/tests/Gameplay.Tests/InteractionServiceTests.cs` and the JSON
  scenarios under `dotnet/tests/Gameplay.Tests/scenarios/` pin the Task 2
  conventions above (pointer-preferred/view-ray-fallback targeting with the
  pitch clamp, hold-to-break timing and resets, placement legality including
  the air-slot skip, hotbar cycling, recentre, the 200 ms tracking-loss
  boundary, `Tick == 0`, determinism, zero allocation and an FsCheck property
  over random input sequences).
- `dotnet/tests/Gameplay.Tests/GestureRecognizerTests.cs` with `MockHands.cs`
  pins the Task 3 conventions above: the pinch and fist band edges and their
  `PinchStrength` map, the oscillation rule, the one-frame 250 ms flick
  (inside, boundary, outside and frozen during short loss), the 200 ms loss
  hold/clear and recovery, left-then-right hand selection, degenerate joints,
  translation invariance, determinism and zero allocation.
- `python -m depcheck --root .` keeps `Cubeglass.Gameplay` free of
  `UnityEngine`, `UnityEditor`, `System.IO` and `System.Threading` and limited
  to the CoreMath + Voxel references.

## Links

- Dossier sections: 3.3, 4.2/4.4, 5.9, 5.11, 6 (S4), 7, 11 (FR-08).
- Related ADRs: [ADR-0003](0003-testing-strategy.md),
  [ADR-0004](0004-coordinate-unit-time-conventions.md),
  [ADR-0005](0005-voxel-purity-and-async-boundary.md),
  [ADR-0006](0006-chunk-storage-and-save-format.md),
  [ADR-0007](0007-meshing-budget-winding-and-atlas.md).
- SDD rulings R26-R29 (support types; `PlayerState` as a sealed class;
  test-side scenario JSON; recentre sets yaw to 0).

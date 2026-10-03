# com.cubeglass.input

Unity input adapters, gaze targeting, the gameplay bridge and the comfort/HUD
components for the S7 vertical slice.

## Input mapping table

`InputMapping.cs` is the pure, UnityEngine-free layer:
`UnityInputProvider` samples a device into one `RawInputSample`, and
`InputMapper.Map` turns it into a `Cubeglass.Gameplay.InputFrame` with S4
semantics. Equivalent gamepad and keyboard/mouse actions produce identical
frames (pinned by `InputMappingTests`).

| Action | Gamepad (legacy joystick) | Keyboard / mouse | InputFrame field |
| --- | --- | --- | --- |
| Move | left stick (axes `Gamepad Move X/Y`) | W/A/S/D | `Move` (radial deadzone, clamped to the unit disc) |
| Continuous turn | right stick X (`Gamepad Turn X`) | mouse X delta | `TurnSnap` in degrees per second (`-look.X * rate`; mouse delta / frame dt) |
| Snap turn | D-pad left/right (`Gamepad Snap X`) | F (right) / Shift+F (left) | `ISnapInputSource.ConsumeSnapDirection()`: −1/0/+1 one-shot edge, **not** an `InputFrame` field |
| Break (primary) | X (`JoystickButton2`) | LMB hold | `Primary` (`Pressed`/`Held`/`Released`/`Up`) |
| Place (secondary) | A (`JoystickButton0`) | RMB | `Secondary` (edge semantics; S4 places on `Pressed`) |
| Recentre | Y (`JoystickButton3`) | R | `RecenterPressed` (one frame on press) |
| Hotbar +1 | RB (`JoystickButton5`) | E | `HotbarDelta` = +1 on the press edge |
| Hotbar −1 | LB (`JoystickButton4`) | Q | `HotbarDelta` = −1 on the press edge |
| Pointer | — | — | filled by `GameplayBridge` from `GazeTargeting` |
| Quality | — | — | filled by `GameplayBridge` from the pose provider/late latch |

Notes:

- The gamepad X (break) / A (place) pair can be swapped with the serialized
  `swapGamepadButtons` flag.
- `InputMode.Auto` selects the gamepad when the configured axes resolve in the
  Input Manager and a joystick is connected; otherwise keyboard/mouse. Call
  `RefreshMode()` after plugging a controller in.
- Hotbar next wins when both hotbar buttons press in the same frame; presses,
  snap turns and recentres are emitted on exactly one frame.
- Survey noise below the radial deadzone (0.15 default) maps to zero; mostly
  the `Gamepad Move X/Y/Turn X` axes (which the project's Input Manager defines
  with a 0.05 dead value so the mapper owns the real deadzone).
- `Gamepad Turn X` reads the 4th joystick axis (the common Windows/Xbox right
  stick X) and `Gamepad Snap X` the 6th (the common Windows/Xbox D-pad
  horizontal); adjust the axis index in `ProjectSettings/InputManager.asset`
  for other controller layouts.

## Components

- `GazeTargeting` builds the `PointerRay` from the eye-camera midpoint and
  averaged forward, routed through the single ADR-0004 `UnityConvert` flip.
- `GameplayBridge` runs one tick in the documented order: input → controller →
  interaction; it owns the `PlayerState`, the `InteractionService` (real
  `DdaRaycaster`, slice registry) and the `SaveRequested` flag for Task 4.
- `SnapTurn` turns the player heading by the signed edge increment (default
  ±45°); the rig follows, and a left and a right snap cancel exactly.
- `MotionVignette` draws a soft-edge overlay whose opacity is a pure function
  of planar speed (0 at ≤0.5 m/s, 0.4 at ≥2 m/s).
- `WorldUi` draws the gaze reticle and a world-locked hotbar strip 1.5 m ahead
  of the rig, highlighting the selected slot.

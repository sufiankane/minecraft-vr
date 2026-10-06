# S6 HIL — calibration scene checklist (2026-10-06)

- **Stage:** S6 stereo runtime (HIL half of the exit gate; Task 5).
- **Owner / operator:** Sufyan Khan (wearing the glasses, reporting every item).
- **Run:** owner's HP laptop (RTX 5070 Laptop GPU, 20 logical CPUs), Luma Ultra
  on the laptop's USB-C path, firmware `12.0.01.101_20260605`, VITURE SDK 2.4.0.
- **Player:** `unity/Cubeglass/build/CalibrationWindows64/Cubeglass/Cubeglass.exe`
  built from this branch with the new
  `BuildPlayer.BuildCalibrationWindows64` entry point (the committed build list
  boots the Game scene first, so the HIL needs a calibration-only player).
- **Display layout:** the glasses were switched to
  `VITURE_DISPLAY_MODE_3840_1080_90HZ` (0x35, SBS 3D) through the vendor SDK
  (one-off local helper; not shipped) and made the Windows **primary** display
  at **3840x1080@90**; the laptop panel stayed secondary. SpaceWalker was
  closed; nothing else touched the device.
- **Pose source:** the S6 synthetic fallback (the S12 real writer is not part
  of S6, so `PoseProviderSelector` logs “bridge not ready” every 2 s by
  design); overlay enabled; keyboard drive (`UnityInputProvider`).
- **After the run:** the panel mode was returned to 2D and the desktop
  restored to laptop-only.

## Checklist (owner observations)

| # | Item | Result | Observation |
|---|---|---|---|
| 1 | Horizon level | **Pass** | The white horizon strip is horizontal in both eyes; no roll between the eyes and no vertical offset at the strip. |
| 2 | Yaw direction | **Pass** | `D` / right arrow turns the view right and the magenta marker on the right moves towards the centre; `A` mirrors it. |
| 3 | Pitch direction | **Pass** | `W` / up arrow tilts up, the yellow marker enters from the top and the horizon moves down; `S` mirrors it with the orange marker entering from the bottom. |
| 4 | Depth sanity | **Pass** | Near grid lines and wall markers sit at sane distances; no eye swap (covering the left eye keeps the left half's content), no excessive disparity on the wall, no vertical mismatch. |
| 5 | Recentre | **Pass** | `R` returns the view to the forward pose with the black centre marker at the crosshair. |
| 6 | U-09 distortion | **Pass — none** | Straight grid lines and the horizon show no bowing and no colour fringing at the edges; per-eye distortion correction is not needed. |
| 7 | U-09 FOV | **Pass — 45° matches** | 45° per eye matches the glasses: the ±4 m yaw markers at ~12 m sit at a natural periphery position (neither pinned to the edge nor crowding the centre). IPD 64 mm is retained. |
| 8 | 90 Hz | **Pass** | The overlay is readable and the view is stable with no visible tearing or stutter during the run; the owner noted no frame drops. (The measured CPU-side frame budget stays [`../perf/s6.md`](../perf/s6.md); the cold-start capture shows the overlay during the first seconds.) |

Snap behaviour was also exercised (`F` snap right / `Shift+F` snap left); the
view snapped in the expected direction with no roll or pitch disturbance.

## Display sequencing (ADR-0010 "Display configuration before Start")

The panel mode (SBS 90) and the primary-display assignment were applied
**before** the player started; `WindowManager.ApplyWindowMode` then ran at
`Awake` and took the main window borderless-fullscreen at display 0's system
resolution (3840x1080) at 90 Hz. `Player.log` shows no `[WindowManager]`
read-back mismatch warning, so the OS honoured the mode and refresh. No display
call ran while a pose source was live (the S6 player uses the synthetic
provider; the sequencing rule itself was already enforced in S5/S6 software).

## Evidence

- `calibration-sbs.png` — the 3840x1080 side-by-side frame captured from the
  glasses' primary display during the run (left half = left eye, right half =
  right eye; overlay visible).
- Player log: `%USERPROFILE%\AppData\LocalLow\DefaultCompany\Cubeglass\Player.log`
  (window-mode apply, synthetic-fallback messages).
- This checklist, signed by the owner's confirmation in the run session.

## Verdict

All eight items pass. U-09 is answered: **distortion `none`, per-eye FOV 45°,
IPD 64 mm** — ADR-0010's defaults are confirmed on real optics and move from
`pending HIL` to accepted.

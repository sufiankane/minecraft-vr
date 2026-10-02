# S6-HIL: calibration scene run on the glasses

- **ID:** S6-HIL (Task 5; the hardware-in-the-loop half of the S6 gate).
- **Stage / milestone:** S6 stereo runtime. The software half is complete and
  recorded in [`../notes/s6-gate.md`](../notes/s6-gate.md); this work item is
  the blocked on-glasses run that ticks the visual checklist and answers U-09.
- **Blocked on:** hardware absence. The build environment has no VITURE Luma
  Ultra (or any stereo display) attached, so the visual checklist (horizon
  level, yaw direction, pitch direction, depth sanity, recentre) and the
  dossier unknown **U-09** (is per-eye distortion correction needed, and what
  are the real FOV defaults?) cannot be measured from here. ADR-0010's status
  line and its "Configuration defaults" rows depend on the same run.
- **What was tried:** everything that does not need the glasses:
  - the calibration scene was built and rebuilt twice on Unity 6000.6.3f1 with
    an identical SHA-256 (byte-stable);
  - the PlayMode lane loaded the committed scene and measured 600 frames at
    1920×1080 per eye with the overlay on (mean 0.359 ms, p99 0.541 ms against
    the 11.1 ms budget) — see [`../perf/s6.md`](../perf/s6.md);
  - the synthetic pose fallback, the legacy input drive (yaw/pitch/recentre),
    the late latch and the overlay were exercised by the PlayMode suite;
  - the production bridge path was exercised against the test-only writer by
    the native stress tests (0 torn reads over 200,000 published samples with 8
    readers);
  - the display path (`WindowManager`) applies only outside the Editor preview
    by default, and no stereo display exists to point it at.
- **Evidence:** [`../notes/s6-gate.md`](../notes/s6-gate.md) (suite counts,
  allocation, bridge stress, frame-time table); [`../perf/s6.md`](../perf/s6.md)
  (method and machine); ADR-0010 lines 3, 103–105 and 258–262 record the U-09
  defaults (`IPD 64 mm`, `FOV 45°`, `distortion none`) as the values this run
  must confirm or replace.
- **Question:** can the owner connect the Luma Ultra to a Windows host as a
  90 Hz display, run the committed calibration scene on it, tick the checklist
  below and commit the artefacts? Specifically: is the horizon level and the
  stereo pair aligned (no vertical disparity / eye swap), do yaw/pitch/recentre
  move the view in the expected direction, is the depth read sane, does the
  image need per-eye distortion correction, and what per-eye FOV matches the
  glasses?
- **Proposed options:**
  1. **Owner hardware run (expected path).** Run the scene as below and commit
     `docs/notes/s6-hil/checklist.md` plus screenshots. If every box ticks and
     U-09 answers "distortion none, FOV 45° (or the recorded value)", ADR-0010
     moves from `proposed` to `accepted` and `stage-6-complete` can be tagged.
  2. **Visual defects or distortion found.** Record the failing item with a
     screenshot, reopen the affected work item: a different FOV is a config
     default change; distortion correction is a new spec/ADR (ADR-0010 requires
     `stage-6-complete` to stay withheld until it lands).
  3. **Defer HIL past S6.** `stage-6-complete` stays withheld and S7 proceeds
     on the recorded controller ruling R45; ADR-0010 stays `proposed` for U-09
     and the HIL checklist is re-run with the S12 real writer.
- **Impact if unresolved:** `stage-6-complete` is withheld and ADR-0010 stays
  `proposed`; per-eye distortion is off and the FOV default is unverified on
  real optics. Everything else is safe to continue: the native and managed
  lanes, the PlayMode stereo coverage, the frame budget and the S7 work do not
  depend on the HIL answer.

## What exists

The committed scene is `unity/Cubeglass/Assets/Scenes/Calibration.unity`; the
builder is `Assets/Editor/CalibrationSceneBuilder.cs`. The relevant geometry
(rig at `(0, 1.6, -2)` looking at +Z, wall at `z = 10`, horizon strip and
yaw/pitch markers at eye height 1.6 m, ±4 m horizontally):

- floor plane and 1 m grid (world XZ); origin marker;
- red `+X`, green `+Y`, blue `+Z` axis cubes;
- horizon strip (white, spanning `x = ±12`) with a black centre marker;
- yaw markers: **cyan** at `x = -4` (left of forward), **magenta** at `x = +4`;
- pitch markers: **yellow** at `y = 3.6` (up), **orange** at `y = -0.4` (down);
- `StereoRig` (defaults: IPD 64 mm, FOV 45°, near 0.05 m, far 500 m, target
  refresh 90 Hz), `LateLatchPose`, bridge-first pose selection with the
  `SyntheticPoseProvider` fallback, and `DebugOverlay` enabled.

## Exact owner steps

```powershell
# from the repository root, once
powershell -File scripts/sync-unity-plugins.ps1
```

1. Connect the Luma Ultra as a Windows display, set its refresh to **90 Hz** in
   Windows display settings, and note whether it is the main display. Open
   `unity/Cubeglass` with Unity 6000.6.3f1.
2. Open `Assets/Scenes/Calibration.unity`. The overlay is on and the pose comes
   from the synthetic fallback (the S12 real writer is not part of S6).
3. To present on the glasses from the Editor, select `StereoRig` and enable
   `WindowManager.applyInEditor`, with `targetDisplayIndex` pointing at the
   glasses display; otherwise build and run a Windows player. `WindowManager`
   sizes the **main** window only: if the glasses display is not main, move the
   window to it at OS level, then confirm borderless fullscreen.
4. Enter Play mode. Controls: **A/D** or left/right arrows yaw, **W/S** or
   up/down arrows pitch, **Q/E** snap-turn, **R** recentre.
5. Tick the checklist in `docs/notes/s6-hil/checklist.md` (copy the template
   from this file below) with a screenshot/photo per observation where useful.

### Checklist to tick (write the result, not just a tick)

- [ ] **Horizon level** — the white horizon strip is horizontal in both eyes;
      no roll between the eyes and no vertical offset at the strip.
- [ ] **Yaw direction** — hold `D` (or right arrow): the view turns right and
      the magenta marker on the right moves toward the centre; `A` mirrors it.
- [ ] **Pitch direction** — hold `W` (or up arrow): the view tilts up and the
      yellow marker enters from the top; `S` mirrors it. The horizon moves down
      as you look up.
- [ ] **Depth sanity** — near grid lines and the wall markers sit at a sane
      distance with no eye swap (the left half of the frame is the left eye),
      no excessive disparity on the wall, and no vertical mismatch.
- [ ] **Recentre** — press `R`: the view returns to the forward pose with the
      black centre marker at the crosshair.
- [ ] **U-09 distortion** — per-eye: are straight grid lines/horizon bowing or
      colour-fringing at the edges? Record "distortion none" or the visible
      correction needed.
- [ ] **U-09 FOV** — does 45° per eye match the glasses, or do the ±4 m
      markers at 12 m sit too close to the edge / too central? Record the FOV
      that matches (vendor spec or visual).
- [ ] **90 Hz** — the overlay is readable and the view is stable on the glasses
      (no visible tearing/stutter); note anything that drops frames.

## What to commit

- `docs/notes/s6-hil/checklist.md` — the checklist above, ticked, with the
  observations (including any distortion/FOV verdict and the Windows display
  layout used), dated and signed by the owner.
- Screenshots or photos under `docs/notes/s6-hil/` if the capture path exists
  (a side-by-side screen capture, or one photo per eye); if no capture is
  possible, say so in the checklist.
- Any ADR-0010 update implied by the answers; record the resolution in this
  file (dated) and link the ADR change.

## What the results must answer

- **Visual checklist** — all items above; a failed item is a finding with a
  screenshot and a reopened work item, not a silent exception.
- **U-09 — distortion.** If "none", ADR-0010's `Distortion` row stays `none`
  and the status changes; if correction is needed, the observed artifact (bow,
  fringe, geometry) drives a correction spec/ADR before the tag.
- **U-09 — FOV.** The per-eye FOV that matches the glasses, replacing the ADR
  default 45° if it differs; IPD confirms the 64 mm default or records the
  measured value.
- **Display sequencing** — confirm the window/display settings were applied
  before the pose source started (ADR-0010 "Display configuration before
  Start").

## ADR-0010 sections the results fill

- **Status line** (line 3) — `proposed (U-09 answer pending HIL; the rest is
  accepted for S6 software)` becomes accepted for S6 once U-09 is answered.
- **"Configuration defaults"** (lines 103–105) — the IPD, per-eye horizontal
  FOV and Distortion rows replace their `pending HIL` provenance with the
  measured verdict.
- **"Consequences"** (lines 258–262) — the "distortion is off until U-09 is
  answered" bad consequence and the U-09 follow-up line resolve, or the
  correction pass is recorded.
- **"Display configuration before Start"** (lines 119–126) — confirmed by the
  owner's display sequencing observation.

## Gate rule

`stage-6-complete` is withheld until `docs/notes/s6-hil/checklist.md` is
committed and ADR-0010 answers U-09 (S6 Task 5). Record the resolution in this
file, dated, and link it to the ADR update; archive this file only once that
link exists (`docs/questions/README.md`, process steps 3–4).

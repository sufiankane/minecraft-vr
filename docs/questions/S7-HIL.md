# S7-HIL: vertical slice playtest on the glasses

- **ID:** S7-HIL (Task 4d; the hardware-in-the-loop half of the S7 exit gate).
- **Stage / milestone:** S7 vertical slice (**Milestone M1**). The software half
  is complete and recorded in [`../notes/s7-gate.md`](../notes/s7-gate.md); this
  work item is the blocked on-glasses playtest plus the CI player build.
- **Blocked on:** hardware absence and owner secrets. The build environment has
  no VITURE Luma Ultra (or any stereo display) attached, so no on-glasses
  observation is possible from here; the release player cannot be built in CI
  without the `UNITY_LICENSE` / `UNITY_EMAIL` / `UNITY_PASSWORD` repository
  secrets (R49).
- **What was tried:** everything that does not need the glasses or the licence:
  - the full local gate is green: EditMode **100/100**, PlayMode **40/40**,
    0 skipped (including the game-scene end-to-end smoke and the new frame
    budget measurement) and `ci-local -SkipUnity` **ALL LANES PASS**
    (463 .NET + 45 Python tests);
  - the game scene streams its full view-distance-8 desired set to **1,445
    chunk views** and measures 600 frames per overlay state on the dev machine:
    mean 1.392 ms, p50 1.293 ms, p95 2.141 ms, p99 2.749 ms (overlay off) —
    see [`../perf/m1.md`](../perf/m1.md);
  - the input parity, comfort, pause/save/reload and no-stuck-state software
    behaviour is pinned by the EditMode and PlayMode suites (S7 gate note
    sections 3–5);
  - the release workflow is committed but dispatch-blocked on the licence
    secrets; the `v0.1.0` tag is deliberately **not** created (R50).
- **Evidence:** [`../notes/s7-gate.md`](../notes/s7-gate.md) (suites, golden
  session hash, budgets, input parity, persistence, frame-time table, CI run
  IDs, deferred minors); [`../perf/m1.md`](../perf/m1.md) (method, machine,
  caveats); [`../../.github/workflows/release.yml`](../../.github/workflows/release.yml)
  (player build); ADR-0010 (11.1 ms at 90 Hz; FOV/distortion defaults) and
  ADR-0011 (input mapping and rig composition).
- **Question:** can the owner build the Windows x64 player (dispatch the
  release workflow with the Unity licence secrets, or build locally) and run it
  on the Luma Ultra as a 90 Hz display, then tick the checklist below and
  commit the artefacts? Specifically: is the view comfortable with snap turn
  and the vignette, do both input paths work, are break/place/save/reload
  correct end to end, and does view distance 8 hold 90 Hz on the RTX 5070?
- **Proposed options:**
  1. **Owner hardware run (expected path).** Build the player (section
     "Exact owner steps"), run it on the glasses, tick the checklist and commit
     `docs/notes/s7-hil/checklist.md` plus the perf log/screenshots. With every
     box ticked, `stage-7-complete` (M1) can be tagged and the `v0.1.0` release
     can point at the built player.
  2. **Defects found.** Record each failing item with a screenshot/log,
     reopen the owning work item (comfort/input/persistence/streaming) and keep
     `stage-7-complete` and the `v0.1.0` tag withheld until the fix lands and
     the checklist is re-run.
  3. **Defer HIL.** `stage-7-complete` stays withheld, the `v0.1.0` release
     notes remain a release candidate and the checklist is re-run with the S12
     real writer before M1 is declared.
- **Impact if unresolved:** `stage-7-complete` (M1) and the `v0.1.0` tag are
  withheld, and no first release artefact is attached to a release. Everything
  else is safe to continue: the scripted CI lanes, the software gates and the
  S12 work do not depend on the HIL answer.

## What exists

The committed scene is `unity/Cubeglass/Assets/Scenes/Game.unity` (built by
`Assets/Editor/GameSceneBuilder.cs`; `[Game, Calibration]` in the committed
`EditorBuildSettings`). The slice wiring: starter ground, `PlayerRoot` +
`StereoRig` (bridge-first pose selection, synthetic fallback), `Streaming`
(`StreamingRuntime` + `ChunkViewManager`, view distance 8, vertical radius 2,
load/unload/upload budgets 4, pool cap 2048, seed 1), `Gameplay`
(`GameplayBridge`, `SnapTurn`, `MotionVignette`, `SaveBatches`), `WorldUi`
(reticle + hotbar), `DebugOverlay`, and `GameBoot` (persistent store under
`Application.persistentDataPath/Cubeglass/saves/default`, loaded back on boot).

Controls:

- **Gamepad:** left stick move, right stick turn, **A** place, **X** break,
  **Y** recentre, **LB/RB** hotbar, **B** snap turn (45°).
- **Keyboard/mouse:** **WASD** move, mouse look, **LMB** break (hold past
  hardness), **RMB** place, **Q/E** hotbar, **R** recentre, **F** snap turn.

## Exact owner steps

```powershell
# from the repository root, once
powershell -File scripts/sync-unity-plugins.ps1
```

1. **Build the player.** Preferred: dispatch the **Release** workflow
   (`Actions → Release → Run workflow`) after adding the
   `UNITY_LICENSE` / `UNITY_EMAIL` / `UNITY_PASSWORD` repository secrets; the
   workflow builds the Windows x64 player and uploads
   `Cubeglass-windows-x64.zip`. Local alternative with Unity 6000.6.3f1
   installed:
   `unity run unity/Cubeglass -- -executeMethod Cubeglass.Editor.BuildPlayer.BuildWindows64`
   (output `unity/Cubeglass/build/StandaloneWindows64/Cubeglass/`).
2. **Set up the display.** Connect the Luma Ultra as a Windows display, set its
   refresh to **90 Hz**, and note whether it is the main display.
   `WindowManager` sizes the main window only; if the glasses are not main,
   move the window to the display at OS level and confirm borderless
   fullscreen.
3. **Run the player.** Unzip the artefact, start `Cubeglass.exe`, and allow the
   stream a few seconds to warm up at spawn.
4. **Play the checklist** below, with a screenshot/photo per observation where
   useful, and copy the player log (`%USERPROFILE%\AppData\LocalLow\*\Cubeglass\Player.log`)
   next to the checklist.
5. **Commit the artefacts** under `docs/notes/s7-hil/`.

### Checklist to tick (write the result, not just a tick)

- [ ] **Comfort — snap turn.** Press **B** (gamepad) / **F** (keyboard): the
      view snaps 45° with no nausea-inducing smooth rotation, and repeated
      snaps do not drift or invert.
- [ ] **Comfort — vignette.** While moving, the soft-edge vignette ramps in and
      fades out when stopping; it never blocks the centre view or flickers.
- [ ] **Input — gamepad.** Move, turn, break, place, hotbar and recentre all
      respond per the controls above; the selected hotbar slot matches the
      placed block.
- [ ] **Input — keyboard/mouse.** The same actions behave identically to the
      gamepad on the glasses (move, mouse look, break/place, hotbar, recentre).
- [ ] **No stuck states.** Walk into terrain, break the block under the gaze,
      walk off a ledge, pause/resume: the player never falls through the world,
      floats, or gets stuck in a chunk seam; tracking loss recovers.
- [ ] **Break / place / save / reload.** Break a block, place another, quit the
      player, relaunch: both edits are still there; a second break/place after
      the reload persists too.
- [ ] **View distance 8 at 90 Hz (RTX 5070).** With streaming fully warm, look
      around the horizon and walk: the player overlay reports stable frame
      cadence, no visible tearing/stutter at 90 Hz, and no hitching while
      chunks load/unload. Record the overlay frame time while walking.
- [ ] **Stereo sanity (regression check).** The horizon is level in both eyes,
      no vertical disparity, no eye swap; the reticle lands on the block the
      gaze breaks.

## What to commit

- `docs/notes/s7-hil/checklist.md` — the checklist above, ticked, with the
  observations (including the display layout, the overlay's walking frame time
  and any defect), dated and signed by the owner.
- The perf evidence: a screenshot/log of the in-player overlay while walking
  (and any capture of the 90 Hz display setup).
- If a defect is found: the screenshot/log plus a reopened work item reference;
  a failed item is a finding, not a silent exception.
- Any ADR-0010/ADR-0011 update implied by the answers (for example a real FOV
  or distortion verdict, or a snap-turn comfort change).

## Gate rule

`stage-7-complete` (M1) and the `v0.1.0` tag wait for **both** this checklist
(committed under `docs/notes/s7-hil/`) and a successful CI player build (the
release workflow with the Unity licence secrets), per R50. Record the
resolution in this file (dated) and link it to the release notes and any ADR
update before archiving this escalation.

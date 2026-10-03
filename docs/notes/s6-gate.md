# S6 exit-gate evidence (software; HIL outstanding)

- **Date:** 2026-10-02
- **Stage:** S6 (Unity stereo runtime: bridge reader, stereo rig, late-latch
  pose, synthetic/legacy input, window management, debug overlay, calibration
  scene and frame budget)
- **Task:** S6 Task 4 (calibration scene, budgets and S6 evidence), plus the
  Task 5 HIL escalation
- **Branch:** `s6/scene-budget` (the Task 4 commit that carries this file)
- **Scope of this evidence:** software only. The Unity lane (`ci-local.ps1`) is
  local; `ci.yml` does not run Unity, so the counts below come from this dev
  machine. The three merged S6 PRs are CI-verified by the run IDs in section 8.
- **Runner:** `unity test unity/Cubeglass --mode EditMode|PlayMode
  --non-interactive`, after `scripts/sync-unity-plugins.ps1`; plus
  `scripts/ci-local.ps1 -SkipUnity`

## 1. Unity test suites

| Suite | Result | Counts |
| --- | --- | --- |
| EditMode | Passed | `total=59 passed=59 failed=0 skipped=0` |
| PlayMode | Passed | `total=5 passed=5 failed=0 skipped=0` |

EditMode is the T3 suite plus the three FOV-conversion tests (placeholder 1,
bridge 10, CoreMath 5, DebugOverlay 7, WindowManager 5, StereoRig/config 19,
LateLatch 10, synthetic provider 2). PlayMode is the four T3 tests (yaw sweep,
pre-cull ordering, manual-tick dedupe, 0-byte/300-frame allocation) plus the
frame-budget helper (`FrameBudgetPlayModeTests`), which loads the committed
calibration scene and measures 600 frames with the overlay on (section 4).

Commands (from the repository root):

```powershell
powershell -File scripts/sync-unity-plugins.ps1
unity test unity/Cubeglass --mode EditMode --non-interactive
unity test unity/Cubeglass --mode PlayMode --non-interactive
```

## 2. Allocation gate (0 bytes over 300 frames)

`RigPlayModeTests.DisabledOverlayAllocatesNothingAcross300Frames` renders both
eyes for 64 warm-up frames, snapshots `GC.GetAllocatedBytesForCurrentThread()`,
renders 300 further frames (late-latch pose applied each frame through
`Camera.onPreCull`) and asserts the delta is exactly 0. The provider call count
is baselined after the warm-up too, and the measured window must show at least
one read per rendered frame (delta ≥ 300), so the 0-byte window cannot pass with
a dead late-latch path. It is green in the PlayMode suite above, so the
per-frame stereo path allocates nothing with the overlay disabled. The overlay's
own formatted path is separately pinned allocation-free over 1000 unchanged
`OverlayText.Set` calls in EditMode.

## 3. Bridge stress (native)

`cg_bridge_stress_tests` (S6 Task 1b) publishes 200,000 samples from the
test-only writer while reader threads hammer the production reader, validating
every field against the sequence number; a start barrier and a guaranteed
post-writer read keep the test independent of CI scheduling. Re-run on this
tree:

```powershell
cpp\build\windows-msvc\tests\bridge\cg_bridge_stress_tests.exe --gtest_brief=1
```

| Test | Readers | Published | Valid reads | Mismatched | Unexpected | Post-read failures |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| `HeadReadsStayConsistentUnderEightReaders` | 8 | 200,000 | 291,783 | 0 | 0 | 0 |
| `HandReadsStayConsistentUnderFourReaders` | 4 | 200,000 | 2,032 | 0 | 0 | 0 |

Every reader observed at least one valid sample; the `timeout` transient
(the bounded 64-attempt retry) is expected under contention and is not a
mismatch. The T1 fix rounds hardened exactly this lane: 50× repeat runs and
2-core runs reproduce clean.

`ctest --preset ci`: **5/5 passed** — `core_math` 0.16 s, `glasses` 6.04 s,
`bridge_layout` 0.02 s, `bridge_reader` 0.28 s, `bridge_stress` 0.56 s.

## 4. Frame-time table

Measured by the PlayMode helper in the committed calibration scene, overlay on,
3840×1080 side-by-side render target (1920×1080 per eye), 32 warm-up + 600
measured frames, on the machine below. The full method and caveats are in
[`../perf/s6.md`](../perf/s6.md).

| Statistic | Frame time (ms) |
| --- | ---: |
| mean | 0.504 |
| p50 | 0.489 |
| p95 | 0.654 |
| p99 | 1.142 |
| min | 0.333 |
| max | 1.767 |

Target: **≤ 11.1 ms** at 90 Hz (ADR-0010). Observed headroom: **22.0×** at the
mean and **9.7×** at p99. The p99 does not exceed the target. The scene is a
light synthetic load (about 50 unlit primitives), not the S7 content load, and
the number is CPU-side submission time in the headless lane; the on-glasses
90 Hz check is part of the HIL run. This table is the fix-wave run on the
regenerated scene (converted FOV, visible pitch-down marker); the original
committed-run/independent-rerun comparison stays in
[`../perf/s6.md`](../perf/s6.md).

Machine: AMD Ryzen AI 9 365 (20 logical CPUs), 23 GiB RAM, NVIDIA GeForce RTX
5070 Laptop GPU (7.9 GB), Windows 11 10.0.26200, Unity 6000.6.3f1, D3D12.

## 5. Calibration scene

`unity/Cubeglass/Assets/Scenes/Calibration.unity` is committed (with the
`Assets/Scenes/` folder meta), built by
`Assets/Editor/CalibrationSceneBuilder.cs`:

- menu `Cubeglass/Build Calibration Scene` and the batch entry
  `-executeMethod Cubeglass.Editor.CalibrationSceneBuilder.Build`;
- floor plane + 1 m grid, red/green/blue +X/+Y/+Z axis cubes, far wall with
  four yaw/pitch markers (cyan left, magenta right, yellow up, orange down at
  `y = 0.6`, above the floor so the rig can see it), a centre marker and a
  horizon strip at eye height;
- `StereoRig` (ADR-0010 config defaults: IPD 64 mm, per-eye horizontal FOV 45°
  — converted to the vertical `Camera.fieldOfView` 26.2313° at the nominal
  3840×1080 side-by-side target, 1920×1080 per eye — refresh 90 Hz) with
  `LateLatchPose`, `PoseProviderSelector` (bridge first, synthetic fallback),
  a `SyntheticPoseProvider` child with `UnityInputProvider` +
  `SyntheticPoseDrive` (move axes turn, Q/E snap-turn, R recentres) and
  `WindowManager`;
- a `DebugOverlay` GameObject with the latch wired and the overlay enabled.

The builder canonicalises the scene after saving: Unity assigns pseudo-random
local file ids and an unstable document order, so the pass walks the graph
deterministically, renumbers every document sequentially and remaps all
`{fileID: n}` references. The committed scene was resynced during S7 Task 4d
because it still carried the pre-rework `UnityInputProvider` fields
(`moveAxisX`/`moveAxisY`/`turnSnapDegrees`/`invertHotbarScroll`); S7 Task 4d
rebuilt it from the same builder the S6 numbers used (only the input provider's
serialized fields differ; the S6 geometry, rig and overlay wiring are
unchanged), and two consecutive rebuilds on Unity 6000.6.3f1 produced the
identical SHA-256
`70F970CE7CFEEDC6A70AD27B8759CE177A496A235E59E45E37E0A243E8E490A1`. The S6
hash `D8C1B61215675879B4BA8C5EFD4591B411D3CEEAE33D2882E08B7E00F8CBA352` is
historical (the pre-resync bytes). The scene is committed by design; the
per-commit lanes do not regenerate it (calibration is a menu/batch action, not
part of `ci-local`).

## 6. Local lanes

`powershell -File scripts/ci-local.ps1 -SkipUnity` → **ALL LANES PASS**
(exit 0): python-env 8.3 s, cpp-windows 14 s (ctest 5/5), dotnet 21.3 s
(439 tests: 78 + 118 + 156 + 87), python 1.5 s, depcheck 0.2 s, unity SKIP.
The Unity lane was exercised separately with the two `unity test` commands in
section 1 (both exit 0; EditMode 59/59, PlayMode 5/5).

## 7. HIL pending (owner)

The visual half of S6 is blocked on the glasses being attached to a Windows
host; no Luma Ultra is present in the build environment. The escalation is
[`../questions/S6-HIL.md`](../questions/S6-HIL.md): it records the blocked
visual checklist (horizon level, yaw direction, pitch direction, depth sanity,
recentre) and U-09 (per-eye distortion and real FOV defaults), the exact scene
and display steps, the artefacts to commit
(`docs/notes/s6-hil/checklist.md` plus any screenshots) and the ADR-0010
sections the results fill. The `stage-6-complete` tag is withheld until that
checklist is committed and U-09 is answered; S7 may proceed meanwhile (recorded
controller ruling R45).

## 8. CI verification (merged S6 PRs)

The Unity lane is local, so the three merged S6 PRs are verified for the rest of
CI; their run IDs are:

| PR | Run | Scope |
| --- | --- | --- |
| #28 | [37011797355](https://github.com/sufiankane/minecraft-vr/actions/runs/37011797355) | S6 Task 1 (contracts + bridge) |
| #29 | [37021006447](https://github.com/sufiankane/minecraft-vr/actions/runs/37021006447) | S6 Task 2 (Unity bridge package) |
| #30 | [37032693109](https://github.com/sufiankane/minecraft-vr/actions/runs/37032693109) | S6 Task 3 (rendering, input, PlayMode) |

The Task 4 commit carrying this file has local evidence only (section 6); no
push is performed by this task.

## 9. Deferred minors

Known non-blocking items recorded during Task 4; none affects the software half
of the exit gate:

- **Overlay command ack until S7.** The 5.12 reader ABI exposes no ack read, so
  `DebugOverlay.CommandAck` is a setter for the S7 command sender; the overlay
  shows `ack -` until the S7 command layer wires it.
- **Input System deferred.** Only the legacy Input Manager mapping is
  implemented (`ENABLE_LEGACY_INPUT_MANAGER`; project `activeInputHandler: 0`);
  `UnityInputProvider` returns `Neutral` on a new-input-only project, and the
  Input System mapping plus package dependency land in S7.
- **WindowManager display check-then-act.** `Display.displays` enumeration and
  `Screen.SetResolution` are not atomic (topology can change between them), and
  Unity has no per-display resolution call, so only the main window is sized;
  this is caller-coordinated and the HIL run should confirm the glass display is
  main or that an OS-level window move is needed.
- **Headless PlayMode lane has no Game View.** Tests drive `Camera.Render()`
  explicitly to reach the same pre-cull hook; the measured frame budget is
  therefore CPU-side submission time (see section 4).
- **CoreMath plugin resolution.** `scripts/sync-unity-plugins.ps1` must run
  before any Unity compile; the managed DLL is a git-ignored build output.
- **Scene regeneration is manual.** The calibration scene is committed and byte
  stable across rebuilds on the same Unity version (section 5); the per-commit
  lanes never regenerate it, and a Unity version change is allowed to re-churn
  the file (rebuild and commit it with the version bump).
- **`config.json` production loading deferred to S7.** `StereoRigConfig.LoadFromJson`
  exists and is EditMode-tested, but no runtime bootstrap reads `config.json`
  yet: the calibration scene uses the serialized/inspector defaults (IPD 64 mm,
  per-eye horizontal FOV 45°). S7 wires the 5.13 config file into the player.
- **ABA re-read has no deterministic falsifier.** The bridge reader's second
  `seq_a` read closes the ABA window by reasoning plus stress coverage; no
  deterministic test forces that exact interleaving, so the native 8-reader
  stress test remains the guarantee. Deferred minor, not a gate blocker.

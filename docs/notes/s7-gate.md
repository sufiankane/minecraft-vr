# S7 exit-gate evidence (software; HIL outstanding)

- **Date:** 2026-10-03
- **Stage:** S7 (vertical slice / Milestone M1: streaming scheduler and golden
  session, pooled chunk views, input/gameplay bridge, world UI and comfort,
  batched persistence, the committed game scene, perf record and release
  setup)
- **Task:** S7 Task 4d (scene resync, performance record, S7 evidence, release
  setup and HIL escalation)
- **Branch:** `s7/scene-gate` (base `434a79c`; this task's commits)
- **Scope of this evidence:** software only. The Unity lane is local; `ci.yml`
  does not run Unity, so the suite counts below come from this dev machine. The
  four merged S7 PRs are CI-verified by the run IDs in section 9.
- **Runner:** `unity test unity/Cubeglass --mode EditMode|PlayMode
  --non-interactive`, after `scripts/sync-unity-plugins.ps1`; plus
  `scripts/ci-local.ps1 -SkipUnity`

## 1. Unity test suites

| Suite | Result | Counts |
| --- | --- | --- |
| EditMode | Passed | `total=98 passed=98 failed=0 skipped=0` |
| PlayMode | Passed | `total=40 passed=40 failed=0 skipped=0` |

EditMode (98) is the S6 suite plus the S7 input/rendering additions:
Rendering.Tests 51 (rig, overlay, window, PlayerRoot, chunk views, input
mapping, gaze, comfort), Input.Tests 29, Bridge.Tests 10, CoreMathTests 5,
Editor.Tests 2 (the spawn-column pin added by the Task 4d mini-fix),
Placeholder 1. PlayMode (40) is the S6 rig/frame-budget tests plus the S7
chunk-view, gameplay, persistence and game-scene suites in
`Cubeglass.Unity.Rendering.PlayTests.dll`; Task 4d adds the game-scene
frame-budget measurement
(`GameFrameBudgetPlayModeTests.GameSceneHoldsTheFrameBudgetAtViewDistanceEight`,
section 6).

Commands (from the repository root):

```powershell
powershell -File scripts/sync-unity-plugins.ps1
unity test unity/Cubeglass --mode EditMode --non-interactive
unity test unity/Cubeglass --mode PlayMode --non-interactive
```

## 2. Golden session reproducibility (S7-WI5)

`Cubeglass.Streaming` is pure (CoreMath+Voxel+Mesh; no engine/IO/threads) with
a 90 % coverage floor. The scripted 5-minute session (18,000 steps at 1/60 s,
40 edits, hotbar changes and recentres) yields the committed golden world hash:

```
WorldHash = 0xB38A50148C01A643
```

`GoldenSessionTests.CanonicalFiveMinuteSessionMatchesTheGoldenHash` pins it, and
`HalfStepDtReplaysTheSameTimelineToTheSameHash` replays the same timeline at
1/30 s for half the steps and requires the identical hash, so the outcome is
independent of frame chunking. The pinned route never leaves the retention box
(`Unloads == 0`); the scheduler's determinism is separately pinned by
`SameTimelineProducesTheSameActionLog`, the priority order tests and the
`TargetPathCallsInTheSameOrderAsTheActionList` check.

## 3. Streaming budgets and view caps (S7-WI1/WI2)

| Property | Evidence (test) |
| --- | --- |
| Loads ≤ `MaxLoadsPerFrame` every frame during a burst | `SchedulerTests.LoadBudgetIsEnforcedEveryFrameDuringAChunkBurst` |
| Unloads and uploads budgeted every frame | `UnloadBudgetIsEnforcedEveryFrame`, `UploadBudgetIsEnforcedEveryFrame` |
| Boundary jitter causes no load/unload churn | `HysteresisPreventsChurnAtTheBoundary`; property test `RandomPathsWithHysteresisStayBetweenDesiredAndRetention` |
| `Update` allocates nothing after warm-up | `AllocationTests.UpdateAllocatesNothingAfterWarmup`, `SessionSteadyStateAllocatesNothingAfterWarmup` |
| Random paths drain to the desired set within budgets | `SchedulerPropertyTests.RandomPathsDrainToTheDesiredSetWithinBudgets`, `RandomWaypointPathsDrainToTheDesiredSetWithinBudgets` |
| 50-chunk burst: ≤ budget uploads in **every** frame, the budget is reached, and the burst drains | `ChunkViewPlayModeTests.BurstOfFiftyChunksNeverUploadsMoreThanTheBudgetPerFrame` |
| Load → upload → unload cycle returns every view; `MeshData` build/release parity; high-water held | `FullInOutCycleReturnsEveryViewAndReleasesEveryMeshData` |
| 100 m walk loads ahead, unloads behind, stays within the pool cap; 0 outstanding mesh data at rest | `WalkingOneHundredMetresLoadsAheadUnloadsBehindAndStaysWithinTheCap` |
| Steady state allocates 0 bytes over 120 frames | `SteadyStateDoesNotAllocatePerFrame` |
| Dirty-remesh burst shares the upload budget and drains across frames | `DirtyBurstSharesTheUploadBudgetAndDrainsAcrossFrames` |
| Full desired set resident: 1,445 views at view distance 8 | `GameFrameBudgetPlayModeTests` drain report (section 6) |

The world-hash equality between the last edit and a rebuild is pinned by the
persistence round trip below (section 5).

## 4. Input parity (S7-WI3)

`InputMappingTests.EquivalentGamepadAndKeyboardActionsProduceIdenticalFrames`
pins the mapping table: the gamepad and keyboard/mouse paths produce the
byte-identical `InputFrame` for equivalent move, turn, break/place, hotbar and
recentre actions. Edges follow the S4 sequence
(`ButtonEdgesFollowTheS4Sequence`); snap turn and recentre are single one-shot
edges outside the frame (`SnapTurnIsASingleOneShotEdgeOutsideTheFrame`,
`RecentreIsASingleEdge`) and do not disturb continuous turn
(`SnapEdgeDoesNotDisturbContinuousTurn`); hotbar deltas are single edges
(`HotbarDeltaIsSingleEdgeAndNextWins`); deadzones and unit-disc normalisation
are pinned by `MoveDeadzoneRejectsNoiseAndClampsToTheUnitDisc` and
`LookDeadzoneGatesContinuousTurn`. Gaze targeting runs the eye-midpoint forward
through the single ADR-0004 flip (`GazeTargetingTests`), and the bridge/rig
composition is pinned by `PlayerRootTests` and the PlayMode gameplay suite
(45° snap changes the heading by exactly 45°, recentre clears heading and head
offset, the rig follows `PlayerState` and never the reverse).

## 5. Persistence round trip and the two Critical fixes (S7-WI4)

`PersistencePlayModeTests` covers the full store path: break + place through
the bridge, `FlushAsync`, a fresh world + generate + replay, and both cells
persist with the per-chunk hash equal to the live chunk
(`EditedBlocksSurviveAStoreRoundTrip`); batching flushes on the 32nd edit or
the 2 s interval (`BatchingWritesOnTheThirtySecondEdit`,
`TimeFlushWritesAfterTheInterval`); a corrupted delta is rejected and the chunk
regenerates (`CorruptDeltaIsRejectedAndTheChunkGenerates`); the quit/pause
paths persist (`QuitFlushPersistsPendingEdits`); and the async write does not
block the main thread (`SaveAsyncDoesNotBlockTheMainThread`). The end-to-end
game-scene smoke (`GameScenePlayModeTests.GameSceneEndToEndSmoke`) drives
boot → stream → land → gaze-break → place → flush → rebuild and asserts both
edits survive.

Two Criticals were found in review and fixed with pre-fix failure evidence
(`6abf9ac`):

1. **Cross-session edit loss.** A delta loaded at boot was not seeded back
   into the in-memory accumulated map, so the next flush replaced the file with
   only the current session's cells. Fixed by `IAppliedEditCache` seeding
   (`TrackLoadedDelta`); regression `EditsSurviveAcrossSessions` (session 1
   places A, session 2 loads A and places B, session 3 boots both — failed
   before the fix with the earlier cell overwritten).
2. **Flush accounting.** A max-version drain could report a flush complete
   before a coalesced older queued chunk was written. Fixed by outstanding-write
   accounting (queued + in-flight); regressions
   `FlushWaitsForEveryChunkWhenCoalescingReordersTheQueue` and
   `QueuedWritesIncludesTheInFlightWrite` (both failed against the pre-fix
   store). In-session unload/reload keeps unflushed edits, and `Dispose`
   completes pending flush waiters.

## 6. Frame-time record (M1 content load)

Measured by `GameFrameBudgetPlayModeTests` in the committed game scene at view
distance 8, after draining to **1,445 resident chunk views**, 32 warm-up + 600
measured frames per overlay state, 3840×1080 side-by-side target (1920×1080 per
eye). `frame` is the slice tick + stereo render submission; `render` is the two
`Camera.Render()` calls alone. Full method and caveats:
[`../perf/m1.md`](../perf/m1.md).

| Statistic | Off frame (ms) | Off render (ms) | On frame (ms) | On render (ms) |
| --- | ---: | ---: | ---: | ---: |
| mean | 1.392 | 1.214 | 1.411 | 1.230 |
| p50 | 1.293 | 1.114 | 1.309 | 1.132 |
| p95 | 2.141 | 1.898 | 2.177 | 1.930 |
| p99 | 2.749 | 2.547 | 2.875 | 2.645 |

Target: **≤ 11.1 ms** at 90 Hz (ADR-0010). Observed headroom is **8.0×** at the
mean and **4.0×** at p99 (overlay off; 7.9×/3.9× with the overlay enabled). The
number is CPU-side submission in the headless PlayMode lane, not a presented
GPU frame; the RTX 5070 p99 requirement on the glasses is HIL/player-build
verified (section 11).

Machine: AMD Ryzen AI 9 365 w/ Radeon 880M (20 logical CPUs), 23 GiB RAM,
NVIDIA GeForce RTX 5070 Laptop GPU (7.9 GB), Windows 11 10.0.26200, Unity
6000.6.3f1, D3D12.

## 7. Scenes and build settings

- `unity/Cubeglass/Assets/Scenes/Game.unity` — SHA-256
  `E4179B18C9FFE36F40F339C5C6938D18176A287C32CC72AC9AB6702A9BDCEE3C`.
  Rebuilt during the Task 4d mini-fix with the corrected spawn sampler: the
  builder now samples the height at the internal column the authored Unity
  spawn cell mirrors to (`internalZ = -unityZ`, floored: `(8, -9)`) instead of
  the naive `(8, 8)`, and places the Unity spawn back at the mirror of that
  cell centre. The rebuilt bytes are unchanged because the seed-1 column
  heights coincide (`HeightAt(8, 8, 1) = HeightAt(8, -9, 1) = 10`), which is
  exactly the coincidence the fix removes; `Cubeglass.Editor.Tests` now pins
  the authored-column → sample-column mapping so a future seed/spawn change
  cannot silently regress. Two consecutive rebuilds remain byte-stable.
- `unity/Cubeglass/Assets/Scenes/Calibration.unity` — **resynced in Task 4d**:
  the committed scene predated the `UnityInputProvider` field rework
  (`moveAxisX`/`moveAxisY`/`turnSnapDegrees`/`invertHotbarScroll`). Rebuilt with
  `CalibrationSceneBuilder` on Unity 6000.6.3f1; two consecutive rebuilds
  produced the identical SHA-256
  `70F970CE7CFEEDC6A70AD27B8759CE177A496A235E59E45E37E0A243E8E490A1`. The S6
  hash `D8C1B61215675879B4BA8C5EFD4591B411D3CEEAE33D2882E08B7E00F8CBA352` is
  kept as historical in [`s6-gate.md`](s6-gate.md) section 5.
- `unity/Cubeglass/ProjectSettings/EditorBuildSettings.asset` — now committed
  with the enabled list `[Assets/Scenes/Game.unity,
  Assets/Scenes/Calibration.unity]`. Both scene builders call
  `Cubeglass.Editor.BuildSettingsBuilder.Apply`, and the menu/batch entry
  `Cubeglass/Update Build Settings` rewrites the same deterministic list, so a
  player build boots into the game scene and the calibration scene stays
  available to the budget lane. The release build method
  (`Cubeglass.Editor.BuildPlayer.BuildWindows64`) passes the same two scenes
  explicitly.

## 8. Local lanes

`powershell -File scripts/ci-local.ps1 -SkipUnity` → **ALL LANES PASS** (exit
0): python-env 6.7 s, cpp-windows 12.4 s (ctest 5/5), dotnet 22.7 s (463
tests: 78 + 118 + 24 + 156 + 87), python 2 s (45 tests), depcheck 0.3 s, unity
SKIP. The Unity lane was exercised separately with the two `unity test`
commands in section 1 (both exit 0; EditMode 98/98, PlayMode 40/40,
0 skipped).

## 9. CI verification (merged S7 PRs)

The Unity lane is local, so the four merged S7 PRs are verified for the rest of
CI; their run IDs are:

| PR | Run | Scope |
| --- | --- | --- |
| #32 | [37109972292](https://github.com/sufiankane/minecraft-vr/actions/runs/37109972292) | S7 Task 1 (deterministic streaming scheduler + golden session) |
| #33 | [37114583092](https://github.com/sufiankane/minecraft-vr/actions/runs/37114583092) | S7 Task 2 (pooled chunk views + streaming runtime) |
| #34 | [37122822029](https://github.com/sufiankane/minecraft-vr/actions/runs/37122822029) | S7 Task 3/4a (input, gameplay bridge, UI, comfort, Unity-space alignment) |
| #35 | [37127666886](https://github.com/sufiankane/minecraft-vr/actions/runs/37127666886) | S7 Task 4b (batched persistence + boot replay) |

The Task 4c game-scene commit (`434a79c`) and this Task 4d commit have local
evidence only (sections 1 and 8); no push is performed by this task. The stage
PR for `s7/scene-gate` carries them to CI.

## 10. Release setup

- `.github/workflows/release.yml` is dispatch-only (`workflow_dispatch`). It
  requires `UNITY_LICENSE`, `UNITY_EMAIL` and `UNITY_PASSWORD` and fails with an
  actionable message naming the missing secret(s) before any build runs. With
  the secrets present it builds the Windows x64 player through
  `game-ci/unity-builder` (`buildMethod:
  Cubeglass.Editor.BuildPlayer.BuildWindows64`), uploads
  `Cubeglass-windows-x64.zip` as an artefact, and attaches the archive to the
  `v0.1.0` release when that tag exists (owner-gated per R50).
- `docs/releases/v0.1.0.md` carries the M1 feature list, the known issues from
  the deferred minors below and the HIL-pending note.
- `docs/ci.md` documents the workflow and its secret requirements.

## 11. HIL / owner pending

`stage-7-complete` (M1) and the `v0.1.0` tag are **withheld** until both owner
events succeed:

1. **S7 HIL playtest on the glasses** — run the built Windows player on the
   Luma Ultra and commit `docs/notes/s7-hil/checklist.md`: comfort (snap turn
   and vignette), input (gamepad and keyboard), no stuck states,
   break/place/save/reload, and view distance 8 holding 90 Hz on the RTX 5070.
   Escalation: [`../questions/S7-HIL.md`](../questions/S7-HIL.md).
2. **CI player build** — dispatch the release workflow with the Unity licence
   secrets; the `v0.1.0` release then receives the Windows x64 player.

The S5 and S6 HIL asks (`S5-HIL.md`, `S6-HIL.md`) remain open and are surfaced
together with this one.

## 12. Deferred minors

Known non-blocking items; none affects the software half of the exit gate:

- **`SliceBlockRegistry` divergence.** `Cubeglass.Voxel.dll` references
  `System.Text.Json` for the optional `BlockRegistry` JSON path, which Unity
  does not ship, so the Unity runtime uses `SliceBlockRegistry` (a code-built
  copy of the six `blocks.json` entries). An EditMode test parses the committed
  `dotnet/src/Voxel/Content/blocks.json` and compares every field, so drift
  fails CI; a slice that grows past these blocks must ship the JSON closure or
  move the definitions to a Unity asset.
- **Per-upload neighbour snapshot allocations.** Each mesh upload snapshots the
  chunk and its 26 neighbours (~213 KB transient per build) synchronously on
  the main thread; snapshot caching and off-thread meshing are later work. The
  steady-state no-allocation tests cover only the no-pending-upload case.
- **Boot double-apply diagnostics.** The boot path replays a stored delta and
  then an in-memory merged map, so loaded cells are applied twice (idempotent:
  the second apply is a no-op over the same values). Counter semantics can be
  confusing when reading the overlay, but there is no behaviour impact.
- **Replace-fallback wording.** `FileWorldStore`'s first-write /
  cross-platform publish fallback (delete-target-then-move) logs through a path
  whose wording suggests an unexpected `File.Replace` failure; the behaviour is
  correct, the wording is a polish item.
- **Compaction one-tick race.** `GameplayBridge` re-resolves the live world at
  the start of its tick, but an edit in the same frame as a `ChunkViewManager`
  world compaction can still land in the pre-compaction world for one tick; the
  next tick picks up the rebuilt world (post-compaction regression test pins
  the settled path).
- **Cell-carrying `ChunkChanged`.** The Voxel event carries the chunk, not the
  edited cell, so the Unity adapter cannot dirty an exact chunk set; a
  cell-carrying edit signal is the fix if remesh cost ever matters.
- **Chebyshev-1 remesh superset.** As a consequence, every edit dirties all 27
  loaded chunks in the Chebyshev-1 neighbourhood; meshing an unchanged
  neighbour rebuilds an identical mesh and is correctness-safe but
  conservative.
- **`System.Text.Json` not shipped.** Unity's runtime does not ship the
  assembly `Cubeglass.Voxel.dll` optionally references; the sync script copies
  only the four Cubeglass assemblies and the runtime never touches
  `BlockRegistry`. Revisit if the managed plugins change.

Additional carried notes (documented in the task reports, not blockers): the
quit/pause flush has a bounded 2 s wait; `SaveBatches` retains each chunk's
full edit map for the session; one `FileWorldStore` per world is required;
`Gamepad Turn X` maps the 4th joystick axis and may need an `InputManager.asset`
adjustment on the HIL pad.

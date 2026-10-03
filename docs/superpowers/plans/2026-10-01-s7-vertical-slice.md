# Cubeglass S7 — Vertical Slice (Milestone M1): Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Integrate S2–S6 into a playable 3DoF game: a deterministic streaming scheduler and Unity adapter, pooled chunk renderers with a bounded upload queue, gamepad/keyboard input with gaze targeting, world-locked reticle/hotbar UI, wired persistence, comfort options, a 5-minute headless integration session with a golden world hash, an 8-chunk-view-distance performance record on the dev machine, and the `v0.1.0` release setup. HIL playtest and the CI Unity build are owner events; `stage-7-complete` (M1) waits for them.

**Architecture:** New pure module `Cubeglass.Streaming` (refs CoreMath+Voxel+Mesh) owns `ChunkStreamingScheduler` (desired-set by radius with hysteresis, priority by distance, per-frame budgets, deterministic action order) and is driven in tests by a deterministic harness. Unity `com.cubeglass.rendering` gains `ChunkViewManager` (bounded per-frame upload queue, pooled GameObjects/Mesh objects, `MeshData.Release()` on despawn). `com.cubeglass.input` gains real gamepad + keyboard/mouse providers and gaze targeting. UI/comfort live in rendering. Persistence: a Unity-side `FileWorldStore` wraps the S2 codec and a background writer queue behind `IWorldStore` (IO only in the adapter); edits save in batches and on quit; boot loads existing deltas. The S7 scene wires it all; the release workflow builds `v0.1.0` where a Unity licence is available (owner secret).

**Tech Stack:** netstandard2.1 + C# 10 pure modules; NUnit/FsCheck; Unity 6000.6.3f1 Built-in RP; BenchmarkDotNet for the scheduler; existing perf/gate doc patterns.

**Spec:** `Cubeglass_Engineering_Dossier.md` — 0, 3.2/3.3, 4.2/4.4, 5.9/5.10, 6/S7 (deliverables, work items, tests, exit gate), 7, 8, 9, 11; ADR-0004/0010; S6 scene/bridge.

## Global Constraints

- `Cubeglass.Streaming` is pure (no engine/IO/threads; CoreMath+Voxel+Mesh refs only); layers.json entry + depcheck; coverage floor 90 in CI.
- Streaming is deterministic: same `(seed, position timeline, budgets)` → same actions and same final world hash.
- Unity adapter: ≤ N mesh uploads per frame (config default 4), pooled objects reused; `MeshData.Release()` exactly once per mesh; no per-frame allocation in the steady state.
- Input: both providers produce the identical `InputFrame` semantics (S4); gaze targeting = camera forward; snap turn (default 45°) and vignette-while-moving are config-toggleable.
- Persistence: pure codec in Voxel; file IO only in the Unity adapter; save in edit batches (default 32 edits or 2 s) and on quit; corrupted saves rejected per S2 rules.
- TDD; Conventional Commits; HIL/licence items escalate; tags gate-driven.

## Review Focus

1. **No thrash at the radius boundary**: hysteresis (load radius 8, unload 10) prevents load/unload oscillation when walking along a boundary; deterministic under a jittered path. Pinned in Task 1.
2. **Upload budget honesty**: a burst of 50 chunks never uploads more than N meshes in one frame; the queue drains fairly (no starvation of near chunks). Pinned in Task 2.
3. **Persistence correctness**: save→reload restores exactly the edited cells (world hash equality), saves happen in batches + on quit, and a flaky write cannot corrupt the existing save. Pinned in Task 4.
4. **Input parity**: gamepad and keyboard/mouse produce the same frame for equivalent actions (move vector, snap turn edges, hotbar, recenter). Pinned in Task 3.
5. **Golden session reproducibility**: the 5-minute scripted session yields a committed world hash, stable across runs and independent of frame chunking. Pinned in Task 1.

---

### Task 1: `Cubeglass.Streaming` scheduler and the golden session (S7-WI1, WI5)

**Branch:** `s7/streaming` (from `main` after the S6 software merge).

**Files:**
- Create: `dotnet/src/Streaming/Cubeglass.Streaming.csproj`, `ChunkStreamingScheduler.cs`, `StreamingConfig.cs`, `IStreamingTarget.cs`
- Create: `dotnet/tests/Streaming.Tests/Cubeglass.Streaming.Tests.csproj`, `SchedulerTests.cs`, `SchedulerPropertyTests.cs`, `GoldenSessionTests.cs`, `SessionHarness.cs`
- Modify: `dotnet/Cubeglass.sln`, `contracts/layers.json`, `.github/workflows/ci.yml` (Streaming coverage run + floor 90), `docs/ci.md`
- Commit: this plan

**Interfaces:**
- `StreamingConfig`: `int ViewDistanceChunks = 8`, `int UnloadHysteresis = 2`, `int MaxLoadsPerFrame = 4`, `int MaxUnloadsPerFrame = 4`, `int MaxMeshUploadsPerFrame = 4` (Unity uses this), `float VerticalRadiusChunks = 2`.
- `ChunkStreamingScheduler`: ctor `(StreamingConfig, long seed)`; `IReadOnlyList<StreamingAction> Update(Vec3 playerPosition)` returning a deterministic ordered list: `Load(ChunkCoord)`, `Unload(ChunkCoord)`, `Upload(ChunkCoord)` (mesh ready), with priority = squared distance then coord order; internal state: desired set, loaded set, pending-mesh set; methods `NotifyLoaded(ChunkCoord)`, `NotifyMeshReady(ChunkCoord)`, `NotifyUnloaded(ChunkCoord)`; budgets enforced per `Update`; no allocation after warm-up (reused lists).
- `IStreamingTarget` (for the Unity adapter): `void OnLoad(ChunkCoord)`, `void OnUnload(ChunkCoord)`, `bool OnUpload(ChunkCoord)` returning false when the per-frame upload budget is exhausted; the scheduler calls it in the deterministic order.
- `SessionHarness`: pure end-to-end driver over Voxel `World` + `TerrainGenerator` + `Mesh.GreedyMesher` + `Gameplay.InteractionService` + scheduler, with a scripted 5-minute timeline (walk path, 40 break/place edits at fixed times, hotbar changes, recentres); produces `SessionResult { ulong WorldHash, int ChunksLoaded, int QuadsEmitted, int EditsApplied }`; hash committed as the golden after first run (recorded in the test).
- Tests: radius/generation ordering; budgets (`MaxLoadsPerFrame` respected every frame; burst of 50 → ≤4/frame); hysteresis (jitter path across the boundary → zero load/unload churn beyond the window); unload only after radius+hysteresis; vertical radius limit; determinism (same timeline → identical action log and hash, frame-chunking independent); golden session hash on the committed timeline; property test: for random paths, the loaded set equals the desired set within budgets after draining; no allocations in `Update` after warm-up (`GC.GetAllocatedBytesForCurrentThread`).

- [ ] **Step 1: RED**; **Step 2: implement**; **Step 3: GREEN**, full dotnet suite + `ci-local -SkipUnity`; commit.

---

### Task 2: Unity chunk view manager with pooled renderers (S7-WI1b, WI2)

**Branch:** `s7/chunk-views` (after Task 1 merges).

**Files:**
- Create: `unity/Cubeglass/Packages/com.cubeglass.rendering/Runtime/ChunkViewManager.cs`, `ChunkViewPool.cs`, `StreamingRuntime.cs` (drives the scheduler each frame from the rig/camera position and raises `IStreamingTarget` events)
- Create: `unity/Cubeglass/Packages/com.cubeglass.rendering/Tests/PlayMode/ChunkViewPlayModeTests.cs`
- Modify: `Cubeglass.Unity.Rendering.asmdef` (reference `Cubeglass.Mesh`/`Cubeglass.Voxel`/`Cubeglass.Streaming` managed plugins), `scripts/sync-unity-plugins.ps1` (copy those DLLs), `manifest.json` unchanged
- Commit(s) at the implementer's discretion

**Interfaces:**
- `ChunkViewPool`: reuse `GameObject`+`MeshFilter`+`MeshRenderer` per chunk; `Mesh` objects reused via `Mesh.Clear()`+`SetVertices/SetNormals/SetUVs/SetColors/SetIndices` from `MeshData` (SoA); `MeshData.Release()` called exactly once when a view is despawned/recycled; pool grows on demand up to a cap (config).
- `ChunkViewManager : MonoBehaviour, IStreamingTarget`: receives `Load` (build a `Chunk` via generator on a worker budget — synchronous for now, a chunk per frame max), `Unload`, `Upload` (per-frame cap from config); maintains the loaded dictionary; uses the S2 `GreedyMesher` with a per-manager `MeshBufferPool`; converts mesh data to Unity (positions as-is, normals as-is; winding per ADR-0010) with `Ao` mapped to vertex colors.
- `StreamingRuntime`: each `Update` samples the player position from a referenced transform (the rig), calls `scheduler.Update`, and forwards to the manager; exposes counters for the overlay/tests.
- PlayMode tests: a burst of 50 desired chunks never uploads more than N per frame; a full in/out cycle leaves the pool at its high-water mark and the active count 0; no leaked `Mesh`/`MeshData` (pool counters + `OutstandingBuffers == 0` after despawn); walking 100 m unloads behind and loads ahead; no per-frame allocations in the steady state (steady = no loads/uploads pending).

- [ ] **Step 1: RED**; **Step 2: implement**; **Step 3: GREEN** (Unity CLI both modes), `ci-local -SkipUnity`; commit.

---

### Task 3: Input providers, gaze targeting, UI and comfort (S7-WI3, WI5b)

**Branch:** `s7/input-ui` (after Task 2 merges).

**Files:**
- Modify: `unity/Cubeglass/Packages/com.cubeglass.input/Runtime/UnityInputProvider.cs` — real gamepad mapping (left stick move, right stick turn, A place / X break per config, Y recenter, LB/RB hotbar, B snap-turn edge) and keyboard/mouse (WASD, mouse look, LMB break, RMB place, Q/E hotbar, R recenter); a `InputMode` enum and auto-detection.
- Create: `Runtime/GazeTargeting.cs` (camera-forward pointer ray into `PointerRay`, limited by reach), `Runtime/SnapTurn.cs`, `Runtime/MotionVignette.cs` (soft-edge sprite/gradient overlay while moving; disabled = no draw)
- Create: `Runtime/WorldUi.cs` (reticle at gaze + hotbar strip world-locked in front of the player at a fixed distance; IMGUI or a canvas with reused buffers; reflects the selected slot)
- Create: `Tests/EditMode/InputProviderTests.cs`, `GazeTargetingTests.cs`; `Tests/PlayMode/ComfortPlayModeTests.cs`
- Commit(s) at the implementer's discretion

**Interfaces:**
- Provider mapping table documented and pinned: equivalent gamepad/keyboard actions produce the identical `InputFrame` (move normalized, snap-turn emits a single edge frame, hotbar delta ±1 per press, primary/secondary as `Pressed`/`Held`/`Released` per S4 semantics, recentre edge).
- `GazeTargeting`: builds `PointerRay` from the active eye camera forward (midpoint of the two eyes) with origin at the camera; tests: yaw right → ray direction rotates right; pitch clamps.
- `SnapTurn`: configurable increment (default 45°); applied to the rig yaw, edge-triggered; smooth mode off in S7.
- `MotionVignette`: opacity ramps with planar speed (config max 0.4, speed threshold 0.5 m/s); disabled when not moving; testable opacity curve (pure function).
- `WorldUi`: reticle centered on gaze; hotbar anchored 1.5 m in front of the rig at start (world-locked, not head-locked); selected slot highlight follows `Hotbar`.

- [ ] **Step 1: RED** (EditMode); **Step 2: implement**; **Step 3: GREEN** (EditMode + PlayMode, record); `ci-local -SkipUnity`; commit.

---

### Task 4: Persistence wiring, S7 scene, performance and gate evidence (S7-WI4, WI5c)

**Branch:** `s7/persistence-scene-gate` (after Task 3 merges).

**Files:**
- Create: `unity/Cubeglass/Packages/com.cubeglass.rendering/Runtime/FileWorldStore.cs` (Unity-side `IWorldStore`: pure codec inside, `Task`-based queue writing to `Application.persistentDataPath/Cubeglass/saves/<world>.cgdl` with atomic temp+rename; rejects corrupted deltas via the S2 rules), `Runtime/SaveBatches.cs` (batch on N edits or T seconds, plus save-on-quit `OnApplicationQuit`/`OnApplicationPause`)
- Create: `unity/Cubeglass/Assets/Editor/GameSceneBuilder.cs` + committed `Assets/Scenes/Game.unity` (ground, rig, streaming runtime, chunk views, input, UI, vignette, debug overlay; deterministic build per the S6 builder pattern)
- Create: `unity/Cubeglass/Packages/com.cubeglass.rendering/Tests/PlayMode/GamePlayModeTests.cs` (boot → world loads → break a block → place a block → save → destroy/rebuild manager → reload → block persisted; also "save on quit" via a direct `SaveNow()` call)
- Create: `docs/perf/m1.md`, `docs/notes/s7-gate.md`, `docs/questions/S7-HIL.md`
- Create: `.github/workflows/release.yml` (dispatch-only: checkout, build the Windows player with the Unity CLI using `UNITY_LICENSE`/`UNITY_EMAIL`/`UNITY_PASSWORD` secrets when present; otherwise fail with a clear message; upload the player as an artefact), `docs/releases/v0.1.0.md` (release notes)
- Modify: `scripts/ci-local.ps1` (Unity lane also runs the game PlayMode smoke), `docs/ci.md`
- Commit(s) at the implementer's discretion; final `ci: add the S7 game gate and release setup`

**Interfaces:**
- Perf: measure the Game scene at view distance 8 with the streaming content load (headless where GPU is unavailable; record method honestly) — frame time mean/p95/p99 in `docs/perf/m1.md` on the dev machine, with the CPU-side caveat; state whether p99 ≤ 11.1 ms holds.
- Gate doc: all S7 tests, the golden session hash, persistence round trip, perf table, CI ids, HIL-pending section (playtest checklist: comfort, input, no stuck states), release status (v0.1.0 tag created locally; CI build pending the owner's Unity licence secrets), deferred minors.
- HIL escalation: exact steps to run the built player on the glasses, checklist wording, artefacts to commit, and the statement that `stage-7-complete` (M1) waits for it.

- [ ] **Step 1**: persistence + tests (RED); **Step 2**: scene builder + scene; **Step 3**: perf run + docs; **Step 4**: release workflow + ci-local gate; **Step 5**: full local gate; commit.

**Controller steps after Task 4:** PR → six checks green → whole-stage review (`91576de..head`) → one fix wave → merge → verify `main` CI → create `v0.1.0` release notes tag (annotated, points at the S7 merge; the CI build is owner-blocked) → surface S5/S6/S7 HIL asks together (tags withheld until HIL).

# Cubeglass S6 — Stereo Renderer and Unity Adapters: Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The Unity project renders side-by-side stereo driven by the newest head pose: `cg_unity_bridge` (5.12) with its shared-memory reader and a test-only fake writer, the `com.cubeglass.{rendering,bridge,input}` packages, a stereo rig with late-latch, a calibration test scene with a debug overlay, EditMode/PlayMode suites green via the Unity CLI, and a frame-time budget. HIL visual checklist and U-09 (distortion/FOV) are owner-executed and gate the tag (same pattern as S5).

**Architecture:** `cpp/bridge` implements the 5.6 shared-memory layout (`Local\cubeglass.v1.state`) and exposes the 5.12 C ABI; the command word lives in the reserved header area (ADR-0010, layout extension). `com.cubeglass.bridge` is the single-file P/Invoke wrapper (blittable structs) loading the native DLL; tests drive it against the native test-only writer. `com.cubeglass.rendering` holds `StereoRig` (two cameras, half-width viewports, config IPD/FOV) with a late-latch hook and window management; `com.cubeglass.input` maps Unity input and a synthetic scripted pose provider. The project stays on the Built-in Render Pipeline (ADR-0010); no URP dependency is added.

**Tech Stack:** C++20 + CMake/vcpkg (gtest, threads; new `cg_bridge` SHARED library), pinned Win32 shared-memory APIs with POSIX shm open for CI compile checks; Unity 6000.6.3f1, Built-in RP, `com.unity.test-framework` 1.8 (EditMode + PlayMode), UPM local packages + asmdefs; existing `Cubeglass.CoreMath` consumed for conversion fixtures (S1 golden).

**Spec:** `Cubeglass_Engineering_Dossier.md` — 0, 2.1 (F-04, U-09), 3.2/3.4 (threads, D-1), 3.6/5.4 (conventions), 5.6 (shm layout + reader protocol), 5.12 (bridge C ABI verbatim), 5.13 (config), 6/S6 (deliverables, work items, tests, exit gate), 7, 9, 11; ADR-0004/0009; S5 artifacts (`IDisplayControl`, pose probe).

## Global Constraints

- Contracts: `contracts/cg_unity_bridge.h` reproduces 5.12 verbatim; `contracts/cg_types.h` gains `cg_hand`/`cg_hand_frame` from 5.6 (interim hand ABI) and `CG_ABI_VERSION` rises to 2 — recorded in ADR-0010 (contract change with ADR + version bump per section 0).
- Shm layout exactly per 5.6: magic `CGSHM001`, abi 1, 64-byte alignment, HeadSlot at 64, HandSlot at 256; reader protocol (seq_a odd → retry; seq_b == seq_a) with release/acquire; stale when `now - heartbeat_ns > 250 ms` → `TrackingLost`.
- Command channel: bytes 32..39 of the reserved header area hold a command word + ack word (ADR-0010 layout extension, ignored by prior readers).
- Unity conversion happens in exactly one place (CoreMath/`UnityConvert`, S1); Unity code calls it — no ad-hoc flips.
- No allocation per frame on the render/rig path; late-latch reads the newest sample once per rendered frame.
- Bridge P/Invoke: one file, blittable structs only, `DllImport` with `CallingConvention.Cdecl`, explicit `MarshalAs`-free layout via `[StructLayout(LayoutKind.Sequential)]`.
- TDD; Conventional Commits; contracts additive/ADR'd; HIL items escalate per `docs/questions/`.

## Review Focus

1. **Torn-read impossibility in the real bridge**: writer/reader under stress never expose mismatched (seq, sample) or a partial hand frame; the C# wrapper surfaces `NotReady`/`TrackingLost` rather than garbage. Pinned in Tasks 1–2 (C++ stress; C# against the native writer).
2. **Stale heartbeat**: after 250 ms without a writer update, reads report `TrackingLost` (never stale-stable); recovering writer clears it. Pinned in Tasks 1–2.
3. **Late-latch correctness**: each rendered frame applies the newest pose exactly once, at the documented hook, and the eyes stay consistent (same sample for both eyes); synthetic yaw→view mapping matches ADR-0004 (yaw right = −X at +90°). Pinned in Task 3.
4. **Rig math**: half-width viewports exactly tile the target, per-eye offsets match IPD/2 along right, FOV from config, no overlap/gap at any configured resolution. Pinned in Task 3.
5. **Frame budget**: the calibration scene holds the recorded frame time on the dev machine with the overlay updating; numbers in `docs/perf/s6.md`. Pinned in Task 4.

---

### Task 1: Contracts (5.6 types, 5.12 header), `cg-bridge` and stress tests (S6 WI1)

**Branch:** `s6/bridge` (from `main` after the S5 software merges).

**Files:**
- Modify: `contracts/cg_types.h` (`cg_hand`, `cg_hand_frame` per 5.6; `CG_ABI_VERSION` 2)
- Create: `contracts/cg_unity_bridge.h` (5.12 verbatim)
- Create: `cpp/bridge/CMakeLists.txt`, `cpp/bridge/include/cg/bridge/shm_layout.hpp`, `cpp/bridge/include/cg/bridge/bridge.h`, `cpp/bridge/src/bridge.cpp`, `cpp/bridge/include/cg/bridge/test_writer.h`, `cpp/bridge/src/test_writer.cpp`
- Create: `cpp/tests/bridge/CMakeLists.txt`, `shm_reader_tests.cpp`, `shm_stress_tests.cpp`
- Modify: `cpp/CMakeLists.txt`, `cpp/tests/CMakeLists.txt`, `contracts/layers.json` (`cg-bridge`/`bridge` entry)
- Create: `docs/adr/0010-unity-version-pipeline-and-bridge-layout.md`
- Commit: this plan

**Interfaces:**
- `shm_layout.hpp`: constants (`kMagic = 0x314D48534743ull` little-endian 'CGSHM001', `kAbiVersion = 1`, `kStateName = "Local\\cubeglass.v1.state"`), `struct ShmHeader` (fixed offsets, `static_assert(offsetof...)`), `HeadSlot`/`HandSlot` seqlock structs, command word offsets 32/36 (uint32 command, uint32 ack).
- `bridge.h`/`bridge.cpp`: the five 5.12 functions exactly; `cg_bridge_open` maps the shm reader-side (create-on-demand? no: open existing; writer created by tests/service), returns `CG_ERR_NOT_READY` when absent; `cg_bridge_read_head` implements the 5.6 reader protocol and stale rule (`CG_ERR_NOT_READY` if no valid sample; sample state forced `CG_TRACK_LOST` when stale); `cg_bridge_read_hands` same; `cg_bridge_send_command` writes the command word with a monotonically increasing value (ack observed by reading the ack word); `cg_bridge_close` unmaps.
- `test_writer.h/.cpp` (test-only native API, header under a `cg/bridge/test_writer.h`): open/create the shm, publish head samples and hand frames with the writer protocol, set heartbeat, read commands/ack them; used by C++ stress tests now and by C# tests in Task 2.
- `cg_bridge` builds as SHARED on Windows (`cg_unity_bridge.dll`) and STATIC on POSIX CI (shm open guarded); the test executable links the shared lib and exercises the exported C symbols.
- ADR-0010: Unity 6000.6.3f1 + Built-in RP (no URP package), SBS design, conversion single-source (CoreMath), config-driven IPD/FOV defaults (64 mm, 45° per eye, pending HIL), distortion default none (U-09 pending), command-word extension, `cg_hand_frame` ABI addition + version bump, test-only writer rationale.
- Tests: open-on-missing → `NotReady`; publish/read round trip; torn/stress with 1 writer + 8 readers (zero mismatches, entries equal `seq` consistency); stale rule at exactly 250 ms; command write/ack round trip; twice-open/close; `cg_hand_frame` size/offset static asserts against 5.6.

- [ ] **Step 1: RED** (contract header + tests); **Step 2: implement**; **Step 3: GREEN**; full ctest + `ci-local -SkipUnity`; commit.

---

### Task 2: Unity bridge package — P/Invoke wrapper and EditMode tests (S6 WI1b)

**Branch:** `s6/unity-bridge` (after Task 1 merges).

**Files:**
- Create: `unity/Cubeglass/Packages/com.cubeglass.bridge/package.json`, `Runtime/Cubeglass.Unity.Bridge.asmdef`, `Runtime/NativeBridge.cs` (single-file P/Invoke), `Runtime/BridgeTypes.cs`
- Create: `unity/Cubeglass/Packages/com.cubeglass.bridge/Tests/EditMode/…asmdef`, `NativeBridgeTests.cs`
- Modify: `unity/Cubeglass/Packages/manifest.json` (local package + `testables`)
- Create: `unity/Cubeglass/Assets/Plugins/README.md` (where `cg_unity_bridge.dll` is copied from the cpp build; add the copy step to `scripts/ci-local.ps1` before the Unity lane)
- Commit(s) at the implementer's discretion

**Interfaces:**
- `NativeBridge`: `static bool TryOpen(out BridgeHandle)`, pinned calls for the five functions, `BridgeHeadSample`/`BridgeHandFrame` blittable structs mirroring 5.6 exactly (packed explicit offsets via `[StructLayout(LayoutKind.Sequential, Pack = 1)]` only if natural layout differs — prefer explicit `LayoutKind.Explicit` with `FieldOffset` for the hand frame to be independent of packing).
- Tests use the native test-only writer (`cg_bridge_test_writer_*` exported from the same DLL) to publish scripted samples: read round trip; `NotReady` before first publish; stale → `TrackingLost`; command sent appears in the writer's command word and ack round-trips; close twice is safe; unaligned/partial publish never observed under a stress writer.
- DLL discovery: from `Assets/Plugins/win-x64/cg_unity_bridge.dll`; tests skip with an explicit failure message if missing (no silent skip) — `scripts/ci-local.ps1` copies it from `cpp/build/windows-msvc/bin` (and the benchmarks build path if needed) before running the Unity lane.

- [ ] **Step 1: RED**; **Step 2: implement**; **Step 3: GREEN** via the Unity CLI EditMode run; commit.

---

### Task 3: Rendering package — stereo rig, late-latch, windows, overlay (S6 WI2..6)

**Branch:** `s6/rendering` (after Task 2 merges).

**Files:**
- Create: `unity/Cubeglass/Packages/com.cubeglass.rendering/package.json`, `Runtime/Cubeglass.Unity.Rendering.asmdef`, `Runtime/StereoRigConfig.cs`, `Runtime/StereoRig.cs`, `Runtime/LateLatchPose.cs`, `Runtime/WindowManager.cs`, `Runtime/DebugOverlay.cs`
- Create: `unity/Cubeglass/Packages/com.cubeglass.input/package.json`, `Runtime/Cubeglass.Unity.Input.asmdef`, `Runtime/SyntheticPoseProvider.cs`, `Runtime/UnityInputProvider.cs` (InputFrame mapping stub for S7)
- Create: `unity/Cubeglass/Packages/com.cubeglass.rendering/Tests/EditMode/RigMathTests.cs`, `Tests/PlayMode/RigPlayModeTests.cs` (+ asmdefs)
- Modify: `unity/Cubeglass/Packages/manifest.json`
- Commit(s) at the implementer's discretion

**Interfaces:**
- `StereoRigConfig` (plain C# in the package): `float IpdMeters = 0.064f`, `float FovDegrees = 45f`, `float Near = 0.05f`, `float Far = 500f`, `bool BorderlessFullscreen = true`, `int TargetRefresh = 90`; loaded from `config.json` (5.13) when present; defaults recorded in ADR-0010.
- `StereoRig` (MonoBehaviour): creates/owns two cameras (left/right), each with `rect = (0,0,0.5,1)` / `(0.5,0,0.5,1)`, local offsets ±IPD/2 along the rig's right axis, per-eye FOV; `LateLatchPose.Apply(BridgeHeadSample)` converts internal pose via CoreMath `UnityConvert` (single source) and applies position/yaw/pitch (position = eye offset + config origin), roll from the sample; no per-frame allocation (no closures/LINQ in Update).
- `LateLatchPose` runs from `Camera.onPreCull` (documented hook) for both eyes with the **same** sample per frame; optional single-step prediction uses the sample's own extrapolation flag; synthetic provider path when the bridge is absent (`SyntheticPoseProvider` drives a fake source so PlayMode tests need no native DLL — but the bridge tests still use it).
- `WindowManager`: `Screen.fullScreenMode = FullScreenWindow` on the configured display when borderless enabled; `#if UNITY_EDITOR`/fallback keeps windowed preview; inspectable target display.
- `DebugOverlay` (IMGUI, allocated strings pre-sized/reused; overlay is editor/preview only, excluded from the per-frame allocation assertion in play mode when disabled): frame time (ms), pose rate (Hz from seq deltas), latency estimate (HostTime now − sample time), tracking state, command ack.
- EditMode tests: viewport tiles exactly at 1080p/1440p/4K; eye offsets = ±IPD/2 on right; FOV/rect math; conversion fixture cases reuse the S1 golden values (internal yaw 90° → Unity −90°).
- PlayMode tests: with `SyntheticPoseProvider` scripting yaw sweeps, assert the camera rig orientation tracks the sample (within tolerance) after a frame; both cameras share the same pose sample per frame; `GC.GetAllocatedBytesForCurrentThread()` delta over 300 frames == 0 with the overlay disabled; late-latch hook order (pose applied before rendering).

- [ ] **Step 1: RED** (EditMode first); **Step 2: implement**; **Step 3: GREEN** + PlayMode run via `unity test`; commit.

---

### Task 4: Calibration scene, budgets and S6 evidence (S6 WI7)

**Branch:** `s6/scene-budget-gate` (after Task 3 merges).

**Files:**
- Create: `unity/Cubeglass/Assets/Editor/CalibrationSceneBuilder.cs` (menu + batch entry that (re)generates the scene deterministically)
- Create: `unity/Cubeglass/Assets/Scenes/Calibration.unity` (generated, committed)
- Create: `docs/perf/s6.md`, `docs/notes/s6-gate.md`
- Create: `docs/questions/S6-HIL.md` (visual checklist + U-09 escalation; tag withheld)
- Modify: `scripts/ci-local.ps1` (Unity lane also runs PlayMode and generates the scene if missing)
- Commit(s) at the implementer's discretion; final `ci: run the Unity S6 suites and record evidence`

**Interfaces:**
- Scene: floor grid + orientation markers (colored axis cubes), a wall of text-free markers for horizon/yaw checks, the `StereoRig` + `SyntheticPoseProvider` + `DebugOverlay` wired; built deterministically (no hand-authored YAML).
- Frame budget: PlayMode measurement mode with the overlay on: record mean/p95 frame time over 600 frames at 1080p per eye on the dev machine into `docs/perf/s6.md` with the machine; budget from ADR-0010 (frame time ≤ 11.1 ms at 90 Hz target, i.e. headroom; record actual).
- `docs/notes/s6-gate.md`: EditMode/PlayMode results, allocation delta 0, bridge stress results, frame-time table, CI run ids; **HIL pending (owner)** section: run the scene on the glasses, tick the visual checklist (horizon level, yaw direction, pitch direction, depth sanity, recentre), answer U-09 (distortion needed? FOV per eye), commit `docs/notes/s6-hil/checklist.md`; no tag until it lands.
- `scripts/ci-local.ps1`: Unity lane runs EditMode + PlayMode and fails loudly when the DLL is missing.

- [ ] **Step 1**: scene builder + scene; **Step 2**: PlayMode perf measurement + docs; **Step 3**: ci-local wiring + escalation doc; **Step 4**: full local gate; commit.

### Task 5 (blocked pending hardware): HIL visual checklist and U-09

Escalation `docs/questions/S6-HIL.md` is created in Task 4; the tag waits for the owner's signed checklist and ADR-0010's U-09 answer (distortion none confirmed or a correction spec added).

**Controller steps after Task 4:** PR → six checks green → whole-stage review → one fix wave → merge → verify `main` CI → surface S5+S6 HIL asks together in the run report (tags withheld for both until HIL).

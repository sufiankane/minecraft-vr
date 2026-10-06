# Cubeglass S8 — Stereo Capture, Recorder and Feasibility (Gate G-A): Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Capture the Luma Ultra stereo cameras reliably, record `.cgrec` datasets, and decide whether stereo hand tracking is viable (gate G-A, resolving U-02/U-03/U-05/U-06). Software deliverables (sources, handoff, recorder, validator, replay, probe, dataset protocol) land first; the hardware recording, clock-offset measurement and feasibility report are owner events that gate `stage-8-complete`.

**Architecture:** `contracts/cpp/ports.hpp` gains the dossier 5.3 stereo port (`StereoImage`, `StereoFrame`, `IStereoFrameSink`, `IStereoFrameSource`) verbatim. A new C++ module `cg-capture` owns the device-independent side: `FakeStereoSource` (synthetic patterns), `NewestFrameSlot` (lock-free single-slot newest-wins handoff), the `.cgrec` recorder (bounded writer queue + drop accounting + PGM frames + manifest + CSVs), the validator, and `ReplayStereoSource` (real-time and fast modes). `cg-glasses` gains `VitureStereoSource` on the existing vendor binding: the loader's camera callback stops being a no-op and delivers `StereoFrame`s (U-02/U-03 resolved from probe data, not guessed). Tools: `cg-recorder` and `cg-capture-probe`. The dataset protocol is a committed document; the feasibility report and ADR-0014 answer the unknowns with data.

**Tech Stack:** C++20 (MSVC/clang-cl + GCC/ASan CI), CMake presets, GTest, the existing vendor dynamic-loading seam (`viture_loader`), PGM + JSON + CSV artefacts, BenchmarkDotNet-style C++ micro-benchmarks under the existing benchmarks preset.

**Spec:** `Cubeglass_Engineering_Dossier.md` — 0 (rules), 2.1 (U-02/U-03/U-05/U-06), 3.2/3.3 (threads, callback budget), 5.3 (stereo port), 5.6 (timestamps), 5.8 (dataset format), 6/S8 (deliverables, work items, tests, exit gate), 7, 9, 11; ADR-0001/0009/0014.

## Global Constraints

- The 5.3 types are the dossier's, verbatim; additive to `ports.hpp` (no existing member changes).
- `IStereoFrameSink::OnFrame` must return quickly: the VITURE callback copies into the handoff slot and returns; no IO, no allocation, no locking that can block on the writer (S8 work item 2).
- The recorder never blocks the callback: a bounded writer queue with counted, reported drops (work item 3).
- `ReplayStereoSource` reproduces original timing within 1 ms in real-time mode and runs as fast as possible in fast mode (5.8).
- Hardware facts are measured, never guessed: probe output decides `f0`/`f1` semantics, strides, and 3DoF camera behaviour (U-02/U-03); the clock offset is measured (U-05).
- TDD; Conventional Commits; the HIL parts escalate via `docs/questions/S8-HIL.md`; tags gate-driven.

## Review Focus

1. **Callback budget honesty**: the sink path copies and returns within the documented budget under a stalled writer; the benchmark pins it, and dropped frames are visible in the manifest, not silent.
2. **No torn frames**: the newest-wins slot never publishes a half-copied pair; a slow consumer sees whole frames and a monotonic `seq`, and the contract tests pin "no sink call after Stop returns".
3. **Format fidelity**: record(fake) → validate → replay is byte-equal on frames and within 1 ms on timing; a corrupted dataset fails validation with a named reason, never a crash.
4. **Vendor seam discipline**: `cg-glasses` stays the only TU that knows vendor symbols; the capture module sees only `IStereoFrameSource`.
5. **G-A provability**: the feasibility report answers U-02/U-03/U-05/U-06 from committed artefacts (probe logs, dataset manifest, annotated frames), so the G-A no-go path is a reasoned escalation, not a vibe.

---

### Task 1: Stereo port + `FakeStereoSource` + contract tests (S8-WI1)

**Branch:** `s8/contracts` (from `main` after the S7 cursor fix; `main` is the release base).

**Files:**
- Modify: `contracts/cpp/ports.hpp` — the 5.3 port verbatim (`StereoImage`, `StereoFrame`, `IStereoFrameSink`, `IStereoFrameSource`), additive.
- Create: `cpp/capture/CMakeLists.txt`, `cpp/capture/include/cg/capture/fake_stereo_source.hpp`, `cpp/capture/src/fake_stereo_source.cpp`
- Create: `cpp/tests/capture/CMakeLists.txt`, `cpp/tests/capture/contract_tests.cpp`
- Modify: `cpp/CMakeLists.txt` (add `capture`), `cpp/tests/CMakeLists.txt`, `contracts/layers.json` (a `capture` entry), `scripts/ci-local.ps1` if the C++ lane enumerates targets explicitly.
- Commit: this plan.

**Interfaces:**
- Contract (verbatim from 5.3): `StereoImage { const uint8_t* left; const uint8_t* right; int width; int height; int stride; }` (strides in bytes, views valid only during the callback), `StereoFrame { HostTime time; uint64_t seq; StereoImage f0; StereoImage f1; }`, `IStereoFrameSink::OnFrame(const StereoFrame&) noexcept`, `IStereoFrameSource::Start(IStereoFrameSink*)` / `Stop() noexcept`.
- `FakeStereoSource`: deterministic synthetic patterns (config: size, stride, f0/f1 content, per-frame time step, seq start); `Start` stores the sink; `EmitFrame()` (test driver, any thread) builds the frame and calls the sink; `Stop` clears the sink and guarantees no further callbacks (test-visible `EmittedAfterStop` counter); accessors for the last frame's buffers for equality checks.
- Contract suite (templated over a source factory so `ReplayStereoSource` reuses it in Task 4): sequence strictly increasing by 1; timestamps non-decreasing; `Start` idempotent; `Stop` before `Start` and double `Stop` are safe; no sink call after `Stop` returns; the buffers passed are stable for the duration of the call.

- [ ] **Step 1: RED** (contract tests against the port + fake; ctest entry `capture_contract`); **Step 2: implement**; **Step 3: GREEN** (`ctest --preset ci`, `windows-analyze` clean, clang-format); commit.

---

### Task 2: Newest-wins handoff and the callback budget (S8-WI2)

**Branch:** `s8/frame-slot` (after Task 1 merges).

**Files:**
- Create: `cpp/capture/include/cg/capture/newest_frame_slot.hpp`, `cpp/capture/src/newest_frame_slot.cpp`
- Create: `cpp/tests/capture/slot_tests.cpp`; `cpp/tests/benchmarks/capture_benchmarks.cpp`
- Modify: `cpp/tests/capture/CMakeLists.txt`, `cpp/tests/benchmarks/CMakeLists.txt`

**Interfaces:**
- `NewestFrameSlot`: single producer (the SDK callback) / single consumer (the writer thread); `Publish(const StereoFrame&)` copies into the inactive buffer, flips the active index with release semantics, counts overwrites (`Overwritten()`); `TryTake(StereoFrame&)` returns the newest whole frame and copies out (or a view type bounded to the slot); never allocates after construction; `FramesPublished`/`FramesTaken`.
- Sink adapter: `SlotSink : IStereoFrameSink` that publishes and returns (the callback budget path).
- Tests: a stalled consumer sees only whole, monotonically sequenced frames; concurrent publish/take stress (TSan lane) never tears; overwrites are counted; zero allocations in `Publish` after warm-up.
- Benchmark: publish cost p99 under a stalled consumer (budget recorded in `docs/perf/s8.md`).

- [ ] **Step 1: RED**; **Step 2: implement**; **Step 3: GREEN** + benchmark record; commit.

---

### Task 3: `cg-recorder` — `.cgrec` writer and validator (S8-WI3, part of WI4)

**Branch:** `s8/recorder` (after Task 2 merges).

**Files:**
- Create: `cpp/capture/include/cg/capture/recorder.hpp`, `src/recorder.cpp`, `include/cg/capture/cgrec.hpp`, `src/cgrec.cpp` (format read/write + validator)
- Create: `cpp/capture/include/cg/capture/pgm.hpp`, `src/pgm.cpp`
- Create: `cpp/tests/capture/recorder_tests.cpp`, `cpp/tests/capture/cgrec_roundtrip_tests.cpp`
- Create: `cpp/tools/recorder/CMakeLists.txt`, `main.cpp` (CLI)
- Modify: CMake wiring, `docs/` as needed.

**Interfaces:**
- `Recorder`: ctor `(RecorderConfig)` (session dir, frame mode `Pgm`/`PackedBin`, queue bound, drop policy), `Start(IStereoFrameSink**)` or `Attach`; `Submit(const StereoFrame&)` (called from the sink; copies into the bounded queue, counts drops, never blocks); writer thread drains to disk; `Close()` flushes and writes the manifest; `Stats { submitted, written, dropped }`.
- `.cgrec` layout per 5.8: `manifest.json` (schema, device info, DOF mode, SDK version, sizes, counts, drop counts), `frames/NNNNNNNN_l0.pgm` etc. (or one packed `.bin` per frame), `stereo.csv` (`seq, host_time_ns, sdk_time_s, width, height`), `pose.csv` (pose rows when available), optional `labels/`.
- Validator: `ValidateSession(path)` → `Result<SessionInfo>`; checks manifest schema/version, counts vs files, CSV monotonicity, PGM headers/strides; named failure reasons.
- Stress test: a stalled disk (temporary directory with a slow writer injected via a test hook) never blocks the callback beyond the budget; drops are counted and reported; the manifest records them.
- Round trip: record a scripted `FakeStereoSource` session, validate it, compare every frame byte-for-byte and every CSV row exactly.

- [ ] **Step 1: RED**; **Step 2: implement**; **Step 3: GREEN** + stress; commit.

---

### Task 4: `ReplayStereoSource` (S8-WI4, part of WI4)

**Branch:** `s8/replay` (after Task 3 merges).

**Files:**
- Create: `cpp/capture/include/cg/capture/replay_stereo_source.hpp`, `src/replay_stereo_source.cpp`
- Create: `cpp/tests/capture/replay_tests.cpp`
- Modify: contract suite template to include the replay factory.

**Interfaces:** `ReplayStereoSource(path, ReplayMode::RealTime|Fast)`; real-time mode schedules frames on the recorded timeline within 1 ms (condition-variable paced on the injected clock in tests); fast mode emits as fast as the sink accepts; reuses the Task 1 contract suite; validation runs on open (invalid dataset → `Unsupported` with the validator reason).

- [ ] **Step 1: RED** (record→validate→replay equality + timing); **Step 2: implement**; **Step 3: GREEN**; commit.

---

### Task 5: `VitureStereoSource` on the vendor seam (S8 hardware path, software half)

**Branch:** `s8/viture-frames` (after Task 4 merges).

**Files:**
- Modify: `cpp/glasses/include/cg/glasses/viture_api.hpp` (additive: frame sink registration + `StartFrames`/`StopFrames` or an extended callback registration contract), `cpp/glasses/src/viture_loader.cpp` (the camera callback forwards to the registered sink; keep the no-op default), `cpp/glasses/src/viture_head_pose_source.cpp` only if the shared lifecycle needs it.
- Create: `cpp/glasses/include/cg/glasses/viture_stereo_source.hpp`, `src/viture_stereo_source.cpp`
- Create: `cpp/tests/glasses/viture_stereo_source_tests.cpp` (against `FakeVitureApi`; callback→frame mapping, stop semantics, no callback after stop).
- Modify: CMake if the glasses target gains sources; layer rules stay (`glasses` may not include `onnxruntime`).

**Interfaces:** `VitureStereoSource : IStereoFrameSource` wrapping an `IVitureApi`; `Start(sink)` registers the sink on the device (if the device is created; error `NotReady` otherwise); the loader's camera callback maps `(left0, right0, left1, right1, timestamp, width, height)` to `StereoFrame` with `host_time` from an injected clock mapped at receipt and `sdk_time`-derived ordering; `Stop` unregisters and guarantees no callback after return. U-03 facts (strides, layouts) stay **configuration**, defaulting to packed rows, until the probe measures them.

- [ ] **Step 1: RED**; **Step 2: implement**; **Step 3: GREEN**; commit.

---

### Task 6: `cg-capture-probe` tool (S8-WI: U-02/U-03 evidence)

**Branch:** `s8/capture-probe` (after Task 5 merges).

**Files:**
- Create: `cpp/tools/capture_probe/CMakeLists.txt`, `main.cpp`
- Modify: tools CMake wiring; `docs/notes/s8-gate.md` (created in Task 7).

**Interfaces:** CLI `cg-capture-probe --dll <path> --dof 3dof|6dof --seconds N --out <log>`; loads the vendor library through the existing `LoadVitureApi` path policy, runs the source for N seconds, and logs: frame rate, sequence gaps, `width`/`height`, per-stream strides (bytes per row vs `width`), whether `f0`/`f1` differ, and a small PGM snapshot per stream for the HIL artefacts. Exits 2 on loader failure (same matrix as the pose probe).

- [ ] **Step 1: RED** (arg parsing + log formatting unit-tested); **Step 2: implement**; **Step 3: GREEN**; commit.

---

### Task 7: Dataset protocol, HIL escalation and the feasibility report (S8-WI5..WI7, gate G-A)

**Branch:** `s8/gate` (after Task 6 merges).

**Files:**
- Create: `docs/notes/s8-dataset-protocol.md` (recording scripts: hand-position volume grid 40x30x30 cm at bent-arm distance, lighting variants, distances; label policy: approximate hand presence per camera on a subset; ≥20 minutes total).
- Create: `docs/questions/S8-HIL.md` (owner runbook: build the tools, run the probe in 3DoF and 6DoF, record sessions, label a subset, commit artefacts under `docs/notes/s8-hil/`).
- Create (owner-run): `docs/notes/s8-hil/probe-3dof.log`, `probe-6dof.log`, the dataset manifest (dataset stored externally), the clock-offset measurement, annotated frames.
- Create (after the data exists): `docs/notes/s8-gate.md` (measurements, U-02/U-03/U-05/U-06 answers, G-A verdict with the 90 % criterion, dataset location) and `docs/adr/0014-stereo-feasibility-and-gate-ga.md`.
- Commit: the software gate + HIL escalation; the ADR/gate note in a follow-up commit when the data exists.

**Exit gate mapping:** U-02/U-03/U-05/U-06 answered in ADR-0014 with data attached; G-A met (both hands visible in both cameras in ≥90 % of frames inside the defined volume) or the no-go escalation raised in `docs/questions/`; ≥20 min dataset stored externally with the manifest committed; then `stage-8-complete`.

- [ ] Software parts: protocol doc + escalation committed; `ci-local -SkipUnity` green.
- [ ] Owner HIL: probe logs, ≥20 min dataset, labels, clock offset committed; ADR-0014 + gate note; tag.

---

## Sequenced dependencies

Task 1 → 2 → 3 → 4 → 5 → 6 → 7. Tasks 1–4 are fully software (a parallel agent could take 5–6 after 4); Task 7's HIL half is an owner event and may run while later software (S9 synthetic calibration) proceeds, per the dossier's "device work may run in parallel" note.

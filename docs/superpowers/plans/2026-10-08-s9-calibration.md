# Cubeglass S9 — Camera Calibration and Hand Profile: Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Reproducible camera calibration and hand profile (dossier S9), resolving U-04 (lens model, FOV, baseline, camera-to-display extrinsics, self-calibrated) and producing a real `calibration.json` for the owner device. The owner authorised starting S9 while S8's remaining HIL (TD-086) is deferred; the S9 exit gate still needs the real board capture at the end.

**Architecture:** Python + OpenCV (`calib` package) does the offline work: a synthetic rig generates ground-truth data, the solver recovers pinhole or fisheye stereo parameters, captures come from `.cgrec` sessions (`calib.session` reads our PGM/packed-bin frames), and the result is written as `calibration.json` (schema per dossier 5.7) whose JSON Schema lives in `contracts/`. The C++ side gains `CalibrationLoader` (glasses module, nlohmann) with schema validation and golden fixtures. Pure Python solvers cover head-from-camera alignment (pointing correspondences → rigid transform) and hand-profile bone lengths. The owner HIL captures chessboard views through the glasses with `cg-recorder --source viture`.

**Tech Stack:** Python 3.14 (`numpy`, `opencv-python-headless` — cp37-abi3 wheel verified), pytest/ruff/mypy strict, C++20 (`cg-glasses`, nlohmann), the existing `.cgrec` recorder, JSON Schema.

**Spec:** `Cubeglass_Engineering_Dossier.md` — U-04 (2.1), 5.7 (`calibration.json` schema), 5.8 (`.cgrec`), 6/S9 (deliverables, work items, tests, exit gate); ADR-0015 (to be written: lens model, quality thresholds, procedure).

## Global Constraints

- The solver is verified against ground truth before any real capture: focal length within 0.5 %, baseline within 0.5 mm on synthetic data.
- `calibration.json` follows 5.7 exactly; the loader rejects unknown major schema versions; golden fixtures pin good/bad files.
- The capture procedure is reproducible: fixed board, fixed capture script, the same `cg-calib` command in the report.
- Python stays strict-typed (`mypy strict`) and lint-clean (`ruff`); cv2 is an untyped module behind an explicit mypy override.
- Software tasks land first; the real device calibration is the final owner event (runbook `docs/questions/S9-HIL.md`).

## Review Focus

1. **Ground-truth honesty**: the synthetic generator and the solver are independent enough that recovering the rig means something (no shared bug that fakes a pass); tolerances are asserted, not eyeballed.
2. **Model selection evidence**: pinhole vs fisheye is chosen by reproduced RMS on the same captures, recorded in the quality report, not by assumption.
3. **Schema discipline**: the JSON Schema, the writer and the C++ loader agree; a wrong major version fails by name on both sides.
4. **Repeatability**: the report records the exact command, board, and view count so the calibration can be reproduced and compared.
5. **Alignment math**: the alignment solver is a pure, unit-tested function (Kabsch/Umeyama) with known-answer tests, not a tuned heuristic.

---

### Task 1: Python foundation + synthetic ground-truth stereo solve

**Branch:** `s9/synth-solve` (from `main`).

**Files:** `python/pyproject.toml` (deps + mypy override), `python/calib/{__init__,types,projection,synth,solver}.py`, `python/tests/test_calib_synth.py`.

- [ ] **Step 1: RED** — ground-truth tests: build a known pinhole rig (fx/fy 600, cx/cy 320/240, baseline 63.5 mm), a 9x6 25 mm board at ~14 seeded poses, project exact pixel points, solve, assert |fx error| ≤ 0.5 %, |baseline error| ≤ 0.5 mm, RMS ≤ 0.5 px; a 0.05 px noise variant keeps the same bounds.
- [ ] **Step 2: implement** `types` (BoardSpec, Intrinsics, StereoRig), `projection` (pinhole/fisheye project, transform helpers), `synth` (poses + views), `solver` (per-camera `calibrateCamera` + `stereoCalibrate` with `CALIB_FIX_INTRINSIC`, `right_from_left` p/q, baseline).
- [ ] **Step 3: GREEN** — pytest, ruff, mypy strict; commit + PR.

### Task 2: `.cgrec` reader, board detection, model selection, writer + schema

**Branch:** `s9/cgcalib` (after Task 1 merges).

**Files:** `python/calib/session.py`, `python/calib/detect.py`, `python/calib/pipeline.py`, `python/calib/writer.py`, `contracts/calibration.schema.json`, `python/calib/__main__.py` (`python -m calib --session DIR --board 9x6 --square 0.025 --out calibration.json`), tests.

- [ ] Reads PGM/packed-bin `.cgrec` frames (reuse the S8 format; `stereo.csv` for ordering).
- [ ] `findChessboardCornersSB` per view per camera; model selection: solve pinhole and fisheye (`cv2.fisheye.calibrate` + `fisheye.stereoCalibrate`), pick the lower stereo RMS, record both in the quality report.
- [ ] Writer emits 5.7 exactly (schema 1, device serial from the manifest when present, `quality.reproj_rms_px`, `n_views`); JSON Schema in `contracts/` + the contract baseline flow.
- [ ] Synthetic detection test: render board views (cv2) to packed-bin sessions, detect and solve end to end.
- [ ] Negative tests: unreadable session, too few views, board not found.

### Task 3: C++ `CalibrationLoader` + golden fixtures

**Branch:** `s9/loader` (after Task 2 merges).

**Files:** `cpp/glasses/include/cg/glasses/calibration.hpp`, `src/calibration.cpp`, `cpp/tests/glasses/calibration_tests.cpp`, fixtures under `cpp/tests/glasses/data/`.

- [ ] Parse + validate 5.7 (all fields, ranges: positive focals, unit quaternion, non-empty dist, finite floats); unknown major schema → `Unsupported` naming it; malformed JSON → `Unsupported` with the parse reason.
- [ ] Golden fixtures: valid, wrong major, missing field, non-finite; round-trip against a real `calibration.json` written by Task 2 (fixture generated in the test from a small helper, or committed).
- [ ] Local gates: ctest, windows-analyze, tidy, format.

### Task 4: Alignment solver + hand profile (pure Python)

**Branch:** `s9/alignment` (after Task 3 merges).

**Files:** `python/calib/alignment.py`, `python/calib/hand_profile.py`, tests.

- [ ] `solve_head_from_camera(points_camera, points_head)` → `(p, q)` via Kabsch/Umeyama with reflection rejection; known-answer tests (exact rotation recovery, noisy recovery within tolerance, degenerate input rejected).
- [ ] `measure_bone_lengths(samples)` → robust (median) lengths + the 15 % tolerance from 5.7; synthetic hand with known lengths; outlier rejection test.
- [ ] The guided capture routine is documented for the HIL (pointing N targets); the solver stays pure.

### Task 5: Docs, HIL runbook, real calibration (gate)

**Branch:** `s9/gate` (after Task 4 merges).

**Files:** `docs/notes/s9-procedure.md`, `docs/questions/S9-HIL.md`, `docs/notes/s9-hil/*` (board views, report), `docs/adr/0015-calibration.md`.

- [ ] Procedure: print the 9x6 25 mm board, record ~20-30 views through the glasses (`cg-recorder --source viture --storage pgm --seconds N`), run `python -m calib ...`, review the quality report (RMS < 0.5 px proposed), repeat if needed.
- [ ] HIL (owner): captures + the run; commit the views (or their manifest), the quality report and the resulting `calibration.json`.
- [ ] ADR-0015: lens model decision, thresholds, alignment procedure, bone-length policy.
- [ ] Exit gate: all tests pass, real calibration stored, quality report committed, tag `stage-9-complete`.

## Sequenced dependencies

Task 1 → 2 → 3 → 4 → 5. Tasks 1 and 4 are hardware-free; Task 2's detection test is synthetic; the owner HIL is the final gate. The dossier's S8 gate dependency is being carried by owner override (TD-086 open); ADR-0015 records that context.

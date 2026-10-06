# S5-HIL: Luma Ultra pose and display run

- **ID:** S5-HIL (Task 5; the hardware-in-the-loop half of S5-WI6).
- **Stage / milestone:** S5 glasses adapter. The software half is complete and
  recorded in [`../notes/s5-gate.md`](../notes/s5-gate.md); this work item is
  the blocked hardware run that closes U-01 and U-08.
- **Blocked on:** hardware absence. The build environment has no VITURE Luma
  Ultra and no vendor SDK DLL, so the dossier unknowns **U-01** (3DoF Carina
  pose polling on Windows: does it work, at what rate and latency) and **U-08**
  (3D-mode switching, SBS signalling, refresh rate) cannot be measured from
  here. The ADR-0009 sections "Vendor symbol binding (pending HIL)", "U-01
  (pending HIL)" and "U-08 (pending HIL)" depend on the same run.
- **What was tried:** the `cg-pose-probe` viture mode was exercised as far as
  the machine allows:
  - `cg-pose-probe --source viture` (no DLL and `CG_VITURE_DLL` unset);
  - `--dll <bad path>`;
  - `--dll kernel32.dll` (opens, but the placeholder entry points are absent).
  The no-DLL case exits 2 with `viture_loader: empty DLL path`
  (`InvalidArgument`); the bad-path and `kernel32.dll` cases exit 2 with
  `Unsupported`, naming the path or the first unresolved symbol. The software
  substitutes were run instead: the contract suite's `viture-fake` factory (the
  wrapper over `FakeVitureApi`), the fake/replay probe smoke runs, and the
  soak.
- **Evidence:** the pre-HIL behaviour is recorded in
  [`../perf/s5.md`](../perf/s5.md) (no DLL → exit 2 with the loader's
  `InvalidArgument: viture_loader: empty DLL path`; a bad path → exit 2 with
  `Unsupported` and the path in the message: the documented pre-HIL behaviour)
  and in the Task 4a exit-code matrix (no DLL / bad path / `kernel32.dll` →
  exit 2 with the loader message). The loader tests pin only the error paths
  (`cpp/tests/glasses/viture_loader_tests.cpp`); the vendor export names and
  calling convention are unresolved placeholders
  (`cpp/glasses/src/viture_loader.cpp`).
- **Question:** can the owner attach the Luma Ultra to a Windows host with the
  VITURE SDK and run the probe below, then commit the CSV and console log?
  Specifically: does Carina pose polling work in 3DoF mode on Windows (U-01),
  what are the measured pose rate and latency, and what does the SDK report for
  3D/SBS mode and refresh (U-08)? The run also resolves the vendor symbol
  names the loader must bind.
- **Proposed options:**
  1. **Owner hardware run (expected path).** Attach the glasses and SDK, run
     the exact command below, commit `pose_probe.csv` + `pose_probe.log`; the
     results answer U-01/U-08 and the loader table is rewritten from the
     observed exports.
  2. **3DoF pose fails.** Take the S5 note's fallback decision (choose 6DoF and
     discard position), record it in ADR-0009's U-01 section from the same
     artefacts, and re-run the probe if the mode change needs a retest.
  3. **Defer HIL past S5.** `stage-5-complete` stays withheld and S6 proceeds
     on the recorded ruling; not recommended because the ADR stays `proposed`
     and the real adapter ships on placeholder bindings.
- **Impact if unresolved:** the `stage-5-complete` tag is withheld until the
  HIL log is committed and ADR-0009 answers U-01/U-08. The display-mode
  surface (`IDisplayControl`) and the vendor symbol table stay provisional, and
  real-hardware pose confidence is untested. Everything else is safe to
  continue: the software lanes, the contract suite across `fake` / `replay` /
  `viture-fake`, and the nightly soak lane do not depend on the HIL answer, and
  S6 software may proceed meanwhile (recorded controller ruling).

## What exists

`cg-pose-probe` is built with the tools from `cpp/` (Release shown; the Debug
preset works too, with `cpp\build\windows-msvc\bin\` as the binary directory):

```powershell
# from cpp/
cmake --preset benchmarks
cmake --build --preset benchmarks
```

Exact owner command (from the repository root; creates the output directory
first):

```powershell
New-Item -ItemType Directory -Force docs\notes\s5-hil | Out-Null
.\cpp\build\benchmarks\bin\cg-pose-probe.exe --source viture --dll <path-to-viture-sdk-dll> --seconds 60 --out docs\notes\s5-hil\pose_probe.csv 2>&1 | Tee-Object docs\notes\s5-hil\pose_probe.log
```

`--dll` may be replaced by the `CG_VITURE_DLL` environment variable. Exit 0
means at least one sample was read; exit 2 means the viture library failed to
load (any loader failure, including `InvalidArgument` for an empty path) or the
source reported `Unsupported`/`NotReady`; the message names the failure, the
path or the first unresolved symbol — that message is itself part of the
symbol-name evidence.

## What the owner must attach / install

- VITURE Luma Ultra glasses (connected, charged, firmware as shipped).
- The VITURE SDK for Windows and its vendor runtime/driver installer; the
  `--dll` path points at the SDK's vendor DLL.
- Windows host with the glasses recognised by the vendor runtime.

## What to commit

- `docs/notes/s5-hil/pose_probe.csv` — the probe's replay-compatible recording
  (`host_time_ns,sdk_time_s,px,py,pz,qw,qx,qy,qz,status`); in viture mode
  `sdk_time_s` is the sample's true SDK seconds stamp from
  `VitureHeadPoseSource::LastSdkSeconds()`.
- `docs/notes/s5-hil/pose_probe.log` — the probe's console log (measured rate,
  inter-sample jitter p50/p95/p99, status histogram, poll counts, first/last
  pose, CSV path, any loader warnings).

Do not edit the probe or the loader to make the run succeed; report the exact
loader message instead. The vendor symbol table is rewritten only after the
real exports are known.

## What the results must answer

- **U-01 — does 3DoF Carina pose work on Luma Ultra on Windows?** Exit 0 and a
  non-zero sample count with a populated `stable` histogram answers "yes"; if
  3DoF fails, the log's error path drives the option-2 decision.
- **U-01 — rate.** The log's measured rate and the sample span; expect the
  SDK's advertised poll rate rather than the probe's 1 ms polling cadence.
- **U-01 — latency, via the ClockMapper offset/jitter.** For each CSV row,
  `host_time_ns / 1e9 - sdk_time_s` is the currently estimated
  `ClockMapper` offset; its variation across the run bounds the pose latency
  and its jitter (the probe's p50/p95/p99 inter-sample jitter is the
  wall-pacing counterpart). This is the measured input for the U-01 latency
  note in ADR-0009.
- **U-08 — 3D SBS / refresh behaviour.** What the SDK/display surface reports
  for 3D mode switching, SBS signalling and refresh rate (`GetRefreshHz`),
  including whether a mode change needs a device restart; the owner may extend
  the run with the same probe plus the SDK's display calls if the strict pose
  run does not surface the mode switch.

## ADR-0009 sections the results fill

- **"Vendor symbol binding (pending HIL)"** — actual exported names, calling
  convention and blocking behaviour; the placeholder table and thin adapter are
  rewritten in `cpp/glasses/src/viture_loader.cpp`.
- **"U-01 (pending HIL)"** — measured pose rate, latency and status semantics,
  plus the choose-6DoF-and-discard-position decision if 3DoF pose fails.
- **"U-08 (pending HIL)"** — display mode API, SBS signalling and refresh-rate
  behaviour; `IDisplayControl` is confirmed or corrected from this.

When those sections are filled, ADR-0009 moves from `proposed` to `accepted`
for the S5 software decisions.

## Gate rule

`stage-5-complete` is withheld until the HIL log is committed and ADR-0009
answers U-01 and U-08 (S5 Task 5). Record the resolution in this file, dated,
and link it to the ADR update; archive this file only once that link exists
(`docs/questions/README.md`, process steps 3–4).

## Resolution (2026-10-06)

Answered from a live run (owner laptop, firmware `12.0.01.101_20260605`, SDK
2.4.0, SpaceWalker closed, `VitureXrRuntime` stopped):

- `docs/notes/s5-hil/pose_probe.csv` + `pose_probe.log` are committed (60 s,
  `cg-pose-probe --source viture --display`).
- ADR-0009 amended: **U-01 answered** (3DoF works; 75.5 Hz measured poll rate
  in the 60 s run, 203 Hz in a short run; 99.9% stable; ~0.65 ms median
  latency proxy; the pose callback is the SDK timestamp source at ~67 Hz) and
  **U-08 answered** (set/get display mode works; SBS is mode 0x35; the switch
  is asynchronous, needs a settle, and a rapid third switch can return -3).
  ADR status: **accepted**.
- The run also found and fixed a wrapper defect: the first poll after `start`
  fails with `-3` while the VIO warms up, and the pre-HIL fault policy tore
  the device down on that first error, so no sample was ever published on real
  hardware. The warm-up grace is pinned by
  `VitureFault.TransientPollErrorsInsideTheGraceKeepTheSession`.
- Gate rule satisfied: `stage-5-complete` is eligible once the run's commit is
  green in CI.
- Archive: this file stays the escalation record; the answers live in
  ADR-0009 (amendment 2026-10-06).

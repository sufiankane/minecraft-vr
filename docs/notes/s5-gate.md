# S5 exit-gate evidence (software; HIL outstanding)

- **Date:** 2026-10-02
- **Stage:** S5 (glasses: frozen contracts, wait-free pose slot, fake source,
  VITURE SDK seam, replay source, display control, probe/soak/benchmark)
- **Task:** S5-WI6 / Task 4 (probe tool, soak, benchmark and software evidence),
  plus the Task 5 HIL escalation
- **Branch:** `s5/probe-budgets`
- **Commit under test:** the software-evidence commits on `s5/probe-budgets`
  (parent `778dda4`). The last CI-verified head is `62b2c21`; the TSan lane run
  [36992064235](https://github.com/sufiankane/minecraft-vr/actions/runs/36992064235)
  is green on it, and this later fix/soak commit is covered by the PR rerun.
- **Runner:** `scripts/ci-local.ps1 -SkipUnity` (Unity is untouched by S5)

## 1. Local lanes

Command (from the repository root):

```powershell
powershell -ExecutionPolicy Bypass -File scripts/ci-local.ps1 -SkipUnity
```

Result: `ci-local: ALL LANES PASS` (exit 0).

| Lane | Result | Time |
| --- | --- | --- |
| python-env | PASS | 8.3 s |
| cpp-windows | PASS | 9.7 s |
| dotnet | PASS | 20.3 s |
| python | PASS | 3.4 s |
| depcheck | PASS | 0.3 s |
| unity | SKIP (`-SkipUnity`) | 0 s |

Test evidence from the same run:

- `ctest --preset ci --output-on-failure` → `100% tests passed, 0 tests failed
  out of 2` (`core_math` 0.12 s, `glasses` 5.75 s).
- `dotnet test Cubeglass.sln --configuration Release` → `Failed: 0` with 78
  (`Cubeglass.CoreMath.Tests`), 118 (`Cubeglass.Gameplay.Tests`), 156
  (`Cubeglass.Voxel.Tests`) and 87 (`Cubeglass.Mesh.Tests`) — **439/439
  passed**.
- `python -m ruff check .` → `All checks passed!`; `python -m mypy calib
  depcheck` → `Success: no issues found in 6 source files`; `python -m pytest`
  → `45 passed`.
- `python -m depcheck --root .` and `python -m depcheck licences --root .` →
  exit 0.

## 2. Contract suite (three factories)

`cg_glasses_tests` runs **92 tests from 12 suites**, all passing (`ctest`
entry `glasses`, 5.7 s). The parameterised contract suite is
`GlassesContract.*`: the same ten cases run against the three registered
factories, `fake`, `replay` and `viture-fake` (the VITURE wrapper over
`FakeVitureApi`), so the frozen `IHeadPoseSource` port is pinned for every
implementation:

| Contract case | fake | replay | viture-fake |
| --- | --- | --- | --- |
| `StartIsIdempotent` | PASS | PASS | PASS |
| `StopIsIdempotentAndTotal` | PASS | PASS | PASS |
| `TryGetLatestIsFalseBeforeTheFirstSample` | PASS | PASS | PASS |
| `SequenceAndTimeStrictlyIncrease` | PASS | PASS | PASS |
| `PosesAreFiniteAndUnitQuaternions` | PASS | PASS | PASS |
| `RecenterZeroesYawAndPreservesPitchRoll` | PASS | PASS | PASS |
| `PredictZeroReturnsTheNewestSampleVerbatim` | PASS | PASS | PASS |
| `PredictExtrapolatesInTheSweepDirection` | PASS | PASS | PASS |
| `PredictIsCappedAtHundredMilliseconds` | PASS | PASS | PASS |
| `RestartResumesOrdering` | PASS | PASS | PASS |

Command:

```powershell
.\build\windows-msvc\tests\glasses\cg_glasses_tests.exe --gtest_filter=GlassesContract.* --gtest_brief=1
```

Result: `[==========] 30 tests from 1 test suite ran. (559 ms total)` /
`[  PASSED  ] 30 tests.` The remaining 62 tests cover the `Status`/`Result`
mapping table, `PoseSlot` behaviour and fake script semantics, the VITURE
fault/quiet/loader paths, display control, replay parsing and the thread-safety
stress cases.

## 3. Thread-safety lane (TSan)

`cpp/tests/glasses/thread_safety_tests.cpp` stresses the pose slot (saturated
writer against 8 readers) and `VitureHeadPoseSource` over `FakeVitureApi` (one
producer, four readers, a manual host clock advanced by a feeder thread),
asserting finite unit poses, per-reader non-decreasing sequence and time, zero
torn and zero out-of-order samples, and stop totality. It is built and run
under TSan by the `linux-tsan` preset inside the `cpp-linux-asan` CI job.

CI run
[36992064235](https://github.com/sufiankane/minecraft-vr/actions/runs/36992064235)
is green on `62b2c21`; this later fix/soak commit is covered by the PR rerun.

## 4. Allocation gate

`cpp/tests/glasses/allocation_tests.cpp` overrides the replaceable global
allocation functions in its translation unit and snapshots the counter around
two warmed-up 1M-iteration loops: `PoseSlot::TryRead` on a published slot and
`VitureHeadPoseSource::TryGetLatest` over the test fake (stopped before the
measured region, so no producer thread can allocate concurrently).

| Run | Result |
| --- | --- |
| Probe present (`delete new int(1);` in each measured loop, RED) | both tests failed, each reporting `1000000` allocations over 1000000 iterations |
| Probe removed (GREEN, committed) | both tests passed; delta `0` for both read paths |

Re-run on this tree:

```powershell
.\build\windows-msvc\tests\glasses\cg_glasses_tests.exe --gtest_filter=AllocationTest.* --gtest_brief=1
```

Result: `[==========] 2 tests from 1 test suite ran. (268 ms total)` /
`[  PASSED  ] 2 tests.` The measured read path — the seqlock copy and the
predict-0 checks — allocates zero bytes over 1M reads.

## 5. Wait-free read benchmark

`BM_TryGetLatest` drives a `VitureHeadPoseSource` over the programmable test
fake, waits for one published sample, stops the source, and times
`TryGetLatest(sample, predict=0)`. Release, 10 repetitions, aggregates only:

```powershell
cmake --preset benchmarks
cmake --build --preset benchmarks
.\build\benchmarks\bin\cg_core_math_benchmarks.exe --benchmark_filter=BM_TryGetLatest --benchmark_min_time=0.2s --benchmark_repetitions=10 --benchmark_report_aggregates_only=true
```

| Statistic | Time (ns/op) | CPU (ns/op) |
| --- | ---: | ---: |
| mean | 6.50 | 6.45 |
| median (p50) | 6.46 | 6.28 |
| stddev | 0.309 | 0.339 |
| cv | 4.75 % | 5.25 % |

The read path is unchanged from the `docs/perf/s5.md` baseline (median 6.19 ns,
mean 6.24 ns, cv 1.94 %); the spread is run-to-run noise on the same machine
(AMD Ryzen AI 9 365, MSVC 19.51.36256, CMake/Ninja Release). The read is
wait-free: no lock, no allocation, no blocking, proved separately by the
allocation gate above. No absolute threshold is applied.

## 6. Probe smoke runs (software sources)

`cg-pose-probe` is the S5 HIL instrument; its fake and replay drivers exercise
the same code on a software source. Release smoke on this tree:

```powershell
.\build\benchmarks\bin\cg-pose-probe.exe --source fake --seconds 2 --out $env:TEMP\cg-pose-probe-smoke.csv
.\build\benchmarks\bin\cg-pose-probe.exe --source replay --csv .\cpp\tests\glasses\data\pose_replay.csv --seconds 3
```

```text
source: fake
samples: 181  sample span: 2.002007478 s  measured rate: 89.910 Hz
jitter (|interval - mean|): p50=11.153 us  p95=11.153 us  p99=408.854 us
status: stable=181 unstable=0 lost=0
polls: 181 (failed: 0)
sdk_time_s: host-derived seconds (the fake source carries no SDK stamp)
csv: wrote 181 samples to ...\cg-pose-probe-smoke.csv

source: replay
samples: 200  sample span: 2.211111111 s  measured rate: 90.000 Hz
jitter (|interval - mean|): p50=0.000 us  p95=0.001 us  p99=0.001 us
status: stable=157 unstable=20 lost=23
polls: 200 (failed: 0)  dataset exhausted
sdk_time_s: host-derived seconds (the replay source carries no SDK stamp)
```

The fake jitter is the probe's own 1 ms-timer pacing against the 11.111 ms
sample period; the replay run reproduces the dataset's exact 90.000 Hz and its
scripted status windows. `--source viture` exits 2 with the loader's
`Unsupported` message on this machine (no vendor DLL), the documented pre-HIL
behaviour; the full probe contract is in
[`../perf/s5.md`](../perf/s5.md).

## 7. Soak and nightly lane

`glasses_soak` (no test registration; `cpp/tools/soak/glasses_soak.cpp`) drives
the fake source at `--rate` (default 500 Hz) for `--minutes` (default 30),
producing samples at absolute wall deadlines, while one consumer thread reads
`TryGetLatest(predict=0)` continuously. Every 60 s it prints the elapsed time,
the published/read/fresh/missed counts and the current RSS (`GetProcessMemoryInfo`
working set on Windows, `getrusage` on POSIX); at exit it prints the RSS delta
after the first-minute baseline and exits non-zero when the growth exceeds
1 MiB. Ctrl+C stops the run cleanly through a `SIGINT`/`SIGTERM` flag.

Local smoke, 3 minutes, Release (`--minutes 3`, the full run is the nightly
lane):

```powershell
.\build\benchmarks\bin\glasses_soak.exe --minutes 3
```

```text
glasses_soak: rate=500.000 Hz minutes=3.000 rss budget=1.00 MiB
[soak] t=60.0 s published=30001 read=6148801408 fresh=29992 misses=8873 rss=4.54 MiB
[soak] t=120.0 s published=60001 read=12343248988 fresh=59981 misses=17596 rss=4.55 MiB
[soak] done t=180.0 s published=90001 read=18491474503 fresh=89970 misses=26597 rss=4.55 MiB
[soak] rss baseline (t=60.0 s)=4.54 MiB final=4.55 MiB delta=0.01 MiB (budget 1.00 MiB)
[soak] PASS: RSS growth 0.01 MiB within the 1.00 MiB budget
```

Exit 0. 90,001 samples published and 18.49 billion reads served in 180 s
(≈102M reads/s), 89,970 distinct sequences observed, and a **0.01 MiB** RSS
growth after the first minute, far inside the 1 MiB budget. The 26,597 missed
reads (0.00014 %) are the R39 bounded-retry transient under writer pressure;
the caller keeps its previous frame.

Nightly plan (`.github/workflows/nightly.yml`, job `soak`; runs on the existing
schedule and `workflow_dispatch` triggers):

- `ubuntu-latest`, vcpkg cloned and bootstrapped at the pinned baseline like
  `bench-cpp`, tools configured/built Release through the `linux-ci` preset;
- `glasses_soak --minutes 30` (≈900k samples), 60-minute job timeout, stdout
  and stderr teed to `cpp/soak.log`;
- the log is uploaded as the `soak-log` artefact (`if: always()`, so a failed
  run keeps the partial log), and `set -o pipefail` makes a non-zero soak exit
  fail the job.

The 3-minute smoke is a laptop run; the 30-minute run and its log artefact are
the nightly job's output, not this document's.

## 8. HIL pending (owner)

The hardware-in-the-loop half of S5 is blocked: the build environment has no
VITURE Luma Ultra and no vendor SDK DLL, so `U-01` (3DoF Carina pose rate and
latency on Windows) and `U-08` (3D/SBS switching and refresh behaviour) cannot
be measured from here. The escalation is
[`../questions/S5-HIL.md`](../questions/S5-HIL.md); it records the blocked work
item, the exact probe command, what the owner must attach and commit, and the
ADR-0009 sections the results fill.

Owner command (Release build, from the repository root):

```powershell
.\cpp\build\benchmarks\bin\cg-pose-probe.exe --source viture --dll <path-to-viture-sdk-dll> --seconds 60 --out docs\notes\s5-hil\pose_probe.csv
```

Artefacts to commit: `docs/notes/s5-hil/pose_probe.csv` and the probe console
log (`pose_probe.log`). The `stage-5-complete` tag is withheld until the HIL log
is committed and ADR-0009 answers U-01 and U-08; S6 software may proceed
meanwhile (recorded controller ruling).

## 9. Deferred minors

Known non-blocking items recorded during Task 4; none affects the software
half of the exit gate:

- The VITURE loader's exported symbol names, calling convention and blocking
  behaviour are documented placeholders until the HIL run; loading the real
  library fails `Unsupported` naming the first unresolved symbol, and the table
  is rewritten in the single loader TU from the HIL answers (ADR-0009).
- `VitureHeadPoseSource::LastSdkSeconds()` returns the **newest** stamp, so a
  reader can capture a stamp one sample ahead of the sample it observed; at the
  probe's 1 ms poll against a 90 Hz feed the captured stamp matches the sample
  in practice, and the loop keeps its own `host_time_ns` column for the offset
  analysis.
- The soak's POSIX RSS is the `getrusage` peak (`ru_maxrss`, KiB on Linux and
  bytes on macOS) while Windows reports the live working set; the leak gate
  compares like-to-like on each platform.
- The soak consumer yields every 1024 reads; the read count is a liveness
  record, not a throughput gate.
- `timeBeginPeriod(1)` is process-global on Windows and restored by RAII at
  exit, the same aid the probe uses for sub-millisecond pacing.
- `IDisplayControl` stays provisional until U-08, and `docs/adr/0009` remains
  `proposed` until the HIL answers land.
- `PoseSlot` transient false reads are contract-tolerated (R39); the soak
  quantifies them (26,597 over 18.49B reads) and the caller keeps its previous
  frame.
- No nightly regression thresholds yet: the soak lane is a leak gate only.

## 10. Gates added

- `cpp/tools/soak/glasses_soak.cpp` + `cpp/tools/CMakeLists.txt`: the soak
  target (not registered with ctest), `winmm`/`psapi` on Windows.
- `.github/workflows/nightly.yml`: the `soak` job above; the existing
  `bench-cpp`, `bench-dotnet` and `mutation` jobs are untouched.
- `docs/questions/S5-HIL.md`: the Task 5 escalation record.

## 11. CI verification

CI run
[36992064235](https://github.com/sufiankane/minecraft-vr/actions/runs/36992064235)
is green on `62b2c21` (TSan, ASan, coverage, Windows, dotnet, python,
depcheck); this later fix/soak commit is covered by the PR rerun.

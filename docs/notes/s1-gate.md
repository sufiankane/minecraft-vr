# S1 exit-gate evidence

- **Date:** 2026-10-01
- **Stage:** S1 (core math: conventions, transforms, clocks)
- **Task:** S1-WI2g / Task 10
- **Branch:** `s1/clock-budgets` (short-lived; not pushed by this task, not tagged —
  the controller performs the remote steps and the `stage-1-complete` tag)
- **Commit under test:** `4039bac fix(core-math): keep the allocation sink
  GCC-clean` (current branch head; includes the coverage-floor/evidence commit
  `c18251b` and the GCC allocation-sink fix)
- **Runner:** `scripts/ci-local.ps1 -SkipUnity` (Unity is untouched by S1)

## 1. Local lanes

Command (from the repository root):

```powershell
powershell -ExecutionPolicy Bypass -File scripts/ci-local.ps1 -SkipUnity
```

Result: `ci-local: ALL LANES PASS` (exit 0).

| Lane | Result | Time |
| --- | --- | --- |
| python-env | PASS | 7.5 s |
| cpp-windows | PASS | 6.3 s |
| dotnet | PASS | 4.2 s |
| python | PASS | 2.7 s |
| depcheck | PASS | 0.3 s |
| unity | SKIP (`-SkipUnity`) | 0 s |

Test evidence from the same run:

- `ctest --preset ci` → `100% tests passed, 0 tests failed out of 1`
  (`core_math`; the golden-fixture, property, allocation and unit cases all live
  in that binary).
- `dotnet test Cubeglass.sln --configuration Release` → `Passed! Failed: 0,
  Passed: 68, Skipped: 0` (`Cubeglass.CoreMath.Tests`, net10.0).
- `python -m ruff check .` → `All checks passed!`; `python -m mypy calib
  depcheck` → `Success: no issues found in 6 source files`; `python -m pytest` →
  `40 passed`.
- `python -m depcheck --root .` and `python -m depcheck licences --root .` →
  exit 0.

## 2. Golden fixture

`contracts/golden/transforms.json` contains **28 cases** (`cases` array length
28, schema 1, tolerance `1e-6`). Both harnesses walk every case and require
every dispatched op to be exercised:

- C++: `GoldenFixture.AllCasesMatchFixture` (`cpp/tests/core-math/golden_test.cpp`)
  — green inside the `core_math` ctest binary.
- C#: `GoldenFixtureTests.AllCasesMatchFixture`
  (`dotnet/tests/CoreMath.Tests/GoldenFixtureTests.cs`) — green, part of the
  68/68 dotnet tests above.

Both harnesses are green against the same 28-case fixture.

## 3. Coverage

### C# — `Cubeglass.CoreMath`

Commands (first from `dotnet/`, second from the repository root):

```powershell
dotnet test Cubeglass.sln --configuration Release --collect:"XPlat Code Coverage" --results-directory ./coverage
$report = (Get-ChildItem -Recurse -Filter coverage.cobertura.xml coverage | Select-Object -First 1).FullName
python\.venv\Scripts\python.exe -m depcheck coverage --report $report --module Cubeglass.CoreMath --floor 95
```

- Report: `dotnet/coverage/ae4cb72a-2fee-4b5c-b8a2-4ac28bdba1e4/coverage.cobertura.xml`
  (the result-directory GUID varies per run; the workflow locates it with
  `find coverage -name 'coverage.cobertura.xml' | head -n 1`).
- Result: `coverage PASS: module 'Cubeglass.CoreMath' observed 95.76%
  (158/165 lines); floor 95%`.
- The repository-root `python` on this machine has no `depcheck` module; the
  invocation above uses the `python/.venv` interpreter in which
  `pip install -e python` is already applied. CI installs it globally and uses
  `python -m depcheck` unchanged.
- No tests were added for this task: the existing suite already clears the
  raised floor.

### C++ — `core-math`

C++ coverage is measured only by `gcovr` in the `cpp-linux-asan` CI job (the
Linux/gcovr pipeline cannot run on this Windows workstation, and no local
substitute number is claimed).

C++ core-math coverage: verified in CI on `4039bac` — the `cpp-linux-asan`
coverage step reports `coverage PASS: module 'core-math' observed 99.19%
(122/123 lines); floor 95%`.

## 4. Allocation gates

Both gates snapshot an allocation counter around a warmed-up 100k-iteration
loop exercising `Quat::Rotate`, `Slerp`, `Pose` `Compose`/`Inverse`,
`ClockMapper::AddSample`/`Map` and `PoseFromSdk`. Counter snapshots, warm-up and
test-framework assertions sit outside the measured loop. Full details:
[`docs/perf/s1.md`](../perf/s1.md).

| Language | Gate | Probe present (RED) | Probe removed (GREEN) |
| --- | --- | --- | --- |
| C++ | `cpp/tests/core-math/allocation_test.cpp` (overridden global `operator new` family, `std::atomic<std::size_t>` counter) | 5 tests failed; each reported `100000` allocations over 100000 iterations (one per `delete new int(1);` probe) | 5 tests passed; delta `0` for every loop |
| C# | `dotnet/tests/CoreMath.Tests/AllocationTests.cs` (`GC.GetAllocatedBytesForCurrentThread()`) | failed: `Core-math operations allocated 2400000 bytes over 100000 iterations` (24 bytes per `new object()` probe) | passed: delta `0` bytes |

Current-run confirmation: `cg_core_math_tests.exe --gtest_filter=AllocationTest.*`
→ `[  PASSED  ] 5 tests`; the dotnet allocation tests are part of the 68/68
green suite. Allocation deltas are **0** in both languages.

## 5. Benchmarks (local baseline)

From [`docs/perf/s1.md`](../perf/s1.md); C++ Release, `--benchmark_min_time=0.2s`,
MSVC 19.51.36256.0, AMD Ryzen AI 9 365. Wall-clock per operation including the
`DoNotOptimize` sink; not comparable across machines and no absolute threshold
is applied.

| Benchmark | Operation | Time (ns/op) | CPU (ns/op) |
| --- | --- | ---: | ---: |
| `BM_QuatRotate` | `Quat::Rotate(Vec3)` | 35.9 | 34.0 |
| `BM_QuatSlerp` | `Slerp(a, b, t)` | 23.5 | 23.4 |
| `BM_PoseCompose` | `Compose(parent, child)` | 45.8 | 47.1 |
| `BM_ClockMap` | `ClockMapper::Map(sdk)` with a full window | 64.2 | 66.3 |
| `BM_PoseFromSdk` | `PoseFromSdk(float[7])` | 5.59 | 5.58 |

`BM_AbiVersion` (the S0 smoke benchmark) reports 2.21 ns/op.

## 6. Coverage floors

This task raises both core-math coverage gates in `.github/workflows/ci.yml`
from 90 to 95:

- `cpp-linux-asan` → `python -m depcheck coverage --report
  coverage.cobertura.xml --module core-math --floor 95`;
- `dotnet` → `python -m depcheck coverage --report "$report" --module
  Cubeglass.CoreMath --floor 95`.

No other job, floor or command changed. `CONTRIBUTING.md` is unchanged (no
documented command changed).

## 7. CI verification

All six required checks are green on PR #10 head `4039bac`:
[run 36922538193](https://github.com/sufiankane/minecraft-vr/actions/runs/36922538193).
The `cpp-linux-asan` coverage step reports `coverage PASS: module 'core-math'
observed 99.19% (122/123 lines); floor 95%`; the C# result above (95.76%,
158/165 lines, floor 95) is the local coverlet measurement recorded in
section 3.

The docs-only fix commit that follows `4039bac` — this evidence update — is
CI-verified by the PR's rerun after that commit.

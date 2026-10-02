# S3 exit-gate evidence

- **Date:** 2026-10-02
- **Stage:** S3 (meshing: contracts, reference and greedy meshers, per-vertex AO,
  border seams, pooled buffers, budget)
- **Task:** S3-WI5 / Task 4 (pooling, benchmarks, budget and exit gate)
- **Branch:** `s3/budgets-gate` (PR pending)
- **Commit under test:** `0741f9c` (S3 Task 3) plus the Task 4 working tree;
  this evidence ships in the final Task 4 commit
  `ci: enforce mesh budgets and record S3 evidence`.
- **Runner:** `scripts/ci-local.ps1 -SkipUnity` (Unity is untouched by S3)

## 1. Local lanes

Command (from the repository root):

```powershell
powershell -ExecutionPolicy Bypass -File scripts/ci-local.ps1 -SkipUnity
```

Result: `ci-local: ALL LANES PASS` (exit 0).

| Lane | Result | Time |
| --- | --- | --- |
| python-env | PASS | 5.6 s |
| cpp-windows | PASS | 4.0 s |
| dotnet | PASS | 12.8 s |
| python | PASS | 1.4 s |
| depcheck | PASS | 0.3 s |
| unity | SKIP (`-SkipUnity`) | 0 s |

Test evidence from the same run:

- `dotnet test Cubeglass.sln --configuration Release` → `Failed: 0, Passed: 78`
  (`Cubeglass.CoreMath.Tests`), `Failed: 0, Passed: 156`
  (`Cubeglass.Voxel.Tests`) and `Failed: 0, Passed: 84`
  (`Cubeglass.Mesh.Tests`, 80 pre-existing plus the 4 new allocation tests).
- `ctest --preset ci` → `100% tests passed, 0 tests failed out of 1`.
- `python -m ruff check .` → `All checks passed!`; `python -m mypy calib
  depcheck` → `Success: no issues found in 6 source files`; `python -m pytest`
  → `45 passed`.
- `python -m depcheck --root .` and `python -m depcheck licences --root .` →
  exit 0.

## 2. Differential and golden suites

Both suites ran unchanged on the pooled implementation, which is the
byte-identity evidence: pooling changes ownership of the arrays, not a single
emitted byte.

- `DifferentialTests.GreedyCoversExactlyTheReferenceExposedFacesOnTwoThousandSeededChunks`:
  2,000 seeded random chunks (250 with neighbour snapshots, 1,978 with faces),
  **10,952,194 exposed faces** compared against the independent oracle; the
  greedy and reference meshes match the oracle exactly (no internal face
  between opaques, every exposed face exactly once). Recorded by a temporary
  probe print in the green run.
- `GoldenMeshTests` FNV-1a 64 hashes, all unchanged from Task 3:
  solid `0xDDFB9E0705773F21`, empty `0xA8C7F832281A39C5`,
  terrain seed 42 `0xBEC043606861A156`, checkerboard `0x974E1E84E4D580C6`,
  single corner `0x16F1B6539053D181`.

## 3. Allocation gate (probe-first)

`dotnet/tests/Mesh.Tests/AllocationTests.cs` warms the pool with 100
build/release cycles, then snapshots `GC.GetAllocatedBytesForCurrentThread()`
around 1,000 `Build`+`Release` cycles; every result is consumed by a checked
sink and no NUnit call sits inside a measured region. Measured loops cover the
greedy terrain chunk, the greedy checkerboard chunk (the growth-path worst
case), the reference culled terrain chunk, and a solid chunk after a build that
threw on an unregistered block id (the per-buffer return path).

Probe-first sequence:

| Run | Gate | Result |
| --- | --- | --- |
| Pre-pooling (RED, temporary test) | greedy terrain | FAIL: `allocated 1544736000 bytes over 1000 build/release cycles` |
| Pre-pooling (RED, temporary test) | greedy checkerboard | FAIL: `allocated 3517137912 bytes over 1000 cycles` |
| Pre-pooling (RED, temporary test) | culled terrain | FAIL: `allocated 3537261448 bytes over 1000 cycles` |
| Pre-pooling (RED, temporary test) | solid after throwing build | FAIL: `allocated 44552000 bytes over 1000 cycles` |
| Probe in the measured loop (`byte[16]` + `GC.KeepAlive`) | all four | FAIL: `allocated 40000 bytes over 1000 cycles` (40 bytes/cycle: 16-byte array + 24-byte header) |
| Probe removed (GREEN, committed) | all four | PASS: delta `0` bytes |

Command:

```powershell
# from dotnet/
dotnet test tests/Mesh.Tests/Cubeglass.Mesh.Tests.csproj --configuration Release --filter FullyQualifiedName~AllocationTests
```

The probe run is red, so the gate cannot silently pass; the committed tests
carry no probe and report delta 0.

## 4. Benchmarks and budget

The ADR-0007 budget is p95 ≤ 2.0 ms per full 16³ chunk for a solid or terrain
chunk built with `GreedyMesher`. The `p95` mode of
`dotnet/benchmarks/Mesh.Benchmarks` runs 100 warm-up builds then 2,000
build/release cycles per shape (Release included) and always exits 0; the
budget is asserted here, not by a timing assertion in `dotnet test`.

Command (from `dotnet/`):

```powershell
dotnet run --configuration Release --project benchmarks/Mesh.Benchmarks/Mesh.Benchmarks.csproj -- p95
```

Machine: AMD Ryzen AI 9 365 w/ Radeon 880M (10 physical / 20 logical cores,
2.00 GHz), 23 GiB RAM, Windows 11 10.0.26200.9550 (25H2), .NET SDK 10.0.401
(runtime 10.0.12, X64 RyuJIT).

| Shape | Quads | p50 (ms) | p95 (ms) | p99 (ms) | Budget (p95) |
| --- | ---: | ---: | ---: | ---: | --- |
| `GreedySolidChunk` | 6 | 0.279 | 0.442 | 0.486 | ≤ 2.0 ms — PASS |
| `GreedyTerrainChunk` | 240 | 0.245 | 0.405 | 0.440 | ≤ 2.0 ms — PASS |
| `GreedyCheckerboardChunk` | 5,568 | 0.990 | 1.363 | 1.585 | recorded (R22 worst case) |

BenchmarkDotNet (`SimpleJob`, one warm-up, three iterations, `[MemoryDiagnoser]`)
reports 263.0 µs, 240.2 µs and 813.6 µs mean with `Allocated = 0 B` for the
three shapes. The full table is in [`../perf/s3.md`](../perf/s3.md).

## 5. Coverage

Command (from `dotnet/`, fresh `coverage/mesh` directory):

```powershell
dotnet test tests/Mesh.Tests/Cubeglass.Mesh.Tests.csproj --configuration Release --collect:"XPlat Code Coverage" --results-directory ./coverage/mesh
```

Command (from the repository root):

```powershell
python\.venv\Scripts\python.exe -m depcheck coverage --report dotnet\coverage\mesh\cf379c5a-aa17-4fca-a8c6-14fbc337ff19\coverage.cobertura.xml --module Cubeglass.Mesh --floor 90
```

Result: `coverage PASS: module 'Cubeglass.Mesh' observed 96.89% (499/515 lines);
floor 90%`. No extra tests were needed; the floor is enforced by the new
`dotnet` CI step with the fail-loud `find coverage/mesh` guard.

## 6. Gates added

- `.github/workflows/ci.yml`: the `dotnet` job gains
  `Enforce Mesh coverage floor` (`Cubeglass.Mesh --floor 90`) using the
  `coverage/mesh` report with the same fail-loud selector as CoreMath/Voxel;
  the solution build and the 95/90 floors are unchanged.
- `.github/workflows/nightly.yml`: `bench-dotnet` runs
  `benchmarks/Mesh.Benchmarks/Mesh.Benchmarks.csproj` next to CoreMath and
  Voxel, so the ADR-0007 harness is exercised nightly.
- `docs/ci.md`: coverage table gains the `Cubeglass.Mesh` 90% row and the
  nightly paragraph names the Mesh benchmark project.
- `docs/perf/s3.md`: machine, commands, p50/p95/p99 table and the budget
  verdict.

## 7. Deferred minors

Known non-blocking items recorded during Task 4; none affects the exit gate:

- `MeshBufferPool` is not thread-safe; parallel workers need one pool per
  meshing thread (the shared pool serves the single-threaded tests and one
  worker at a time).
- Using a `MeshData` after `Release` is undefined and the pooled wrapper may
  already be re-issued; only the double-release guard is contractual.
- No nightly regression thresholds yet: ADR-0007 defers them until the hot path
  stabilises.
- ADR-0007's `-Y -> Top` atlas mapping stays provisional until bottom-face art
  exists.
- The benchmark `SimpleJob` remains deliberately short (one warm-up, three
  iterations), matching the S2 lane.

## 8. CI verification

CI verification: pending

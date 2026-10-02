# S3 exit-gate evidence

- **Date:** 2026-10-02
- **Stage:** S3 (meshing: contracts, reference and greedy meshers, per-vertex AO,
  border seams, pooled buffers, budget)
- **Task:** S3-WI5 / Task 4 (pooling, benchmarks, budget and exit gate), plus the
  final-review fix wave (injected pools, shared build scaffolding)
- **Branch:** `s3/budgets-gate` (PR #19)
- **Commit under test:** `19445ad` (last CI-verified S3 head). The fix-wave
  commits on top change only pool ownership plumbing and build scaffolding, and
  the golden and differential suites prove the emitted bytes are unchanged. The
  fresh coverage and p95 numbers below were re-measured on the fix-wave tree.
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
| cpp-windows | PASS | 4.1 s |
| dotnet | PASS | 13.2 s |
| python | PASS | 1.5 s |
| depcheck | PASS | 0.3 s |
| unity | SKIP (`-SkipUnity`) | 0 s |

Test evidence from the same run:

- `dotnet test Cubeglass.sln --configuration Release` → `Failed: 0, Passed: 78`
  (`Cubeglass.CoreMath.Tests`), `Failed: 0, Passed: 156`
  (`Cubeglass.Voxel.Tests`) and `Failed: 0, Passed: 87`
  (`Cubeglass.Mesh.Tests`, 84 pre-existing plus the 3 new fix-wave tests:
  injected-pool rent/return, null-argument rejection and the
  negative-coordinate centre chunk).
- `ctest --preset ci` → `100% tests passed, 0 tests failed out of 1`.
- `python -m ruff check .` → `All checks passed!`; `python -m mypy calib
  depcheck` → `Success: no issues found in 6 source files`; `python -m pytest`
  → `45 passed`.
- `python -m depcheck --root .` and `python -m depcheck licences --root .` →
  exit 0.

## 2. Differential and golden suites

Both suites ran unchanged on the pooled implementation, and again on the
fix-wave pool-injection and scaffolding refactor, which is the byte-identity
evidence: pooling and the shared build flow change ownership of the arrays,
not a single emitted byte.

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
case) and the reference culled terrain chunk.

The fourth test, `ThrowingBuildReturnsRentedBuffersToThePool`, warms the pool
**first**, then builds a chunk holding an unregistered block id so both meshers
throw from mid-build. It checks `MeshBufferPool.OutstandingBuffers` (rents minus
returns) across the throw and then measures a **single** following
build/release cycle. The counter is the deterministic detector — a simulated
one-array leak fails it — while the single-cycle allocation snapshot alone can
be masked by a larger pooled array left by another test; the single cycle then
also proves the pool still serves the next build with zero allocations. A
warm-up after the throw would refill a leak and hide it, which is why the order
is warm-up, throw, measure.

Probe-first sequence:

| Run | Gate | Result |
| --- | --- | --- |
| Pre-pooling (RED, temporary test) | greedy terrain | FAIL: `allocated 1544736000 bytes over 1000 build/release cycles` |
| Pre-pooling (RED, temporary test) | greedy checkerboard | FAIL: `allocated 3517137912 bytes over 1000 cycles` |
| Pre-pooling (RED, temporary test) | culled terrain | FAIL: `allocated 3537261448 bytes over 1000 cycles` |
| Leak probe: `positions.Return()` temporarily removed from the greedy catch | throw path | FAIL: `the greedy throw path leaked 1 rented buffer(s)` |
| Leak probe: same removal from the culled catch | throw path | FAIL: `the culled throw path leaked 1 rented buffer(s)` |
| Probe in the measured loop (`byte[16]` + `GC.KeepAlive`) | warm-up gates | FAIL: `allocated 40000 bytes over 1000 cycles` (40 bytes/cycle: 16-byte array + 24-byte header) |
| All probes removed (GREEN, committed) | all four | PASS: delta `0` bytes, `0` leaked buffers |

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
| `GreedySolidChunk` | 6 | 0.287 | 0.438 | 0.498 | ≤ 2.0 ms — PASS |
| `GreedyTerrainChunk` | 240 | 0.239 | 0.280 | 0.404 | ≤ 2.0 ms — PASS |
| `GreedyCheckerboardChunk` | 5,568 | 0.971 | 1.115 | 1.579 | recorded (R22 worst case) |

The numbers above were re-measured on the fix-wave tree; every case clears the
budget. BenchmarkDotNet (`SimpleJob`, one warm-up, three iterations,
`[MemoryDiagnoser]`) recorded 263.0 µs, 240.2 µs and 813.6 µs mean with
`Allocated = 0 B` for the three shapes at `19445ad` (not re-run in the fix
wave). The full table is in [`../perf/s3.md`](../perf/s3.md).

## 5. Coverage

Command (from `dotnet/`, fresh `coverage/mesh` directory):

```powershell
dotnet test tests/Mesh.Tests/Cubeglass.Mesh.Tests.csproj --configuration Release --collect:"XPlat Code Coverage" --results-directory ./coverage/mesh
```

Command (from the repository root):

```powershell
python\.venv\Scripts\python.exe -m depcheck coverage --report dotnet\coverage\mesh\b3fbb4e3-019a-4428-92f9-fb7db87a2d63\coverage.cobertura.xml --module Cubeglass.Mesh --floor 90
```

Result: `coverage PASS: module 'Cubeglass.Mesh' observed 97.26% (462/475 lines);
floor 90%` (up from 96.94% at `19445ad`; the scaffolding extraction removed
duplicated lines and the new constructor/helper lines are covered). No extra
tests were needed for the floor; the floor is enforced by the `dotnet` CI step
with the fail-loud `find coverage/mesh` guard.

## 6. Gates added

- `.github/workflows/ci.yml`: the `dotnet` job gains
  `Enforce Mesh coverage floor` (`Cubeglass.Mesh --floor 90`) using the
  `coverage/mesh` report with the same fail-loud selector as CoreMath/Voxel;
  the solution build and the 95/90 floors are unchanged.
- `.github/workflows/nightly.yml`: `bench-dotnet` runs
  `benchmarks/Mesh.Benchmarks/Mesh.Benchmarks.csproj` (BenchmarkDotNet) next to
  CoreMath and Voxel. The ADR-0007 `p95` harness (`-- p95`) is a local/release
  gate recorded in `docs/perf/s3.md`; the nightly BDN run does not invoke it.
- `docs/ci.md`: coverage table gains the `Cubeglass.Mesh` 90% row and the
  nightly paragraph names the Mesh benchmark project.
- `docs/perf/s3.md`: machine, commands, p50/p95/p99 table and the budget
  verdict.

## 7. Deferred minors

Known non-blocking items recorded during Task 4; none affects the exit gate:

- `MeshBufferPool` is not thread-safe; parallel workers need one pool per
  meshing thread and now inject it through `CulledMesher(AtlasLayout,
  MeshBufferPool)` / `GreedyMesher(AtlasLayout, MeshBufferPool)`; `Shared`
  serves the single-threaded tests and one worker at a time (ADR-0007).
- Using a `MeshData` after `Release` is undefined and the pooled wrapper may
  already be re-issued; only the double-release guard is contractual.
- No nightly regression thresholds yet: ADR-0007 defers them until the hot path
  stabilises.
- ADR-0007's `-Y -> Top` atlas mapping stays provisional until bottom-face art
  exists.
- The benchmark `SimpleJob` remains deliberately short (one warm-up, three
  iterations), matching the S2 lane.

## 8. CI verification

PR #19 run
[36955165377](https://github.com/sufiankane/minecraft-vr/actions/runs/36955165377)
green on `19445ad`; this fix-wave commit is covered by the PR rerun.

# S2 exit-gate evidence

- **Date:** 2026-10-02
- **Stage:** S2 (voxel core: coordinates, chunk/world model, DDA raycaster, terrain generation, delta persistence, collision)
- **Task:** S2-WI7 / Task 5 (budgets, mutation score and exit gate)
- **Branch:** `s2/budgets-gate` (not pushed by this task; the controller runs the
  PR checks and completes the CI line below)
- **Commit under test:** `d6f93704e3627f891e8d29be0f8a5f608d0cbf35`
  (`ci(voxel): enforce 90 percent coverage and add stryker lane`), preceded by
  `a16de81 test(voxel): gate cast get and apply hot paths at zero allocations`
  and `f6f7a09 build(voxel): add BenchmarkDotNet project for chunk generation
  and raycast`
- **Runner:** `scripts/ci-local.ps1 -SkipUnity` (Unity is untouched by S2)

## 1. Local lanes

Command (from the repository root):

```powershell
powershell -ExecutionPolicy Bypass -File scripts/ci-local.ps1 -SkipUnity
```

Result: `ci-local: ALL LANES PASS` (exit 0).

| Lane | Result | Time |
| --- | --- | --- |
| python-env | PASS | 6.2 s |
| cpp-windows | PASS | 5.2 s |
| dotnet | PASS | 7.5 s |
| python | PASS | 1.9 s |
| depcheck | PASS | 0.3 s |
| unity | SKIP (`-SkipUnity`) | 0 s |

Test evidence from the same run:

- `dotnet test Cubeglass.sln --configuration Release` → `Passed! Failed: 0,
  Passed: 68` (`Cubeglass.CoreMath.Tests`) and `Failed: 0, Passed: 126`
  (`Cubeglass.Voxel.Tests`, includes the two new allocation gates).
- `ctest --preset ci` → `100% tests passed, 0 tests failed out of 1`.
- `python -m ruff check .` → `All checks passed!`; `python -m mypy calib
  depcheck` → `Success: no issues found in 6 source files`; `python -m pytest`
  → `43 passed`.
- `python -m depcheck --root .` and `python -m depcheck licences --root .` →
  exit 0.

## 2. Coverage

Commands (first from `dotnet/`, then from the repository root):

```powershell
dotnet test Cubeglass.sln --configuration Release --collect:"XPlat Code Coverage" --results-directory ./coverage
python\.venv\Scripts\python.exe -m depcheck coverage --report <selected> --module Cubeglass.Voxel --floor 90
python\.venv\Scripts\python.exe -m depcheck coverage --report <selected> --module Cubeglass.CoreMath --floor 95
```

Two coverlet reports are produced, one per test project. The report with real
`Cubeglass.Voxel` coverage also contains a mostly uncovered
`Cubeglass.CoreMath` package, so both .NET floors select their report by
content instead of "sole package":

- CoreMath: the report whose `<source>` root is `src/CoreMath/` — observed
  `95.76%` (158/165 lines), floor 95 — PASS.
- Voxel: the report containing the `Cubeglass.Voxel` package — observed
  `92.68%` (418/451 lines), floor 90 — PASS, no additional tests were needed.

Note: coverlet 10.1.0 writes the report source root as `dotnet/src/` and class
filenames as `Voxel/...`/`CoreMath/...` (no `src/` prefix), so the workflow
selectors match the `<source>` element for CoreMath and the package element for
Voxel; both fail loudly when no report matches (see `.github/workflows/ci.yml`).

## 3. Mutation score

Tool: `dotnet-stryker` **5.0.0**, pinned in
`dotnet/.config/dotnet-tools.json` (installed with `dotnet tool restore`).

Config: `dotnet/stryker-config.json` — project `src/Voxel/Cubeglass.Voxel.csproj`,
test project `tests/Voxel.Tests/Cubeglass.Voxel.Tests.csproj`, mutate `**/*.cs`
excluding `**/AssemblyMarker.cs` and `**/IsExternalInit.cs`, thresholds
break 70 / low 70 / high 90, reporters `json` and `cleartext`,
`ignore-methods` empty. (Stryker resolves `mutate` globs relative to the
project directory, so the brief's `src/Voxel/**/*.cs` form matched nothing;
the project-relative form is the working equivalent.)

Command (from `dotnet/`):

```powershell
dotnet tool restore
dotnet stryker --config-file stryker-config.json
```

Result: **mutation score 83.05%** (break threshold 70, exit 0), 3 min 46 s.

| Status | Mutants |
| --- | ---: |
| Created | 751 |
| Killed | 472 |
| Survived | 63 |
| Timeout | 13 |
| NoCoverage | 36 |
| CompileError (excluded) | 55 |
| Ignored (block already covered filter) | 112 |
| Tested | 548 |

Surviving-mutant summary (63, by file): `DdaRaycaster.cs` 15,
`ChunkDeltaCodec.cs` 11, `Aabb.cs` 8, `BlockRegistry.cs` 7, `Noise.cs` 5,
`VoxelCollision.cs` 5, `TerrainGenerator.cs` 4, `Chunk.cs` 3, `ChunkDelta.cs` 3,
`ChunkMath.cs` 1, `ChunkSnapshot.cs` 1. Most are string mutations in
diagnostic/exception messages (`BlockRegistry` load errors, `Chunk` bounds,
`Aabb`, `ChunkDeltaCodec`) and equality/logical/statement-removal boundary
mutations in `DdaRaycaster` (zero-distance/face-origin tie handling) and
`ChunkDeltaCodec` (run-length packing). The score clears the 70 exit gate, so no
extra tests were added for survivors; they are candidates for S3 hardening.

## 4. Allocation gates

`dotnet/tests/Voxel.Tests/AllocationTests.cs` snapshots
`GC.GetAllocatedBytesForCurrentThread()` around warmed-up (`1_000` iterations)
measured loops of `100_000` iterations on a loaded `TerrainGenerator` chunk at
the origin (seed 42): `IRaycaster.Cast` (100k rays) and `IWorld.Get` +
`IWorld.Apply` (100k reads plus alternating Applied edits). Counter snapshots,
warm-up and NUnit assertions sit outside the measured regions.

| Gate | Probe present (`new object()` in loop) | Probe removed |
| --- | --- | --- |
| `DdaRaycaster.Cast` | FAIL: `allocated 215976 bytes over 100000 iterations` | PASS: delta `0` bytes |
| `IWorld.Get`/`Apply` | FAIL: `allocated 215976 bytes over 100000 iterations` | PASS: delta `0` bytes |

The probe (temporary `_ = new object();` inside both loops) is caught by the
gate; it was then removed and both tests pass with `allocated == 0` (the probe
bytes are below `24 * 100000` because the .NET 10 JIT elides part of the
non-escaping probe allocation, but the gate still detects it — a red run is not
a silent pass). Both gates ran green inside the `dotnet` lane above.

## 5. Benchmarks (local baseline)

From `dotnet/`, Release, BenchmarkDotNet 0.15.8, Windows 11 (10.0.26200),
SimpleJob: 1 warm-up, 3 iterations. `Raycast100k` uses
`OperationsPerInvoke = 100_000`, so its time is per ray.

```powershell
dotnet run --configuration Release --project benchmarks/Voxel.Benchmarks/Voxel.Benchmarks.csproj
```

| Benchmark | Operation | Mean | Allocated |
| --- | --- | ---: | ---: |
| `ChunkGeneration` | one full `TerrainGenerator` chunk | 15,687.7 ns/chunk | 8,256 B |
| `Raycast100k` | one ray of 100k rays across a loaded chunk | 104.1 ns/ray | 0 B |

The benchmark project is in `dotnet/Cubeglass.sln`; the nightly `bench-dotnet`
job runs it next to `CoreMath.Benchmarks` and uploads the artefacts.

## 6. Golden hash

`0x1EF678D6ADDA1CEC` — `TerrainGeneratorTests.GoldenHashOfTheOriginChunkForSeed42`
pins the hash of `TerrainGenerator.Generate((0,0,0), 42)`; green in the
126/126 `Cubeglass.Voxel.Tests` run above.

## 7. Gates added

- `.github/workflows/ci.yml`: `dotnet` job enforces `Cubeglass.Voxel` line
  coverage ≥ 90 (new step) and selects both .NET reports by content with a
  fail-loud guard (CoreMath keeps floor 95).
- `.github/workflows/nightly.yml`: `bench-dotnet` runs both benchmark projects;
  the nightly workflow additionally gains a `mutation` job that runs
  `dotnet tool restore` + `dotnet stryker --config-file stryker-config.json`
  from `dotnet/`.
- `docs/ci.md`: coverage table gains `Cubeglass.Voxel` at 90 (calib/depcheck 90,
  core-math/CoreMath 95 unchanged).

CI verification: pending

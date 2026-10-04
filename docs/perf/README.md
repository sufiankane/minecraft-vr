# Performance numbers

Measured performance numbers are **not** committed to the repository, except
the regression baseline `nightly-baseline.json` (see below). The nightly
workflow produces the numbers, enforces the budgets and uploads the raw results
as GitHub Actions artefacts. This directory holds the human-authored notes,
methodology and budget tables.

## What is measured where

- **CI gates** (`ci.yml`) enforce behaviour and coverage only. They must not be
  used as a source of performance numbers.
- **Nightly benchmarks** (`nightly.yml`) produce the raw numbers and enforce the
  budgets:
  - `bench-cpp` builds the Google Benchmark target `cg_core_math_benchmarks`
    from the `benchmarks` CMake preset and uploads `benchmark_results.json`.
  - `bench-dotnet` runs the BenchmarkDotNet console projects
    `dotnet/benchmarks/CoreMath.Benchmarks`,
    `dotnet/benchmarks/Voxel.Benchmarks` and
    `dotnet/benchmarks/Mesh.Benchmarks` (each exports a
    `*-report.json` via BenchmarkDotNet's JSON exporter), uploads
    `BenchmarkDotNet.Artifacts/`, and then runs the mesh p95 budget harness.
- The `dotnet` solution build includes the benchmark project, so a broken
  benchmark does not only fail the nightly lane: it also fails the required
  `dotnet` CI gate.

## CoreMath coverage floor

The S0 stub caveat no longer applies: `Cubeglass.CoreMath` has contained real
instrumentable code since S1, so its floor is enforced at 95 percent from the
required `dotnet` job's coverage report (`dotnet test … --collect:"XPlat Code
Coverage"`, then `python -m depcheck coverage --module Cubeglass.CoreMath
--floor 95`). A matched module with zero coverable lines would still print a
`WARNING` and exit 0, but the current `Cubeglass.CoreMath` does not take that
path.

## Regression budgets and enforcement

ADR-0007 records the release/local budget: meshing p95 ≤ 2.0 ms per full 16³
chunk on the dev/release machine, measured by the `p95` mode of
`Mesh.Benchmarks` and recorded in [`s3.md`](s3.md).

The nightly lane enforces performance, it no longer only records it:

- `bench-dotnet` runs
  `dotnet run --configuration Release --project benchmarks/Mesh.Benchmarks/Mesh.Benchmarks.csproj -- p95 --budget-ms 8`.
  The harness exits non-zero when any shape's p95 exceeds the budget and names
  the offending shape. **8 ms is the CI shared-runner budget** (GitHub-hosted
  runners are noisier than the dev/release machine); the **ADR-0007 2.0 ms
  budget remains the release/local budget**, checked by running the same command
  with `--budget-ms 2`.
- `bench-compare` runs `python -m depcheck benchregress` over the benchmark JSON
  from both jobs and `docs/perf/nightly-baseline.json`. Any benchmark present in
  both the run and the baseline that regresses by more than **10 percent** fails
  the job and names the offender (`<name>: <baseline> ns -> <run> ns
  (+x%, threshold 10%)`). The threshold matches dossier section 9 item 9 and is
  a `--threshold` flag (`depcheck benchregress`) if it ever needs tuning.
- The baseline is **captured on the first run**: while
  `docs/perf/nightly-baseline.json` is missing, `bench-compare` passes with a
  `baseline established` message and uploads the captured run as the
  `nightly-baseline` artefact. Commit that artefact as
  `docs/perf/nightly-baseline.json` to enable the regression gate; it has the
  shape `{"benchmarks": {"<prefix>/<name>": <nanoseconds>}}` (`cpp/…` for the
  Google Benchmark names, `dotnet/…` for the BenchmarkDotNet full names).
  Benchmarks absent from the baseline are new and never fail.

## Metrology discipline

- Benchmarks must stay hardware-independent in code; budgets are enforced by the
  nightly flags and the committed baseline, not by absolute timing assertions in
  tests.
- Runtime is deliberately short (one .NET warmup, three iterations) so the
  nightly lane stays cheap; the p95 harness additionally runs 100 warm-up and
  2,000 measured builds per shape.

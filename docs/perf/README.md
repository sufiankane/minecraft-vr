# Performance numbers

Measured performance numbers are **not** committed to the repository. The nightly
workflow produces them and uploads them as GitHub Actions artefacts. This
directory holds the human-authored notes, methodology and budget tables that
later stages introduce.

## What is measured where

- **CI gates** (`ci.yml`) enforce behaviour and coverage only. They must not be
  used as a source of performance numbers.
- **Nightly benchmarks** (`nightly.yml`) produce the raw numbers. The lane runs
  two jobs:
  - `bench-cpp` builds the Google Benchmark target `cg_core_math_benchmarks`
    from the `benchmarks` CMake preset and uploads `benchmark_results.json`.
  - `bench-dotnet` runs the BenchmarkDotNet console projects
    `dotnet/benchmarks/CoreMath.Benchmarks`,
    `dotnet/benchmarks/Voxel.Benchmarks` and
    `dotnet/benchmarks/Mesh.Benchmarks`, and uploads
    `BenchmarkDotNet.Artifacts/`.
- Both jobs upload their output as GitHub Actions artefacts. Download the
  artefacts from a workflow run to compare numbers; nothing is committed
  automatically.
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

## Regression budgets

S3 introduces the first budget, recorded in ADR-0007: meshing p95 ≤ 2.0 ms per
full 16³ chunk, measured by the `p95` mode of `Mesh.Benchmarks` on the
dev/release machine and recorded in [`s3.md`](s3.md). The nightly lane still
only produces baseline numbers; it does not run the `p95` mode or enforce the
threshold automatically.

## Metrology discipline

- Benchmarks must stay hardware-independent in code (no absolute timing
  assertions); thresholds are applied to artefacts by reviewers.
- Runtime is deliberately short (one .NET warmup, three iterations) so the
  nightly lane stays cheap.

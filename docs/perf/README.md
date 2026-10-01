# Performance numbers

Measured performance data for Cubeglass lives in `docs/perf/`. This directory is
the home for checked-in performance notes and any budget tables introduced by
later stages.

## What is measured where

- **CI gates** (`ci.yml`) enforce behaviour and coverage only. They must not be
  used as a source of performance numbers.
- **Nightly benchmarks** (`nightly.yml`) produce the raw numbers. The lane runs
  two jobs:
  - `bench-cpp` builds the Google Benchmark target `cg_core_math_benchmarks`
    from the `benchmarks` CMake preset and uploads `benchmark_results.json`.
  - `bench-dotnet` runs the BenchmarkDotNet console project
    `dotnet/benchmarks/CoreMath.Benchmarks` and uploads
    `BenchmarkDotNet.Artifacts/`.
- Both jobs upload their output as GitHub Actions artefacts. Download the
  artefacts from a workflow run to compare numbers; nothing is committed
  automatically.

## Regression budgets

There are no regression thresholds yet. Budgets are introduced per stage as the
hot paths stabilise, and when they arrive they belong in this directory alongside
the stage that introduces them. Until then the nightly lane exists only to
establish the baseline numbers.

## Metrology discipline

- Benchmarks must stay hardware-independent in code (no absolute timing
  assertions); thresholds are applied to artefacts by reviewers.
- Runtime is deliberately short (one .NET warmup, three iterations) so the
  nightly lane stays cheap.

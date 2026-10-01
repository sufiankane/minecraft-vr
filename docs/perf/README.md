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
  - `bench-dotnet` runs the BenchmarkDotNet console project
    `dotnet/benchmarks/CoreMath.Benchmarks` and uploads
    `BenchmarkDotNet.Artifacts/`.
- Both jobs upload their output as GitHub Actions artefacts. Download the
  artefacts from a workflow run to compare numbers; nothing is committed
  automatically.
- The `dotnet` solution build includes the benchmark project, so a broken
  benchmark does not only fail the nightly lane: it also fails the required
  `dotnet` CI gate.

## S0 stub caveat

The S0 modules are stubs. `Cubeglass.CoreMath` currently contains only a
`const`, which emits no IL, so the .NET coverage report has zero coverable lines
and the 90% floor cannot be enforced yet: the coverage tool prints a `WARNING`
and exits 0. The same applies to any matched-but-empty module. The floor becomes
real as instrumentable code lands in S1, so a green `dotnet` job does not
currently mean CoreMath coverage is enforced.

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

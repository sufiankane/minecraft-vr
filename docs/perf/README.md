# Performance numbers

Measured performance numbers are **not** committed to the repository, and
neither is a regression baseline: the nightly regression gate is self-seeding
from its own history cache (see below). The nightly workflow produces the
numbers, enforces the budgets and uploads the raw results as GitHub Actions
artefacts. This directory holds the human-authored notes, methodology and budget
tables.

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
--floor 95`). A matched module with zero coverable lines now **fails** the gate
unless `--allow-empty` is passed (TD-032); no current module uses the flag.

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
- `bench-compare` runs `python -m depcheck benchregress --history
  benchmark-history --history-window 5` over the benchmark JSON from both jobs.
  It compares each benchmark against the **median of the previous up-to-five
  nightly summaries**, which are stored in an `actions/cache` entry
  (`nightly-bench-history-*`) and uploaded as the `nightly-bench-history`
  artefact. Any benchmark present in both that regresses by more than
  **10 percent** fails the job and names the offender (`<name>: <median> ns ->
  <run> ns (+x%, threshold 10%)`). The threshold matches dossier section 9 item
  9 and is a `--threshold` flag (`depcheck benchregress`) if it ever needs
  tuning; `--baseline-from` keeps the strict single-file mode for pinned
  comparisons.
- The gate is **self-seeding** (TD-033/TD-054). The first run has no history:
  `bench-compare` passes with a `no prior history` message and writes its
  summaries to `benchmark-history/bench-<run_id>-cpp.json` and
  `…-dotnet.json`; the next run's cache restore includes them and comparison
  starts. Nothing is committed and there is no baseline file to forget to
  update, so the gate cannot pass silently because a committed baseline was
  never refreshed. Benchmarks absent from the history are new and never fail;
  a renamed benchmark appears as new **and** as an absent old name, so the
  intersection-by-name lifecycle weakness is unchanged (TD-033 residual).
- **Why the median and why 10 percent** (TD-034). GitHub-hosted runners are
  shared and noisy: a single night can measure a 1.5–2× outlier for reasons
  unrelated to the code (a busy host, CPU frequency/steal, cold caches). A
  single-run comparison would either false-fail constantly at a tight threshold
  or be loosened until it is useless. The median of up to five prior runs
  discards a lone outlier without hiding a persistent shift: two consecutive
  runs move the median, and a real >10 percent regression that lasts one night
  fails the next one. Ten percent is wide enough to absorb run-to-run variance
  after the median, and narrow enough to catch the regressions the gate exists
  for; the p95 budget harness above independently enforces the absolute
  shared-runner budget.

## Metrology discipline

- Benchmarks must stay hardware-independent in code; budgets are enforced by the
  nightly flags and the median-of-history gate, not by absolute timing
  assertions in tests.
- Runtime is deliberately short (one .NET warmup, three iterations) so the
  nightly lane stays cheap; the p95 harness additionally runs 100 warm-up and
  2,000 measured builds per shape.

# CI gates

`.github/workflows/ci.yml` runs on every pull request and on every push to
`main`. A run is cancelled when a newer commit arrives on the same ref
(`concurrency: cancel-in-progress`). Every job below is a required status check:
merging is blocked while any of them fails.

## Jobs

| Check context | Runner | Enforces |
| --- | --- | --- |
| `cpp-windows` | `windows-latest` | MSVC build (warnings as errors), `ctest`, `clang-format --dry-run --Werror` over every `cpp/**` source/header, `clang-tidy` over `cpp/core-math/src` (other modules deferred; see below) |
| `cpp-linux-asan` | `ubuntu-latest` | Linux ASan/UBSan build and `ctest` |
| `dotnet` | `ubuntu-latest` | `dotnet build Cubeglass.sln --configuration Release`, per-project `dotnet test` with `XPlat Code Coverage`, and the module coverage floors |
| `python` | `ubuntu-latest` | `ruff check`, strict `mypy`, `pytest` |
| `depcheck` | `ubuntu-latest` | `python -m depcheck --root .` (inward-only dependency rules and `layers.json` completeness) and `python -m depcheck contracts --root .` (contract-compatibility gate) |
| `licences` | `ubuntu-latest` | `python -m depcheck licences --root .` (licence allowlist for declared dependencies, and SPDX id validation of every allowlist entry) |

C++ dependencies are resolved from vcpkg at the pinned baseline
`eb2d3a3279fd019cb7733072d86900d0ad2a1aef`. `cpp-windows` caches the manifest-mode
tree (`cpp/build/windows-msvc/vcpkg_installed`) keyed on the baseline commit and
`hashFiles('cpp/vcpkg.json')`.

## Toolchain policy

vcpkg (pinned baseline above), the .NET SDK (`dotnet/global.json`) and the Unity
editor are version-pinned. Tools provided by the hosted runner image (CMake,
Ninja, clang-format, clang-tidy, MSVC, Python) are deliberately not
version-locked: each relevant job prints a "Toolchain versions" step so image
drift is visible in the run log. Local development pins live in
[`docs/toolchains.md`](toolchains.md).

`cpp-linux-asan` runs the `linux-asan` test preset because the `ci` test preset
in `cpp/CMakePresets.json` is bound to the `windows-msvc` configure preset.

## Licence allowlist

`contracts/licence-allowlist.json` maps every declared dependency to its SPDX
licence id. The `licences` gate reads the declared packages from
`cpp/vcpkg.json` (`dependencies`) and `dotnet/Directory.Packages.props`
(`<PackageVersion Include="..." />` ids) and fails — listing each unknown
package — when one is missing from the allowlist. Add the package with its
licence id to the allowlist to clear the gate.

The gate also validates each allowlist value against an embedded list of common
SPDX identifiers (`depcheck/licences.py: KNOWN_SPDX_IDS`); an unknown or empty
id fails and names the offending package. The list is curated, not the full SPDX
registry: extend it when a legitimate new dependency needs an id it lacks.

## Dependency rules and manifest completeness

`python -m depcheck --root .` enforces the inward-only include/namespace rules
from `contracts/layers.json`, and the same file must be complete:

- every `cpp/<module>` directory that contains a `CMakeLists.txt` (excluding
  `tests`, `tools` and `build*`) needs an entry, and every entry needs a module
  directory;
- every `dotnet/src/<Dir>/*.csproj` project needs an entry, and every entry
  needs a project directory.

A missing entry is reported as `layersEntryMissing` and a stale entry as
`layersEntryStale` (line 0, since neither points at a source line). Forward-
looking modules that have not landed yet are listed under the top-level
`deferred` object, e.g. `"deferred": {"cpp": ["handcore"]}`, which tolerates the
absent directory without weakening the check for every other module.

## Contract-compatibility gate

Dossier section 9 item 7 requires contract files to stay frozen unless the ABI
version is bumped (and an ADR explains it). `python -m depcheck contracts
--root .` (required `depcheck` job) computes a normalised fingerprint over
`contracts/cg_types.h`, `contracts/cg_unity_bridge.h`,
`contracts/cpp/ports.hpp` and `contracts/cpp/result.hpp` — comments and
whitespace stripped, so formatting-only edits pass — and compares it with the
committed `contracts/abi-baseline.json`.

- An unchanged fingerprint passes.
- A changed fingerprint fails when `CG_ABI_VERSION` (declared in
  `contracts/cg_types.h`) is unchanged, naming the changed files.
- After bumping `CG_ABI_VERSION`, regenerate the baseline with
  `python -m depcheck contracts --root . --update`. `--update` refuses to write
  while the version is unchanged, so a contract edit cannot silently rebase the
  baseline.

The baseline was added 2026-10-03 (infra review part A); ADR-0003 records why
the S1 follow-up did not happen until then.

## Formatting and clang-tidy scope

`clang-format --dry-run --Werror` runs over every `cpp/**` file with a
`.cpp`, `.hpp`, `.h`, `.hh`, `.cc` or `.cxx` extension (60 files today).
`contracts/**` is deliberately excluded: those headers are dossier-verbatim and
are frozen by the contract gate above instead.

`clang-tidy` currently runs over `cpp/core-math/src/*.cpp` only. Widening it to
`cpp/glasses/src` and `cpp/bridge/src` surfaces 159 warnings-as-errors with the
repo `.clang-tidy` policy (e.g. `bugprone-multi-level-implicit-pointer-conversion`
in `viture_loader.cpp`, `cppcoreguidelines-avoid-magic-numbers` in
`yaw_unwrap.hpp`), which is a dedicated follow-up, not part of this wave.
Deferred directories, explicitly: `cpp/glasses/src`, `cpp/bridge/src`,
`cpp/tests`, `cpp/tools` (the negative format fixture and any Unity C# are
outside clang-tidy by design). `cpp/core-math/src` is the only module that must
stay clean; a change there is gated by the required `cpp-windows` job.

## Reproduce locally

From the repository root, using the Python environment in `python/.venv`:

```powershell
# Python gate
cd python
python -m pip install -r requirements-dev.txt
python -m pip install -e .
python -m ruff check .
python -m mypy calib depcheck
python -m pytest

# Dependency-rule gate
cd ..
python -m depcheck --root .

# Contract-compatibility gate
python -m depcheck contracts --root .

# Licence gate
python -m depcheck licences --root .
```

C++ gates need an MSVC environment (`scripts/dev-shell.ps1`) and
`%USERPROFILE%\vcpkg` checked out at the pinned commit:

```powershell
cd cpp
cmake --preset windows-msvc
cmake --build --preset windows-msvc
ctest --preset ci
clang-format --dry-run --Werror @(git ls-files cpp | Where-Object { $_ -match '\.(cpp|hpp|h|hh|cc|cxx)$' })
clang-tidy -p build/windows-msvc @(git ls-files core-math/src | Where-Object { $_ -match '\.cpp$' })
```

.NET gate:

```powershell
cd dotnet
dotnet test Cubeglass.sln --configuration Release
```

Workflow syntax can be checked with
[`actionlint`](https://github.com/rhysd/actionlint):

```powershell
actionlint .github/workflows/ci.yml
```

## Coverage floors

The required jobs enforce the module line-coverage floors from dossier NFR-05
(at least 90 percent). S1 raised the two core-math floors to 95 percent — C++
`core-math` and .NET `Cubeglass.CoreMath` — because both modules now contain
real instrumentable code; the `calib` and `depcheck` floors remain at 90.

| Module | Job | Floor |
| --- | --- | ---: |
| `core-math` (C++) | `cpp-linux-asan` | 95% |
| `Cubeglass.CoreMath` (.NET) | `dotnet` | 95% |
| `Cubeglass.Voxel` (.NET) | `dotnet` | 90% |
| `Cubeglass.Mesh` (.NET) | `dotnet` | 90% |
| `Cubeglass.Gameplay` (.NET) | `dotnet` | 90% |
| `Cubeglass.Streaming` (.NET) | `dotnet` | 90% |
| `calib` (Python) | `python` | 90% |
| `depcheck` (Python) | `python` | 90% |

`cpp-linux-asan` builds and runs the `linux-coverage` preset, generates
Cobertura XML with `gcovr`, and runs
`python -m depcheck coverage --report coverage.cobertura.xml --module core-math
--floor 95`. The `dotnet` job builds the solution, runs each .NET test project
with `XPlat Code Coverage` into `coverage/coremath`, `coverage/voxel`,
`coverage/mesh`, `coverage/gameplay` and `coverage/streaming`, and enforces
`--module Cubeglass.CoreMath --floor 95`,
`--module Cubeglass.Voxel --floor 90`, `--module Cubeglass.Mesh --floor 90`,
`--module Cubeglass.Gameplay --floor 90` and
`--module Cubeglass.Streaming --floor 90`.
Each floor step resolves the first `coverage.cobertura.xml` found under its own
per-project results directory — `coverage/coremath`, `coverage/voxel`,
`coverage/mesh`, `coverage/gameplay`, `coverage/streaming` — and fails loudly
when none exists, so the
five test runs and their reports stay separate. A report from the Voxel run
also instruments the referenced `Cubeglass.CoreMath` package; the `--module`
filter selects the module each step enforces. The `python` job runs `pytest` with
coverage and enforces `--module calib --floor 90` and `--module depcheck --floor
90`. A matched module with zero coverable
lines still prints a `WARNING` and exits 0, but no current module takes that
path: every floor above is enforced from its job's coverage report. See
[`docs/perf/README.md`](perf/README.md) for details.

## Nightly benchmark, supply-chain and mutation lanes

`.github/workflows/nightly.yml` (schedule + `workflow_dispatch`) runs:

- `bench-cpp`: the Google Benchmark target `cg_core_math_benchmarks` from the
  `benchmarks` CMake preset, uploading `benchmark_results.json`.
- `bench-dotnet`: the `dotnet/benchmarks/CoreMath.Benchmarks`,
  `dotnet/benchmarks/Voxel.Benchmarks` and `dotnet/benchmarks/Mesh.Benchmarks`
  console projects (each emits a `JsonExporter` report), then the ADR-0007 mesh
  budget harness `Mesh.Benchmarks -- p95 --budget-ms 8`, which exits non-zero
  when any chunk shape's p95 exceeds 8 ms. 8 ms is the shared-runner CI budget;
  the release/local budget remains the 2.0 ms of ADR-0007 (`docs/perf/s3.md`).
- `bench-compare`: downloads both artefacts and runs
  `python -m depcheck benchregress` against the committed
  `docs/perf/nightly-baseline.json`. A benchmark present in both the run and the
  baseline that regresses by more than 15 percent fails the job and names the
  offender. While the baseline file is missing, the job passes with a
  `baseline established` message and uploads the captured run as the
  `nightly-baseline` artefact; committing that file enables enforcement from the
  next run. Benchmarks absent from the baseline are new and never fail.
- `supply-chain`: `dotnet list dotnet/Cubeglass.sln package --vulnerable
  --include-transitive` (fails on any vulnerable package, transitive included)
  and `pip-audit -r python/requirements-dev.txt` (fails on any finding).
- `mutation`: `dotnet stryker` against `Cubeglass.Voxel` with the break
  threshold at 70.
- `soak`: the 30-minute `glasses_soak` run.

The performance methodology and artefact locations are documented in
[`docs/perf/README.md`](perf/README.md); that file is the single source of
truth for what is enforced where.

## Release workflow

`.github/workflows/release.yml` is **dispatch-only** (`workflow_dispatch`) and
does not run on pull requests or pushes. It builds the Windows x64 player on
`windows-latest` and attaches it to the `v0.1.0` release when that tag exists;
the tag itself is owner-gated and not created by CI (R50). The workflow first
requires three repository secrets — `UNITY_LICENSE`, `UNITY_EMAIL` and
`UNITY_PASSWORD` — and fails before any build step with an error naming every
missing one. With the secrets present it builds the managed plugins
(`dotnet build dotnet/Cubeglass.sln --configuration Release` and
`scripts/sync-unity-plugins.ps1`), builds the native `cg_bridge` target from the
pinned vcpkg baseline and copies `cg_unity_bridge.dll` into
`unity/Cubeglass/Assets/Plugins/win-x64/`. The staged DLLs are git-ignored, so
`allowDirtyBuild: false` still holds. It then runs `game-ci/unity-builder@v4`
(Unity 6000.6.3f1 from `ProjectSettings/ProjectVersion.txt`,
`StandaloneWindows64`) with the committed build method
`Cubeglass.Editor.BuildPlayer.BuildWindows64`, packages
`Cubeglass-windows-x64.zip`, uploads it as the `Cubeglass-windows-x64` artefact
and, when `gh release view v0.1.0` succeeds, attaches the archive to the
release. Until the secrets and the HIL playtest exist, the release notes in
[`releases/v0.1.0.md`](releases/v0.1.0.md) describe a release candidate and the
`stage-7-complete` tag is withheld.

## Negative gates

Negative gates — checks that deliberately broken inputs are rejected — are
dispatch-only and documented in
[`docs/ci/negative-gates.md`](ci/negative-gates.md). They are self-tests for the
positive gates and are deliberately absent from the required checks; see that
document for why they must stay dispatch-only.

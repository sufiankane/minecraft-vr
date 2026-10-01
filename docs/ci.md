# CI gates

`.github/workflows/ci.yml` runs on every pull request and on every push to
`main`. A run is cancelled when a newer commit arrives on the same ref
(`concurrency: cancel-in-progress`). Every job below is a required status check:
merging is blocked while any of them fails.

## Jobs

| Check context | Runner | Enforces |
| --- | --- | --- |
| `cpp-windows` | `windows-latest` | MSVC build (warnings as errors), `ctest`, `clang-format --dry-run --Werror`, `clang-tidy` |
| `cpp-linux-asan` | `ubuntu-latest` | Linux ASan/UBSan build and `ctest` |
| `dotnet` | `ubuntu-latest` | `dotnet test Cubeglass.sln --configuration Release` on the SDK pinned by `dotnet/global.json` |
| `python` | `ubuntu-latest` | `ruff check`, strict `mypy`, `pytest` |
| `depcheck` | `ubuntu-latest` | `python -m depcheck --root .` (inward-only dependency rules) |
| `licences` | `ubuntu-latest` | `python -m depcheck licences --root .` (licence allowlist for declared dependencies) |

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
clang-format --dry-run --Werror @(git ls-files | Where-Object { $_ -match '\.(cpp|hpp)$' })
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
| `calib` (Python) | `python` | 90% |
| `depcheck` (Python) | `python` | 90% |

`cpp-linux-asan` builds and runs the `linux-coverage` preset, generates
Cobertura XML with `gcovr`, and runs
`python -m depcheck coverage --report coverage.cobertura.xml --module core-math
--floor 95`. The `dotnet` job runs
`dotnet test Cubeglass.sln --configuration Release` with `XPlat Code Coverage`
and enforces `--module Cubeglass.CoreMath --floor 95` and `--module
Cubeglass.Voxel --floor 90`. Both .NET floors select their report by content,
because the Voxel test run also emits a (mostly uncovered)
`Cubeglass.CoreMath` package: the CoreMath step matches the report whose
`<source>` root is `src/CoreMath/`, the Voxel step matches the report containing
the `Cubeglass.Voxel` package, and each fails loudly when no report matches. The
`python` job runs `pytest` with coverage and enforces `--module calib --floor
90` and `--module depcheck --floor 90`. A matched module with zero coverable
lines still prints a `WARNING` and exits 0, but no current module takes that
path: every floor above is enforced from its job's coverage report. See
[`docs/perf/README.md`](perf/README.md) for details.

## Nightly benchmark lane

Performance numbers are produced by `.github/workflows/nightly.yml`, not by the
required CI gates. The nightly lane runs `bench-cpp` (the Google Benchmark target
`cg_core_math_benchmarks` from the `benchmarks` CMake preset) and `bench-dotnet`
(the `dotnet/benchmarks/CoreMath.Benchmarks` and
`dotnet/benchmarks/Voxel.Benchmarks` console projects), and uploads both outputs
as GitHub Actions artefacts. A dispatch-only `mutation` job runs `dotnet stryker`
against `Cubeglass.Voxel` with the break threshold at 70. Nothing is committed
automatically. The methodology, the artefact locations and the S0 caveat are
documented in [`docs/perf/README.md`](perf/README.md).

## Negative gates

Negative gates — checks that deliberately broken inputs are rejected — are
dispatch-only and documented in
[`docs/ci/negative-gates.md`](ci/negative-gates.md). They are self-tests for the
positive gates and are deliberately absent from the required checks; see that
document for why they must stay dispatch-only.

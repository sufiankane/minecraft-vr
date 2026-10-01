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
`eb2d3a3279fd019cb7733072d86900d0ad2a1aef`. `cpp-windows` caches the vcpkg
`installed` tree keyed on `cpp/vcpkg.json`.

`cpp-linux-asan` runs the `linux-asan` test preset because Task 2 binds the `ci`
test preset to the `windows-msvc` configure preset.

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

## Negative gates

This workflow covers the positive gates only. Negative gates — checks that
deliberately broken inputs are rejected — arrive in Task 8 and will be
referenced here.

# Cubeglass

Cubeglass is a layered voxel / VR reconstruction platform. This is a monorepo
holding each language lane as an isolated, independently testable unit: C++
core math and algorithms, C# .NET libraries, Python calibration and dependency
tooling, and a Unity client. Every lane is pinned in
[`docs/toolchains.md`](docs/toolchains.md) and exercised by the same commands
locally and in CI.

**Status (2026-10-04):** the S0–S7 software is complete (the M1 vertical
slice). The stage tags (`stage-5-complete`, `stage-6-complete`,
`stage-7-complete`) and the `v0.1.0` tag are **withheld** until the
hardware-in-the-loop runs in [`docs/questions/`](docs/questions/) are committed
and a CI player build succeeds.

## Start here

| Document | What it is |
| --- | --- |
| [`docs/ONBOARDING.md`](docs/ONBOARDING.md) | Day-one setup, bootstrap, repository map, per-lane commands, running the game, CI/CD, HIL and troubleshooting |
| [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) | The implemented architecture: modules, layers, dependency rules, data flow and milestone state |
| [`docs/CONTRACTS.md`](docs/CONTRACTS.md) | Contract register and change runbook: frozen interfaces, ABI versioning, the fingerprint gate and the read/write matrix per format |
| [`docs/CHANGELOG.md`](docs/CHANGELOG.md) | Consolidated engineering history by stage (S0–S7, review waves, withheld tags) |
| [`docs/notes/tech-debt.md`](docs/notes/tech-debt.md) | The open programme tech-debt register (TD-NNN) |

## Bootstrap

On Windows, install or verify the pinned toolchain in one step:

```powershell
powershell -File scripts/bootstrap-dev.ps1
powershell -File scripts/bootstrap-dev.ps1 -Check
```

`-Check` prints a PASS/FAIL table for every pin and exits non-zero if any tool
is missing or older than its pin. Enter the MSVC x64 developer shell before any
local C++ build:

```powershell
. scripts/dev-shell.ps1
```

## Local commands

These are the canonical lane commands, mirroring the shared lanes in
[`CONTRIBUTING.md`](CONTRIBUTING.md). See that file for the full conventions.

C++ (enter the MSVC x64 developer shell first; it sets `VCPKG_ROOT`):

```powershell
. .\scripts\dev-shell.ps1
cd cpp
cmake --preset windows-msvc
cmake --build --preset windows-msvc
ctest --preset ci
```

C# (.NET), from the SDK pinned in `dotnet/global.json`:

```powershell
cd dotnet
dotnet test Cubeglass.sln --configuration Release
```

Python, using the virtual environment in `python/.venv`:

```powershell
cd python
python -m pip install -r requirements-dev.txt
python -m pip install -e .
python -m pytest
python -m ruff check .
python -m mypy calib depcheck
```

Dependency, contract and licence gates, from the repository root:

```powershell
python -m depcheck --root .
python -m depcheck contracts --root .
python -m depcheck licences --root .
```

Unity (stage the managed and native plugins first):

```powershell
powershell -File scripts/sync-unity-plugins.ps1
unity test unity/Cubeglass --mode EditMode --non-interactive
```

CI gate details are in [`docs/ci.md`](docs/ci.md).

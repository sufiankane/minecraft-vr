# Cubeglass

Cubeglass is a layered voxel / VR reconstruction platform. This is a monorepo
holding each language lane as an isolated, independently testable unit: C++
core math and algorithms, C# .NET libraries, Python calibration and dependency
tooling, and a Unity client. Every lane is pinned in
[`docs/toolchains.md`](docs/toolchains.md) and exercised by the same commands
locally and in CI.

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

These are the canonical lane commands, kept in sync with
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

Dependency and licence gates, from the repository root:

```powershell
python -m depcheck --root .
python -m depcheck licences --root .
```

Unity:

```powershell
unity test unity/Cubeglass --mode EditMode --non-interactive
```

CI gate details are in [`docs/ci.md`](docs/ci.md).

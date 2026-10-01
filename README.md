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

## One command per language

These are the canonical lane commands (kept in sync with `CONTRIBUTING.md`):

| Lane | Command |
| --- | --- |
| C++ | `cmake -S cpp -B build -G Ninja; cmake --build build; ctest --test-dir build` |
| C# (.NET) | `dotnet test dotnet/Cubeglass.sln` |
| Python | `python -m pytest python; python -m ruff check python; python -m mypy python` |
| Unity | `unity test --project unity/Cubeglass` |
| Dependency check | `python -m depcheck --root .` |

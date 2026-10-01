# Toolchain pins

Single source of truth for every tool version Cubeglass builds against. Every
later task and CI lane reads this file; `scripts/bootstrap-dev.ps1` parses the
`Required` column and fails loudly when an installed tool is older than the pin.

Comparison semantics:

- Numeric tools are compared as version floors: the installed version must be
  greater than or equal to `Required`.
- `vcpkg` is compared as an exact commit: the checked-out `HEAD` must equal the
  recorded baseline commit.
- `Unity editor` is compared by the presence of the exact editor directory.

Baseline recorded on 2026-10-01 (Windows x64).

| Tool | Required | How checked | Install |
| --- | --- | --- | --- |
| cmake | 4.4.3 | `cmake --version` | `winget install --id Kitware.CMake --exact --accept-source-agreements --accept-package-agreements` |
| ninja | 1.13.2 | `ninja --version` | `winget install --id Ninja-build.Ninja --exact --accept-source-agreements --accept-package-agreements` |
| clang-format | 23.1.2 | `clang-format --version` | `winget install --id LLVM.LLVM --exact --accept-source-agreements --accept-package-agreements` |
| clang-tidy | 23.1.2 | `clang-tidy --version` | `winget install --id LLVM.LLVM --exact --accept-source-agreements --accept-package-agreements` |
| VS Build Tools | 18.9.12105.275 | `vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationVersion` | already installed at `C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools` |
| .NET SDK | 10.0.401 | `dotnet --version` | `winget install --id Microsoft.DotNet.SDK.10 --exact --accept-source-agreements --accept-package-agreements` |
| vcpkg | eb2d3a3279fd019cb7733072d86900d0ad2a1aef | `git -C %USERPROFILE%\vcpkg rev-parse HEAD` | `git clone https://github.com/microsoft/vcpkg %USERPROFILE%\vcpkg` then `bootstrap-vcpkg.bat -disableMetrics` |
| Python | 3.14.7 | `python --version` | already installed (python.org 3.14.7) |
| Unity editor | 6000.6.3f1 | editor directory `C:\Program Files\Unity\Hub\Editor\6000.6.3f1` exists | installed via Unity Hub (editor 6000.6.3f1) |
| GitHub CLI | 2.102.0 | `gh --version` | `winget install --id GitHub.cli --exact --accept-source-agreements --accept-package-agreements` |
| Node | 24.19.0 | `node --version` | already installed (Node.js 24 LTS) |

## Bootstrap

```powershell
powershell -File scripts/bootstrap-dev.ps1
powershell -File scripts/bootstrap-dev.ps1 -Check
```

`scripts/dev-shell.ps1` enters the MSVC x64 developer shell used by every local
C++ build.

## vcpkg

- Install root: `%USERPROFILE%\vcpkg`
- Baseline commit: `eb2d3a3279fd019cb7733072d86900d0ad2a1aef`
- No C++ dependencies are built by this task; the commit is the reproducibility
  baseline for later tasks.

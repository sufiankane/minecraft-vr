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

Out-of-band prerequisite (not parsed by `bootstrap-dev.ps1 -Check`): the Unity
CLI (`unity`) **1.0.0-beta.11**, required by the `ci-local.ps1` Unity lane and
the release test gate. Install the pinned version from a PowerShell prompt:

```powershell
$env:UNITY_CLI_CHANNEL = 'beta'
Invoke-WebRequest -UseBasicParsing -Uri 'https://public-cdn.cloud.unity3d.com/hub/prod/cli/install.ps1' -OutFile "$env:TEMP\unity-cli-install.ps1"
& powershell -NoProfile -ExecutionPolicy Bypass -File "$env:TEMP\unity-cli-install.ps1" -Target '1.0.0-beta.11'
```

Verify with `unity --version`; the editor itself is the separately pinned
"Unity editor" row above.

## Format and lint scope

`clang-format` covers every `cpp/**/*.{cpp,hpp,h,hh,cc,cxx}` file in the
required `cpp-windows` job; `contracts/**` is excluded because those headers are
dossier-verbatim and frozen by the contract gate instead.
`clang-tidy` currently runs over `cpp/core-math/src/*.cpp` only;
`cpp/glasses/src`, `cpp/bridge/src`, `cpp/tests` and `cpp/tools` are deferred
(widening to the adapters surfaces 159 warnings-as-errors today). The exact
scope and the deferral evidence are recorded in [`docs/ci.md`](ci.md).

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

## Unity

Run the `unity/Cubeglass` EditMode tests with the Unity CLI (`unity` 1.0.0-beta.11):

```powershell
unity test unity/Cubeglass --mode EditMode --non-interactive
```

The project path is a positional argument (`unity test [project]`); there is no
`--project` flag. `--mode EditMode` selects the platform, `--non-interactive`
is the global flag required for headless/CI runs, and `--output <path>` writes
the NUnit XML results report (default `test-results.xml`). Flags confirmed
against `unity test --help`.

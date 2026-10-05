# Cubeglass onboarding

Cubeglass is a layered voxel / VR reconstruction platform: pure C++ and C#
modules behind frozen contracts, a Unity client that adapts them to the
glasses, and Python tooling that keeps the layering honest. This document takes
a new contributor from a bare Windows machine to a green local gate and a
running player. Every command is copy-pasteable; every version, name and count
is taken from the repository's own scripts, workflows and gate notes.

Software status: **S0–S7 are complete** (the M1 vertical slice). The stage tags
(`stage-5-complete`, `stage-6-complete`, `stage-7-complete`) and the `v0.1.0`
tag are **withheld** until the hardware-in-the-loop (HIL) runs in
[`docs/questions/`](questions/) are committed and a CI player build succeeds.
See [`docs/releases/v0.1.0.md`](releases/v0.1.0.md).

Primary sources: [`docs/toolchains.md`](toolchains.md), [`docs/ci.md`](ci.md), `scripts/`, `.github/workflows/`, [`docs/adr/`](adr/) and the [stage gate notes](notes/).

## 1. Day one: prerequisites and exact versions

Host: Windows 10/11 x64. The version table below is the single source of truth
([`docs/toolchains.md`](toolchains.md), baseline recorded 2026-10-01);
`scripts/bootstrap-dev.ps1 -Check` parses this exact table and fails when an
installed tool is older than its pin, when the vcpkg checkout is not at the
recorded commit, or when the pinned Unity editor directory is missing.

| Tool | Required | How `-Check` verifies it |
| --- | --- | --- |
| CMake | 4.4.3 | `cmake --version` |
| Ninja | 1.13.2 | `ninja --version` |
| clang-format | 23.1.2 (LLVM) | `clang-format --version` |
| clang-tidy | 23.1.2 (LLVM) | `clang-tidy --version` |
| VS Build Tools | 18.9.12105.275 | `vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationVersion` |
| .NET SDK | 10.0.401 | `dotnet --version` (also pinned by `dotnet/global.json`, `rollForward: latestPatch`) |
| vcpkg | commit `eb2d3a3279fd019cb7733072d86900d0ad2a1aef` | `git -C $env:USERPROFILE\vcpkg rev-parse HEAD` |
| Python | 3.14.7 | `python --version` |
| Unity editor | 6000.6.3f1 | `C:\Program Files\Unity\Hub\Editor\6000.6.3f1` exists |
| GitHub CLI | 2.102.0 | `gh --version` |
| Node | 24.19.0 | `node --version` |

Numeric tools are compared as version floors (installed ≥ required); vcpkg is an
exact commit match; the Unity editor is an exact directory match.

Install commands (winget ids, the vcpkg clone, the python.org installer) are in the `Install` column of [`docs/toolchains.md`](toolchains.md). Python and Node are assumed installed; the Unity editor comes from the Hub.

One prerequisite is **out of band** and is not parsed by `-Check`: the Unity
CLI, pinned to **1.0.0-beta.11**, required by the `ci-local.ps1` Unity lane and
the release test gate. From a PowerShell prompt:

```powershell
$env:UNITY_CLI_CHANNEL = 'beta'
Invoke-WebRequest -UseBasicParsing -Uri 'https://public-cdn.cloud.unity3d.com/hub/prod/cli/install.ps1' -OutFile "$env:TEMP\unity-cli-install.ps1"
& powershell -NoProfile -ExecutionPolicy Bypass -File "$env:TEMP\unity-cli-install.ps1" -Target '1.0.0-beta.11'
unity --version
```

`scripts/ci-local.ps1` expects the CLI executable at
`%LOCALAPPDATA%\Unity\bin\unity.exe`; the editor it drives is the separately
pinned 6000.6.3f1 installation. The project path is a positional argument
(`unity test [project]`); there is no `--project` flag.

### PowerShell 5.1

Every repository script declares `#requires -Version 5.1`, and the self-hosted release job deliberately uses `shell: powershell` (Windows PowerShell 5.1), not `pwsh`: the licensed machine has no PowerShell 7 ([release.yml](../.github/workflows/release.yml), [ADR-0012](adr/0012-ci-unity-licensing-and-release-routes.md)). Run scripts as `powershell -File scripts\<name>.ps1`. The 5.1 quirks are handled inside the scripts (native exit-code resets in `ci-local.ps1`, stderr handling around `gh` probes in `release.yml`); do not switch shells blindly. Save non-ASCII scripts/docs as UTF-8 (the C++ build passes MSVC `/utf-8` for this reason; see [Troubleshooting](#8-troubleshooting)).

## 2. Bootstrap

Install or verify the pinned toolchain in one step, from the repository root:

```powershell
powershell -File scripts/bootstrap-dev.ps1
powershell -File scripts/bootstrap-dev.ps1 -Check
```

`bootstrap-dev.ps1` (no switch) is idempotent: it installs the winget packages
(CMake, Ninja, LLVM, .NET SDK 10, GitHub CLI), clones vcpkg to
`%USERPROFILE%\vcpkg` when missing, checks out the pinned baseline commit, and
runs `bootstrap-vcpkg.bat -disableMetrics`. It prints
`[skip]`/`[install]`/`[checkout]` lines per step and ends with:

```
vcpkg HEAD: eb2d3a3279fd019cb7733072d86900d0ad2a1aef
Bootstrap complete. Run: powershell -File scripts/bootstrap-dev.ps1 -Check
```

With `-Check` it installs nothing. It parses the `docs/toolchains.md` table,
verifies all eleven pins, prints a table and exits non-zero if any row fails:

```
TOOL           REQUIRED      INSTALLED    STATUS
----           --------      ---------    ------
cmake          4.4.3         4.4.3        PASS
...
All toolchain pins satisfied.
```

A failure prints one line per failing pin and exits 1:

```
FAIL cmake: required 4.4.3 but found 3.31.0
```

`-ExpectedOverride` forces one expected value for the duration of a `-Check`
run, e.g. `powershell -File scripts/bootstrap-dev.ps1 -Check -ExpectedOverride 'cmake=99.0.0'`;
installs are never affected. It is a testing aid for the FAIL path.

What bootstrap does **not** do: install Python, Node, the Unity editor or the
Unity CLI (all out of band, per section 1), and it does not create
`python/.venv` — `scripts/ci-local.ps1` does that on first run, or you can
create it manually (section 4.3).

When a pin fails:

- **A winget tool (cmake, ninja, LLVM, dotnet, gh):** install the required version from the `Install` column, open a new shell, re-run `-Check`. The script repairs `PATH` for the known install dirs (`C:\Program Files\{LLVM\bin,CMake\bin,GitHub CLI,nodejs,dotnet}`, `C:\Python314`), but a stale parent shell may still need restarting.
- **vcpkg:** `git -C "$env:USERPROFILE\vcpkg" checkout eb2d3a3279fd019cb7733072d86900d0ad2a1aef`, then re-run `bootstrap-dev.ps1` (it re-bootstraps after a checkout).
- **Unity editor:** install exactly 6000.6.3f1 via the Unity Hub; `-Check` matches the editor directory name exactly.
- **Unity CLI (`unity`):** `-Check` does not see it; install 1.0.0-beta.11 with the snippet in section 1 and confirm `unity --version`.

Before any local C++ build, enter the MSVC x64 developer shell (it also sets
`VCPKG_ROOT` to `%USERPROFILE%\vcpkg` when unset):

```powershell
. .\scripts\dev-shell.ps1
```

## 3. Repository map

Annotated tree; each row carries the dependency rule for that directory.

| Path | Contents and dependency rule |
| --- | --- |
| `contracts/` | Frozen headers (`cg_types.h`, `cg_unity_bridge.h`, `cpp/ports.hpp`, `cpp/result.hpp`): changed only with an ADR and a `CG_ABI_VERSION` bump, enforced by the contract gate. `layers.json` is the machine-readable inward-only rule set; `golden/` holds shared fixtures. |
| `cpp/` | Native modules, `CMakePresets.json` and the `vcpkg.json` manifest; everything builds inward to the contracts. |
| `cpp/core-math/` | Pure math (conventions, transforms, clocks); forbids `windows.h`, `viture`, `thread`, `fstream`, `filesystem`, `mutex`, `onnxruntime`. |
| `cpp/glasses/` | VITURE adapter seam (`fake` / `replay` / `viture-fake`); may use the OS, but not `onnxruntime`; an edge adapter. |
| `cpp/bridge/` | Shared-memory bridge (`cg_unity_bridge.dll`); forbids `onnxruntime`; exports only the frozen bridge ABI. |
| `cpp/tests/`, `cpp/tools/` | Unit/contract/stress tests and the probe/soak tools (`cg-pose-probe`, `glasses_soak`); never dependencies of the modules. |
| `dotnet/src/` | `CoreMath`, `Voxel`, `Mesh`, `Gameplay`, `Streaming`: pure modules, no `UnityEngine`/`UnityEditor`/`System.IO`/`System.Threading`, references restricted to `allowedProjectReferences` in `layers.json` (CoreMath ← Voxel; Voxel ← Mesh, Gameplay; Mesh ← Streaming). |
| `dotnet/tests/`, `dotnet/benchmarks/` | One test project per module (NUnit + FsCheck) plus BenchmarkDotNet projects; depend inward only. The solution and pins live in `dotnet/Cubeglass.sln`, `global.json`, `Directory.Packages.props`. |
| `unity/Cubeglass/` | The adapter layer: `Assets/` (editor builders, `Scenes/{Game,Calibration}.unity`, `Plugins/`) and `Packages/com.cubeglass.{bridge,input,rendering,placeholder}`. Only these packages touch UnityEngine/UnityEditor; they consume the bridge and managed plugins, never the reverse. |
| `python/` | Offline tooling: `calib/` (calibration) and `depcheck/` (dependency, contract, licence, coverage and benchmark gates). It reads the repo, never is imported by it. |
| `scripts/` | Dev tooling only: bootstrap, dev-shell, ci-local, sync-unity-plugins, negative fixtures. |
| `docs/`, `data/` | Documentation and small shared fixtures; no build inputs. |
| `.github/` | CI definitions; the only place Unity secrets are referenced (release only). |

`python -m depcheck --root .` enforces the `cpp`/`dotnet` rules and checks that
`layers.json` is complete (missing entry → `layersEntryMissing`, stale entry →
`layersEntryStale`; `handcore` is listed under the top-level `deferred` object).

## 4. Build and test per lane

### 4.1 C++ (Windows MSVC)

```powershell
. .\scripts\dev-shell.ps1
cd cpp
cmake --preset windows-msvc
cmake --build --preset windows-msvc
ctest --preset ci
```

Expected: configure + build clean under `/W4 /WX`; `ctest --preset ci` reports
`100% tests passed, 0 tests failed out of 5` — `core_math`, `glasses`,
`bridge_layout`, `bridge_reader`, `bridge_stress` (see
[`docs/notes/s6-gate.md`](notes/s6-gate.md) §3). Built binaries land in
`cpp/build/windows-msvc/`; the native bridge DLL is
`cpp/build/windows-msvc/bridge/cg_unity_bridge.dll`.

Presets (from `cpp/CMakePresets.json`): configure `windows-msvc`, `linux-ci`,
`linux-asan`, `linux-tsan`, `linux-coverage`, `benchmarks`; test `ci` (bound to
`windows-msvc`), `linux-ci`, `linux-asan`, `linux-tsan`, `linux-coverage`,
`benchmarks`. The Linux presets run only in CI containers.

Format and tidy checks (run from the repository root after the configure step;
`contracts/**` is deliberately excluded):

```powershell
clang-format --dry-run --Werror @(git ls-files cpp | Where-Object { $_ -match '\.(cpp|hpp|h|hh|cc|cxx)$' })
clang-tidy -p cpp/build/windows-msvc @(git ls-files cpp/core-math/src | Where-Object { $_ -match '\.cpp$' })
```

`clang-tidy` currently covers `cpp/core-math/src` only; `glasses`, `bridge`,
`tests` and `tools` are deferred (widening surfaces 159 warnings-as-errors —
see [`docs/ci.md`](ci.md) and TD-029).

### 4.2 .NET

```powershell
dotnet test dotnet/Cubeglass.sln --configuration Release
```

Expected at the current head: **471 tests, 0 failed** across
`Cubeglass.CoreMath.Tests` (78), `Cubeglass.Gameplay.Tests` (121),
`Cubeglass.Streaming.Tests` (24), `Cubeglass.Voxel.Tests` (161) and
`Cubeglass.Mesh.Tests` (87) — the count recorded in
[`docs/notes/s7-gate.md`](notes/s7-gate.md) §8 and confirmed by the
[2026-10-03/04 review](reviews/2026-10-03-full-review.md).

Coverage is collected and the module floors enforced by the `dotnet` CI job, one results directory per test project (`coverage/{coremath,voxel,mesh,gameplay,streaming}`); a missing report fails the job. Local CoreMath example:

```powershell
dotnet test dotnet/tests/CoreMath.Tests --configuration Release --collect:"XPlat Code Coverage" --results-directory dotnet/coverage/coremath
python\.venv\Scripts\python.exe -m depcheck coverage --report dotnet/coverage/coremath --module Cubeglass.CoreMath --floor 95
```

Pass a results **directory** that holds exactly one Cobertura report:
`depcheck coverage` resolves it deterministically and fails on zero or several
candidates, so it can never compare against the wrong run (TD-032). A matched
module with zero coverable lines fails unless `--allow-empty` is passed.

### 4.3 Python and the dependency gates

Create and activate the virtual environment once (ci-local creates it too, if
missing):

```powershell
python -m venv python/.venv
. .\python\.venv\Scripts\Activate.ps1
```

Then, mirroring the `python` CI job:

```powershell
cd python
python -m pip install -r requirements-dev.txt
python -m pip install -e .
python -m ruff check .
python -m mypy calib depcheck
python -m pytest
```

Expected: `ruff` → `All checks passed!`; `mypy` → `Success: no issues found in
6 source files`; `pytest` → all green (78 tests at the
[2026-10-03/04 review](reviews/2026-10-03-full-review.md); the S7 gate note
records 45 before that review's fix waves). `pyproject.toml` sets
`testpaths = ["tests"]`, so the negative fixtures under `scripts/negative/`
never enter this run.

The dependency, contract and licence gates, from the repository root (with the
venv activated):

```powershell
python -m depcheck --root .
python -m depcheck contracts --root .
python -m depcheck licences --root .
```

- `depcheck --root .` — inward-only rules and `layers.json` completeness.
- `depcheck contracts --root .` — compares a normalised fingerprint of the four
  frozen contract files against `contracts/abi-baseline.json`. After a real
  `CG_ABI_VERSION` bump (with an ADR), regenerate the baseline with
  `python -m depcheck contracts --root . --update`; `--update` refuses while the
  version is unchanged.
- `depcheck licences --root .` — dependency → SPDX-id allowlist and SPDX-id
  validation (`contracts/licence-allowlist.json`).

Coverage, CI-style: from `python/`, `python -m pytest --cov=. --cov-report=xml:coverage.xml`, then
`python -m depcheck coverage --report coverage.xml --module calib --floor 90` (repeat with `--module depcheck`).

### 4.4 Unity

Managed and native plugins are git-ignored build outputs, so stage them first
(the script builds `dotnet/src/Streaming` and `dotnet/src/Gameplay` in Release
and copies the five DLLs; it fails loudly when an output is missing):

```powershell
powershell -File scripts/sync-unity-plugins.ps1
unity test unity/Cubeglass --mode EditMode --non-interactive
unity test unity/Cubeglass --mode PlayMode --non-interactive
```

Expected: **EditMode 112/112** and **PlayMode 44/44**, 0 failures, 0 skipped
([`docs/notes/s7-gate.md`](notes/s7-gate.md) §1). `ci-local.ps1` additionally
asserts that the required test assemblies ran: EditMode
`Cubeglass.Unity.Bridge.Tests.dll`, `Cubeglass.Unity.Input.Tests.dll`,
`Cubeglass.Unity.Rendering.Tests.dll`; PlayMode
`Cubeglass.Unity.Rendering.PlayTests.dll`. The CLI writes `test-results.xml` in the working directory by default (git-ignored); `--output <path>` writes elsewhere, which is what `ci-local.ps1` does.

A manual Unity run also needs the native bridge DLL copied by hand if you did
not go through `ci-local.ps1`:

```powershell
Copy-Item cpp/build/windows-msvc/bridge/cg_unity_bridge.dll unity/Cubeglass/Assets/Plugins/win-x64/
```

The scenes are committed and byte-stable; per-commit lanes never regenerate
them. Regeneration is a deliberate menu/batch action
(`Cubeglass/Build Calibration Scene`,
`-executeMethod Cubeglass.Editor.CalibrationSceneBuilder.Build`, and the Game
scene builder) and is only expected when the scene content or Unity version
changes.

### 4.5 All lanes: `scripts/ci-local.ps1`

This is the one-command Windows reproduction of the required CI gates, in order, fail-fast, with a per-lane PASS/FAIL summary. Six lanes: `python-env` (create/reuse `python/.venv`, install requirements + editable package), `cpp-windows` (dev shell, configure/build `windows-msvc`, `ctest --preset ci`), `dotnet` (`dotnet test Cubeglass.sln --configuration Release`), `python` (`ruff`, strict `mypy`, `pytest`), `depcheck` (dependency, contract and licence gates) and `unity` (stage both plugin sets, then EditMode/PlayMode with result and assembly assertions).

```powershell
powershell -File scripts/ci-local.ps1
powershell -File scripts/ci-local.ps1 -SkipUnity
```

Expected tail: `ci-local: ALL LANES PASS` (exit 0). `-SkipUnity` is for
machines without the editor. Deliberately **not** reproduced locally: the
clang-format/clang-tidy checks, the Linux ASan/UBSan/TSan presets and coverage
floors, and the nightly benchmark/supply-chain/mutation/soak lanes — run those
through the workflows or their commands in [`docs/ci.md`](ci.md).

### 4.6 Coverage floors

Enforced in the required jobs from dossier NFR-05 (≥ 90 %), raised to 95 % for
the two core-math modules in S1 ([`docs/ci.md`](ci.md) "Coverage floors"):

| Module | Job | Floor |
| --- | --- | ---: |
| `core-math` (C++) | `cpp-linux-asan` (gcovr + `depcheck coverage`) | 95% |
| `Cubeglass.CoreMath` (.NET) | `dotnet` | 95% |
| `Cubeglass.Voxel` (.NET) | `dotnet` | 90% |
| `Cubeglass.Mesh` (.NET) | `dotnet` | 90% |
| `Cubeglass.Gameplay` (.NET) | `dotnet` | 90% |
| `Cubeglass.Streaming` (.NET) | `dotnet` | 90% |
| `calib` (Python) | `python` | 90% |
| `depcheck` (Python) | `python` | 90% |

### 4.7 Observed timings

Lane times recorded in the gate notes. The most recent full run is the S7 gate
(`scripts/ci-local.ps1 -SkipUnity`, 2026-10-03); a state-free clone is slower
because `python-env` builds the virtual environment from scratch.

| Lane | S7 gate (`-SkipUnity`) | S6 gate | S5 gate | S0 fresh clone |
| --- | ---: | ---: | ---: | ---: |
| python-env | 6.7 s | 8.3 s | 8.3 s | 35.8 s |
| cpp-windows | 16.8 s | 14.0 s | 9.7 s | 10.5 s |
| dotnet | 19.3 s | 21.3 s | 20.3 s | 8.4 s |
| python | 2.3 s | 1.5 s | 3.4 s | 4.3 s |
| depcheck | 0.3 s | 0.2 s | 0.3 s | 0.3 s |
| unity | SKIP | SKIP | SKIP | 24.6 s |
| **overall** | — | — | — | **~84 s** (path-with-spaces clone ~88 s) |

Sources: [`docs/notes/s7-gate.md`](notes/s7-gate.md) §8,
[`s6-gate.md`](notes/s6-gate.md) §6, [`s5-gate.md`](notes/s5-gate.md) §1,
[`s0-gate.md`](notes/s0-gate.md) §3.1–3.2. These are the same scripts CI runs;
the Linux jobs add the sanitizers, TSan and coverage work.

## 5. Running the game

Open `unity/Cubeglass` with Unity **6000.6.3f1** (Unity Hub → Add project) and
open `Assets/Scenes/Game.unity`. `EditorBuildSettings` lists
`[Game, Calibration]`, so the built player boots into the game scene and the
calibration scene stays available for the budget lane. A local Windows x64
build:

```powershell
powershell -File scripts/sync-unity-plugins.ps1
unity run unity/Cubeglass -- -executeMethod Cubeglass.Editor.BuildPlayer.BuildWindows64
```

Output: `unity/Cubeglass/build/StandaloneWindows64/Cubeglass/Cubeglass.exe`.
Saves live under
`%USERPROFILE%\AppData\LocalLow\<company>\<product>\Cubeglass\saves\default`
([`docs/releases/v0.1.0.md`](releases/v0.1.0.md)).

Controls (ADR-0011 mapping table; both backends produce identical frames):

| Action | Gamepad | Keyboard / mouse |
| --- | --- | --- |
| Move | left stick | WASD |
| Continuous turn | right stick X | mouse X |
| Break | X | LMB (hold past hardness) |
| Place | A | RMB |
| Recentre | Y | R |
| Hotbar | RB / LB | E / Q |
| Snap turn ±45° | D-pad left / right | F right / Shift+F left |

`TurnSnap` is degrees **per second**; the snap edge is a separate one-shot
direction; a device-right look is a negative internal yaw. See
[ADR-0011](adr/0011-unity-input-mapping-and-rig-composition.md).

### The calibration scene

`Assets/Scenes/Calibration.unity` is the stereo reference scene for the S6 visual checklist (horizon level, yaw/pitch direction, depth sanity, recentre, U-09 distortion/FOV) and the frame-budget lane: a world-space horizon strip, ±X/±Y/±Z axes and colour-coded yaw/pitch markers at eye height, with `StereoRig`, `LateLatchPose`, the bridge-first `PoseProviderSelector` + synthetic fallback and a `DebugOverlay`. It is committed, byte-stable and regenerated only by its builder; the HIL checklist is in [`docs/questions/S6-HIL.md`](questions/S6-HIL.md).

### How the pipeline loads plugins

- **Managed:** `scripts/sync-unity-plugins.ps1` builds `dotnet/src/Streaming` and `dotnet/src/Gameplay` in Release and copies `Cubeglass.{CoreMath,Voxel,Mesh,Streaming,Gameplay}.dll` into `unity/Cubeglass/Assets/Plugins/managed/`; Unity auto-references them (test asmdefs list them in `precompiledReferences`).
- **Native:** the C++ lane builds `cg_unity_bridge.dll`; `scripts/ci-local.ps1` and the release workflow copy it into `unity/Cubeglass/Assets/Plugins/win-x64/`.
- Both are **git-ignored build outputs**. `System.Text.Json.dll` is deliberately not copied: only the optional `BlockRegistry` JSON path needs it, and the Unity runtime uses `SliceBlockRegistry`.

### Without the native bridge

`PoseProviderSelector` tries the bridge first and falls back to the serialized synthetic provider. A missing, wrong-architecture or stale-export `cg_unity_bridge.dll` (`DllNotFoundException`, `EntryPointNotFoundException`, `BadImageFormatException`) logs an explicit warning and sets `PluginUnavailable`: `cg_unity_bridge could not be loaded (missing, wrong architecture or stale exports); using the synthetic fallback — this run has no real head tracking.`

So the calibration scene and PlayMode tests work with no writer present, and a
packaging regression is visible rather than silent. When the plugin loads but
no writer is running, the fallback is the normal `NotReady` path and is quiet
(TD-023). The game-scene smoke and calibration run on the synthetic fallback by
design until the S12 real writer lands.

## 6. CI/CD

### Required checks (pull requests and `main`)

`ci.yml` runs on every PR and push to `main`; a newer commit cancels an
in-flight run. These six contexts are the branch-protection required checks:

| Check context | Runner | Enforces |
| --- | --- | --- |
| `cpp-windows` | `windows-latest` | MSVC build (warnings as errors), `ctest`, `clang-format --dry-run --Werror` over all `cpp/**`, `clang-tidy` over `cpp/core-math/src` |
| `cpp-linux-asan` | `ubuntu-latest` | ASan/UBSan `ctest`, **the TSan preset and thread-safety tests**, plus the `linux-coverage` build, gcovr report and the `core-math` 95% floor |
| `dotnet` | `ubuntu-latest` | solution build, per-project tests with coverage, the five module floors |
| `python` | `ubuntu-latest` | `ruff`, strict `mypy`, `pytest` with coverage, `calib`/`depcheck` floors |
| `depcheck` | `ubuntu-latest` | inward dependency rules + `layers.json` completeness, and the contract-compatibility gate |
| `licences` | `ubuntu-latest` | licence allowlist and SPDX-id validation |

TSan deliberately runs **inside** `cpp-linux-asan` (R33) so branch protection
keeps its six checks; that job attempts to lower `vm.mmap_rnd_bits` to 28 for
GCC's TSan runtime before running the `linux-tsan` preset, warning and running
at the image default entropy if the write is denied. C++ dependencies come from
vcpkg at the pinned baseline; `cpp-windows` caches the manifest tree keyed on
the baseline and `cpp/vcpkg.json`'s hash. Every action is pinned to a full
commit SHA with a version comment; Dependabot proposes grouped minor/patch
updates weekly.

Per-PR CI does **not** run Unity: the required jobs are secret-free and
`windows-latest` has no editor. The per-commit Unity lane is local
(`scripts/ci-local.ps1`); the EditMode/PlayMode suites become mandatory in CI
in the release workflow.

### Nightly

`nightly.yml` runs daily at 06:00 UTC and on `workflow_dispatch`
(cancel-in-progress is off):

| Job | What it does |
| --- | --- |
| `bench-cpp` | builds and runs `cg_core_math_benchmarks` (JSON artefact) |
| `bench-dotnet` | runs the CoreMath/Voxel/Mesh benchmark projects and the mesh p95 budget harness (`--budget-ms 8`; 8 ms is the shared-runner CI budget, ADR-0007's 2.0 ms stays the release/local budget) |
| `bench-compare` | 10 % regression check against the median of the last five nightly summaries in the `nightly-bench-history-*` actions cache (self-seeding, TD-033/TD-054); the first run bootstraps |
| `supply-chain` | machine-readable NuGet `--vulnerable --include-transitive --format json` via `depcheck nuget`, pinned OSV-Scanner over the Python requirements, and `pip-audit` |
| `mutation` | Stryker on `Cubeglass.Voxel`, break threshold 70 |
| `mutation-gameplay` | Stryker on `Cubeglass.Gameplay`, break threshold 70 (TD-031) |
| `soak` | 30-minute `glasses_soak` leak gate, log always uploaded |

### Release

`release.yml` is **dispatch-only** (`Actions → Release → Run workflow`) with a
`build_target` input; tagged releases are test-gated on both routes (EditMode +
PlayMode run before the player build, and any failure fails the release).

| Route | Runner | Unity licence | Suites / artefact |
| --- | --- | --- | --- |
| `hosted` (default) | `windows-latest` | `UNITY_LICENSE` **or** `UNITY_SERIAL` (one secret only) | EditMode + PlayMode; installs the pinned CLI + editor; uploads `Cubeglass-windows-x64` |
| `self-hosted` | `[self-hosted, windows]` | the machine's own Unity Hub activation (no secrets) | EditMode + PlayMode with the machine's editor; uploads `Cubeglass-v0.1.0-win-x64` |

Credential modes for the hosted route (see
[ADR-0012](adr/0012-ci-unity-licensing-and-release-routes.md)):

| Mode | Secrets | Activation |
| --- | --- | --- |
| Offline licence file | `UNITY_LICENSE` only | writes `Unity_lic.ulf`, `unity license activate --file` |
| Serial | `UNITY_SERIAL` only | `unity license activate --serial`, seat returned with `unity license return --yes` (`if: always()`) |
| Floating server | — | `unity license activate --floating`; not wired into the workflow |
| Hub activation | none | the `self-hosted` route |

Both routes attach the player to the `v0.1.0` release **when that tag
exists**; the tag is owner-gated (R50) and a missing release is a normal
outcome — the player stays a workflow artefact.

### Current operational caveats

- **Hosted checks are billing-blocked (TD-045), so merges are temporarily not check-gated (TD-046).** Since 2026-10-04 09:49Z every hosted Actions job is refused at start ("recent account payments have failed or your spending limit needs to be increased"), blocking per-PR CI, Nightly and the hosted release; two CI-only PRs were admin-merged while that lasted. Fix is account-level (GitHub Billing → Plans and usage → settle/raise the limit), then `gh run rerun` the failed runs and return to gated merges; the self-hosted route is unaffected.
- **Self-hosted runner setup (one-off).** Repo → **Settings → Actions → Runners → New self-hosted runner → Windows**; run `config.cmd` with labels `self-hosted, windows`. Start it as the Windows user that activated Unity through the Hub (the Personal licence lives in `%LOCALAPPDATA%\Unity\licenses` — a system service cannot see it). That user's PATH needs the runner agent, the Unity CLI and VS Build Tools with the C++ x64 toolset; `gh` is needed only for the release attach (skipped with a warning when absent, TD-036). Prefer PowerShell 5.1, not `pwsh`.
- **Runner persistence (TD-047).** `cubeglass-local` currently runs as a session-scoped background process and stops when its session ends; for persistence across logins install the service (`C:\actions-runner\svc.cmd install`) as the same Windows user.
- **Unity licence options** are in the table above; the owner's Unity Personal licence is Hub-activated, which is why the self-hosted route exists. `unity license activate --personal` is rejected when the CLI runs with service-account tokens. The self-hosted route was verified end to end in run 37198879954 (2026-10-04), producing `Cubeglass-v0.1.0-win-x64` (~36 MB); the hosted route remains unverified while TD-045 is open.

## 7. HIL runbooks

Three hardware-in-the-loop escalations gate the withheld tags and the release.
Each is a full runbook under [`docs/questions/`](questions/) with the exact
commands, what to attach, what to commit and how the resolution is recorded.

| Runbook | Stage | The ask | Artefacts to commit | Unblocks on success |
| --- | --- | --- | --- | --- |
| [`S5-HIL.md`](questions/S5-HIL.md) | S5 glasses | Attach the Luma Ultra + VITURE SDK on Windows and run `cg-pose-probe --source viture` for 60 s: does 3DoF Carina pose work, at what rate/latency (U-01), and how do 3D/SBS mode and refresh behave (U-08)? | `docs/notes/s5-hil/pose_probe.csv` + `pose_probe.log` | `stage-5-complete`; ADR-0009 `proposed → accepted` |
| [`S6-HIL.md`](questions/S6-HIL.md) | S6 stereo | Run the committed Calibration scene on the glasses at 90 Hz and tick the visual checklist (horizon, yaw/pitch, depth, recentre, distortion/FOV U-09) | `docs/notes/s6-hil/checklist.md` (+ screenshots if capturable) | `stage-6-complete`; ADR-0010 status/defaults |
| [`S7-HIL.md`](questions/S7-HIL.md) | S7 / M1 | Play the built Windows x64 player on the glasses: comfort (snap turn + vignette), both input paths, no stuck states, break/place/save/reload, view distance 8 at 90 Hz on the RTX 5070 | `docs/notes/s7-hil/checklist.md` + perf log/screenshots | `stage-7-complete` (M1) **and** the `v0.1.0` tag (with the CI player build) |

An S7 checklist box that fails is a finding: record the screenshot/log, reopen the owning work item, and leave the tag withheld until the fix and a re-run. The S5/S6 HIL asks also remain open, surfaced together with S7 ([`docs/notes/s7-gate.md`](notes/s7-gate.md) §11). Escalation mechanics and the "record the resolution, dated, before archiving" rule are in [`docs/questions/README.md`](questions/README.md).

## 8. Troubleshooting

| Symptom | Cause | Fix |
| --- | --- | --- |
| `Unity CLI not found at '...\Unity\bin\unity.exe'` from `ci-local.ps1` | The out-of-band Unity CLI 1.0.0-beta.11 is not installed (it is not a `bootstrap-dev.ps1` pin) | Run the install snippet in section 1; open a new shell; `unity --version` |
| `Can't reach the Unity licensing client` (hosted release) | A fresh runner has not installed the CLI's managed licensing client | The workflow runs `unity plugin install licensingClient` before activation; do the same manually before `unity license activate` (ADR-0012) |
| `actions/setup-dotnet` fails with an elevation/access error on the self-hosted runner | The unelevated runner user cannot write `C:\Program Files\dotnet` | The self-hosted job verifies `dotnet --list-sdks` against `dotnet/global.json` instead; install SDK 10.0.401 on the machine, or use the hosted route |
| vcpkg baseline mismatch (`bootstrap-dev.ps1 -Check` fails the vcpkg row) | The `%USERPROFILE%\vcpkg` checkout is not at the pinned commit | `git -C "$env:USERPROFILE\vcpkg" checkout eb2d3a3279fd019cb7733072d86900d0ad2a1aef`, then re-run bootstrap. On the self-hosted release route a mismatch only warns and proceeds (manifest versioning targets the baseline) |
| Release builder refused a dirty checkout (historical, game-ci era) | The old `game-ci/unity-builder` demanded a clean `git status --porcelain`; staged plugins/vcpkg inside the workspace made it dirty | Current workflow uses the pinned Unity CLI; all staged DLLs are git-ignored and vcpkg lives outside the checkout (`release.yml`, [`docs/ci.md`](ci.md)). Never move build outputs into tracked paths |
| `ProjectSettings/*.asset` shows as modified with no content diff (CRLF/LF warning) after opening Unity | Unity/line-ending churn on a tracked settings file (`ProjectAuditorSettings.asset`, `ProjectSettings.asset`) | Do not commit it: restore the file (`git restore unity/Cubeglass/ProjectSettings/<file>`) and keep `.gitattributes`/`core.autocrlf` consistent; the review waves explicitly restored this churn before each commit |
| `No module named depcheck` / `pytest` fails with the wrong interpreter | The repository-root `python` is not the environment with `pip install -e python` applied | Use `python\.venv\Scripts\python.exe -m depcheck ...` (or activate `python/.venv`); `ci-local.ps1` creates and reuses that venv |
| `ci-local.ps1` says the venv exists but the interpreter is missing / stale packages | `python/.venv` was created by a different Python or was partially deleted; ci-local reuses an existing venv (`python/.venv already exists; reusing it.`) and throws when `Scripts\python.exe` is absent | Delete `python/.venv` and re-run `ci-local.ps1` (it recreates and reinstalls), or recreate manually per section 4.3 |
| Older docs display `â€”`, `Ã±`-style mojibake | A UTF-8 file read with the legacy system codepage (CP1252/CP949); Windows PowerShell 5.1 and some editors pick the codepage when there is no BOM — the C++ lane fixed the same class of failure with MSVC `/utf-8` (M-10) | Read/write with explicit UTF-8 (`Get-Content -Encoding UTF8`; save as UTF-8 with BOM or ASCII-only in editors). A repo-wide scan at this snapshot found no mojibake sequences left |
| `gh: not found` during the self-hosted release attach | The GitHub CLI is not on the runner user's PATH | The attach is optional: the job skips it with a warning and the player stays a workflow artefact (TD-036). Install GitHub CLI 2.102.0 on the runner to publish |
| `native bridge plugin not found at '...Assets/Plugins/win-x64/cg_unity_bridge.dll'` / `DllNotFoundException` in Unity tests | Plugin DLLs are git-ignored build outputs and were never staged after a clean clone | Build `cg_unity_bridge.dll` (`cmake --build --preset windows-msvc`), run `powershell -File scripts/sync-unity-plugins.ps1`, or run `scripts/ci-local.ps1` which stages both; the tests fail loudly rather than skipping |
| Hosted Actions jobs refused: "recent account payments have failed..." | Account-level billing block (TD-045) | Settle the payment / raise the spending limit, then re-run; per-PR CI, Nightly and the hosted release are all affected. Use the self-hosted release route meanwhile |
| Unity tests report no results, total=0, skipped tests, or a missing assembly | Stale/missing NUnit output, wrong mode, or results from another suite | `ci-local.ps1` fails loudly; re-run both `unity test` commands from the repository root with the relative project path `unity/Cubeglass`, and check the required assembly names in section 4.4 |
| Unity test aborts with a doubled project path on a pristine clone | The Unity CLI resolves the project argument against the current directory when the project has no `Assets` folder yet | `ci-local.ps1` runs from the root and creates `unity/Cubeglass/Assets` first; do the same manually (s0-gate §4) |

## 9. Where the truth lives

| Source | What it is |
| --- | --- |
| [`Cubeglass_Engineering_Dossier.md`](../Cubeglass_Engineering_Dossier.md) | The authoritative specification and build contract; where any other document disagrees, the dossier wins. Stages, contracts, gates and hard rules live here |
| [`docs/ARCHITECTURE.md`](ARCHITECTURE.md) | The implemented architecture: modules, layers, dependency rules, data flow, milestone state and the implementation notes where the code differs from the dossier |
| [`docs/CONTRACTS.md`](CONTRACTS.md) | Contract register and change runbook: what a contract is, the frozen-contract rule, the per-artefact register (header, layout, save format, toolchain/Unity pins), the `depcheck contracts` fingerprint gate and the ADR process |
| [`docs/CHANGELOG.md`](CHANGELOG.md) | Consolidated engineering history by stage (S0–S7, the review waves, CI/release fixes), citing the merge commits and PRs |
| [`docs/adr/`](adr/) | Architecture decision records 0001–0012 (MADR; change a contract, dependency or architecture rule only with one) |
| [`docs/notes/s0-gate.md`](notes/s0-gate.md) … [`s7-gate.md`](notes/s7-gate.md) | Stage exit-gate evidence: commands, counts, hashes, run IDs and deferred minors |
| [`docs/notes/tech-debt.md`](notes/tech-debt.md) | The programme debt register (TD-NNN). Add rows, never delete or renumber; fixed items move to Closed with the PR |
| [`docs/releases/v0.1.0.md`](releases/v0.1.0.md) | Release-candidate notes for M1, known issues and build/run instructions |
| [`docs/reviews/2026-10-03-full-review.md`](reviews/2026-10-03-full-review.md) | The consolidated programme review: findings, dispositions, verification and residual owner actions |
| [`docs/perf/README.md`](perf/README.md) | Performance methodology and budgets. Measured numbers are **not** committed; the nightly artefacts and the self-seeding benchmark-history cache are the evidence |
| [`docs/ci.md`](ci.md), [`docs/ci/negative-gates.md`](ci/negative-gates.md) | Gate details, local reproduction and the dispatch-only negative self-tests |
| [`docs/questions/`](questions/) | Escalations and HIL runbooks. Stop and ask via this directory if a gate cannot be met, a contract conflicts or hardware contradicts a recorded fact |
| [`docs/superpowers/plans/`](superpowers/plans/) | The S0–S7 implementation plans (historical; the gate notes supersede them for results) |
| [`docs/toolchains.md`](toolchains.md) | Version pins (section 1 is a summary; this file is the truth) |
| [`CONTRIBUTING.md`](../CONTRIBUTING.md) | Hard rules, branching/commits, the work-item template and the PR protocol |

Two rules govern everything above:

1. **One stage at a time.** Do not start stage N+1 until every exit-gate item of stage N is green in CI and tagged `stage-N-complete` (dossier §0, rule 1). The withheld S5–S7 tags are why the HIL runbooks exist.
2. **Contracts are frozen.** Changing one needs an ADR and an ABI version bump; never change one silently to make code compile. Tests come first, hardware stays behind ports/fakes outside HIL, and unknowns are escalated in `docs/questions/` rather than guessed (dossier §0, rules 2–7).

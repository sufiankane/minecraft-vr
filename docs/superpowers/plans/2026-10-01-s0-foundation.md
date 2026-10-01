# Cubeglass S0 — Foundation and Engineering Platform: Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A repository where every later stage can be built, tested and reviewed automatically — monorepo layout, pinned toolchains, one green lane per language, CI gates that block merges, dependency-rule and licence checks, coverage and nightly benchmarks, ADR-0001..0003, CONTRIBUTING and the PR checklist — verified from a fresh clone and tagged `stage-0-complete`.

**Architecture:** One repository, one build per language. C++ builds with CMake + presets + vcpkg (pinned baseline) and Ninja; C# with a .NET solution of netstandard2.1 domain libraries and NUnit tests; Unity as a project with asmdef packages and EditMode tests driven by the Unity CLI; Python as one project for offline tools. Cross-cutting gates (dependency rule, licence, coverage) are implemented once as tested Python tooling and wired into GitHub Actions. No contract behaviour is implemented in S0 — skeletons only.

**Tech Stack:** C++20 (MSVC 18 / clang), CMake + presets, vcpkg manifest, Ninja, GoogleTest, Google Benchmark; .NET SDK 10 (LTS), netstandard2.1 + NUnit + coverlet; Unity 6000.6.3f1 with `com.unity.test-framework`; Python 3.14 with ruff, mypy --strict, pytest, gcovr; GitHub Actions (windows-latest, ubuntu-latest).

**Spec:** `Cubeglass_Engineering_Dossier.md` — section 0 (rules), 3.3 (dependency rule), 4.1 (layout), 4.2 (tooling), 4.3 (lifecycle), 4.4 (test strategy), section 6 / S0 (deliverables, work items, required tests, exit gate), 9 (CI gates), 11 (operating protocol), 12.A (ADR list).

## Global Constraints

- C++20. Warnings as errors: `-Wall -Wextra -Wpedantic -Wconversion -Werror`; MSVC `/W4 /WX`. No raw owning pointers; RAII; no exceptions across module or ABI boundaries; errors via `Result<T>`.
- C# nullable enabled; `TreatWarningsAsErrors`; domain libraries target `netstandard2.1`; no `UnityEngine` in non-Unity assemblies; no allocation in hot paths.
- Pure logic modules never reference the engine, the VITURE SDK, ONNX Runtime, the file system or threads. Pure set: `core-math`, `handcore` (C++) and `Cubeglass.CoreMath`, `Cubeglass.Voxel`, `Cubeglass.Mesh`, `Cubeglass.Gameplay` (C#).
- Tests deterministic and hardware-free by default; hardware tests are tagged `hil` and excluded from default runs.
- Contracts (section 5) are frozen; changes require an ADR and a version bump. No dependency or architecture change without an ADR.
- Conventional Commits; trunk-based; short-lived branches; squash-merge; PR checklist from section 11.3; reviewer separate from author.
- One stage at a time: no S1 work in this plan.
- Every pin lives in `docs/toolchains.md`; scripts fail loudly on mismatch, never silently proceed.
- Repository layout follows section 4.1. Additions beyond it (e.g. `python/depcheck`) are recorded in ADR-0001.
- Unity editor pinned to 6000.6.3f1 (the installed editor); recorded in `ProjectVersion.txt` and ADR-0001. Rendering-pipeline choice stays with ADR-0005 (S6).

## Review Focus

1. **Fresh clone on a clean machine** (no vcpkg cache, no `.venv`, no global SDK pins): one command per language must go green. Tested in Task 11.
2. **Checkout path containing spaces**: bootstrap and build scripts must quote every path end-to-end. Tested in Task 11 (clone into a spaced directory).
3. **Linux checkout freshness**: shell scripts and CI configs must survive LF conversion and behave identically to Windows. `.gitattributes` in Task 1; exercised by the `cpp-linux-asan` and `python` jobs in Task 7.
4. **Missing or wrong toolchain version**: bootstrap `-Check` must fail naming the exact required version. Tested in Task 1 with a forced bogus pin.
5. **Dependency-rule false negatives**: the checker must catch forbidden `#include`s and forbidden C# namespaces/project references, and must not flag compliant code. Tested with fixtures in Task 5.

---

### Task 1: Bootstrap, toolchain pins, repository hygiene

**Work item:** S0-WI1a

**Files:**
- Create: `scripts/bootstrap-dev.ps1`
- Create: `scripts/dev-shell.ps1`
- Create: `docs/toolchains.md`
- Create: `.gitattributes`
- Create: `.gitignore`
- Create: `README.md`
- Commit: `docs/superpowers/plans/2026-10-01-s0-foundation.md` (this plan)

**Interfaces:**
- Produces: `scripts/bootstrap-dev.ps1 [-Check] [-ExpectedOverride "<tool>=<version>"]` — exit 0 when every pin matches, exit 1 with a PASS/FAIL table otherwise. `scripts/dev-shell.ps1` — enters the MSVC x64 developer shell (later used by every local C++ build). `docs/toolchains.md` — the single pin table every later task and CI reads.

- [ ] **Step 1: Write `docs/toolchains.md`** with one row per tool: name, required version, how checked, install command. Pins: CMake (latest stable, record exact after install), Ninja (latest stable, record exact), LLVM clang-format/clang-tidy (latest stable, record exact), VS Build Tools 18 with `Microsoft.VisualStudio.Component.VC.Tools.x86.x64` (already installed at `C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools`), .NET SDK 10.0.x (record exact from `dotnet --version`), vcpkg at a recorded commit, Python 3.14.7, Unity editor 6000.6.3f1, GitHub CLI (latest stable), Node ≥24 (Unity CLI host, already present).
- [ ] **Step 2: Write `scripts/bootstrap-dev.ps1`.** Idempotent winget installs using `--accept-source-agreements --accept-package-agreements`: `Kitware.CMake`, `Ninja-build.Ninja`, `LLVM.LLVM`, `Microsoft.DotNet.SDK.10`, `GitHub.cli`. vcpkg: clone `https://github.com/microsoft/vcpkg` to `$env:USERPROFILE\vcpkg` if absent, run `bootstrap-vcpkg.bat -disableMetrics`, print the cloned HEAD sha. `-Check` mode: verify each pin (parse floors from `docs/toolchains.md`), print a PASS/FAIL table, exit 1 on any failure; `-ExpectedOverride "<tool>=<version>"` forces an expected version for testing the failure path.
- [ ] **Step 3: Write `scripts/dev-shell.ps1`.** Locate VS with `vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64`, import `Microsoft.VisualStudio.DevShell.dll`, call `Enter-VsDevShell` for x64. Fail with a clear message if not found.
- [ ] **Step 4: Write `.gitattributes`.** `* text=auto`; `*.sh text eol=lf`; `*.ps1 text eol=crlf`; `*.cs`, `*.cpp`, `*.hpp`, `*.h`, `*.json`, `*.yml`, `*.md text eol=lf`; `*.pgm -text`; `data/** -text`.
- [ ] **Step 5: Write `.gitignore`.** `build/`, `out/`, `vcpkg_installed/`, `**/.venv/`, `__pycache__/`, `.mypy_cache/`, `.ruff_cache/`, `.pytest_cache/`, `bin/`, `obj/`, `unity/**/Library/`, `unity/**/Temp/`, `unity/**/Logs/`, `unity/**/UserSettings/`, `unity/**/obj/`, `.ctxo/.cache/`, `.DS_Store`, `Thumbs.db`.
- [ ] **Step 6: Write `README.md`.** One paragraph of purpose, bootstrap instructions, and the one command per language (kept in sync with `CONTRIBUTING.md`): C++, C#, Python, Unity, depcheck.
- [ ] **Step 7: Install and verify.** Run `powershell -File scripts/bootstrap-dev.ps1`, then `powershell -File scripts/bootstrap-dev.ps1 -Check`. Expected: every row PASS, exit code 0. Record exact installed versions into `docs/toolchains.md`.
- [ ] **Step 8: Verify the version gate fails loudly.** Run `powershell -File scripts/bootstrap-dev.ps1 -Check -ExpectedOverride "cmake=99.0.0"`. Expected: cmake row FAIL naming 99.0.0 vs the installed version, exit code 1.
- [ ] **Step 9: Commit**

```powershell
git add scripts docs/toolchains.md docs/superpowers/plans .gitattributes .gitignore README.md
git commit -m "build: add developer bootstrap, toolchain pins and repo hygiene"
```

---

### Task 2: C++ lane — CMake, vcpkg, core-math skeleton, format/lint configs

**Work item:** S0-WI1b

**Files:**
- Create: `cpp/CMakeLists.txt`, `cpp/CMakePresets.json`, `cpp/vcpkg.json`
- Create: `cpp/core-math/CMakeLists.txt`, `cpp/core-math/include/cg/core_math/version.hpp`, `cpp/core-math/src/version.cpp`
- Create: `cpp/tests/CMakeLists.txt`, `cpp/tests/core-math/version_test.cpp`
- Create: `.clang-format`, `.clang-tidy`
- Create: `contracts/.gitkeep` (empty contracts directory for later stages)

**Interfaces:**
- Produces: C++ target `cg_core_math` (alias `cg::core_math`) exposing `int cg::core_math::AbiVersion() noexcept` returning `1`; test executable `cg_core_math_tests` registered with CTest as `core_math`; CMake presets `windows-msvc`, `linux-ci`, `linux-asan`, `benchmarks` (configure+build+test preset names identical to configure names, test preset `ci`); vcpkg manifest with `gtest`. CI and later stages consume these exact names.

- [ ] **Step 1: Write the failing test** `cpp/tests/core-math/version_test.cpp`:

```cpp
#include <gtest/gtest.h>
#include "cg/core_math/version.hpp"

TEST(CoreMathVersion, AbiVersionIsOne) {
  EXPECT_EQ(cg::core_math::AbiVersion(), 1);
}
```

- [ ] **Step 2: Write the build files** — `cpp/vcpkg.json` (name `cubeglass-cpp`, `builtin-baseline` set to the vcpkg commit recorded in Task 1, dependency `gtest`), `cpp/CMakeLists.txt` (C++20, `-Wall -Wextra -Wpedantic -Wconversion -Werror` or `/W4 /WX`, `find_package(GTest CONFIG REQUIRED)`, enable `ctest`), module `CMakeLists.txt` files, and `cpp/CMakePresets.json` with the four presets (Ninja generator; `windows-msvc` uses `$env{VCPKG_ROOT}/scripts/buildsystems/vcpkg.cmake` and inherits the dev-shell environment; `linux-asan` adds `-fsanitize=address,undefined -fno-omit-frame-pointer`).
- [ ] **Step 3: Run to verify failure.** From `scripts/dev-shell.ps1`: `cmake --preset windows-msvc; cmake --build --preset windows-msvc`. Expected: compile failure — `cg/core_math/version.hpp` not found.
- [ ] **Step 4: Implement the skeleton.** `version.hpp` declares `inline constexpr int kAbiVersion = 1;` and `int AbiVersion() noexcept;`; `version.cpp` returns it. No contract logic.
- [ ] **Step 5: Run to verify pass.** `cmake --preset windows-msvc; cmake --build --preset windows-msvc; ctest --preset ci`. Expected: `core_math` test PASS.
- [ ] **Step 6: Verify formatting and lint.** `clang-format --dry-run --Werror cpp/core-math/include/cg/core_math/version.hpp cpp/core-math/src/version.cpp`, then `clang-tidy -p build/windows-msvc cpp/core-math/src/version.cpp`. Expected: no findings. Add `.clang-format` (LLVM base, `IndentWidth: 4`, `ColumnLimit: 120`) and `.clang-tidy` (checks: `bugprone-*`, `performance-*`, `cppcoreguidelines-*` subset; `WarningsAsErrors: '*'`).
- [ ] **Step 7: Commit**

```powershell
git add cpp .clang-format .clang-tidy contracts/.gitkeep
git commit -m "build(cpp): add CMake/vcpkg skeleton and core-math placeholder with gtest"
```

---

### Task 3: .NET lane — solution, skeleton libraries, NUnit test

**Work item:** S0-WI1c

**Files:**
- Create: `dotnet/Cubeglass.sln`, `dotnet/global.json`, `dotnet/Directory.Build.props`, `dotnet/Directory.Packages.props`
- Create: `dotnet/src/CoreMath/Cubeglass.CoreMath.csproj`, `dotnet/src/CoreMath/AbiVersion.cs`
- Create: `dotnet/src/Voxel/Cubeglass.Voxel.csproj` (+ placeholder `AssemblyInfo` marker class), `dotnet/src/Mesh/Cubeglass.Mesh.csproj`, `dotnet/src/Gameplay/Cubeglass.Gameplay.csproj`
- Create: `dotnet/tests/CoreMath.Tests/Cubeglass.CoreMath.Tests.csproj`, `dotnet/tests/CoreMath.Tests/AbiVersionTests.cs`

**Interfaces:**
- Produces: solution `dotnet/Cubeglass.sln`; `public static class Cubeglass.CoreMath.AbiVersion { public const int Value = 1; }`; test project `Cubeglass.CoreMath.Tests` using NUnit; `dotnet test dotnet/Cubeglass.sln` is the single .NET command for all stages.

- [ ] **Step 1: Pin the SDK.** Run `dotnet --version` after Task 1; write `dotnet/global.json` with that exact SDK version and `"rollForward": "latestPatch"`. Write `Directory.Build.props`: `Nullable=enable`, `TreatWarningsAsErrors=true`, `EnableNETAnalyzers=true`, `AnalysisLevel=latest-recommended`, `Deterministic=true`; domain libraries add `TargetFramework=netstandard2.1`, `LangVersion=9.0`.
- [ ] **Step 2: Write the failing test** `AbiVersionTests.cs` asserting `Cubeglass.CoreMath.AbiVersion.Value == 1`.
- [ ] **Step 3: Write the projects** — four class libraries (Voxel, Mesh, Gameplay reference CoreMath only, matching section 3.2 dependency direction) and the NUnit test project (net10.0, `NUnit`, `NUnit3TestAdapter`, `Microsoft.NET.Test.Sdk`, `coverlet.collector`; versions centralized and pinned in `Directory.Packages.props`).
- [ ] **Step 4: Run to verify failure.** `dotnet test dotnet/Cubeglass.sln`. Expected: compile error — `AbiVersion` does not exist.
- [ ] **Step 5: Implement the skeleton types** (one marker class per library; only `AbiVersion.Value` has behaviour).
- [ ] **Step 6: Run to verify pass.** `dotnet test dotnet/Cubeglass.sln`. Expected: 1 test PASS, no warnings.
- [ ] **Step 7: Commit**

```powershell
git add dotnet
git commit -m "build(dotnet): add solution, netstandard2.1 skeletons and NUnit placeholder test"
```

---

### Task 4: Python lane — calib project skeleton, ruff, mypy --strict, pytest

**Work item:** S0-WI1d

**Files:**
- Create: `python/pyproject.toml`
- Create: `python/calib/__init__.py`, `python/calib/py.typed`
- Create: `python/tests/test_calib.py`
- Create: `python/requirements-dev.txt`

**Interfaces:**
- Produces: importable package `calib` exposing `calib.SCHEMA_VERSION: int = 1`; commands `python -m pytest python`, `python -m ruff check python`, `python -m mypy python` (mypy --strict via pyproject) — the single Python lane used by CI and later stages.

- [ ] **Step 1: Write the failing test** `python/tests/test_calib.py`: `from calib import SCHEMA_VERSION` and assert it equals 1.
- [ ] **Step 2: Write `python/pyproject.toml`** — project `cubeglass-tools`, requires-python `>=3.11`, packages `calib`, `depcheck`; `[tool.ruff]` (target py311, line-length 120), `[tool.mypy]` (`strict = true`), `[tool.pytest.ini_options]` (`testpaths = ["tests"]`). Write `requirements-dev.txt` with exact pinned ruff, mypy, pytest, coverage versions (record in `docs/toolchains.md`).
- [ ] **Step 3: Run to verify failure.** In `python/`: create `.venv`, `pip install -r requirements-dev.txt`, `python -m pytest`. Expected: collection error — `calib` not found.
- [ ] **Step 4: Implement the skeleton** `calib/__init__.py` with `SCHEMA_VERSION: int = 1` and a docstring.
- [ ] **Step 5: Run to verify pass.** `python -m pytest`, `python -m ruff check .`, `python -m mypy calib`. Expected: PASS, no findings.
- [ ] **Step 6: Commit**

```powershell
git add python
git commit -m "build(python): add calib skeleton with ruff, strict mypy and pytest"
```

---

### Task 5: Dependency-rule checker (Python, tested)

**Work item:** S0-WI3

**Files:**
- Create: `python/depcheck/__init__.py`, `python/depcheck/__main__.py`, `python/depcheck/rules.py`
- Create: `contracts/layers.json`
- Create: `python/tests/test_depcheck.py`, `python/tests/fixtures/depcheck/**`

**Interfaces:**
- Consumes: `python/` project from Task 4.
- Produces: `python -m depcheck [--root PATH]` — scans C++ includes and C# sources/project references, prints each violation as `path:line rule`, exits 1 on any violation, 0 otherwise. Rule data comes from `contracts/layers.json` (schema below) — the only place rule lists may change.
- `contracts/layers.json` shape:

```json
{
  "cpp": { "core-math": { "forbidIncludes": ["windows.h", "onnxruntime", "xr_", "viture", "thread", "fstream", "filesystem", "mutex"] },
           "handcore":  { "forbidIncludes": ["windows.h", "onnxruntime", "xr_", "viture", "thread", "fstream", "filesystem", "mutex"] } },
  "dotnet": { "Cubeglass.CoreMath": { "forbidNamespaces": ["UnityEngine", "UnityEditor", "System.IO", "System.Threading"],
                                       "allowedProjectReferences": [] },
              "Cubeglass.Voxel":     { "forbidNamespaces": ["UnityEngine", "UnityEditor"],
                                       "allowedProjectReferences": ["Cubeglass.CoreMath"] },
              "Cubeglass.Mesh":      { "forbidNamespaces": ["UnityEngine", "UnityEditor"],
                                       "allowedProjectReferences": ["Cubeglass.CoreMath", "Cubeglass.Voxel"] },
              "Cubeglass.Gameplay":  { "forbidNamespaces": ["UnityEngine", "UnityEditor"],
                                       "allowedProjectReferences": ["Cubeglass.CoreMath", "Cubeglass.Voxel"] } }
}
```

- [ ] **Step 1: Write failing tests** in `python/tests/test_depcheck.py` using fixture trees: (a) compliant C++ file → no violations; (b) `#include <thread>` in a core-math file → violation at the right line; (c) compliant C# file/project → none; (d) `using UnityEngine;` in `Cubeglass.Voxel` → violation; (e) a `<ProjectReference>` not in `allowedProjectReferences` → violation. Assert exit codes via the CLI entry point.
- [ ] **Step 2: Run to verify failure.** `python -m pytest python/tests/test_depcheck.py`. Expected: import error — `depcheck` not found.
- [ ] **Step 3: Implement `depcheck`** — load `contracts/layers.json`, walk `cpp/**` matching module directory to rule key, parse `#include` lines; walk `dotnet/src/**` for `.cs` `using` directives and `.csproj` `ProjectReference` entries; return violations. Keep parsing line-based and dependency-free (standard library only).
- [ ] **Step 4: Run to verify pass.** `python -m pytest python/tests` and `python -m depcheck --root .` (from repository root). Expected: tests PASS; clean repo exit 0.
- [ ] **Step 5: Verify against a real violation.** Temporarily add `#include <thread>` to `cpp/core-math/src/version.cpp`, run `python -m depcheck`, expect exit 1 naming the file; revert.
- [ ] **Step 6: Commit**

```powershell
git add python/depcheck python/tests contracts/layers.json
git commit -m "feat(depcheck): enforce inward-only dependencies for C++ and C#"
```

---

### Task 6: Unity lane — project skeleton, asmdef package, EditMode test

**Work item:** S0-WI2

**Files:**
- Create: `unity/Cubeglass/` (project created with editor 6000.6.3f1)
- Create: `unity/Cubeglass/Packages/com.cubeglass.placeholder/package.json`, `Runtime/Cubeglass.Placeholder.asmdef`, `Runtime/AbiVersion.cs`
- Create: `unity/Cubeglass/Packages/com.cubeglass.placeholder/Tests/EditMode/Cubeglass.Placeholder.Tests.asmdef`, `Tests/EditMode/AbiVersionTests.cs`
- Modify: `unity/Cubeglass/Packages/manifest.json` (add `com.unity.test-framework`)

**Interfaces:**
- Produces: Unity project at `unity/Cubeglass` pinned to 6000.6.3f1; package `com.cubeglass.placeholder` with assembly `Cubeglass.Placeholder` exposing `public static class AbiVersion { public const int Value = 1; }`; EditMode test assembly `Cubeglass.Placeholder.Tests`; command `unity test --project unity/Cubeglass` (exact flags confirmed from `unity test --help` and recorded in `docs/toolchains.md`) — the Unity lane for later stages.

- [ ] **Step 1: Create the project** with the installed editor: `& "C:\Program Files\Unity\Hub\Editor\6000.6.3f1\Editor\Unity.exe" -batchmode -quit -createProject "<repo>\unity\Cubeglass" -logFile -`. Confirm `ProjectSettings/ProjectVersion.txt` says `m_EditorVersion: 6000.6.3f1`.
- [ ] **Step 2: Write the failing EditMode test** `AbiVersionTests.cs` (NUnit, `[Test] public void AbiVersion_IsOne()`) referencing `Cubeglass.Placeholder.AbiVersion`.
- [ ] **Step 3: Write the package and asmdefs** — `package.json` (name `com.cubeglass.placeholder`, version 0.1.0, `testables` for EditMode), runtime asmdef with no references, test asmdef with `"references": ["Cubeglass.Placeholder", "UnityEngine.TestRunner", "UnityEditor.TestRunner"]`, `"includePlatforms": ["Editor"]`, `"defineConstraints": ["UNITY_INCLUDE_TESTS"]`.
- [ ] **Step 4: Run to verify failure.** Run `unity test --help` to capture the exact invocation, record it in `docs/toolchains.md`, then run it against the project. Expected: compile error — `AbiVersion` not found.
- [ ] **Step 5: Implement `AbiVersion.cs`** and re-run. Expected: EditMode test PASS.
- [ ] **Step 6: Commit** (Unity `Library/`, `Temp/`, `Logs/`, `UserSettings/` are already ignored)

```powershell
git add unity/Cubeglass unity/Cubeglass/Packages
git commit -m "build(unity): add pinned project skeleton with placeholder EditMode test"
```

---

### Task 7: CI pipeline — per-language jobs, dependency-rule and licence gates, branch protection

**Work item:** S0-WI2b

**Files:**
- Create: `.github/workflows/ci.yml`
- Create: `contracts/licence-allowlist.json`
- Create: `python/depcheck/licences.py`, tests in `python/tests/test_depcheck_licences.py`
- Create: `docs/ci.md`

**Interfaces:**
- Consumes: presets from Task 2, solution from Task 3, Python project from Task 4, checker from Task 5, Unity project from Task 6.
- Produces: required check contexts (used for branch protection): `cpp-windows`, `cpp-linux-asan`, `dotnet`, `python`, `depcheck`, `licences`; `python -m depcheck licences` verifying every declared dependency (vcpkg ports and NuGet packages) has an entry with an allowed licence id in `contracts/licence-allowlist.json`.

- [ ] **Step 1: Write licence-check tests first** — fixture manifest with a package missing from the allowlist fails; complete allowlist passes. Run `python -m pytest` to see them fail.
- [ ] **Step 2: Implement `depcheck licences`** — read `cpp/vcpkg.json` dependencies and `dotnet/Directory.Packages.props` package ids; require each in `contracts/licence-allowlist.json` (seed: gtest BSD-3-Clause, benchmark Apache-2.0, NUnit/Microsoft.NET.Test.Sdk/NUnit3TestAdapter/coverlet.collector MIT); exit 1 listing unknown packages.
- [ ] **Step 3: Write `.github/workflows/ci.yml`** with jobs:
  - `cpp-windows` (windows-latest): `ilammy/msvc-dev-cmd@v1`, vcpkg cache, `cmake --preset windows-msvc`, build, `ctest --preset ci`, `clang-format --dry-run --Werror`, `clang-tidy`;
  - `cpp-linux-asan` (ubuntu-latest): vcpkg bootstrap, `cmake --preset linux-asan`, build, `ctest --preset ci`;
  - `dotnet` (ubuntu-latest): `actions/setup-dotnet` with the `global.json` version, `dotnet test dotnet/Cubeglass.sln --configuration Release`;
  - `python` (ubuntu-latest, Python 3.14): install dev requirements, ruff, mypy --strict, pytest;
  - `depcheck` (ubuntu-latest): `python -m depcheck --root .`;
  - `licences` (ubuntu-latest): `python -m depcheck licences --root .`.
  Trigger on `pull_request` and `push` to `main`; `concurrency` cancel-in-progress per ref.
- [ ] **Step 4: Validate workflow syntax.** Install `actionlint` via winget (`winget install --id rhysd.actionlint -e`) and run it on `.github/workflows/ci.yml`. Expected: no errors. If the package is unavailable, validate by pushing (Step 6).
- [ ] **Step 5: Document the gates** in `docs/ci.md` (job names, what each enforces, how to reproduce locally, negative-gate pointer to Task 8).
- [ ] **Step 6: Push the branch and verify CI green.** Create `s0/ci` branch, push, open a PR with `gh pr create`, `gh run watch --exit-status`. Expected: all six checks green. Then squash-merge.
- [ ] **Step 7: Protect `main`.** `gh api -X PUT repos/sufiankane/minecraft-vr/branches/main/protection` requiring the six check contexts, PR reviews not required for solo owner, no force-push. If `gh` lacks admin rights, stop and write `docs/questions/S0-branch-protection.md` with the exact failing command and request owner action (dossier rule 7).
- [ ] **Step 8: Commit** (if any files remain uncommitted after the PR merge, land them via a follow-up PR; do not bypass protection)

```powershell
git add .github contracts/licence-allowlist.json python docs/ci.md
git commit -m "ci: add per-language pipelines with dependency-rule and licence gates"
```

---

### Task 8: Negative gate self-tests (dispatch-only, expected red)

**Work item:** S0-WI2c

**Files:**
- Create: `.github/workflows/negative-gates.yml`
- Create: `scripts/negative/fixtures/fail-format.cpp`, `scripts/negative/fixtures/fail-test.cpp`, `scripts/negative/fixtures/forbidden-include.cpp`
- Create: `docs/ci/negative-gates.md`

**Interfaces:**
- Consumes: CI gates from Task 7.
- Produces: a `workflow_dispatch`-only workflow with jobs `expect-red-format`, `expect-red-test`, `expect-red-depcheck`; each job is green only when the underlying gate correctly rejects its fixture.

- [ ] **Step 1: Write the fixtures** — `fail-format.cpp` deliberately mis-formatted; `forbidden-include.cpp` including `<thread>`; `fail-test.cpp` with a failing assertion. Keep them outside all build globs (directory `scripts/negative/`) so they never enter normal builds.
- [ ] **Step 2: Write the workflow.** `on: workflow_dispatch`; jobs run clang-format on the fixture, run the C++ test fixture, and run `python -m depcheck --root scripts/negative` against a fixture layers file; each job inverts the result (`if gate succeeds → exit 1`) so a working gate shows the job green.
- [ ] **Step 3: Run it and verify red-on-failure semantics.** `gh workflow run negative-gates.yml --ref main`, `gh run watch --exit-status`. Expected: all three jobs green (they prove the gates fail). Then temporarily repair one fixture in a scratch branch and confirm the job turns red (proving the inversion works), then delete the scratch branch.
- [ ] **Step 4: Document** in `docs/ci/negative-gates.md`: purpose, how to run, expected output, why they are dispatch-only.
- [ ] **Step 5: Commit via PR** (open `s0/negative-gates`, push, PR, green CI, squash-merge)

```powershell
git add .github/workflows/negative-gates.yml scripts/negative docs/ci/negative-gates.md
git commit -m "ci: add dispatch-only negative gate self-tests"
```

---

### Task 9: Coverage gates and nightly benchmarks

**Work item:** S0-WI4

**Files:**
- Modify: `.github/workflows/ci.yml` (add coverage to `cpp-linux-asan`, `dotnet`, `python` jobs)
- Create: `.github/workflows/nightly.yml`
- Create: `cpp/tests/benchmarks/version_benchmark.cpp` (+ CMake wiring under the `benchmarks` preset)
- Create: `dotnet/benchmarks/CoreMath.Benchmarks/CoreMath.Benchmarks.csproj` + `Program.cs`
- Create: `python/depcheck/coverage_check.py`, tests in `python/tests/test_coverage_check.py`
- Create: `docs/perf/README.md`

**Interfaces:**
- Produces: `python -m depcheck coverage --report <cobertura.xml> --module <name> --floor <pct>` (parses cobertura XML, exits 1 below floor); nightly workflow jobs `bench-cpp`, `bench-dotnet`; thresholds: `core-math`/`Cubeglass.CoreMath`/`calib` floor 90 (NFR-05).

- [ ] **Step 1: Write failing tests for `coverage_check`** — cobertura fixture above floor passes, below floor fails, missing module fails with a clear message.
- [ ] **Step 2: Implement `coverage_check`** and run `python -m pytest`.
- [ ] **Step 3: Wire coverage into CI** — C++ Linux job configures with `--coverage`, runs `gcovr --xml`; .NET job collects `XPlat Code Coverage` via coverlet and runs `python -m depcheck coverage`; Python job uses `pytest --cov=calib --cov=depcheck`; all floors 90.
- [ ] **Step 4: Write the benchmark smoke tests** — one Google Benchmark (`BM_AbiVersion`) building under the `benchmarks` preset; one BenchmarkDotNet console project. They assert nothing; they establish the nightly lane and print numbers.
- [ ] **Step 5: Write `.github/workflows/nightly.yml`** — `on: schedule` (cron) plus `workflow_dispatch`; jobs `bench-cpp` and `bench-dotnet` build and run the benchmarks; upload results as artefacts; regression thresholds come later (budgets are set per stage).
- [ ] **Step 6: Run gates locally and dispatch nightly once.** Run the coverage commands locally on all three lanes (expect PASS); `gh workflow run nightly.yml --ref main`, `gh run watch --exit-status` (expect green).
- [ ] **Step 7: Commit via PR**

```powershell
git add .github cpp dotnet python docs/perf
git commit -m "ci: enforce coverage floors and add nightly benchmark lane"
```

---

### Task 10: ADR-0001..0003, CONTRIBUTING, PR template, questions directory

**Work item:** S0-WI5

**Files:**
- Create: `docs/adr/0000-template.md`, `docs/adr/0001-layered-architecture.md`, `docs/adr/0002-shared-memory-ipc.md`, `docs/adr/0003-testing-strategy.md`
- Create: `docs/questions/README.md`
- Create: `CONTRIBUTING.md`, `.github/PULL_REQUEST_TEMPLATE.md`

**Interfaces:**
- Produces: MADR template; three merged ADRs that later stages cite; PR template containing the section 11.3 checklist verbatim; `docs/questions/` escalation path for rule 7.

- [ ] **Step 1: Write the MADR template** (`0000-template.md`): context, options, decision, consequences, status, date.
- [ ] **Step 2: Write ADR-0001 (layered architecture).** Records: ports-and-adapters, inward-only dependencies, the pure-module set, repo layout from section 4.1 plus the `python/depcheck` tool addition, toolchain pins (including Unity 6000.6.3f1), and that CI enforces the dependency rule.
- [ ] **Step 3: Write ADR-0002 (shared-memory IPC).** Records the decision from D-1 and section 5.6: single writer `cg-handservice`, seqlock slots, `Local\cubeglass.v1.state`, heartbeat/staleness rules. No implementation in S0.
- [ ] **Step 4: Write ADR-0003 (testing strategy).** Records the section 4.4 pyramid, the `hil` tag rule, the negative gate self-tests, coverage floors, and the deferral note: the contract-compatibility gate (section 9.7) is added when the first contract file lands in S1.
- [ ] **Step 5: Write `CONTRIBUTING.md`** — section 0 hard rules verbatim, branch/commit/versioning conventions from 4.3, local build commands per language, and the work-item/PR protocol from section 11.
- [ ] **Step 6: Write `.github/PULL_REQUEST_TEMPLATE.md`** — the section 11.3 checklist verbatim plus work-item ID and stage fields.
- [ ] **Step 7: Write `docs/questions/README.md`** — when and how to escalate (dossier rule 7 and section 11.5).
- [ ] **Step 8: Verify content against the dossier** — each ADR cites its dossier sections; checklist text matches 11.3; run `python -m depcheck --root .` and the Markdown link check by inspection.
- [ ] **Step 9: Commit via PR**

```powershell
git add docs/adr docs/questions CONTRIBUTING.md .github/PULL_REQUEST_TEMPLATE.md
git commit -m "docs: add ADR-0001..0003, CONTRIBUTING and PR checklist"
```

---

### Task 11: Fresh-clone validation and S0 exit gate

**Work item:** S0-WI6

**Files:**
- Create: `scripts/ci-local.ps1`
- Create: `docs/notes/s0-gate.md` (evidence log)

**Interfaces:**
- Produces: `scripts/ci-local.ps1` — runs all lanes in order (cpp configure/build/test, dotnet test, python ruff/mypy/pytest, depcheck, licences, Unity EditMode test) and exits non-zero on the first failure; the fresh-clone evidence recorded in `docs/notes/s0-gate.md`.

- [ ] **Step 1: Write `scripts/ci-local.ps1`** — fail-fast, echo each command, skip Unity with `-SkipUnity` for machines without the editor.
- [ ] **Step 2: Fresh-clone test (state-free).** Delete any local build state; `git clone https://github.com/sufiankane/minecraft-vr.git "$env:TEMP\cg-fresh"`; run `git lfs pull` if needed; run `scripts/ci-local.ps1`. Expected: all lanes green from a clone with no prior build state and no `.venv`.
- [ ] **Step 3: Path-with-spaces test.** Repeat the clone into `"$env:TEMP\cg fresh clone"` and run `scripts/ci-local.ps1`. Expected: identical green result; fix any unquoted path found.
- [ ] **Step 4: Record evidence** in `docs/notes/s0-gate.md`: exact commands, versions from `docs/toolchains.md`, pass/fail for each exit-gate item, plus the CI run URL.
- [ ] **Step 5: Verify every S0 exit gate** and tick them in this plan: fresh clone green (Step 2), CI blocks merge (Task 7 Step 7), ADR-0001..0003 merged (Task 10), tag.
- [ ] **Step 6: Tag and push**

```powershell
git add scripts/ci-local.ps1 docs/notes/s0-gate.md
git commit -m "docs: record S0 exit-gate evidence and add local CI runner"
git tag -a stage-0-complete -m "Stage 0: foundation and engineering platform complete"
git push origin main --follow-tags
```

---

## Self-Review (completed)

- **Spec coverage:** S0 deliverables — layout (Tasks 1–6), CMake/vcpkg presets (2), .NET solution (3), Unity skeleton (6), Python tooling (4), CI incl. build/lint/test/coverage/sanitizers/depcheck/licence (7, 9), `.editorconfig`/`.clang-format`/`.clang-tidy`/PR template/ADR template (1, 2, 10), ADR-0001..0003 (10); work items 1–5 (1–4, 7, 10); required tests — negative gates (8), fresh clone (11); exit gate (11).
- **Type consistency:** preset names, target names, package ids, check contexts and CLI signatures are identical wherever referenced.
- **Review Focus:** all five failure modes have owning-task tests.
- **Proportion:** decisions and verification commands only; no implementation bodies beyond the interfaces later tasks consume.

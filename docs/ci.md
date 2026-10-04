# CI gates

`.github/workflows/ci.yml` runs on every pull request and on every push to
`main`. A run is cancelled when a newer commit arrives on the same ref
(`concurrency: cancel-in-progress`). The six job contexts below are the required
status checks: merging is blocked while any of them fails. Branch protection
lives in the GitHub repository settings (recorded in
[`docs/notes/s0-gate.md`](notes/s0-gate.md)), not in this repository, so verify
it after any job rename.

## Jobs

| Check context | Runner | Enforces |
| --- | --- | --- |
| `cpp-windows` | `windows-latest` | MSVC build (warnings as errors), `ctest`, `clang-format --dry-run --Werror` over every `cpp/**` source/header, `clang-tidy` over `cpp/core-math/src` (other modules deferred; see below) |
| `cpp-linux-asan` | `ubuntu-latest` | Linux ASan/UBSan `ctest`, the TSan preset and concurrency tests, plus the `linux-coverage` build and the `core-math` coverage floor |
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

Python dependencies are version-pinned in `python/requirements-dev.txt` but not
hash-pinned (`--require-hashes`). Hash-locking every transitive dependency plus
the inline `gcovr==8.6` install is deferred as a dedicated supply-chain task
rather than half-implemented; the nightly `supply-chain` job audits the same
requirements with `pip-audit` in the meantime.

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
actionlint .github/workflows/ci.yml .github/workflows/nightly.yml .github/workflows/release.yml .github/workflows/negative-gates.yml
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
  baseline that regresses by more than 10 percent fails the job and names the
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
does not run on pull requests or pushes. The `build_target` input chooses one of
two build routes and skips the other job:

| Route | Runner | Unity licence | Unity suites |
| --- | --- | --- | --- |
| `hosted` (default) | `windows-latest` | `UNITY_LICENSE` alone (offline `.ulf`) **or** `UNITY_SERIAL` alone (serial) | EditMode + PlayMode before the build |
| `self-hosted` | `[self-hosted, windows]` | the machine's own Unity Hub activation (no secrets) | EditMode + PlayMode before the build |

Both routes attach the player to the `v0.1.0` release when that tag exists; the
tag itself is owner-gated and not created by CI (R50). A missing release is a
normal outcome on either route: the probe captures its exit code, clears the
native exit state, and the player stays available as the workflow artefact (the
bug fixed in the 2026-10-03 infra review, I-1).

### Route 1: `hosted` (default)

Builds the Windows x64 player on `windows-latest`. It first requires Unity
credentials in one of two modes and fails before any build step with an
actionable error:

- **offline licence** — `UNITY_LICENSE` alone, the contents of a
  Personal/Student `.ulf` exported from
  [license.unity3d.com/manual](https://license.unity3d.com/manual).
  The release gate writes
  it to `Unity_lic.ulf` and runs `unity license activate --file`.
- **serial** — `UNITY_SERIAL` alone,
  a Plus/Pro/Education serial. The release gate runs
  `unity license activate --serial` and returns the seat at the end of the job.

With credentials present it builds the managed plugins
(`dotnet build dotnet/Cubeglass.sln --configuration Release` and
`scripts/sync-unity-plugins.ps1`), builds the native `cg_bridge` target from the
pinned vcpkg baseline and copies `cg_unity_bridge.dll` into
`unity/Cubeglass/Assets/Plugins/win-x64/`. The staged DLLs are git-ignored, so
`allowDirtyBuild: false` still holds.

Tagged releases are test-gated on both routes. Per-PR CI does not run Unity (the
required jobs are secret-free and `windows-latest` has no editor), so
`scripts/ci-local.ps1` is the only per-commit Unity lane and the release is
where the suites become mandatory in CI: the EditMode and PlayMode suites run
before the player build and any failure fails the job. On this route the
release installs the pinned Unity CLI (`1.0.0-beta.11`) and Editor
(`6000.6.3f1`), activates the licence from `UNITY_LICENSE` (offline `.ulf`) or
`UNITY_SERIAL` and runs the same
`unity test unity/Cubeglass --mode EditMode --non-interactive` and
`--mode PlayMode --non-interactive` suites ci-local runs locally. The release
fails when results are missing, a required test assembly is absent, any test
fails or any test is skipped.

The player build runs the same pinned Unity CLI
(`unity run unity/Cubeglass -- -executeMethod
Cubeglass.Editor.BuildPlayer.BuildWindows64`; editor 6000.6.3f1 from
`ProjectSettings/ProjectVersion.txt`, target StandaloneWindows64) under the
licence activated in the step above - no third-party action and no account
credentials - and packages
`Cubeglass-windows-x64.zip`, uploads it as the `Cubeglass-windows-x64` artefact
and attaches the archive to the release when `v0.1.0` exists.

### Route 2: `self-hosted`

Builds on a self-hosted Windows runner (`runs-on: [self-hosted, windows]`) — the
machine whose Unity editor is already activated through the Unity Hub. It needs
no Unity secrets: the machine's own Hub activation (Unity Personal here) is the
licence. The job runs the same EditMode and PlayMode suites with the machine's
editor (`unity test unity/Cubeglass --mode EditMode --non-interactive` and
`--mode PlayMode --non-interactive`) after the plugins are staged, then
`unity run unity/Cubeglass --non-interactive -- -executeMethod
Cubeglass.Editor.BuildPlayer.BuildWindows64` directly, with no editor install
and no activation step. The build is guarded: a non-zero `unity run` exit code
or a missing
`unity/Cubeglass/build/StandaloneWindows64/Cubeglass/Cubeglass.exe` fails the
job with a message naming the Hub-activation requirement. The player is
packaged as `Cubeglass-v0.1.0-win-x64.zip` and uploaded as the
`Cubeglass-v0.1.0-win-x64` artefact before the attach.

Runner setup (one-off): repo → **Settings → Actions → Runners → New self-hosted
runner → Windows**; run `config.cmd` with the labels `self-hosted, windows`.
Start the runner as the Windows user that activated Unity through the Hub: the
Personal licence lives in that user's profile under
`%LOCALAPPDATA%\Unity\licenses`, so a service under a system account cannot see
it. That user's PATH needs the Unity CLI (`unity`), the GitHub runner agent and
Visual Studio Build Tools with the C++ x64 toolset (CMake and Ninja come from
its developer shell); `gh` is needed only for the release attach, and the job
skips the attach with a warning when it is absent. The job reuses a vcpkg
checkout from `VCPKG_ROOT` or `%USERPROFILE%\vcpkg` when either is present,
checks its HEAD against `cpp/vcpkg.json`'s `builtin-baseline` (a mismatch warns
and still proceeds) and bootstraps `vcpkg.exe` when it is missing; otherwise it
clones the pinned baseline under the runner temp dir and bootstraps. The
installed tree stays outside the workspace, so the checkout clean cannot wipe
it between runs.

### Licence options

The hosted route needs one of two credential modes; the self-hosted route
needs neither (it uses the machine's Hub activation):

| Mode | Secrets | Command | Needs |
| --- | --- | --- | --- |
| Offline licence file | `UNITY_LICENSE` only | `unity license activate --file` | a Personal/Student `.ulf` from license.unity3d.com/manual |
| Serial | `UNITY_SERIAL` | `unity license activate --serial` | a Plus/Pro/Education serial |
| Floating licence server | — | `unity license activate --floating` | a reachable Unity licence server; not wired into the workflow |
| Self-hosted runner with Hub activation | none | — | the licensed Windows machine — the `self-hosted` route |

Owner note: Unity Student/Personal licences use the offline `.ulf` exported
from [license.unity3d.com/manual](https://license.unity3d.com/manual) and the
`unity license activate --file` CLI path. The CLI explicitly rejects
`unity license activate --personal` when it runs with service-account tokens,
so the serial mode is for Plus/Pro/Education serials only.

Until the HIL playtest exists, the release notes in
[`releases/v0.1.0.md`](releases/v0.1.0.md) describe a release candidate and the
`stage-7-complete` tag is withheld.

All four workflows pin every action to a full commit SHA with a version comment,
and `.github/dependabot.yml` proposes grouped minor/patch updates weekly for
GitHub Actions, NuGet and pip.

## Negative gates

Negative gates — checks that deliberately broken inputs are rejected — are
dispatch-only and documented in
[`docs/ci/negative-gates.md`](ci/negative-gates.md). They are self-tests for the
positive gates and are deliberately absent from the required checks; see that
document for why they must stay dispatch-only.

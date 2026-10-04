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

vcpkg (pinned baseline above), the .NET SDK (`dotnet/global.json`), the Unity
editor and the Unity CLI are version-pinned. CMake, Ninja, MSVC and Python come
from the hosted runner image and are not version-locked; each relevant job still
prints a "Toolchain versions" step so image drift is visible in the run log.

**clang-format and clang-tidy are pinned to LLVM 23.1.2** (TD-037) to match
[`docs/toolchains.md`](toolchains.md). The `cpp-windows` job asserts the exact
version at the start of the job and fails with install instructions before any
source is checked; the `expect-red-format` negative gate installs the pinned
clang-format from `python/requirements-ci.txt` (a hash-pinned wheel) and asserts
the same version, so the format self-test cannot silently use a distro build.

**Python dependencies are hash-pinned** (TD-028). `python/requirements-ci.txt`
(gcovr and clang-format), `python/requirements-dev.txt` (pytest, pytest-cov,
mypy, ruff and every transitive dependency) and `python/requirements-audit.txt`
(pip-audit for the nightly supply-chain job) carry `--hash=sha256:` hashes for
every pin and declare `--require-hashes`; CI and `scripts/ci-local.ps1` install
them with `--require-hashes`. The split the row left open is resolved by pinning
**all** files rather than only CI: pip-compile was run on Windows but the hash
sets cover every platform wheel, so the Windows dev flow and the Ubuntu runners
install the same artefacts. `pip install -e python`/`.` stays unpinned by
design: it installs the checkout, not a distribution. Regeneration is a reviewed
commit (`python -m piptools compile --generate-hashes ...`); the nightly
`supply-chain` job audits both dependency files with `pip-audit` and
OSV-Scanner.

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

The same run prints **report-only hygiene warnings** (`licence_report`, TD-065):
an allowlist entry that no manifest declares is named on stdout prefixed with
`warning:` but never fails the gate. Removing such an entry is an owner decision
because a dormant platform dependency may be deliberately pre-recorded; the
warning keeps the allowlist honest without blocking.

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
--root .` (required `depcheck` job) recomputes a normalised fingerprint over the
live contract surface - `contracts/cg_types.h`, `contracts/cg_unity_bridge.h`,
`contracts/cpp/ports.hpp`, `contracts/cpp/result.hpp` and the golden fixture
schema - with comments/whitespace stripped, string and raw-string literals
masked, line continuations spliced and C++ digraphs translated (`%:include` is
an include).

- The live fingerprints are compared against **`KNOWN_FINGERPRINTS` in
  `python/depcheck/contracts.py`**. Any layout drift requires a review-visible
  code-constant edit; editing `contracts/abi-baseline.json` alone (even
  consistently rewriting its `files` and `fingerprint` maps) cannot admit drift
  (the second critical review's bypass, closed 2026-10-04).
- The source ABI version must also equal `KNOWN_ABI_VERSIONS`; a real change is
  a three-edit commit: bump `CG_ABI_VERSION` in `contracts/cg_types.h`, update
  both constants, then `python -m depcheck contracts --root . --update`.
  `--update` refuses while a live fingerprint differs from the code constant,
  and the baseline is bookkeeping, not the trust anchor.
- **Every file under `contracts/` is accounted for** (TD-039). A file is either
  fingerprinted by `CONTRACT_FILES` or listed in `EXEMPT_CONTRACT_FILES` with a
  reason; a new artefact fails the gate until one of the two is updated, and a
  file cannot be both. `contracts/layers.json` (dependency-rule configuration)
  and `contracts/licence-allowlist.json` (licence policy) are deliberately
  exempt: they are configuration, not ABI surface, and are enforced by their own
  gates; `contracts/abi-baseline.json` is exempt as generated bookkeeping. The
  original four files plus `contracts/golden/transforms.json` are the full ABI
  surface and all five are fingerprinted.
- **Version-bump discipline is enforced in CI** (TD-049). On a pull request the
  `depcheck` job fetches the base branch (`fetch-depth: 0`) and runs the gate
  with `CG_CONTRACT_BASE_REF=origin/<base>`. The gate loads
  `contracts/abi-baseline.json` from `git merge-base <ref> HEAD` and fails when
  the surface changed but `CG_ABI_VERSION` stayed the same, naming the changed
  files and the three edits required. A missing baseline at the merge base (the
  bootstrap case) is skipped; an unresolvable base ref is an error. Without the
  ref (local runs, pushes) the check does not run and the discipline stays
  review-enforced, which is the documented local fallback.
- A **cross-check of the golden values** (TD-065) runs in the same job:
  `python -m depcheck golden --root .` recomputes all 28
  `contracts/golden/transforms.json` cases with an independent Python reference
  (`depcheck/golden.py`) derived from ADR-0004 and fails on any value outside
  the fixture tolerance, an unknown op or a reference branch no case exercises.
  This detects both sides of the C++ golden gate drifting together.

The textual scan is a lexer, not a compiler. It handles C# interpolated strings
without masking the interpolation holes (TD-048): `$"{System.IO.File.Exists(p)}"`
and `$@"..."` holes are scanned as code, nested interpolations are handled to a
depth of 8 (deeper holes are masked whole, fail-safe), and an interpolation
format component (`{value:format}`) stays masked. The remaining limits are
precise: a qualified name assembled at runtime (string concatenation,
`nameof`, reflection, `Type.GetType`) is not visible; text inside a masked hole
nested deeper than 8 is not visible; and a `::` alias qualifier inside a hole is
treated as the format separator, masking the rest of that hole. These are
documented rather than approximated.

The baseline was added 2026-10-03 (infra review part A); ADR-0003 records why
the S1 follow-up did not happen until then. The gate was hardened after the
2026-10-04 critical review.

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
python -m pip install --require-hashes -r requirements-dev.txt
python -m pip install -e .
python -m ruff check .
python -m mypy calib depcheck tests
python -m pytest

# Dependency-rule gate
cd ..
python -m depcheck --root .

# Contract-compatibility gate (add --since origin/main to exercise the
# merge-base version-bump check locally)
python -m depcheck contracts --root .

# Golden fixture cross-check
python -m depcheck golden --root .

# Licence gate (report-only warnings do not change the exit code)
python -m depcheck licences --root .

# NuGet machine-readable scan (after capturing the report)
# dotnet list dotnet/Cubeglass.sln package --vulnerable --include-transitive --format json > nuget.json
python -m depcheck nuget --report nuget.json
```

C++ gates need an MSVC environment (`scripts/dev-shell.ps1`) and
`%USERPROFILE%\vcpkg` checked out at the pinned commit:

```powershell
cd cpp
cmake --preset windows-msvc
cmake --build --preset windows-msvc
ctest --preset ci
clang-format --dry-run --Werror @(git ls-files cpp | Where-Object { $_ -match '\.(cpp|hpp|h|hh|cc|cxx)$' })
clang-tidy --header-filter='[\\/](core-math|glasses|bridge)[\\/](include|src)[\\/].*\.(h|hpp)$' -p build/windows-msvc @(git ls-files core-math/src glasses/src bridge/src | Where-Object { $_ -match '\.cpp$' })
```

The `--header-filter` deliberately narrows the repository header filter to the
three C++ module directories: `.clang-tidy`'s default `(^|[/\\])cpp[/\\]…`
would match the frozen, dossier-verbatim headers under `contracts/` (notably
`contracts/cpp/result.hpp`) once the glasses and bridge sources include them,
and those contracts are not ours to edit. Module headers under
`core-math|glasses|bridge/{include,src}` are checked; contract headers are not.

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
Each floor step passes its own per-project results **directory** —
`coverage/coremath`, `coverage/voxel`, `coverage/mesh`, `coverage/gameplay`,
`coverage/streaming` — and `depcheck coverage` resolves it to exactly one
Cobertura report (`*.cobertura.xml`/`coverage.xml`, sorted): zero candidates and
several candidates are both hard errors (TD-032). The old `find … | head -n 1`
selection silently compared against the first report, which could be the wrong
run. The five test runs and their reports therefore stay separate, and a report
from the Voxel run that also instruments the referenced `Cubeglass.CoreMath`
package is harmless: the `--module` filter selects the module each step
enforces. The `python` job runs `pytest` with coverage and enforces
`--module calib --floor 90` and `--module depcheck --floor 90`.

A matched module with **zero coverable lines fails by default** (TD-032): the
old behaviour gave such a module a fake 100 percent. `--allow-empty` waives the
failure with a `WARNING` for a module that genuinely has no instrumentable code;
no current module uses the flag, so every floor above is enforced from its job's
coverage report. See [`docs/perf/README.md`](perf/README.md) for details.

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
  `python -m depcheck benchregress --history benchmark-history --history-window 5`
  for the C++ and .NET reports. The gate is **self-seeding** (TD-033/TD-054):
  the history directory is restored from and saved to an `actions/cache` entry
  (`nightly-bench-history-*`), holds the up-to-five most recent nightly
  summaries, and no baseline file is committed. The current run is compared
  against the **median** of the previous summaries; any benchmark present in
  both that regresses by more than 10 percent fails the job naming the
  offender (`<name>: <median> ns -> <run> ns (+x%, threshold 10%)`,
  TD-034). The first run has no history: it passes with a `no prior history`
  message, captures its measurements under
  `benchmark-history/bench-<run_id>-{cpp,dotnet}.json`, and the next run
  compares against them. A zero-report glob fails the step instead of silently
  comparing nothing; benchmarks absent from the history are new and never fail,
  and a renamed benchmark is treated as new plus absent, so the intersection by
  name stays the documented lifecycle weakness. The tolerance and rationale are
  in [`docs/perf/README.md`](perf/README.md).
- `supply-chain`: a **machine-readable NuGet scan** — `dotnet list
  dotnet/Cubeglass.sln package --vulnerable --include-transitive --format json`
  is parsed by `python -m depcheck nuget`, which fails on any finding (transitive
  included) and fails loudly if the SDK stops emitting parseable JSON (TD-053);
  the old human marker-text grep is gone. A pinned **OSV-Scanner** v2.6.0 step
  then scans the hash-pinned Python requirement files. OSV has no vcpkg
  extractor and the Unity UPM registry has no open-source scanner, so
  `cpp/vcpkg.json` and the Unity packages remain a documented residual: vcpkg
  dependencies are bound by the pinned baseline and covered by the licence
  allowlist, and Unity packages are covered by the Unity suite and the
  editor/manifest pins (ADR-0010). `pip-audit` still audits both requirement
  files.
- `mutation`: `dotnet stryker` against `Cubeglass.Voxel` with the break
  threshold at 70.
- `mutation-gameplay`: `dotnet stryker --project
  src/Gameplay/Cubeglass.Gameplay.csproj --test-project
  tests/Gameplay.Tests/Cubeglass.Gameplay.Tests.csproj --break-at 70
  --threshold-high 90` (TD-031), with the JSON report uploaded even on failure.
  The project, test project and thresholds are inline so
  `dotnet/stryker-config.json` stays owned by the .NET workstream. The
  test-project scope matters: auto-discovery also picked up
  `Streaming.Tests`, whose shared-memory fixtures conflict across Stryker's
  concurrent test hosts (observed locally, TD-031). The first hosted run
  establishes the Gameplay mutation score; until it is recorded the lane may
  fail on the break threshold even though it is wired correctly (the thresholds
  stay at the agreed 70 until a clean run is seen).
- `soak`: the 30-minute `glasses_soak` run.

The performance methodology and artefact locations are documented in
[`docs/perf/README.md`](perf/README.md); that file is the single source of
truth for what is enforced where.

## Release workflow

`.github/workflows/release.yml` is **dispatch-only** (`workflow_dispatch`) and
does not run on pull requests or pushes. The `build_target` input chooses one of
two build routes and skips the other job:

The licensing and test-gating decision behind the two routes is recorded in
[ADR-0012](adr/0012-ci-unity-licensing-and-release-routes.md).

| Route | Runner | Unity licence | Unity suites |
| --- | --- | --- | --- |
| `hosted` (default) | `windows-latest` | `UNITY_LICENSE` alone (offline `.ulf`) **or** `UNITY_SERIAL` alone (serial) | EditMode + PlayMode before the build |
| `self-hosted` | `[self-hosted, windows]` | the machine's own Unity Hub activation (no secrets) | EditMode + PlayMode before the build |

The self-hosted job verifies the pinned .NET SDK (`dotnet --list-sdks` against
`dotnet/global.json`) rather than installing one: the runner user is unelevated
and `actions/setup-dotnet` cannot write to `C:\Program Files\dotnet`. The route
was first verified end to end in run 37198879954 (2026-10-04), which produced
the `Cubeglass-v0.1.0-win-x64` artefact.

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
the checkout stays clean for the builder.

Tagged releases are test-gated on both routes. Per-PR CI does not run Unity (the
required jobs are secret-free and `windows-latest` has no editor), so
`scripts/ci-local.ps1` is the only per-commit Unity lane and the release is
where the suites become mandatory in CI: the EditMode and PlayMode suites run
before the player build and any failure fails the job. On this route the
release installs the pinned Unity CLI (`1.0.0-beta.11`) and Editor
(`6000.6.3f1`), installs the CLI's managed licensing client
(`unity plugin install licensingClient`, required before any activation on a
fresh runner), activates the licence from `UNITY_LICENSE` (offline `.ulf`) or
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
`Cubeglass-windows-x64.zip` plus a `Cubeglass-windows-x64.zip.sha256` in
`sha256sum` format, uploads them as the `Cubeglass-windows-x64` artefact and
attaches both to the release when `v0.1.0` exists. The CDN installer script is
**SHA-256 pinned** before execution (TD-040): the expected digest is a constant
in the workflow, `CG_UNITY_CLI_SHA256_OVERRIDE` (repository variable) overrides
it for a reviewed upgrade, and a mismatch fails with the override instructions
instead of running changed remote code. Build steps hold `contents: read` with
`persist-credentials: false`; repository write is granted only to the separate
`attach-hosted` job.

The attach job **prefers `gh` and falls back to the REST API** (TD-036):
`scripts/publish-release-assets.ps1` uses `gh release upload --clobber` when the
CLI is present, otherwise `Invoke-RestMethod` with `GITHUB_TOKEN`, probing
`GET /releases/tags/v0.1.0` (404 = missing release, a normal outcome), deleting
an existing asset by name and uploading through the asset endpoint (the
`--clobber` equivalent). The request builders are covered by
`scripts/tests/publish-release-assets.tests.ps1`. Checksums are attached on both
routes (TD-038, resolved by PR #52; verified here in `release.yml`: the package
steps write `.sha256` files and both attach calls pass them).

The attach job also produces a **GitHub-signed build provenance attestation**
(`actions/attest-build-provenance`, TD-055) for the shipped zip; checksums are
kept. Verify a downloaded release artefact with:

```bash
gh attestation verify Cubeglass-windows-x64.zip --repo <owner>/<repo>
sha256sum --check Cubeglass-windows-x64.zip.sha256
```

The attestation proves which workflow, commit and runner built the zip;
cryptographic release signing with an owner-held key remains a vendor decision
and is not wired. TD-061 tracks the attach-job verification: the controller has
a hosted dispatch in flight; this repository-side work does not re-dispatch and
the residual is the live-run observation only.

### Route 2: `self-hosted`

Builds on a self-hosted Windows runner (`runs-on: [self-hosted, windows]`) — the
machine whose Unity editor is already activated through the Unity Hub. It needs
no Unity secrets: the machine's own Hub activation (Unity Personal here) is the
licence. The job runs the same EditMode and PlayMode suites with the machine's
editor (`unity test unity/Cubeglass --mode EditMode --non-interactive` and
`--mode PlayMode --non-interactive`) after the plugins are staged, asserted by
the shared `scripts/check-unity-results.ps1` (totals, failures, skips and
required assemblies - the same gate as the hosted route), then
`unity run unity/Cubeglass --non-interactive -- -executeMethod
Cubeglass.Editor.BuildPlayer.BuildWindows64` directly, with no editor install
and no activation step. The build is guarded: a non-zero `unity run` exit code
or a missing
`unity/Cubeglass/build/StandaloneWindows64/Cubeglass/Cubeglass.exe` fails the
job with a message naming the Hub-activation requirement. The player is
packaged as `Cubeglass-v0.1.0-win-x64.zip` with a matching
`.sha256` and uploaded as the `Cubeglass-v0.1.0-win-x64` artefact; the
`attach-self-hosted` job (the only writer) attaches both when the tag exists,
pins the machine's Unity CLI by asserting `unity --version` is
`1.0.0-beta.11` before any build, and attests the zip's provenance like the
hosted route. It needs a second runner pickup, so the runner must be running as
a service to be reliable (TD-047 runbook below); TD-061 tracks the live
verification of the attach step.

### Self-hosted runner service (TD-047)

The attach step only runs reliably when the runner survives logout. From an
**elevated** PowerShell prompt on the licensed machine:

```powershell
./scripts/install-runner-service.ps1                 # defaults to C:\actions-runner
./scripts/install-runner-service.ps1 -SkipStart     # install without starting
```

The script checks elevation, that the directory is a configured runner
(`svc.cmd` and `.runner` present), installs the service with the runner's own
`svc.cmd install` (idempotent: an installed service is reported and left alone)
and starts it. It prints `sc.exe qc <service>` so the account can be checked:
the service must run as the Windows user that activated Unity through the Hub,
because the Personal licence lives in that user's profile
(`%LOCALAPPDATA%\Unity\licenses`) and a system account cannot see it. If the
account is wrong, stop the service and reconfigure it
(`sc.exe config <service> obj= "<machine>\<user>" password= "<password>"`), then
start it again. **The actual install remains an owner action**; the script is
documented and tested only for its checks.

Runner setup (one-off): repo → **Settings → Actions → Runners → New self-hosted
runner → Windows**; run `config.cmd` with the labels `self-hosted, windows`.
Start the runner as the Windows user that activated Unity through the Hub: the
Personal licence lives in that user's profile under
`%LOCALAPPDATA%\Unity\licenses`, so a service under a system account cannot see
it. Install it as a service with `scripts/install-runner-service.ps1` (runbook
above). That user's PATH needs the Unity CLI (`unity`), the GitHub runner agent
and Visual Studio Build Tools with the C++ x64 toolset (CMake and Ninja come
from its developer shell); `gh` is preferred for the release attach but optional
now that the REST fallback exists. The job reuses a vcpkg
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
| Serial | `UNITY_SERIAL` only | `unity license activate --serial` | a Plus/Pro/Education serial |
| Floating licence server | — | `unity license activate --floating` | a reachable Unity licence server; not wired into the workflow |
| Self-hosted runner with Hub activation | none | — | the licensed Windows machine — the `self-hosted` route |

Owner note: Unity Student/Personal licences use the offline `.ulf` exported
from [license.unity3d.com/manual](https://license.unity3d.com/manual) and the
`unity license activate --file` CLI path. The CLI explicitly rejects
`unity license activate --personal` when it runs with service-account tokens,
so the serial mode is for Plus/Pro/Education serials only.

### Licence expiry and rotation runbook (TD-066)

The Unity **Student seat expires 2027-10-07** (the date recorded in ADR-0012's
amendment). Plan the renewal before that date:

1. **Record the expiry.** Add a calendar reminder 30 days before and confirm the
   seat's end date in the Unity account; renew the Student/Personal entitlement
   there. Hub activation follows the signed-in account.
2. **Hosted route.** If the renewal changes the offline licence, export a fresh
   `.ulf` from [license.unity3d.com/manual](https://license.unity3d.com/manual)
   and replace the `UNITY_LICENSE` secret (Settings → Secrets and variables →
   Actions). A Plus/Pro/Education serial replaces the `UNITY_SERIAL` secret
   instead. No workflow edit is needed; the release gate picks the secret up on
   the next dispatch.
3. **Self-hosted route.** Sign in to the Unity Hub on the runner machine as the
   licensed user and re-activate. The service account is described in the
   runbook above; verify `unity run` still builds before the next release.
4. **Verify.** Dispatch `release.yml` with `build_target: hosted` (or
   `self-hosted`) and confirm the release gate's `unity license activate` step
   and the EditMode/PlayMode suites pass. A failed activation fails before any
   build step with the actionable message described above.
5. **TSan ASLR.** Unrelated to licensing but job-scoped for the same reason:
   the `cpp-linux-asan` job lowers `vm.mmap_rnd_bits` to 28 for the TSan step
   and restores the captured original in an `if: always()` step, so a failed or
   cancelled TSan test cannot leave the runner's ASLR entropy reduced (TD-066).

The licensing decision itself is recorded in
[ADR-0012](adr/0012-ci-unity-licensing-and-release-routes.md); the expiry
amendment there names the date and this runbook.

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

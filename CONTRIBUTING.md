# Contributing to Cubeglass

This document is the entry point for anyone — human or agent — making a change.
The authoritative specification is [`Cubeglass_Engineering_Dossier.md`](Cubeglass_Engineering_Dossier.md);
where this file and the dossier disagree, the dossier wins.
New to the repository? Start with [`docs/ONBOARDING.md`](docs/ONBOARDING.md);
the architecture and contract references are
[`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) and
[`docs/CONTRACTS.md`](docs/CONTRACTS.md).

## 0. Hard rules

The following is dossier section 0, reproduced verbatim. It is a build contract:
read it before starting any work item.

> This dossier is a build contract. It is divided into **stages**. Each stage has a goal, scope, interfaces, tasks, tests and an **exit gate**.
>
> Hard rules for any agent working from it:
>
> 1. **One stage at a time.** Do not start stage N+1 until every exit-gate item of stage N is green in CI and the stage is tagged `stage-N-complete`.
> 2. **Contracts are frozen.** Section 5 defines the public interfaces and data formats. Changing a contract needs a new ADR (architecture decision record) and a version bump. Never change a contract silently to make code compile.
> 3. **Tests first.** For each unit of work, write the failing test, then the implementation, then refactor. Tests must be deterministic and runnable without hardware unless tagged `hil` (hardware-in-the-loop).
> 4. **No hardware in the default test run.** Every hardware dependency sits behind a port (interface) with a fake and a recorded-data replay implementation.
> 5. **No monoliths.** Each module has a single responsibility, a public API of at most one header or one interface file, and dependencies pointing inward only (section 3.3).
> 6. **Unknowns are spikes, not guesses.** Facts not verified are listed in section 2.2. Resolve them in the stage that owns them and record the answer in an ADR before building on it.
> 7. **Stop and ask** if a requirement conflicts with a contract, a gate cannot be met, or an unknown changes the architecture. Write the question in `docs/questions/` and halt that work item.

Escalation details are in [`docs/questions/README.md`](docs/questions/README.md).

## Branching, commits and versioning

Dossier section 4.3 defines the software lifecycle. In short:

- **Branching:** trunk-based. Short-lived branches (maximum 1 day of work
  each), pull request required, squash-merge. Name branches
  `s<stage>/<short-topic>` (for example `s0/docs-adr`).
- **Commits:** Conventional Commits (`feat:`, `fix:`, `test:`, `refactor:`,
  `docs:`, `perf:`, `build:`, `ci:`). One logical change per commit.
- **Versioning:** SemVer for each contract and each library. Contract changes
  need an ADR and a changelog entry.
- **ADRs:** `docs/adr/NNNN-title.md` using the MADR template
  ([`docs/adr/0000-template.md`](docs/adr/0000-template.md): context, options,
  decision, consequences). Mandatory for any change to a contract, dependency or
  architecture rule.
- **Review:** every PR needs a green CI, the checklist (dossier section 11.3)
  completed in the PR description, and a reviewer separate from the author (a
  second agent or the owner).
- **Dependencies:** pinned versions, a licence check in CI, and a recorded
  reason for each dependency.
- **Releases:** tagged `vX.Y.Z`, build artefacts produced by CI only, release
  notes generated from commits.
- **Logging and telemetry:** structured logs (levels, module, timestamp); a
  lightweight metrics API (counters, histograms) with no allocation on hot
  paths; a debug overlay reads these.

## Local commands

Run these from the repository root unless a step says otherwise.

### C++

Enter the MSVC x64 developer shell first (it sets `VCPKG_ROOT`):

```powershell
. .\scripts\dev-shell.ps1
cd cpp
cmake --preset windows-msvc
cmake --build --preset windows-msvc
ctest --preset ci
```

Configure presets: `windows-msvc`, `linux-ci`, `linux-asan`, `linux-tsan`,
`linux-coverage`, `benchmarks`. Test presets: `ci` (bound to the `windows-msvc`
configure preset), `linux-asan`, `linux-tsan` and `linux-coverage` (used by
`ci.yml` on Linux).

### C# (.NET)

The SDK is pinned in `dotnet/global.json`.

```powershell
cd dotnet
dotnet test Cubeglass.sln --configuration Release
```

### Python

Use the virtual environment in `python/.venv` (created by
`scripts/ci-local.ps1` on first run, or manually). From `python/`:

```powershell
cd python
python -m pip install -r requirements-dev.txt
python -m pip install -e .
python -m pytest
python -m ruff check .
python -m mypy calib depcheck
```

Then from the repository root:

```powershell
cd ..
python -m depcheck --root .
python -m depcheck licences --root .
```

### Unity

```powershell
unity test unity/Cubeglass --mode EditMode --non-interactive
```

Toolchain pins are in [`docs/toolchains.md`](docs/toolchains.md), which is the
single source of truth for versions.

## Work item template

One work item per PR, from dossier section 11.1, reproduced verbatim:

```
ID: S<stage>-WI<number>
Title:
Stage / milestone:
Contracts touched: (none | list + ADR link)
Inputs (files, fixtures, ADRs):
Acceptance tests to write first:
Out of scope:
Performance budget (if any):
Definition of done: see section 4.4
```

## PR protocol

Follow the process from dossier section 11.2: restate the acceptance criteria;
write failing tests; implement the smallest change; refactor; run all gates
locally; fill the PR checklist. Constraints: do not change contracts; do not add
dependencies without an ADR; no engine or hardware types in pure modules; no
allocations in hot paths; no TODO without an issue.

Every PR must complete the checklist in
[`.github/PULL_REQUEST_TEMPLATE.md`](.github/PULL_REQUEST_TEMPLATE.md), which is
dossier section 11.3 verbatim plus the work-item ID and stage fields, and must be
reviewed by someone other than the author (dossier section 11.4 lists the
reviewer checks). A reviewer rejects tests that would still pass with the
implementation deleted, untested behaviour, classes with more than one reason to
change, functions over roughly 50 lines or with deep nesting, and hardware or
engine types in pure modules.

For CI gates and how to reproduce them, see [`docs/ci.md`](docs/ci.md). For the
dispatch-only negative gates, see
[`docs/ci/negative-gates.md`](docs/ci/negative-gates.md). To change a contract,
follow the contract runbook in [`docs/CONTRACTS.md`](docs/CONTRACTS.md) (the
`python -m depcheck contracts --root .` gate is documented in
[`docs/ci.md`](docs/ci.md#contract-compatibility-gate)).

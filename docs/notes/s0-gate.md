# S0 exit-gate evidence

- **Date:** 2026-10-01
- **Stage:** S0 (foundation and engineering platform)
- **Task:** S0-WI6 / Task 11
- **Branch:** `s0/exit-gate` (short-lived; not pushed, not tagged — the
  controller performs the remote steps and the `stage-0-complete` tag)
- **Commit under test:** `29d3da6 S0: fresh-clone validation and exit-gate
  evidence (#5)` (squash-merge of this work onto `main`; the pre-squash branch
  commit was `f171df2 fix(ci): create the Unity Assets folder before the test
  lane`, script final — the evidence-log commit that followed does not change the
  runner)
- **Runner:** `scripts/ci-local.ps1`

## 1. Toolchain versions

From [`docs/toolchains.md`](../toolchains.md) (single source of truth; Windows
x64 baseline recorded 2026-10-01), all pinned versions
`scripts/bootstrap-dev.ps1 -Check` reports PASS for:

| Tool | Required |
| --- | --- |
| cmake | 4.4.3 |
| ninja | 1.13.2 |
| clang-format | 23.1.2 |
| clang-tidy | 23.1.2 |
| VS Build Tools | 18.9.12105.275 |
| .NET SDK | 10.0.401 |
| vcpkg | `eb2d3a3279fd019cb7733072d86900d0ad2a1aef` |
| Python | 3.14.7 |
| Unity editor | 6000.6.3f1 |
| Unity CLI | 1.0.0-beta.11 |
| GitHub CLI | 2.102.0 |
| Node | 24.19.0 |

Observed in the runs below: `cmake` 4.4.3, Ninja, MSVC 19.51.36256.0 (VS 18.9
BuildTools), `.NET` SDK 10.0.401, Python 3.14.7, Unity CLI 1.0.0-beta.11 →
editor 6000.6.3f1, vcpkg baseline commit above.

## 2. What `scripts/ci-local.ps1` runs

One fail-fast command, echoing every command, with a per-lane PASS/FAIL summary:

1. `python-env` — create `python/.venv` if missing, `pip install -r
   requirements-dev.txt`, `pip install -e .`.
2. `cpp-windows` — `. scripts/dev-shell.ps1`, then from `cpp/`:
   `cmake --preset windows-msvc`, `cmake --build --preset windows-msvc`,
   `ctest --preset ci`.
3. `dotnet` — from `dotnet/`: `dotnet test Cubeglass.sln --configuration
   Release`.
4. `python` — from `python/`: `ruff check .`, `mypy calib depcheck`, `pytest`.
5. `depcheck` — from the repo root: `python -m depcheck --root .` and
   `python -m depcheck licences --root .`.
6. `unity` — ensure `unity/Cubeglass/Assets` exists, then
   `unity test unity/Cubeglass --mode EditMode --non-interactive` with the Unity
   CLI at `%LOCALAPPDATA%\Unity\bin\unity.exe` (skippable with `-SkipUnity`).

Every native command is passed as a quoted argument (never interpolated into a
shell string), so paths containing spaces work unchanged.

## 3. Fresh-clone validation

Remote `main` did not yet contain `scripts/ci-local.ps1` (this task's
deliverable; the controller pushes it after review), so each clone was taken
from the GitHub URL and then the local branch commit was fetched into it:

```powershell
$local = (Get-Location).Path
git clone https://github.com/sufiankane/minecraft-vr.git $cloneDir
git -C $cloneDir fetch $local s0/exit-gate
git -C $cloneDir checkout -q FETCH_HEAD      # f171df2 (pre-squash branch commit)
git -C $cloneDir clean -xdn                  # no ignored build state
```

Both clones reported `HEAD f171df2`, no `python/.venv`, no `cpp/build`, no
`unity/Cubeglass/Library` and no `unity/Cubeglass/Assets` before the run.

### 3.1 `$env:TEMP\cg-fresh` (state-free)

Command:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "$env:TEMP\cg-fresh\scripts\ci-local.ps1"
```

| Lane | Result | Time |
| --- | --- | --- |
| python-env | PASS | 35.8 s |
| cpp-windows | PASS | 10.5 s |
| dotnet | PASS | 8.4 s |
| python | PASS | 4.3 s |
| depcheck | PASS | 0.3 s |
| unity | PASS | 24.6 s |
| **overall** | **PASS (exit 0)** | ~84 s |

Test evidence: `ctest --preset ci` → `100% tests passed, 0 tests failed out of 1`
(`core_math`); `dotnet test` → `Passed! Failed: 0, Passed: 1`; `pytest` → `35
passed`; `ruff` → `All checks passed!`; `mypy` → `Success: no issues found in 6
source files`; `depcheck` and `depcheck licences` → exit 0; Unity EditMode NUnit
XML → `total 1, passed 1, failed 0, result Passed`.

### 3.2 `$env:TEMP\cg fresh clone` (path with spaces)

Command:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "$env:TEMP\cg fresh clone\scripts\ci-local.ps1"
```

| Lane | Result | Time |
| --- | --- | --- |
| python-env | PASS | 35.2 s |
| cpp-windows | PASS | 8.9 s |
| dotnet | PASS | 5.6 s |
| python | PASS | 4.1 s |
| depcheck | PASS | 0.4 s |
| unity | PASS | 33.8 s |
| **overall** | **PASS (exit 0)** | ~88 s |

Identical lane results to 3.1. The only path-specific detail is the URL-encoded
editable-install source (`file:///C:/.../cg%20fresh%20clone/python`) that
`pip` produces and resolves correctly; no unquoted path failed, so no
quoting change was needed for spaces.

### 3.3 Notes on the clone step

- Deletion of `$env:TEMP\cg-fresh` after the first (pre-fix) attempt was blocked
  by a lingering OS handle on the directory root. Its contents were removed and
  verified empty (`0` entries), then `git clone` was run into the now-empty
  directory; the same pristine checkout was produced. The path-with-spaces clone
  was created and pre-cleaned normally.
- `git lfs pull` was not needed: no tracked file uses a `filter=lfs` rule
  (`.gitattributes` only normalises line endings).

## 4. Quoting / ordering fixes made to `ci-local.ps1`

The Unity lane initially failed in a pristine clone:

```
Aborting batchmode due to failure:
Couldn't set project path to:
C:/.../<clone>/C:/.../<clone>/unity/Cubeglass
Error: Test run did not complete. Unity exited with code 1 before reporting results.
```

Root cause: the pinned Unity CLI (`1.0.0-beta.11`) resolves the `project`
argument against the current directory and, for a project that has no `Assets`
folder yet, passes a doubled path to the editor, which aborts before the first
import. A pristine clone has no `Assets` folder because empty directories are
not tracked by git.

Controlled isolation (outside the repository, copied project trees):

- project without `Assets` → exit 6, doubled-path error (reproduced at
  `%TEMP%\cg-fresh`, `%TEMP%\cg-unity-diag` and `C:\Users\sufia\cg-unity-diag`);
- same project once an empty `Assets` folder exists → exit 0, `1 passed`;
- main workspace (which has had Unity create `Assets`) → exit 0.

Fix (pre-squash branch commits `206dfca` hardening + `f171df2`, merged as
`29d3da6`):

- run the Unity lane from the repository root using the canonical relative
  project path `unity/Cubeglass` (matching `docs/toolchains.md`) rather than an
  absolute project path;
- create `unity/Cubeglass/Assets` if it is missing before invoking the CLI.

This is a scaffold prerequisite, not a weakened gate: the EditMode tests still
run and must pass. No lane, workflow or contract was modified.

## 5. CI run that proves merge blocking

Branch protection requires the six check contexts (`cpp-windows`,
`cpp-linux-asan`, `dotnet`, `python`, `depcheck`, `licences`); a PR cannot merge
while any is failing. Recorded runs (run ids recorded in the Task 7/8/9/10
reports):

| Evidence | Run | Outcome |
| --- | --- | --- |
| All six required checks green | `https://github.com/sufiankane/minecraft-vr/actions/runs/36872836638` | all six jobs pass |
| Negative-gate self-tests (dispatch-only) | `https://github.com/sufiankane/minecraft-vr/actions/runs/36875444355` | `expect-red-format`, `expect-red-test`, `expect-red-depcheck` all reject their fixtures |
| Inversion proof: a gate can go red | `https://github.com/sufiankane/minecraft-vr/actions/runs/36875811720` | scratch branch with a repaired fixture → `expect-red-test` fails, others pass |
| Merge gated by checks (coverage lane) | `https://github.com/sufiankane/minecraft-vr/actions/runs/36878134064` (PR #3) | green before squash-merge to `6b23005` |
| Merge gated by checks (docs lane) | `https://github.com/sufiankane/minecraft-vr/actions/runs/36880680872` (PR #4) | green before squash-merge to `18ea1e0` |

The inversion run shows a gate turning red is observed and reported by CI, and
the merge runs show PRs only land on `main` after all required checks pass.

## 6. Exit-gate checklist

| # | Item | Status | Evidence |
| --- | --- | --- | --- |
| 1 | Fresh clone builds and tests green with one command per language | **GREEN** | §3.1 and §3.2: `ci-local.ps1` exit 0, all six lanes PASS from state-free clones (including the path-with-spaces clone). |
| 2 | CI blocks merging when any gate fails | **GREEN** | §5: six required checks under branch protection; negative-gate dispatch run 36875444355 and inversion run 36875811720 prove gates reject bad input; PR #3/#4 merged only on green. |
| 3 | ADR-0001..0003 merged on `main` | **GREEN** | `git ls-tree 18ea1e0 docs/adr/` lists `0000-template.md`, `0001-layered-architecture.md`, `0002-shared-memory-ipc.md`, `0003-testing-strategy.md`. |
| 4 | `stage-0-complete` tag created | **PENDING controller action** | This task does not tag or push; the controller tags after this change merges to `main`. |

## 7. Files added by this task

- `scripts/ci-local.ps1` — fail-fast local CI runner (pre-squash branch commits
  `8b804b7`, `206dfca`, `f171df2`; merged as `29d3da6`).
- `docs/notes/s0-gate.md` — this evidence log.

## 8. Local smoke test (pre-clone)

Before the fresh-clone runs, `scripts/ci-local.ps1 -SkipUnity` was run in the
development checkout: `python-env`, `cpp-windows`, `dotnet`, `python` and
`depcheck` all PASS and the script exited 0 (the Unity lane was exercised
separately and then in both fresh clones).

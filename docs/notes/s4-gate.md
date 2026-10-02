# S4 exit-gate evidence

- **Date:** 2026-10-02
- **Stage:** S4 (gameplay: input abstraction, player controller, interaction
  service, scenario replays, gesture recogniser)
- **Task:** S4-WI6 / Task 4 (coverage floor and exit-gate evidence)
- **Branch:** `s4/gate` (PR #22)
- **Commit under test:** `6e0ba0b` (last CI-verified S4 head). The gate commit on
  top adds only the coverage-floor wiring in `.github/workflows/ci.yml`, the
  `docs/ci.md` row and this note; the scenario, property, determinism and
  coverage numbers below were measured on the `6e0ba0b` tree.
- **Runner:** `scripts/ci-local.ps1 -SkipUnity` (Unity is untouched by S4)

## 1. Local lanes

Command (from the repository root):

```powershell
powershell -ExecutionPolicy Bypass -File scripts/ci-local.ps1 -SkipUnity
```

Result: `ci-local: ALL LANES PASS` (exit 0).

| Lane | Result | Time |
| --- | --- | --- |
| python-env | PASS | 5.8 s |
| cpp-windows | PASS | 4.1 s |
| dotnet | PASS | 15 s |
| python | PASS | 1.5 s |
| depcheck | PASS | 0.3 s |
| unity | SKIP (`-SkipUnity`) | 0 s |

Test evidence from the same run:

- `dotnet test Cubeglass.sln --configuration Release` → `Failed: 0, Passed: 78`
  (`Cubeglass.CoreMath.Tests`), `Failed: 0, Passed: 115`
  (`Cubeglass.Gameplay.Tests`), `Failed: 0, Passed: 156`
  (`Cubeglass.Voxel.Tests`) and `Failed: 0, Passed: 87`
  (`Cubeglass.Mesh.Tests`) — **436/436 passed**.
- `ctest --preset ci` → `100% tests passed, 0 tests failed out of 1`.
- `python -m ruff check .` → `All checks passed!`; `python -m mypy calib
  depcheck` → `Success: no issues found in 6 source files`; `python -m pytest`
  → `45 passed`.
- `python -m depcheck --root .` and `python -m depcheck licences --root .` →
  exit 0.

## 2. Scenario replays and property

The seven JSON scripts under `dotnet/tests/Gameplay.Tests/scenarios/` replay
through `ScriptedInputProvider` on a fixed `dt` with a hand-written
accepted-command `modelEdits` list. For every script the runner asserts the
service world hash equals the model world hash equals the pinned
`expected.worldHash`, then checks the per-step observations (target, break
progress, `Edited`, state, `Recentered`, yaw, hotbar, position, optional
per-step hash). All seven are green:

| Script | Pins | Result |
| --- | --- | --- |
| `walk` | 60 physics frames at dt 0.02: forward to z = 2.6, view ray retargets the wall at (8,1,0), no edits | PASS |
| `break` | stone hardness 1.5 s at dt 0.1: `Pressed` arms nothing, step 1 progress 1/15, step 15 the break edit, step 16 retargets the floor at 0.1/1.5 | PASS |
| `place` | slot 0 Stone placed on the first `Pressed` edge, `Held` no repeat, cycle to slot 1, Dirt placed on the second edge | PASS |
| `place-inside-player` | floor face under the feet: `CanPlace` false, no edit, `Idle`, world unchanged | PASS |
| `tracking-loss-cancel` | 0.2 s loss kept progress at 0.8; the boundary frame cancels with no edit and the step-4 hash; sustained loss inert; recovery re-arms and completes | PASS |
| `hotbar-mid-break` | two cycles during a dirt break keep target (2,1,4), the edit lands there, index ends at 2 | PASS |
| `recentre` | `RecenterPressed` pulse sets yaw 1.25 → 0 on that step only, `Recentered` true once | PASS |

Property (`InteractionServiceTests`, FsCheck, 100 cases): random 0..255 byte
scripts (pointer variants, `Held`/`Up`, `Pressed`/`Up`,
`None`/`Degraded`/`Good`, hotbar deltas) run for up to 40 fixed steps of 0.1 s.
It proves no edit while the modelled loss exceeds 200 ms, that every observed
world change replays as an `Applied` command on the accepted-command model, that
`Edited` and world changes agree, and that the model replay reproduces the final
world hash.

## 3. Determinism

Every world hash is the test-side FNV-1a 64 hash
`dotnet/tests/Gameplay.Tests/ScenarioRunner.cs` computes over the inclusive cell
box and block ids; a moved block changes the hash. Pinned by the scenarios:

| World | Pinned hash | Pinned by |
| --- | --- | --- |
| floor only | `0xCF5916F26DDD5725` | `break` (after the edit and reset), `place-inside-player`, `hotbar-mid-break`, `recentre` |
| floor + dirt at (2,1,4) | `0xF469A9411EA3B4FB` | `tracking-loss-cancel` step 4 (mid-loss), its final hash |
| placed block world | `0x667D0DB9CB118EC8` | `place` |
| walk world (no edits) | `0xD3EAA06F7F47B435` | `walk` |

Same-input determinism beyond the scripts is pinned by
`SameScriptProducesIdenticalResultsAndWorldHash` (identical state hashes, yaw,
hotbar and per-step results), by the player-controller same-script state hash
and the 20 ms vs 2 × 10 ms frame-split invariance (1e-3 tolerance), and by the
gesture twin-run determinism test. `PlayerController.Step` and
`InteractionService.Update` are allocation-free on the hot path (0 bytes over
100,000 measured steps after warm-up; gesture path likewise).

## 4. Coverage

Command (from `dotnet/`, fresh `coverage/gameplay` directory):

```powershell
dotnet test tests/Gameplay.Tests/Cubeglass.Gameplay.Tests.csproj --configuration Release --collect:"XPlat Code Coverage" --results-directory ./coverage/gameplay
```

Command (from the repository root):

```powershell
python\.venv\Scripts\python.exe -m depcheck coverage --report dotnet\coverage\gameplay\fe7eab23-173a-4467-a41b-b303458cada3\coverage.cobertura.xml --module Cubeglass.Gameplay --floor 90
```

Result: `coverage PASS: module 'Cubeglass.Gameplay' observed 98.61% (355/360
lines); floor 90%`. No extra tests were needed for the floor; the floor is
enforced by the `dotnet` CI step with the fail-loud `find coverage/gameplay`
guard.

## 5. Gates added

- `.github/workflows/ci.yml`: the `dotnet` job gains
  `Test Gameplay with coverage` (`--results-directory ./coverage/gameplay`) and
  `Enforce Gameplay coverage floor` (`Cubeglass.Gameplay --floor 90`) using the
  same fail-loud selector as CoreMath/Voxel/Mesh; the solution build and the
  95/90 floors are unchanged.
- `docs/ci.md`: the coverage table gains the `Cubeglass.Gameplay` 90% row and
  the floor paragraph now names the fourth results directory and floor.

## 6. Deferred minors

Known non-blocking items recorded during Task 4; none affects the exit gate:

- ADR-0008 wording items: the `OnGround` "upward injection" prose and the
  Task 3 band paragraph phrases differ from the code's exact terms; the code,
  tests and behaviour are the reference.
- No explicit `MaxStep`/sweep guard in `PlayerController`: per-axis try/revert
  does not sweep, so a step larger than one cell can tunnel; the ADR documents
  that the fixed timestep and 4.5 m/s walk speed bound this in S4.
- ADR-0008 hotbar-default rows cite block names rather than the ADR-0006
  block-id table; the tests prove the ids through `BlockRegistry.Default`, so a
  content-id change cannot slip through.
- `InputFrame.Neutral` stays a static field (frozen contract style) instead of a
  property; tests bind it to locals because `in` arguments cannot take rvalues.
- Boundary inclusivity tests for every threshold pair (200 ms loss, 250 ms
  flick, 0.2/0.5/0.7 ratios) are covered at the current edges but not
  exhaustively for both directions.
- A dedicated gesture hand-switch carry test (band state latched across a
  left→right switch mid-band) is deferred; the existing test covers the
  left-then-right selection path.

## 7. CI verification

PR #22 run
[36967581480](https://github.com/sufiankane/minecraft-vr/actions/runs/36967581480)
green on `ab71c42`; this fix/gate commit is covered by the PR rerun.

# 0003. Testing strategy and the CI gate model

- Status: accepted
- Date: 2026-10-01
- Deciders: Sufyan Khan (owner)
- Consulted: Stage S0 build agents
- Informed: all later-stage builders and reviewers

## Context and problem statement

Cubeglass must be testable without hardware in the default run, must prove each
contract against every implementation, and must block a merge the moment a gate
goes red. The dossier fixes the test pyramid in section 4.4 and the CI quality
gates in section 9. This ADR records how those are applied, what is enforced
today in S0, and what is deliberately deferred.

## Decision drivers

- Tests must be deterministic and runnable without hardware unless tagged `hil`
  (dossier section 0 rule 3).
- Every hardware dependency must sit behind a port with a fake and a
  recorded-data replay implementation (dossier section 0 rule 4).
- Contracts are tested once and run against every implementation of a port
  (dossier section 4.4, section 5.2, section 5.3, section 5.4).
- Coverage and gate thresholds must be enforced by CI, not by review.

## Considered options

- **The dossier pyramid, enforced by required CI jobs.** Chosen.
- **Unit tests only, hardware tested by hand.** Rejected: violates section 0
  rules 3 and 4 and cannot gate merges.
- **A single monolithic test job.** Rejected: it hides which gate failed and
  cannot express the per-module coverage floors.

## Decision outcome

Chosen option: **the section 4.4 test pyramid, enforced by the required CI jobs
and complemented by dispatch-only negative gates**, per dossier section 4.4 and
section 9.

### Test pyramid

| Level | Scope | Hardware | Runs |
| --- | --- | --- | --- |
| Unit | Single class or function, pure logic | none | every commit |
| Property-based | Invariants (e.g. raycast never returns an empty cell, save/load round trip) | none | every commit |
| Contract | Same behavioural suite against every implementation of a port (real, fake, replay) | none (fake/replay) | every commit |
| Golden-file | Data formats, calibration maths, meshes | none | every commit |
| Integration | Two or more modules together using replay data | none | every commit |
| Performance | Benchmarks with budgets; regression threshold fails CI | none (GPU runner optional) | nightly and per release |
| Hardware-in-loop (`hil`) | Real glasses | glasses | manual, scripted, results committed as artefacts |
| Soak | 30-minute run with fakes: leaks, thread count, memory growth | none | nightly |

### The `hil` tag rule

A test may require real hardware only when it is tagged `hil`. Every other test
must run deterministically with no hardware. A hardware dependency is expressed
as a port with a fake implementation and a recorded-data replay implementation,
so the default run exercises the fake or replay, and the `hil` run exercises the
real device. This is dossier section 0 rules 3 and 4 applied to the pyramid.

### Coverage floors

The dossier NFR-05 sets at least 90 percent line coverage for core libraries.
In S0 this is enforced as a floor of 90 in the required CI jobs:

- `cpp-linux-asan` enforces the `core-math` C++ coverage floor after running
  `ctest --preset linux-coverage` and generating Cobertura XML.
- `dotnet` enforces the `Cubeglass.CoreMath` coverage floor under
  `dotnet test Cubeglass.sln --configuration Release`.
- `python` enforces the `calib` and `depcheck` coverage floors.

All three use `python -m depcheck coverage --report <cobertura.xml> --module
<name> --floor 90` from `.github/workflows/ci.yml`. A module with zero coverable
lines prints a `WARNING` and exits 0 until instrumentable code lands; the floor
becomes real as code arrives in later stages.

### Negative gates (dispatch-only)

Positive gates prove that good input passes. Negative gates prove that a gate
still rejects bad input, and they run only when dispatched. They live in
`.github/workflows/negative-gates.yml`, are documented in
[`docs/ci/negative-gates.md`](../ci/negative-gates.md), and are never wired into
the required checks: their fixtures are deliberately broken, so enabling them
normally would invert the red semantics. A green negative run means
`clang-format`, `pytest` and `depcheck` each still rejected their fixture. These
are the `Required tests` self-tests of S0 in dossier section 6.

### Deferral: contract-compatibility gate

Dossier section 9 item 7 defines a contract-compatibility check: contract files
must be unchanged unless the PR carries an ADR and a version bump. This gate is
**not** implemented in S0 because no contract file exists yet under `contracts/`
(the directory currently holds only `layers.json`, `licence-allowlist.json` and
a `.gitkeep`). The gate is added when the first contract file lands in S1, which
is the stage that introduces the shared types and golden fixtures. Until then,
the rule is upheld by review (PR checklist item "No contract changes, or ADR
linked and version bumped").

### Consequences

- Good: the default run needs no hardware and no GPU, and every merge is gated
  by the same commands a human runs locally.
- Good: ports are proven by one contract suite against real, fake and replay
  implementations.
- Bad: the contract-compatibility gate has a one-stage gap (S0) and relies on
  review until S1; the deferral is explicit here so it is not forgotten.
- Follow-up: add the section 9.7 contract-compatibility gate in S1 when the
  first contract file lands.

## Confirmation

The pyramid levels and their triggers are enforced by the required jobs in
`.github/workflows/ci.yml` and the nightly and per-release jobs in
`.github/workflows/nightly.yml` (performance and soak). Coverage floors are
enforced by `python -m depcheck coverage`. Negative gates are proven by
`.github/workflows/negative-gates.yml`. The deferral has a named trigger: the
first contract file in S1.

## Links

- Dossier sections: 0 (rules 3 and 4), 4.4, 6 (S0), 9 (items 3, 5, 7, 9), 11.3.
- Related ADRs: [ADR-0001](0001-layered-architecture.md),
  [ADR-0002](0002-shared-memory-ipc.md).

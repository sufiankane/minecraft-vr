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

### Contract-compatibility gate (added 2026-10-03)

Dossier section 9 item 7 defines a contract-compatibility check: contract files
must be unchanged unless the PR carries an ADR and a version bump. The original
text of this ADR deferred the gate to S1, when the first contract file was
expected to land. That trigger fired in S1 (`contracts/cg_types.h`), but the
follow-up did not happen, and S4–S7 changed contracts repeatedly (ABI 1→2, new
headers) with only prose in the ADRs; CI would have stayed green through a
contract edit with no ADR. The gate was therefore added on **2026-10-03**
(infra review part A), not in S1.

It is implemented as `python -m depcheck contracts` in the required `depcheck`
job. The check computes a normalised fingerprint (comments and whitespace
stripped) over `contracts/cg_types.h`, `contracts/cg_unity_bridge.h`,
`contracts/cpp/ports.hpp` and `contracts/cpp/result.hpp`, compares it with the
committed `contracts/abi-baseline.json`, and fails when the fingerprint changed
without a `CG_ABI_VERSION` bump. `--update` regenerates the baseline but refuses
to write while the version is unchanged, so the bump cannot be skipped; the ADR
requirement itself stays a review responsibility recorded in the PR checklist.

### Consequences

- Good: the default run needs no hardware and no GPU, and every merge is gated
  by the same commands a human runs locally.
- Good: ports are proven by one contract suite against real, fake and replay
  implementations.
- Bad: the contract-compatibility gate arrived late — S1 through S7 were
  machine-checked only for review; the gap is recorded here rather than implied
  to have been closed in S1.
- Follow-up: none for section 9.7; the gate exists. Future contract changes must
  bump `CG_ABI_VERSION` and regenerate the baseline in the same PR.

## Confirmation

The pyramid levels and their triggers are enforced by the required jobs in
`.github/workflows/ci.yml` and the nightly and per-release jobs in
`.github/workflows/nightly.yml` (performance and soak). Coverage floors are
enforced by `python -m depcheck coverage`. The contract-compatibility gate is
enforced by `python -m depcheck contracts` against
`contracts/abi-baseline.json` (added 2026-10-03). Negative gates are proven by
`.github/workflows/negative-gates.yml`.

## Links

- Dossier sections: 0 (rules 3 and 4), 4.4, 6 (S0), 9 (items 3, 5, 7, 9), 11.3.
- Related ADRs: [ADR-0001](0001-layered-architecture.md),
  [ADR-0002](0002-shared-memory-ipc.md).

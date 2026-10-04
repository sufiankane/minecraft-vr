# Architecture Decision Records

This directory holds the architecture decision records (ADRs) for Cubeglass.
Each ADR follows the MADR-style template in
[`0000-template.md`](0000-template.md): context and problem statement, decision
drivers, considered options, decision outcome with consequences, confirmation
(how the decision is enforced or verified), and links to the dossier sections
and related ADRs.

ADRs exist because the dossier freezes contracts and architecture rules
(dossier section 0 rule 2, section 4.3): **any change to a contract, a
dependency or an architecture rule requires an ADR**, and a contract change
also requires a version bump. The repository enforces the version half
mechanically: `python -m depcheck contracts` compares a normalised fingerprint
of the four contract files with `contracts/abi-baseline.json` and fails unless
`CG_ABI_VERSION` was bumped (ADR-0003, `docs/ci.md`). The "one ADR per change"
half is a review responsibility recorded in the pull-request checklist
(dossier section 11.3).

## Index

| ADR | Title | Status | One-line decision | Date |
| --- | --- | --- | --- | --- |
| [`0000`](0000-template.md) | Short title of the decision (template) | template | The MADR-style template every ADR in this directory follows (context, drivers, options, outcome, consequences, confirmation, links). | n/a (template; the date field is `YYYY-MM-DD`) |
| [`0001`](0001-layered-architecture.md) | Layered architecture: ports and adapters with inward-only dependencies | accepted | Dependencies point inward only; inner pure logic owns the ports and edge adapters implement them, machine-checked by `depcheck` against `contracts/layers.json`. | 2026-10-01 |
| [`0002`](0002-shared-memory-ipc.md) | Shared-memory IPC between the hand service and the game | accepted | Exactly one process opens the glasses; the hand service is the sole writer of `Local\cubeglass.v1.state`, publishing head and hand slots as single-writer seqlocks with a 250 ms heartbeat staleness rule. | 2026-10-01 |
| [`0003`](0003-testing-strategy.md) | Testing strategy and the CI gate model | accepted | The dossier test pyramid is enforced by the required CI jobs, complemented by dispatch-only negative gates; the contract-compatibility gate arrived on 2026-10-03 rather than in S1. | 2026-10-01 |
| [`0004`](0004-coordinate-unit-time-conventions.md) | Coordinate, unit and time conventions | accepted | One recorded convention per item: right-handed Y-up -Z internal frame, `(w, x, y, z)` unit quaternions, doubles internally with `float` only at boundaries, `HostTime` int64 ns mapped once by a 32-sample median `ClockMapper`, pinned by the shared 28-case golden fixture. | 2026-10-01 |
| [`0005`](0005-voxel-purity-and-async-boundary.md) | Voxel purity and the async store boundary | accepted | `IWorldStore` stays in `Cubeglass.Voxel` with a signature-only `System.Threading.Tasks` namespace exception, and block definitions load from embedded `Content/blocks.json` through `System.Text.Json`. | 2026-10-01 |
| [`0006`](0006-chunk-storage-and-save-format.md) | Chunk storage and save format | accepted (amended pre-release) | 16 x 16 x 16 chunks with `ushort` block ids (`Air = 0`) and a version-1 little-endian RLE delta (`CGDL`, full 4096-cell coverage, `0xFFFF` no-edit sentinel so mined Air edits persist). | 2026-10-01 |
| [`0007`](0007-meshing-budget-winding-and-atlas.md) | Meshing budget, winding, atlas mapping and border rules | accepted | Pins the fixed CCW winding table, per-vertex AO quantised 0/85/170/255 with the conservative four-byte merge rule, the 26-neighbour copy snapshot, the Chebyshev-1 dirty rule, and the p95 <= 2.0 ms mesh budget. | 2026-10-02 |
| [`0008`](0008-gameplay-contracts-and-tuning.md) | Gameplay contracts, support types, player physics and tuning | accepted | Defines the additive gameplay support types, `PlayerState` as a sealed class, the velocity model (4.5 m/s walk, -25 m/s^2 gravity, terminal -40 m/s), reach 5.0 m, break time `max(0.05, hardness)`, hotbar 9 slots, and the 200 ms tracking-loss cancellation. | 2026-10-02 |
| [`0009`](0009-glasses-adapter-pose-semantics.md) | Glasses adapter: status mapping, pose slot, recentre and predict semantics | proposed (U-01/U-08 answers pending HIL; the rest accepted for S5 software) | The additive C++ `Status`/`Result` vocabulary, a bounded-retry wait-free `PoseSlot` that returns false rather than a torn sample, read-time recentre correction plus posted `reset_origin_carina`, and 100 ms-capped linear prediction. | 2026-10-02 |
| [`0010`](0010-unity-version-pipeline-and-bridge-layout.md) | Unity version, stereo pipeline and the bridge layout | proposed (U-09 answer pending HIL; the rest accepted for S6 software) | Unity 6000.6.3f1 on the Built-in Render Pipeline with two SBS eye cameras and config-driven IPD/FOV, `CG_ABI_VERSION` 1 -> 2 and region ABI 1 -> 2, the command/ack word in reserved header bytes 32/36, magic published last, and a test-only native writer. | 2026-10-02 |
| [`0011`](0011-unity-input-mapping-and-rig-composition.md) | Unity input mapping and rig composition | accepted | Ruling R52: the scene is built in Unity space with one mirror plus a mesh winding flip at the adapter, `PlayerRoot` owns the body pose while the head contributes a recentred rotation, `TurnSnap` stays degrees per second, and snap turn is a separate one-shot edge. | 2026-10-03 |
| [`0012`](0012-ci-unity-licensing-and-release-routes.md) | CI Unity licensing and the release build routes | accepted | A dispatch-only release with two routes: hosted (pinned Unity CLI, exactly one credential mode: `UNITY_LICENSE` or `UNITY_SERIAL`) and self-hosted (the machine's Hub activation, no secrets), both test-gated by the EditMode and PlayMode suites before building. | 2026-10-04 |

## Numbering conventions

- `0000` is the template and is never used for a decision. The next free number
  at this snapshot is `0013`.
- Numbers are assigned in blocks by subject, and the block boundaries are
  historical, not thematic:
  - `0001-0004` — foundation: layering, shared-memory IPC, test strategy,
    conventions.
  - `0005-0006` — voxel: purity/async boundary and chunk storage/save format.
  - `0007` — meshing.
  - `0008` — gameplay.
  - `0009` — glasses.
  - `0010` — Unity and the native bridge.
  - `0011` — input mapping and rig composition (S7).
  - `0012` — CI licensing and release routes (S7 release plumbing).
- Numbers are append-only: never renumber a merged ADR. Supersede or amend with
  a new ADR that links back, or record an explicit amendment in the existing
  file (ADR-0006 carries a dated "Amended (pre-release)" note; ADR-0003 and
  ADR-0004 were corrected in place during the 2026-10-03 review, and the
  correction is recorded in the text and in
  `docs/reviews/2026-10-03-full-review.md`).
- When an ADR is superseded, set its status to `superseded by ADR-NNNN` and add
  the replacement to this index in the same pull request.
- Statuses used: `accepted`, `proposed`, `rejected`, `superseded by ADR-NNNN`.
  ADR-0009 and ADR-0010 stay `proposed` until the owner's HIL runs answer
  U-01/U-08 and U-09.

## The "one ADR per contract, dependency or architecture change" rule

Dossier rule 2 (section 0) freezes the contracts: "Changing a contract needs a
new ADR (architecture decision record) and a version bump. Never change a
contract silently to make code compile." Section 4.3 repeats that ADRs are
mandatory for any change to a contract, a dependency or an architecture rule.

In practice:

1. A contract change requires its own ADR, a SemVer bump of the affected
   contract (`CG_ABI_VERSION`, the fixture `schema`, the save-format version or
   the region `abi_version`), and a regenerated `contracts/abi-baseline.json`
   in the same pull request. `python -m depcheck contracts --update` refuses to
   write the baseline while `CG_ABI_VERSION` is unchanged, so a contract edit
   cannot be silently rebased (the CI gate runs
   `python -m depcheck contracts --root .`).
2. Adding or removing a dependency requires an ADR plus an entry in
   `contracts/licence-allowlist.json` (`python -m depcheck licences` fails on
   an undeclared package).
3. A change to the dependency rule or the layer allowlists requires an ADR;
   the lists themselves live in `contracts/layers.json` and are enforced by
   `python -m depcheck --root .`.
4. Decisions that do not change contracts but fix conventions the dossier left
   open (the mesh winding/AO/atlas rules, the gameplay tuning constants, the
   Unity rig composition) also get an ADR so later stages have one reference,
   as ADR-0007, ADR-0008 and ADR-0011 do.

Contract changes that did not take this route are tracked: the S1-S7 window
was machine-checked only from 2026-10-03, and ADR-0003 records that gap
honestly.

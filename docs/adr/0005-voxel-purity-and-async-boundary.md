# 0005. Voxel purity and the async store boundary

- Status: accepted
- Date: 2026-10-01
- Deciders: Sufyan Khan (owner)
- Consulted: Stage S2 build agents
- Informed: all later-stage builders and reviewers

## Context and problem statement

`Cubeglass.Voxel` is the pure domain module of S2 (dossier sections 3.3, 5.9 and
6/S2): deterministic world model, generation, raycast, the single edit path,
versioned delta persistence and collision — with no engine types, no file system
and no threads. The dependency rule of section 3.3 is enforced by depcheck from
`contracts/layers.json`; for `Cubeglass.Voxel` the forbidden namespaces are
`UnityEngine`, `UnityEditor`, `System.IO` and `System.Threading`.

Two S2 requirements push against that boundary:

1. Dossier section 5.9 freezes `IWorldStore` inside `Cubeglass.Voxel`, and its
   methods are asynchronous: `ValueTask SaveAsync(ChunkCoord, ChunkDelta,
   CancellationToken)` and `ValueTask<ChunkDelta?> LoadAsync(ChunkCoord,
   CancellationToken)`. `ValueTask` and `CancellationToken` live under
   `System.Threading`, which the forbid list rejects.
2. Block definitions are data, not code: `IBlockRegistry` (section 5.9) serves
   names, solidity, opacity, atlas indices and hardness. S2 loads them from one
   embedded JSON resource, which needs a JSON reader.

The question: how do the frozen async signatures and the JSON content live in a
module that must never start a task, touch IO or depend on an engine?

## Decision drivers

- Section 5.9 is frozen; moving `IWorldStore` out of `Cubeglass.Voxel` changes
  the contract.
- The purity rule must stay enforceable by `python -m depcheck`: an exception
  must be the narrowest expressible and must not weaken any other project.
- Block content must not require file IO at runtime, and must be testable
  without a file system (malformed inputs are part of the test matrix).
- Production and test dependencies must be permissively licensed and declared
  to the licence gate (dossier sections 4.2 and 9).
- The hot paths (`Get`, `Apply`, `Cast`) must allocate nothing; parsing and
  resource loading happen once, at registry construction.

## Considered options

- **Move `IWorldStore` to an adapter assembly** so Voxel stays async-free.
  Rejected: it changes frozen contract 5.9, and an adapter would own a
  domain-facing interface.
- **Hand-roll an awaitable type** to avoid `System.Threading.Tasks`. Rejected:
  non-standard, error-prone for implementers, and more code than one reviewed
  namespace exception.
- **Read `blocks.json` through `System.IO`** (for example `File.ReadAllText`).
  Rejected: breaks purity and makes tests depend on the working directory.
- **Keep `IWorldStore` in Voxel and allow exactly `System.Threading.Tasks` for
  signature-only use through a new `allowNamespaces` mechanism.** Chosen.

## Decision outcome

Chosen option: keep the frozen contract in `Cubeglass.Voxel` and carve one
explicit namespace exception that depcheck enforces.

### `IWorldStore` stays in `Cubeglass.Voxel`

`IWorldStore` is declared exactly as section 5.9, in `Cubeglass.Voxel`.
`ValueTask` and `CancellationToken` appear only in method signatures. Voxel code
never starts, awaits, schedules or cancels a task, never reads a clock and never
performs IO; the S7 adapters implement `IWorldStore` against a real file system.
`InMemoryWorldStore` (Task 2) is the only implementation in S2 and is pure.

### The `allowNamespaces` layer mechanism

`contracts/layers.json` may carry an optional per-project `allowNamespaces`
list. A namespace is a violation only when it matches `forbidNamespaces` and
does **not** match `allowNamespaces`; matching is exact or `entry + "."` prefix
— the same rule the forbid list already uses. The allow list can only subtract
from the forbid list; it never grants anything in another project.

Only `Cubeglass.Voxel` declares one:

```json
"allowNamespaces": ["System.Threading.Tasks"]
```

Because `System.Threading` itself stays forbidden, `using System.Threading;` in
Voxel remains a violation, as do `System.Threading.Channels`,
`System.Threading.Thread` and every other sibling. The exception covers
`System.Threading.Tasks` and its child namespaces (for example
`System.Threading.Tasks.Sources`). `System.IO` and the engine namespaces remain
fully forbidden. Every other project entry is unchanged.

Enforcement: `python/depcheck/rules.py` (`_check_cs_file`) applies forbid then
allow; `python/tests/test_depcheck.py` pins allowed exact, allowed child, a
forbidden sibling still caught, and missing-allow-list behaviour unchanged.
This adds a rule capability; it changes no contract file and needs no contract
version bump.

### Block definitions: embedded JSON with System.Text.Json

`Content/blocks.json` is embedded in the `Cubeglass.Voxel` assembly (Task 2) and
parsed with `System.Text.Json` 10.0.12 (MIT, allowlisted). Resource loading uses
`Assembly.GetManifestResourceStream`, not `System.IO`, so purity holds. The
registry exposes a `Parse(string json)` seam so tests feed fixture text,
including malformed JSON, duplicate ids and a missing `Air`, which are rejected
with `FormatException` — a pure BCL type (`System.IO.InvalidDataException` is
not usable because `System.IO` is forbidden). Parsing and resource loading
happen once per registry instance; `Get` is a lookup.

### Test-only dependencies

`FsCheck` 3.4.0 (BSD-3-Clause) is added for the S2 property tests and declared
in `contracts/licence-allowlist.json`. It never enters a shipped artefact.
Property runs use a fixed `Rnd` seed so failures replay deterministically.

### Contract shape and language level

The dossier writes the value contracts as literal C# 10 `readonly record
struct`s. `Cubeglass.Voxel` therefore sets `LangVersion 10.0` and carries a
one-file internal `System.Runtime.CompilerServices.IsExternalInit` polyfill
(`dotnet/src/Voxel/IsExternalInit.cs`), because `netstandard2.1` predates that
type. The language level is a build setting, not a contract: the frozen
contract shape wins, so `Int3` and `ChunkCoord` (Task 1) and `BlockId`,
`EditCommand`, `Ray` and `RayHit` (later tasks) are declared exactly as dossier
section 5.9 writes them, with compiler-generated equality, hashing and
formatting.

### Consequences

- Good: contract 5.9 is untouched and the async boundary is type-only,
  documented and reviewable.
- Good: the purity gate stays strict; the exception lives in the small,
  reviewed, test-covered `contracts/layers.json`.
- Good: block content is data; malformed content is rejected in pure code and
  testable without a file system.
- Good: the value contracts are literal record structs, so equality, hashing
  and formatting are compiler-generated and identical to the dossier type
  declarations.
- Bad: depcheck cannot distinguish "signature only" from "starts a task", so a
  `using System.Threading.Tasks;` anywhere in Voxel passes the gate; review and
  the S2 invariant tests carry that part.
- Bad: the build needs `LangVersion 10.0` and an `IsExternalInit` polyfill on
  `netstandard2.1`; both are recorded here so no later stage mistakes them for
  drift.
- Follow-up: S7 implements `IWorldStore` with real IO and is the only place
  allowed to await; Task 5 allocation tests pin the hot paths.

## Confirmation

- `python -m depcheck --root .` rejects `System.IO`, `System.Threading`, engine
  namespaces and every other forbidden namespace in Voxel, while
  `System.Threading.Tasks` is accepted; `python/tests/test_depcheck.py` pins all
  four allow-list behaviours.
- `contracts/layers.json` lists `System.Threading.Tasks` only under
  `Cubeglass.Voxel`.
- Task 2 `BlockRegistryTests` prove embedded parsing and `FormatException` for
  malformed, duplicate-id and missing-Air inputs.
- FsCheck properties run in `dotnet/tests/Voxel.Tests` with fixed seeds.
- Reviewer checklist: no `Task.Run`, `async`, `lock`, `Thread`, `File` or
  `Stream` in `dotnet/src/Voxel`.

## Links

- Dossier sections: 3.3, 4.2, 4.4, 5.9, 6 (S2), 9.
- Related ADRs: [ADR-0003](0003-testing-strategy.md),
  [ADR-0004](0004-coordinate-unit-time-conventions.md),
  [ADR-0006](0006-chunk-storage-and-save-format.md).

## Amendment (2026-10-04, critical review)

contracts/layers.json admits System.Threading.CancellationToken for
Cubeglass.Voxel in addition to System.Threading.Tasks. CancellationToken
is the inert cooperative-cancellation handle that accompanies ValueTask in the
IWorldStore signatures (dossier 5.9); passing it does not start threads or
locks. Threading primitives (Thread, locks, timers, Task.Run,
CancellationTokenSource) remain forbidden. The dotted-prefix rule also admits
System.Threading.CancellationTokenSource; tightening to exact-type matching is
tracked as TD-050.

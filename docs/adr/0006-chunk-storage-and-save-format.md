# 0006. Chunk storage and save format

- Status: accepted
- Date: 2026-10-01
- Deciders: Sufyan Khan (owner)
- Consulted: Stage S2 build agents
- Informed: all later-stage builders and reviewers

## Context and problem statement

Dossier section 5.9 freezes the voxel contracts (`BlockId`, `ChunkDelta`,
`IWorldStore`) but leaves the chunk size, the in-memory block identifier width
and the byte format used to persist edits open. Without one recorded choice, the
mesher, raycaster, collision code and the future file store would each assume a
different layout, and save files written by one build could not be read by
another. A save file is untrusted input: the loader must reject corrupted data
without any exception escaping (dossier sections 4.2 and 6/S2).

This ADR fixes the chunk at `16 × 16 × 16` cells, `BlockId` as `ushort` with
`Air = 0`, and the versioned little-endian RLE delta format written by
`ChunkDeltaCodec.Serialize` and read by `ChunkDeltaCodec.TryDeserialize`. It
records new format facts; no section 5.9 contract file changes, so no contract
version bump is required. Any change to this layout requires a new ADR and a
format version bump.

## Decision drivers

- Deterministic generation and byte-stable serialisation (S2 global
  constraints); the same edit set must always produce the same bytes.
- Corrupt input must never throw out of `TryDeserialize`: malformed, truncated
  and unknown-version data are rejected (section 6/S2, review focus 3).
- A save stores edits relative to the deterministic baseline, not the generated
  world, so files stay small and generation stays the source of truth.
- Hot paths (`Get`, `Apply`, `Cast`) must allocate nothing; chunk storage is a
  fixed array, not per-cell objects.
- RLE keeps solid and empty regions compact while staying trivial to validate.

## Considered options

- **16³ chunks** (chosen) versus 32³: 16³ is 4096 cells (8 KiB as `ushort`),
  small enough for mesh/collision unit tests and per-chunk streaming within
  headset memory budgets; 32³ would reduce chunk counts but raise worst-case
  work and load latency per chunk.
- **Sparse per-chunk dictionary as storage.** Rejected: dictionary and boxing
  costs on `Get`/`Apply`; a fixed array is allocation-free on read and write.
- **Compressed (zlib) or JSON save format.** Rejected: compression belongs to
  the S7 adapter, JSON loses byte-stability and makes corruption handling
  ambiguous, and both add dependencies to the pure module.
- **Full-grid RLE with an explicit version header and exact coverage
  validation** (chosen) versus a sparse run list carrying per-run coordinates:
  implicit sequential coverage is smaller, and validation reduces to one sum
  over runs.

## Decision outcome

### Chunk size and coordinates

A chunk is `16 × 16 × 16` cells (`ChunkMath.ChunkSize = 16`), local coordinates
`[0, 15]` per axis, world cell = `chunk * 16 + local` (`ChunkMath.ToWorld`).
World-to-chunk conversion uses floor division and floor modulo, so negative
coordinates map correctly (`ToChunk(-1) == -1`, `ToLocal(-1) == 15`); C#
truncating division is never used for these conversions.

### Block ids

`BlockId` wraps a `ushort`, `Air = 0`; a zero-initialised cell array is
therefore air, and generation starts from air. The embedded block registry
(Task 2) assigns ids: unique, `Air` present at id 0. Numeric ids — not names —
are what generation, hashing and persistence use.

### Save format, version 1 (deltas)

A delta is the set of edits for one chunk relative to its generated baseline:
`ChunkDelta` carries the `ChunkCoord` and a map from local cell to `BlockId`.
Save writes the delta; load regenerates the chunk from `(seed, coord)` and
replays it (Task 2/4 semantics).

All integers are little-endian. Layout:

| Offset | Size | Field | Value |
| --- | --- | --- | --- |
| 0 | 4 | magic | ASCII `CGDL` (`0x43 0x47 0x44 0x4C`) |
| 4 | 2 | version | `uint16`, currently `1` |
| 6 | 2 | reserved | `uint16`, `0` for version 1 |
| 8 | 4 | entryCount | `uint32`, number of RLE runs |
| 12 | `6 × entryCount` | runs | `uint16 blockId`, `uint32 runLength` |

Runs cover the chunk's full 4096-cell grid in Z-major local order: linear index
`i = x + 16*y + 256*z`, so X varies fastest and Z slowest. Every `runLength` is
at least 1 and the run lengths sum to exactly 4096: no gaps, no overruns, no
trailing bytes. An all-air delta is a single run `(Air, 4096)`; an all-stone
delta is also a single run. `Serialize` expands the edit map over an air-filled
grid and emits the canonical bytes; `TryDeserialize(Serialize(d))` returns an
equal delta, and re-serialising yields identical bytes.

### Versioning and migration hook

The reader switches on `version`. Version 1 is the only accepted version in S2.
Unknown and newer versions are rejected outright, never partially applied;
future versions add a reader case and keep writing the newest version. The
`reserved` field is written as 0 and must be 0 for version 1.

### Rejection rule

`ChunkDeltaCodec.TryDeserialize(ReadOnlySpan<byte>, out ChunkDelta?)` returns
`false` and never throws for any input: bad magic; a truncated header or body;
an unknown, newer or zero version; a non-zero `reserved`; an `entryCount`
inconsistent with the remaining byte count or above 4096; a zero run length;
run coverage summing to less or more than 4096; and trailing bytes after the
last run. The S2 fuzz test mutates valid bytes, truncates at every length and
feeds random byte arrays, asserting that no exception escapes (Task 4).

### Consequences

- Good: chunk bounds, id width and persistence are pinned in one place, so the
  mesher, raycaster, `ChunkHash` and save/load tests can assert exact values.
- Good: deltas are compact for uniform and sparse chunks alike, and validation
  is a single coverage sum plus length checks.
- Bad: 16³ fixes upper bounds on meshing batches and raycast step counts; a
  future larger chunk needs a new ADR and format version.
- Bad: zero-length runs and `entryCount > 4096` need explicit checks to avoid
  pathological decode loops; Task 4 tests and the fuzz test pin them.
- Follow-up: a future format version must extend the reader switch and record
  the new layout here; file IO and compression stay in S7 adapters.

## Confirmation

- Task 1 `ChunkMathTests` pin floor conversion including `(-1, -1, -1)` and
  `(-17, 0, 15)`; FsCheck properties pin the world-chunk-local round trip in
  `[-1000, 1000]³` and the `[0, 15]` local range.
- Task 3 pins a golden `ChunkHash` over `Generate((0, 0, 0), seed: 42)`.
- Task 4 `ChunkDeltaCodecTests` pin a hand-built version-1 byte fixture, the
  byte-stable round trip and the single-run cases; `ChunkDeltaFuzzTests` pin
  no-throw rejection of every malformed input.
- Review focus 3: corrupted save data never escapes `TryDeserialize`.

## Links

- Dossier sections: 5.9, 6 (S2), 7, 8, 9.
- Related ADRs: [ADR-0003](0003-testing-strategy.md),
  [ADR-0005](0005-voxel-purity-and-async-boundary.md).

# Cubeglass S2 — Voxel Core (Pure Domain): Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `Cubeglass.Voxel` implements dossier section 5.9 — deterministic world model, generation, DDA raycast, the single edit path, versioned delta persistence and AABB collision — with FsCheck properties, a golden chunk hash, a corruption fuzz test and zero-allocation hot paths; tagged `stage-2-complete`.

**Architecture:** One netstandard2.1 assembly `Cubeglass.Voxel` (pure: no engine, no file system, no threads; see ADR-0005) built from small units: `Int3`/`ChunkCoord` maths, a data-driven block registry, `Chunk`/`World` with `Apply` as the only mutation path, a DDA `Raycaster`, a seeded `TerrainGenerator`, a byte-level `ChunkDeltaCodec` (versioned, RLE) and `VoxelCollision`. File IO and streaming stay in adapters (S7); the async signatures of `IWorldStore` are type-only, recorded in ADR-0005. ADR-0006 fixes chunk size (16×16×16) and the save format.

**Tech Stack:** .NET 10 test project against netstandard2.1 library; NUnit; FsCheck (property tests); System.Text.Json (embedded block definitions); BenchmarkDotNet (new `Voxel.Benchmarks` project); coverlet + depcheck (coverage floor 90); Stryker.NET (mutation score ≥70) via a pinned local tool manifest.

**Spec:** `Cubeglass_Engineering_Dossier.md` — section 0, 3.3 (dependency rule), 4.2/4.4, 5.9 (voxel contracts and invariants), 6/S2 (deliverables, work items, tests, exit gate), 7 (test matrix), 8 (budgets), 9 (CI gates), 11. S1 artifacts: ADR-0004, `Cubeglass.CoreMath`.

## Global Constraints

- Pure module: no `UnityEngine`, no `System.IO`, no `System.Threading` (ADR-0005 adds a narrow `System.Threading.Tasks` exception for `IWorldStore` signatures only), no file system, no threads. `contracts/layers.json` gains `allowNamespaces` support in depcheck and this exception for `Cubeglass.Voxel`.
- `Cubeglass.Voxel` targets `netstandard2.1`, `LangVersion 10.0` with the `IsExternalInit` polyfill so the frozen contracts are literal `readonly record struct`s (ADR-0005), nullable, warnings as errors. No allocation on hot paths (`Get`, `Apply`, `Cast`; verified by counter tests).
- Contracts frozen: `Int3`, `BlockId`, `IBlockRegistry`, `IWorld`, `IWorldGenerator`, `IRaycaster`, `IWorldStore`, `EditCommand` exactly as section 5.9. Chunk size and byte format are new (ADR-0006).
- Deterministic: generation depends only on `(seed, ChunkCoord)`; serialisation is byte-stable; no clocks, no randomness outside explicit seeds.
- TDD for every task; Conventional Commits; one logical change per commit.

## Review Focus

1. **Negative coordinates**: floor division/modulo everywhere (world→chunk→local→world round trip for cells like `(-1,-1,-1)`, `(-17,0,15)`). Pinned in Task 1.
2. **`Apply` is the only mutation path**: `Expected` mismatch rejected; a rejected command changes nothing; `ChunkChanged` fires exactly once per applied edit with the right coord. Pinned in Task 2.
3. **Corrupted save data**: fuzzed bytes must be rejected without any exception escaping (`TryDeserialize` returns false), including truncated headers, bad magic, unknown version, RLE overflow/underrun. Pinned in Task 4.
4. **Raycast invariants on boundaries**: hit cell solid, entry-face normal points toward the ray origin, ray starting inside a solid cell, axis-aligned rays exactly through edges, miss when out of range/unloaded. Pinned in Task 3.
5. **Placement never overlaps the player**: `AABB` vs voxel overlap uses half-open cell bounds, so a box exactly touching a face does not overlap. Pinned in Task 4.

---

### Task 1: Foundations — ADR-0005/0006, layer exception, coordinate maths, dependencies (S2-WI1)

**Branch:** `s2/foundations` (cut from `main` after `stage-1-complete`).

**Files:**
- Create: `docs/adr/0005-voxel-purity-and-async-boundary.md`
- Create: `docs/adr/0006-chunk-storage-and-save-format.md`
- Modify: `contracts/layers.json` (add `"allowNamespaces": ["System.Threading.Tasks"]` under `Cubeglass.Voxel`; no other entry changes)
- Modify: `python/depcheck/rules.py` (+ tests in `python/tests/test_depcheck.py`) — `allowNamespaces` semantics: a namespace matching an allowed entry (exact or `entry.`) is not a violation even if a forbid entry matches
- Modify: `dotnet/Directory.Packages.props` (add `FsCheck` and `System.Text.Json`, latest stable versions; record exact versions in the commit message)
- Modify: `contracts/licence-allowlist.json` (`"FsCheck": "BSD-3-Clause"`, `"System.Text.Json": "MIT"`)
- Create: `dotnet/src/Voxel/Int3.cs`, `ChunkCoord.cs`, `ChunkMath.cs`
- Create: `dotnet/tests/Voxel.Tests/Cubeglass.Voxel.Tests.csproj`, `ChunkMathTests.cs`, `ChunkMathPropertyTests.cs`
- Modify: `dotnet/Cubeglass.sln` (add the test project)
- Modify: `.gitignore` (add `dotnet/coverage/`)
- Commit: this plan

**Interfaces:**
- Produces: `public readonly record struct Int3(int X, int Y, int Z);` with `operator +`/`-`, `static Int3 Zero`; `public readonly record struct ChunkCoord(int X, int Y, int Z)`; `public static class ChunkMath` — `const int ChunkSize = 16`, `ChunkCoord ToChunk(Int3 cell)` (floor division), `Int3 ToLocal(Int3 cell)` (floor modulo, always `[0,15]`), `Int3 ToWorld(ChunkCoord chunk, Int3 local)`.
- Produces: depcheck supports `allowNamespaces` per project in `contracts/layers.json`.
- ADR-0006 pins: chunk `16×16×16`; `BlockId` is `ushort`, `Air = 0`; save format magic `CGDL`, version 1, little-endian, RLE; delta semantics.

- [ ] **Step 1: ADR-0005** (MADR): `IWorldStore` stays in `Cubeglass.Voxel` per contract 5.9; `System.Threading.Tasks` types (`ValueTask`, `CancellationToken`) are signatures only — Voxel never starts tasks or touches IO; `layers.json` gains the narrow `allowNamespaces` mechanism; block definitions are read from an embedded JSON resource with System.Text.Json (MIT, test-visible); FsCheck (BSD-3-Clause) for property tests. `docs/adr/0006-...`: chunk size 16³, Air=0, save format header (`magic "CGDL"`, `uint16 version = 1`, `uint32 count`), RLE layout (u16 blockId, u32 runLength in Z-major local order), migration hook (switch on version), and the rule that unknown/newer versions and malformed data are rejected by `TryDeserialize`.
- [ ] **Step 2: depcheck rule.** Extend the project rule model with optional `allowNamespaces`; a namespace is a violation when it matches `forbidNamespaces` and does not match `allowNamespaces` (exact or `entry + "."` prefix, same semantics as the forbid list). Add tests: allowed exact, allowed child, forbidden sibling still caught, missing allow list unchanged behaviour.
- [ ] **Step 3: Tests first for `Int3`/`ChunkCoord`/`ChunkMath`.** NUnit: floor behaviour (`ToChunk((-1,-1,-1)) == (-1,-1,-1)`, `ToLocal((-1,-1,-1)) == (15,15,15)`, `ToWorld((-1,-1,-1),(15,15,15)) == (-1,-1,-1)`, `ToChunk((-17,0,15)) == (-2,0,0)`, `ToLocal((-17,0,15)) == (15,0,15)`); local range invariant; FsCheck property `ToWorld(ToChunk(c), ToLocal(c)) == c` for arbitrary `Int3` in `[-1000,1000]³` and `ToLocal` components in `[0,15]` always.
- [ ] **Step 4: Implement** the three value types as literal C# 10 `readonly record struct`s (`dotnet/src/Voxel/IsExternalInit.cs` polyfill plus `LangVersion 10.0`; ADR-0005); docs with preconditions.
- [ ] **Step 5: Run** `dotnet test Cubeglass.sln --configuration Release` (from `dotnet/`), `python -m pytest python/tests -q` from repo root (depcheck tests), `powershell -File scripts/ci-local.ps1 -SkipUnity`. Commit: `feat(voxel): add chunk coordinate maths, layer exception and S2 ADRs`.

---

### Task 2: Domain core — registry, chunk, world, store (S2-WI2..4)

**Branch:** `s2/domain-core` (cut after Task 1 merges).

**Files:**
- Create: `dotnet/src/Voxel/BlockId.cs`, `BlockDefinition.cs`, `IBlockRegistry.cs`, `BlockRegistry.cs`, `Content/blocks.json` (embedded resource)
- Create: `dotnet/src/Voxel/Chunk.cs`, `ChunkSnapshot.cs`, `IWorld.cs`, `World.cs`, `EditCommand.cs`, `EditResult.cs`, `IWorldGenerator.cs`, `IWorldStore.cs`, `ChunkDelta.cs`, `InMemoryWorldStore.cs`
- Create: `dotnet/tests/Voxel.Tests/BlockRegistryTests.cs`, `WorldTests.cs`, `WorldInvariantTests.cs`, `TestWorld.cs` (test helper: a world with a scripted generator)
- Modify: `dotnet/src/Voxel/Cubeglass.Voxel.csproj` (EmbeddedResource `Content/blocks.json`, PackageReference System.Text.Json)
- Modify: `dotnet/src/Voxel/AssemblyMarker.cs` (remove if it becomes the only file left; keep otherwise)
- Commit(s) at the implementer's discretion (one logical change per commit)

**Interfaces (exactly section 5.9, plus the types they need):**
- `public readonly record struct BlockId(ushort Value)` with `static BlockId Air => new(0)`.
- `BlockDefinition` (record): `BlockId Id`, `bool Solid`, `bool Opaque`, `float Hardness`, `int AtlasIndexTop/Front/Side` (atlas indices as one `int` triple is acceptable if documented in ADR-0006). Data file names first six blocks: Air, Stone, Dirt, Grass, Sand, Wood; `Placeable` excludes Air.
- `BlockRegistry` loads `Content/blocks.json` (embedded) via System.Text.Json, validates: ids unique, Air present with id 0, all atlas indices >= 0, hardness >= 0; malformed JSON or duplicate ids throw `InvalidDataException`? No — System.IO is banned: throw `FormatException` with a descriptive message (pure BCL type).
- `Chunk`: fixed `BlockId[16,16,16]` (allocation at construction only), `BlockId Get(Int3 local)` (validates local range via guard, throws `ArgumentOutOfRangeException`), `void Set(Int3 local, BlockId)` internal, `ChunkSnapshot Snapshot()` returning a copy for worker use. Actually `Get` on a guard throw is a hot path; guard cheap. Chunk exposes `ChunkCoord Coord`.
- `World : IWorld` — holds `IWorldGenerator? generator`, `IWorldStore? store` (optional), `Dictionary<ChunkCoord, Chunk>`; `BlockId Get(Int3 cell)` returns `BlockId.Air` for unloaded; `bool IsLoaded(Int3 cell)`; `EditResult Apply(in EditCommand cmd)` — `Rejected` when not loaded or `Expected` mismatch, `Applied` sets the cell and raises `ChunkChanged` once; `event Action<ChunkCoord> ChunkChanged`; `void LoadChunk(Chunk chunk)` / `Chunk? GetChunk(ChunkCoord)` for tests and S7 streaming.
- `EditResult` enum: `Applied`, `Rejected`.
- `IWorldStore` exactly per 5.9; `InMemoryWorldStore` implements it with `Dictionary<ChunkCoord, byte[]>` and passes the same store contract tests a future file store will reuse (tests call `SaveAsync`/`LoadAsync`).

- [ ] **Step 1: Tests first.** `BlockRegistryTests`: data loads (>=6 blocks, Air id 0, Grass solid/opaque, Placeable excludes Air); malformed/duplicate/missing-Air fixtures rejected with `FormatException` (load from a `string`/`ReadOnlySpan<byte>` test seam — expose `BlockRegistry.Parse(string json)` for tests). `WorldTests`: Get on unloaded == Air && `IsLoaded` false; `Apply` with matching `Expected` applies and raises `ChunkChanged` once with the cell's chunk; mismatching `Expected` returns `Rejected` and leaves the block unchanged; `Apply` on unloaded cell returns `Rejected`; applying Air→Stone→Dirt sequences.
- [ ] **Step 2: Properties (FsCheck).** After any sequence of `Apply` commands on a loaded chunk, every replay of accepted commands from the same start reproduces the same world hash (model-based: apply to a `Dictionary` model and compare); a rejected `Apply` never changes the world hash.
- [ ] **Step 3: Implement.** Keep `World` and `Chunk` focused; no LINQ/allocation in `Get`/`Apply` (dictionary lookup only; `Dictionary<ChunkCoord, Chunk>` with struct key, no boxing).
- [ ] **Step 4: Run** full solution tests + `ci-local -SkipUnity`; commit.

---

### Task 3: Raycast and generation (S2-WI3, S2-WI5..6)

**Branch:** `s2/raycast-generation` (cut after Task 2 merges).

**Files:**
- Create: `dotnet/src/Voxel/Ray.cs`, `RayHit.cs`, `IRaycaster.cs`, `DdaRaycaster.cs`
- Create: `dotnet/src/Voxel/Noise.cs` (seeded value/simplex noise, pure), `IWorldGenerator.cs` (if not created in Task 2), `TerrainGenerator.cs`
- Create: `dotnet/tests/Voxel.Tests/DdaRaycasterTests.cs`, `RaycasterPropertyTests.cs`, `TerrainGeneratorTests.cs`, `ChunkHash.cs` (test-only FNV-1a 64 helper)
- Commit(s) at the implementer's discretion

**Interfaces:**
- `Ray` (record struct): `Vec3 Origin`, `Vec3 Direction` (need not be unit; `Cast` normalises internally).
- `RayHit` (record struct): `Int3 Cell`, `Int3 Normal` (entry face, pointing toward the ray origin), `float Distance`, `BlockId Block`.
- `DdaRaycaster : IRaycaster` — Amanatides–Woo over unit cells; `Cast(IWorld, Ray, float maxDistance)` returns `RayHit?`; steps into unloaded cells are treated as non-solid but continue while `IsLoaded` traces allow (define: the ray continues through unloaded cells and returns null if it never hits a solid cell within range; document).
- `TerrainGenerator : IWorldGenerator` — 2D seeded noise (implement a compact deterministic simplex or value-noise in `Noise.cs`; no external dependency), layered heights (stone below `h-3`, dirt `h-3..h-1`, grass at `h`), `Y<0` stone, above `h` air; `Generate(ChunkCoord, long seed)` deterministic; chunk-local Y range maps to world Y `chunk.Y*16 + localY`.
- `ChunkHash.Hash(Chunk)` → `ulong` FNV-1a over ids in Z-major local order (test helper, not production).

- [ ] **Step 1: Raycast tests.** Axis-aligned `+X` ray from `(-0.5, 0.5, 0.5)` at a solid cell `(2,0,0)`: hit `(2,0,0)`, normal `(-1,0,0)`, distance `2.5`; ray starting inside a solid cell hits that cell with normal `(0,0,0)` (documented as "inside"); diagonal ray hits the expected first cell (hand-computed case); ray through an exact edge/corner picks one deterministic cell (pin the tie rule in the test and doc); miss beyond `maxDistance`; empty world → null; normal always satisfies `Dot(Normal, Direction) < 0` for non-inside hits.
- [ ] **Step 2: Properties (FsCheck).** For random origin/direction in a world with random solid cells: any returned hit has a solid block, `Distance` in `(0, maxDistance]`, and `Dot(Normal, Direction) < 0`. The implementation must be allocation-free (counter test arrives in Task 5; keep the code path struct-only).
- [ ] **Step 3: Generator tests.** Determinism: `Generate(c, seed)` equals itself across calls and a fresh instance; changing seed changes the chunk; a golden test pins `ChunkHash.Hash(Generate((0,0,0), seed: 42)` to a committed constant (implementer runs it once and writes the value; reviewer recomputes); height layering checks at a few cells; `Generate` never writes out of local bounds.
- [ ] **Step 4: Run** full tests + `ci-local -SkipUnity`; commit.

---

### Task 4: Persistence and collision (S2-WI5b, S2-WI6b)

**Branch:** `s2/persistence-collision` (cut after Task 3 merges).

**Files:**
- Create: `dotnet/src/Voxel/ChunkDeltaCodec.cs`, `ChunkDelta.cs` if not already created
- Create: `dotnet/src/Voxel/Aabb.cs`, `VoxelCollision.cs`
- Create: `dotnet/tests/Voxel.Tests/ChunkDeltaCodecTests.cs`, `ChunkDeltaFuzzTests.cs`, `CollisionTests.cs`, `CollisionPropertyTests.cs`
- Commit(s) at the implementer's discretion

**Interfaces:**
- `ChunkDelta` (immutable): `ChunkCoord Coord`, `IReadOnlyDictionary<Int3, BlockId> Edits` (local cells); `ChunkDelta.Empty(ChunkCoord)`.
- `ChunkDeltaCodec` (static, pure): `byte[] Serialize(ChunkDelta)`; `bool TryDeserialize(ReadOnlySpan<byte>, out ChunkDelta?)` — never throws for any input; rejects bad magic, unknown/newer version, truncated data, count mismatch, run over/underrun (the declared entry count must not exceed 4096 and the run lengths must sum to exactly the expected 4096-cell total), and trailing bytes (a full-grid RLE payload has no per-run coordinates, so out-of-range locals and duplicates cannot occur).
- Format per ADR-0006: `magic "CGDL"` (4 bytes), `uint16 version = 1`, `uint16 reserved = 0`, `uint32 entryCount`, then RLE runs `uint16 blockId, uint32 runLength` in Z-major local order; `Serialize` sorts edits into that order; round trip is byte-stable (`Serialize(Deserialize(bytes))` equals the original byte prefix).
- `Aabb` (readonly struct): `Vec3 Min`, `Vec3 Max` (half-open semantics: contains `p` iff `Min <= p < Max`); `Overlaps(Aabb other)`.
- `VoxelCollision` (static): `bool Overlaps(IWorld world, Aabb box)` — iterates the cell range covering the box, treats unloaded as Air, true iff any solid cell's half-open cube overlaps; `bool CanPlace(IWorld world, Int3 cell, Aabb playerBox)` — false when `cell`'s cube overlaps the player box.

- [ ] **Step 1: Codec tests.** Round trip property (FsCheck): random deltas with random cells/blocks serialize→deserialize to an equal delta; byte stability; empty delta; a fixed hand-built byte fixture for version 1 (bytes written by hand in the test) decodes to the expected delta; single-run RLE case (all 4096 cells Stone) produces 1 run.
- [ ] **Step 2: Fuzz tests.** Seed `20261001`; mutate a valid serialisation byte-by-byte (all 256 values at each position, plus truncations at every length, plus random byte flips) and assert `TryDeserialize` either returns true with a valid delta or false — no exception escapes (catch-all in the test fails the test if thrown). Also fuzz random byte arrays of random lengths (1000 cases).
- [ ] **Step 3: Collision tests.** Half-open boundary: box `[0,1)³` adjacent to solid `(1,0,0)` does not overlap; sharing a face exactly (`Max == cell.Min`) is no overlap; a box straddling a solid cell overlaps; unloaded → no overlap. Placement: cannot place into the cell the player box occupies; can place adjacent; property: for random boxes, `CanPlace(world, cell, box)` false iff the cell cube intersects the box.
- [ ] **Step 4: Run** full tests + `ci-local -SkipUnity`; commit.

---

### Task 5: Budgets, mutation score and exit gate (S2-WI7)

**Branch:** `s2/budgets-gate` (cut after Task 4 merges).

**Files:**
- Create: `dotnet/benchmarks/Voxel.Benchmarks/Voxel.Benchmarks.csproj`, `Program.cs` (BenchmarkDotNet: `ChunkGeneration` per full chunk and `Raycast100k` over a generated chunk)
- Create: `dotnet/tests/Voxel.Tests/AllocationTests.cs` (`GC.GetAllocatedBytesForCurrentThread()`: `Cast` 100k iterations delta 0; `Get`/`Apply` 100k iterations delta 0 after warm-up)
- Create: `.config/dotnet-tools.json` (pin `dotnet-stryker`, latest stable 4.x; exact version recorded in the commit message)
- Create: `dotnet/stryker-config.json` (project `src/Voxel/Cubeglass.Voxel.csproj`, test project `tests/Voxel.Tests`, mutate `src/Voxel/**/*.cs` excluding `AssemblyMarker.cs`, thresholds break 70 low 70 high 90, reporters `json`,`cleartext`)
- Modify: `dotnet/Cubeglass.sln` (add Voxel.Benchmarks)
- Modify: `.github/workflows/ci.yml` (dotnet job: add `python -m depcheck coverage --report "$report" --module Cubeglass.Voxel --floor 90`)
- Modify: `.github/workflows/nightly.yml` (bench-dotnet: run both benchmark projects; add a `mutation` job running `dotnet tool restore` + `dotnet stryker --config-file stryker-config.json`)
- Modify: `docs/ci.md` (coverage table gains `Cubeglass.Voxel` at 90)
- Create: `docs/notes/s2-gate.md` (evidence: coverage %, mutation score, benchmark numbers, allocation deltas, golden hash value, CI run URLs — the controller fills CI lines after the PR)
- Commit(s) at the implementer's discretion; final commit `ci: enforce voxel budgets and record S2 evidence`

**Interfaces:**
- Consumes everything above; produces the S2 exit-gate evidence.

- [ ] **Step 1: Allocation tests** (probe-first: an allocating probe inside the loop must be caught, then removed; measured regions contain no NUnit calls).
- [ ] **Step 2: Benchmarks** run locally with `dotnet run -c Release --project benchmarks/Voxel.Benchmarks/Voxel.Benchmarks.csproj`; record numbers.
- [ ] **Step 3: Coverage locally**: `dotnet test ... --collect:"XPlat Code Coverage"` then `python -m depcheck coverage --report <file> --module Cubeglass.Voxel --floor 90`; add tests until green if needed.
- [ ] **Step 4: Mutation**: `dotnet tool restore` then `dotnet stryker --config-file stryker-config.json` from `dotnet/`; if the score is below 70, add tests that kill surviving mutants and re-run; record the exact score and the surviving-mutant summary in `s2-gate.md`.
- [ ] **Step 5: Run every local lane** (`ci-local -SkipUnity`), commit, and push the branch; the controller runs PR checks and completes the CI section of `s2-gate.md` before the tag.

**Controller steps after Task 5:** PR → six checks green → whole-stage review (`stage-1-complete..head`) → one fix wave if needed → merge → verify `main` CI → tag `stage-2-complete` → push.

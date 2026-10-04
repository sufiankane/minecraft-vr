# Critical .NET surface review — Cubeglass

- **Date:** 2026-10-04
- **Tree:** `docs/full-docs` HEAD (same content as `main` `0d3ecb6`)
- **Scope:** `dotnet/src/{CoreMath,Voxel,Mesh,Gameplay,Streaming}`, `dotnet/tests/**`, `dotnet/benchmarks/**`, project/package pins. Intent read from dossier 3.x/4.x/5.9–5.11/6 S1–S7, ADR-0004/0005/0006/0007/0008/0011, `docs/CONTRACTS.md`.
- **Standard:** life-saving-device review — every defect assumed able to injure the wearer, corrupt saves, or brick the game; “minor” still ships fixed.
- **Read-only:** nothing in the repo was modified. All runtime probes were built and run in `%TEMP%\kilo\review-scratch` against the repo projects.
- **Baseline:** `dotnet test dotnet/Cubeglass.sln` → **471 passed, 0 failed** (net10.0, Windows). Previous review F1–F4 verified fixed; F5 remains documented-deferred and is **not** re-reported.
- **Context:** the `ctxo` index is empty in this checkout (schema only), so all findings are from direct source reading plus runtime probes.

## Counts

| Severity | Count |
|---|---|
| Critical | 0 |
| Important | 3 |
| Minor | 10 |
| Needs a run (verification gaps) | 5 |

No confirmed Critical. One Important finding (I1 below) escalates to Critical if the S7 adapter-side check in “Needs a run #1” turns out to be absent. Finding IDs continue the previous review’s sequence (F1–F5); I1–I3 are Important, M1–M10 Minor.

## Critical / Important one-liners

- **Important I1 — untrusted block ids reach throwing paths.** `World.Apply` accepts any `BlockId`, the codec accepts `0x0001..0xFFFE`, and the mesher/interaction then call `IBlockRegistry.Get`, which throws `KeyNotFoundException`. A crafted/corrupt delta poisons a chunk and crashes meshing or the interaction update; repeatable from a corrupted save.
- **Important I2 — streaming config is unvalidated and can hang/OOM.** `StreamingConfig.ViewDistanceChunks = int.MaxValue` (or any huge radius) makes `RecomputeDesired` wrap past `int.MaxValue` and never terminate (OOM for merely large values); negative radii silently empty the world.
- **Important I3 — collision queries can hang and `CanPlace` can overflow.** `VoxelCollision.Overlaps` loops from `minX` to a saturating `int.MaxValue`, wrapping forever (confirmed: >1 s, no completion, for a box with `max.X = 2^31`); `CellOverlaps` computes `x + 1` in `int`, so `CanPlace(Int3(int.MaxValue,0,0), box-covering-cell)` returns **true** (confirmed).

---

## Important findings

### I1 — Untrusted block ids are accepted at every data boundary, then throw in use

- **Severity:** Important (Critical if adapter-side validation is absent — see Needs a run #1)
- **Files:**
  - `dotnet/src/Voxel/World.cs:57-73` — `Apply` checks only `Expected`; it has no registry and accepts any `BlockId`.
  - `dotnet/src/Voxel/ChunkDeltaCodec.cs:152-162` — every `0x0001..0xFFFE` decodes into a `BlockId` with no registry validation (format-level correct, but nothing else checks).
  - `dotnet/src/Mesh/CulledMesher.cs:140`, `dotnet/src/Mesh/GreedyMesher.cs:271`, `dotnet/src/Mesh/AmbientOcclusion.cs:174,178` — `blocks.Get(...)` throws `KeyNotFoundException`.
  - `dotnet/src/Gameplay/InteractionService.cs:210` — `_blocks.Get(block).Hardness` throws on the first held frame targeting a poisoned cell.
  - `dotnet/src/Mesh/AtlasMap.cs` (via `CulledMesher`/`GreedyMesher` tile lookup) — an out-of-range atlas index throws `ArgumentOutOfRangeException`.
- **Failure scenario (confirmed):** `World.Apply(new EditCommand(cell, Air, new BlockId(65000), 0))` returns `Applied` and `World.Get` returns 65000 (probed). A following `GreedyMesher.Build` throws `KeyNotFoundException` (probed); the interaction service throws the same when the player looks at that cell. For the S7 replay path, a syntactically valid delta containing an unknown id therefore poisons a chunk and turns every subsequent remesh/breaking interaction into a crash. `MesherBuild.Run` returns its buffers before rethrowing (`MesherBuild.cs:144`), so the pool survives, but the worker/main-thread caller dies. `0xFFFF` is additionally asymmetric: `World.Apply` accepts it while `ChunkDeltaCodec.Serialize` rejects it (`ChunkDeltaCodec.cs:65-70`).
- **Confirmed by:** reading plus runtime probes. **Suspected:** end-to-end corrupt-save crash — the S7 file store/adapter is not in this tree; if it also does not validate, escalate to Critical (“corrupt save bricks the game”).
- **Suggested fix:** validate ids at every boundary that knows the registry (delta replay, `World.Apply` wrapper, generator output) and reject with a descriptive `EditResult`/`TryGet`; make mesher/interaction treat unknown ids as a defined fallback (Air plus one-time diagnostic) rather than throwing mid-frame; add a fuzz/rejection test over ids `6..0xFFFE` through decode → apply → mesh/interact.

### I2 — `StreamingConfig` is unvalidated; `RecomputeDesired` can never terminate or OOM

- **Severity:** Important
- **Files:** `dotnet/src/Streaming/StreamingConfig.cs:15,30` (plain settable properties, no validation); `dotnet/src/Streaming/ChunkStreamingScheduler.cs:195-218` (triple loop), `:310-316` (position → chunk cast), `:275-281` (distance), `:61-66` (constructor stores config unchecked).
- **Failure scenario (confirmed by reading + isolated replication; full run withheld to avoid OOM):** with `ViewDistanceChunks = int.MaxValue`, `RecomputeDesired` starts the `dz` loop at `-int.MaxValue`; when `dz` reaches `int.MaxValue`, `dz++` wraps to `int.MinValue` and the condition `dz <= int.MaxValue` stays true — the loop never terminates. Merely large values (e.g. 100000 ≈ 4·10¹⁰ additions) hang then OOM. `ViewDistanceChunks = -1` yields an empty desired set, silently unloading the world. `VerticalRadiusChunks = NaN`/huge takes `(int)Math.Floor(...)`; on CoreCLR this saturates (probed: `Update(NaN)` completed mapping to chunk 0), on Unity Mono/IL2CPP x64 it yields `int.MinValue`, so the desired set is runtime-dependent. Negative `MaxLoadsPerFrame`/`MaxUnloadsPerFrame`/`MaxMeshUploadsPerFrame` silently disable those groups; negative `UnloadHysteresis` shrinks retention.
- **Confirmed by:** reading; the arithmetic wrap was reproduced in isolation; the full scheduler path not executed (deliberate).
- **Suggested fix:** validate in the constructor (fail fast, per dossier 5.13): `ViewDistanceChunks >= 1` and ≤ a sane cap, `VerticalRadiusChunks` finite in `[0, cap]`, budgets non-negative; iterate with `long`/clamped bounds; reject non-finite player positions or map them by a documented rule. Add unit tests for `int.MaxValue`, negative, NaN/Inf config and NaN player positions.

### I3 — `VoxelCollision` hangs on extreme boxes; `CanPlace` wraps `x + 1`

- **Severity:** Important (reopens the S2 “collision area guard” deferred item with new confirmed failure modes)
- **Files:** `dotnet/src/Voxel/VoxelCollision.cs:38-49` (`Overlaps` loop), `:83-88` (`CellOverlaps`, `box.Min.X < x + 1`), `:96-99` (`CellOverlaps` loop), `dotnet/src/Voxel/Aabb.cs` (constructor accepts ±Inf; rejects NaN).
- **Failure scenario A (confirmed at runtime):** `Overlaps(world, Aabb((0,0,0),(2147483648.0,1,1)))` did not complete in 1 s. `Math.Floor(2^31)` casts saturating to `int.MaxValue`; the `x` loop then wraps to `int.MinValue` and `int.MinValue <= int.MaxValue` keeps it spinning forever. A box with `+Inf` bounds behaves the same on CoreCLR; on Mono x64 the floor-cast of `+Inf`/`-Inf` yields `int.MinValue`, a different (still wrong) result.
- **Failure scenario B (confirmed at runtime):** `CanPlace(world, new Int3(int.MaxValue,0,0), playerBoxCoveringThatCell)` returns `true` because `x + 1` is computed in `int` and wraps to `int.MinValue`, so the overlap test fails. The predicate permits inserting a block where the player stands at the top of the coordinate range.
- **Confirmed by:** reading + runtime probes.
- **Suggested fix:** use `(double)x + 1.0` in `CellOverlaps`; bound `Overlaps` with an explicit cell budget (throw `ArgumentException` above it, satisfying the deferred guard) and reject non-finite box bounds; guard `minX/maxX` wrap explicitly. Add tests for boxes at `int.MaxValue`/`int.MinValue`/±Inf and `CanPlace` at the extremes.

---

## Minor findings

### M1 — `HostTime.ToNanoseconds` / `ClockMapper.Map` non-finite and out-of-range mapping is runtime-dependent

- **Severity:** Minor
- **Files:** `dotnet/src/CoreMath/HostTime.cs:28-33`; `dotnet/src/CoreMath/ClockMapper.cs` (`Map` deliberately delegates non-finite `sdkSeconds` to `HostTime.ToNanoseconds`).
- **Failure scenario (confirmed at runtime on net10/CoreCLR):** `ToNanoseconds(NaN) = 0`, `ToNanoseconds(+Inf) = long.MaxValue`, `ToNanoseconds(1e300) = long.MaxValue`. Under Unity Mono/IL2CPP on x64 the same double→long cast yields `0x8000_0000_0000_0000` (`long.MinValue`) for NaN and out-of-range values, and the C++ counterpart `llround` is undefined for out-of-range inputs — so the “one mapping” contract diverges outside the fixture’s in-range cases, and a broken SDK clock is silently saturated rather than diagnosed. Non-finite input is out of the documented contract, which is why this is Minor, but the failure is silent and runtime-dependent.
- **Confirmed by:** reading + runtime probe (CoreCLR only; Mono behaviour is the documented cast difference, see Needs a run #2).
- **Suggested fix:** document the precondition explicitly and reject non-finite/out-of-range input with a defined sentinel or exception on both languages; add fixture rows once the C++/C# ruling is fixed (the ADR-0004 “fixture is the contract” rule).

### M2 — PlayerController does not sanitise pre-existing non-finite state

- **Severity:** Minor (remainder of the fixed F2 class; F2 sanitised `InputFrame`, not `PlayerState`)
- **Files:** `dotnet/src/Gameplay/PlayerController.cs:99` (`ClampPitch` leaves NaN), `:128-141` (velocity used raw), `:254-267`; `dotnet/src/Gameplay/PlayerState.cs:51-53` (`Body` constructs an `Aabb`).
- **Failure scenario (confirmed at runtime):** with `PitchRadians = NaN`, `Step` returns with pitch still NaN (doc says a finite `dt` leaves a finite state); with `Velocity.Y = NaN`, `MoveY` writes NaN into `Position` and the next `player.Body` throws `ArgumentException` from the `Aabb` constructor — a corrupt adapter write bricks the step loop with an exception instead of clamping.
- **Confirmed by:** reading + runtime probes.
- **Suggested fix:** sanitise pitch (NaN → 0/held) and velocity (or document `Step`’s precondition “state is finite”) and add a test.

### M3 — Extreme-coordinate wrap in dirty/neighbour math

- **Severity:** Minor
- **Files:** `dotnet/src/Voxel/ChunkEditPropagation.cs:39` (`cell.X + dx` etc.), `dotnet/src/Voxel/World.cs:96` (`coord.X + dx`).
- **Failure scenario (confirmed at runtime):** `GetAffectedChunks(new Int3(int.MaxValue,0,0))` includes the bogus chunk `(-134217728,-1,-1)` from `int` wrap. The true containing chunks are still present, so no remesh is missed; the bogus entry causes extra remesh/upload bookkeeping churn.
- **Confirmed by:** reading + runtime probe.
- **Suggested fix:** compute neighbour cells/chunks in `long` and clamp/reject.

### M4 — `Noise.Value2D` lattice wraps beyond ±2³¹

- **Severity:** Minor
- **File:** `dotnet/src/Voxel/Noise.cs:34-42` with `FloorToInt` at `:110-113`.
- **Failure scenario:** `FloorToInt` clamps `x` to `int.MaxValue`, then `x0 + 1` wraps to `int.MinValue`, so the `+1` lattice sample comes from the opposite end of the coordinate space — a discontinuity in a public, documented-deterministic function. `TerrainGenerator` cannot reach it (`|worldX/16| ≤ 1.34e8`), but the public API can.
- **Confirmed by:** reading.
- **Suggested fix:** compute lattice neighbours in `long` (or clamp the neighbour index coherently) and add an extreme-coordinate test.

### M5 — DDA conflates overflowed normalisation with “no hit”

- **Severity:** Minor
- **Files:** `dotnet/src/CoreMath/Vec3.cs:86-95`, `dotnet/src/Voxel/DdaRaycaster.cs:99-104`.
- **Failure scenario (confirmed at runtime):** `Cast(origin, (1e200,0,0), 10)` returns `null`: the squared length overflows to `Inf`, `Normalized` divides to `(0,0,0)`, and the all-zero guard rejects it. `PointerRay` documents/rejects this at the provider boundary (`PointerRay.cs:21-25`), but `IRaycaster.Cast` silently returns the same result as “nothing within reach”.
- **Confirmed by:** reading + runtime probe.
- **Suggested fix:** document the case on `DdaRaycaster.Cast`, or normalise scale-safely (`v / maxAbsComponent` before `Length`).

### M6 — Quat overflow-to-Identity is undocumented

- **Severity:** Minor
- **Files:** `dotnet/src/CoreMath/Quat.cs:50-54` (`FromComponents`), `:68-72` (`FromAxisAngle`).
- **Failure scenario (confirmed at runtime):** `FromComponents(1e200,0,0,0)` returns `Identity` because the squared norm overflows to `Inf`; the doc only names `squaredNorm < 1e-24` and non-finite components. A huge-but-finite (otherwise valid) quaternion silently becomes the identity rotation instead of the normalised value.
- **Confirmed by:** reading + runtime probe.
- **Suggested fix:** document the overflow case (or scale before squaring) and pin it with a test.

### M7 — `Hotbar.Cycle` overflows for out-of-contract deltas

- **Severity:** Minor
- **File:** `dotnet/src/Gameplay/Hotbar.cs:54`.
- **Failure scenario:** `_selectedIndex + delta` overflows `int` for |delta| near `int.MaxValue`; the result stays in `[0,9)` but is not the true modular sum (e.g. index 5 with `delta = int.MaxValue` cycles to 2 instead of 6). `InputFrame.HotbarDelta` is contractually −1/0/+1, so shipped providers are safe.
- **Confirmed by:** reading/arithmetic (not probed).
- **Suggested fix:** `WrapIndex(_selectedIndex) + WrapIndex(delta)` or a `long` sum; document.

### M8 — Scheduler priority distance overflows `long` at extreme separations

- **Severity:** Minor
- **File:** `dotnet/src/Streaming/ChunkStreamingScheduler.cs:275-281`.
- **Failure scenario:** chunk coordinates span `int`; `dx` can be up to ~4.29e9, and `dx*dx ≈ 1.84e19` exceeds `long.MaxValue ≈ 9.22e18`, wrapping the squared distance. Ordering stays deterministic (same function on both sides, unique XYZ tiebreak), but “nearest” semantics can invert for far-apart chunks.
- **Confirmed by:** reading/arithmetic (not probed).
- **Suggested fix:** compare squared distance in `double`, or cap `|dx|` before squaring.

### M9 — `AbiVersion.Value` is stale relative to `CG_ABI_VERSION`

- **Severity:** Minor
- **Files:** `dotnet/src/CoreMath/AbiVersion.cs:5` (`Value = 1`), `dotnet/tests/CoreMath.Tests/AbiVersionTests.cs` (`ValueIsOne` pins 1); `contracts/cg_types.h` and `docs/CONTRACTS.md` register `CG_ABI_VERSION` **2** (ADR-0010 R42).
- **Failure scenario:** no current .NET consumer (only the benchmark uses it), and the S0 plan specified `Value = 1`, so this is spec-compliant — but if a future managed handshake treats `AbiVersion.Value` as the mirror of `CG_ABI_VERSION`, it will claim ABI 1 against an ABI-2 contract.
- **Confirmed by:** reading (+ `git grep -c CG_ABI_VERSION contracts/cg_types.h`).
- **Suggested fix:** either rename/document it as an independent smoke constant, or set it to 2 and add a test that ties it to the header.

### M10 — `World.ChunkChanged` has no subscriber exception isolation

- **Severity:** Minor
- **File:** `dotnet/src/Voxel/World.cs:71`.
- **Failure scenario:** the mutation is applied, then `_chunkChanged?.Invoke` runs subscribers in order; if one throws, the remaining subscribers never see the event and keep stale mesh/derived state. Standard C# event semantics; the only in-repo subscriber (`SessionHarness`) cannot throw, and the Unity manager is out of this scope.
- **Confirmed by:** reading.
- **Suggested fix:** document that handlers must not throw, or isolate per-subscriber exceptions.

---

## Verified-sound areas

These were checked line-by-line and, where useful, by the existing tests/probes.

1. **CoreMath contract/fixture.** 28-case `contracts/golden/transforms.json` applied exactly for clock mapping and within `1e-6` elsewhere; `HostTime.ToSeconds` mirrors C++ division; `Quat.Slerp` shortest path + LERP above 0.9995 and always-normalised results; `Pose.Compose`/`Inverse`/`TransformPoint` order correct; `UnityConvert` is the single flip (`x,y,−z` / `w,−x,−y,z`) and re-normalises.
2. **`ChunkMath` floor maths.** `ToChunk`/`ToLocal` are total for the full `int` range including `int.MinValue`/`int.MaxValue`, and `ToWorld(ToChunk(c), ToLocal(c)) == c` exactly at the extremes; `ToWorld`’s precondition is documented.
3. **Chunk-delta codec.** Magic/version/reserved/entry-count checks use `entryCount ≤ 4096` before any multiply (no overflow); every run read is in bounds; zero runs, short/long coverage and trailing bytes are rejected; `0xFFFF`/`0x0000` semantics correct; canonical RLE; round-trips are byte-stable; `TryDeserialize` never throws on 1000 random and byte-mutated payloads.
4. **`World` semantics.** `Apply` requires exact `Expected` and rejects unloaded chunks; unloaded reads are Air; one `ChunkChanged` per applied edit; `Chunk.Snapshot`/`NeighbourSnapshot` copy data (no aliasing); `InMemoryWorldStore` stores immutable deltas and honours cancellation.
5. **DDA.** Inside-hit zero distance with zero normal vs face-origin entry hit at distance 0 with non-zero normal; inclusive `maxDistance` with `(float)t ≤ maxDistance`; deterministic X→Y→Z tie rule; `WouldOverflow` terminates at coordinate extremes; step cap derived from distance×|direction|₁; brute-force nearest-hit oracle passes across 500 generated worlds.
6. **Collision half-open rules (in-range cells).** Touching boxes do not overlap; a cell exactly at `box.Max` does not count; zero-extent boxes are inert by `HasVolume`; `CanPlace` correctly keeps the player box out. (Extreme-coordinate failures are I3.)
7. **Meshing.** Faces culled only against opaque neighbours (including cross-chunk via snapshots); greedy merge key `(id<<8)|aoBits` bounded by `0xFFFE`; maximal-width-then-height rectangles never overlap; CCW winding verified by hand for all six faces (cross products all point along the face normal); AO three-neighbour rule and 2-bit encode correct per face/corner; UVs tile the atlas once per block cell; differential-tested on 2000 chunks / >1M exposed faces / 250 neighbour cases.
8. **Mesh ownership.** `MeshData.Release` returns each buffer exactly once, clears windows before the next step, throws on double release, and is safe under a mid-sequence throw; `MeshBuffers`/`MesherBuild` return every rent on the throw path (gated by `OutstandingBuffers`); `PooledStream.Detach`/`Return` are symmetric for empty arrays; pool is documented single-threaded with per-worker instances.
9. **Gameplay contracts.** `Move`/`TurnSnap`/`dt` sanitisation (F2) holds for finite prior state; break accumulates per cell with `max(0.05, hardness)`, resets on target/hardness change, release and loss; placement only on `Pressed` with `expected: Air`, ignored for an Air slot; hotbar and recentre apply during loss; tracking-loss cancel happens exactly once and recovery re-arms; gesture hysteresis engages at the closed edge and releases at the open edge; flick window is inclusive and consume-once; degenerate frames hold without advancing timers.
10. **Streaming behaviour.** Load/unload/upload grouping and per-frame budgets; upload rejection re-pends every skipped upload and stops the group; idempotent notification APIs; deterministic action order via a strict-total-order comparator over sorted candidates; `Update` reuses lists and allocates nothing per frame.
11. **Determinism and purity.** No `System.Random`, `UnityEngine`, `System.IO` or clock reads in `dotnet/src` (ADR-0005); no dictionary-iteration-order-dependent output (world/dirty hashing sorts chunks; scheduler sorts candidates); FNV-1a 64 noise is a pure function; no `string.Format`/`ToString` culture dependence in library output; no LINQ/closures in hot paths.
12. **Pins.** SDK `10.0.401` with `latestPatch`, seven exact package versions, `Nullable`+`TreatWarningsAsErrors`+analyzers+`Deterministic`, `netstandard2.1` libraries / `net10.0` tests+benchmarks, `IsExternalInit` polyfills present — all match `docs/CONTRACTS.md`.

## Needs a run

1. **S7 adapter delta validation** (decides I1’s Critical escalation): apply a crafted delta containing block id 65000 through the real file-store/boot path and confirm whether any layer validates against the registry before `World.Apply`. Not visible in this tree.
2. **Unity/Mono/IL2CPP cast divergence:** run `(int)Math.Floor(NaN)`, `(int)Math.Floor(±Inf)`, `(long)Math.Round(NaN/±Inf/1e300)` and an `Overlaps` call with `+Inf` bounds on the Unity runtime to confirm the I2/M1-class platform differences.
3. **Mesh p95 budget** on target hardware: `dotnet run -c Release --project dotnet/benchmarks/Mesh.Benchmarks -- p95 --budget-ms <budget>` (ADR-0007).
4. **Mutation gate:** `dotnet stryker` against `stryker-config.json` (Voxel only, thresholds 70/90) — not run here.
5. **Allocation/GC soak** of a real session (the 5-minute golden session asserts correctness, not allocation/GC pause).

## Test-integrity assessment

- **Sound:** differential mesh oracle (fixed seed 20261001, non-vacuity asserts `chunksWithFaces > 1900`, `totalExposed > 1e6`, `neighbourCases == 250`); AO shared-vertex comparison with `>1000` shared vertices; DDA nearest-hit oracle plus “at least one hit”/“every finite case completed” asserts; gesture/scenario tests observe real state changes; golden session asserts `chunksLoaded > 0`, `quadsEmitted > 0`, walk completed, `unloads == 0`, and both canonical and half-`dt` hashes. `ChunkDeltaFuzzTests` mutates every byte of three structured payloads and feeds 1000 random arrays to `TryDeserialize`, asserting false/no-throw.
- **Provenance:** `GoldenWorldHash 0xB38A…` and the mesh golden hashes are documented as produced by the implementation’s own green run, i.e. regression pins, not independent oracles (the mesh differential test supplies the independent oracle; the session hash is additionally cross-checked by the half-`dt` run and event counts).
- **Gaps that allowed the findings above:** no test for unknown ids through apply→mesh/interact (I1); no test for extreme/negative/NaN `StreamingConfig` or non-finite player positions (I2); no test for collision boxes at `int` extremes/±Inf or `CanPlace` at `int.MaxValue` (I3); no `HostTime` non-finite/overflow rows (M1); no `Quat` overflow / `Noise` extreme-coordinate tests (M4/M6); the DDA brute-force oracle covers only chunks `−1..1`, so far-from-origin behaviour is hand-tested only.
- **Style note:** the interaction fuzz property fails by throwing inside `Prop.ForAll` and returns `Prop.ToProperty(true)`, so failures are real but lose FsCheck shrinking/counterexample reporting; a boolean property with observed values would be stronger. `RandomByteArraysNeverThrow` asserts only no-throw, which is adequate for a Try-pattern but does not pin rejection rates.

## Previously deferred (explicitly not re-reported)

- **F5** (`GetAffectedChunks` per-call allocation) remains documented-deferred per `.superpowers/reviews/fixes-dotnet.md`; not counted above.
- The S2 “collision area guard” deferral is **re-opened as I3** because the new probes show a concrete hang and an in-range `CanPlace` overflow, not just a slow path.

*Report generated by reading the pinned sources and running the focused probes described above; no repository files were changed.*

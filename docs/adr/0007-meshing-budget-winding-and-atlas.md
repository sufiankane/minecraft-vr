# 0007. Meshing budget, winding, atlas mapping and border rules

- Status: accepted
- Date: 2026-10-02
- Deciders: Sufyan Khan (owner)
- Consulted: Stage S3 build agents
- Informed: all later-stage builders and reviewers

## Context and problem statement

Dossier section 5.10 freezes the mesh contract for `Cubeglass.Mesh` verbatim:

```csharp
public interface IChunkMesher {
    // Pure. Neighbour access through a read-only snapshot so meshing can run on a worker thread.
    MeshData Build(ChunkSnapshot chunk, NeighbourSnapshot neighbours, IBlockRegistry blocks);
}
public sealed class MeshData {                 // engine-neutral, pooled buffers
    public ReadOnlyMemory<Vector3f> Positions; public ReadOnlyMemory<Vector3f> Normals;
    public ReadOnlyMemory<Vector2f> Uvs; public ReadOnlyMemory<byte> Ao;
    public ReadOnlyMemory<int> Indices; }
```

The declaration names `Vector3f` and `Vector2f` but never defines them, has no
bounds or lifecycle, and leaves the conventions that make two meshers
interchangeable open: vertex winding, the meaning of the `Ao` byte, the atlas
UV mapping, the neighbour scope, which chunks a cell edit makes dirty, and the
stage's performance budget. Left unrecorded, each worker would guess; a wrong
winding makes faces invisible in Unity, a wrong AO encoding shows as seam
steps between chunks, and a wrong dirty rule shows as stale holes at borders.

This ADR pins those decisions. `Cubeglass.Mesh` (netstandard2.1, C# 10)
references exactly `Cubeglass.CoreMath` and `Cubeglass.Voxel`
(`contracts/layers.json`), starts no threads and touches no engine or file
system type. It **adds** types to `Cubeglass.CoreMath` and `Cubeglass.Voxel`
but changes no section 5.10 declaration text, so no contract version bump is
required; any later change to these conventions needs a new ADR.

## Decision drivers

- Section 5.10 is frozen; the missing pieces must be additive and recorded, not
  edits to the quoted contract.
- The invariant set is testable without an engine: no internal faces between
  two opaque blocks, a fully solid chunk emits exactly the surface quads in the
  reference mesher (6 per cell face region before Task 2 merging), the same
  input gives byte-identical output.
- `Cubeglass.CoreMath` stays C# 9 (hand-written structs, no `record struct`);
  `Cubeglass.Mesh` may use C# 10 but avoids an `IsExternalInit` polyfill.
- Border correctness needs a read-only copy of the 26 neighbours so meshing can
  run on a worker thread while the world keeps mutating.
- The performance budget must be checked by a benchmark harness, never by a
  timing assertion in `dotnet test` (dossier section 7; ADR-0003).

## Considered options

- **One recorded convention per item, with the additive types living in the
  module that owns the data** (chosen): float vectors in CoreMath, neighbour
  view and dirty rule in Voxel, mesh data and atlas mapping in Mesh.
- **Put `Vector3f`/`Vector2f` in `Cubeglass.Mesh`** to avoid touching CoreMath.
  Rejected: section 5.10 consumes them as boundary types and S4/S6 input and
  adapter contracts name them too; CoreMath is where the shared value types
  live and it is already referenced by Mesh.
- **Positional `record`s for `AtlasLayout`/`MeshData` metadata.** Rejected:
  positional records emit `init` accessors, which need an `IsExternalInit`
  polyfill on netstandard2.1; `MeshData`'s public fields are frozen anyway and
  `AtlasLayout` uses explicit get-only properties.
- **Padding each atlas tile in S3.** Rejected: texture filtering seams are not
  a stage requirement, and no padding keeps `TileMin`/`TileUvSize` exact and
  trivially testable; a later padded atlas is a new ADR.
- **A budget asserted in unit tests.** Rejected: CI machines vary; the dossier
  puts performance in nightly/per-release runs.

## Decision outcome

Chosen option: "one recorded convention per item, with the additive types in
the module that owns the data", because it keeps section 5.10 untouched while
giving every convention a single reviewable home and a pure, engine-free test.

### Additive types

Recorded as additive to the S1/S2 contracts:

- `Vector3f`, `Vector2f` in `Cubeglass.CoreMath`: hand-written C# 9
  `readonly struct`s (float components, constructor, `Zero`, `IEquatable`,
  `==`/`!=`, invariant-culture `ToString`) following `Vec3`'s style. Section
  5.10 names them without defining them (ruling R21).
- `MeshData`: the five section 5.10 fields verbatim, plus `int VertexCount`,
  `int IndexCount`, `Vector3f Min`, `Vector3f Max` and `void Release()`.
- `NeighbourSnapshot` in `Cubeglass.Voxel`, plus
  `World.CreateNeighbourSnapshot(ChunkCoord)`.
- `ChunkEditPropagation.GetAffectedChunks(Int3)` in `Cubeglass.Voxel`.
- `IChunkMesher`, `CulledMesher`, `AtlasLayout`, `AtlasMap`,
  `MeshBufferPool` in `Cubeglass.Mesh`.

### MeshData storage and ownership

The five public fields are `ReadOnlyMemory` windows over privately held,
full-capacity arrays owned by the origin `MeshBufferPool`; `VertexCount` and
`IndexCount` delimit the live region of every stream. `Min`/`Max` are the
inclusive bounds of all positions and are both the origin for an empty mesh.
`Release()` returns all five buffers to their origin pool exactly once; a
second call throws `InvalidOperationException`. Task 1 deliberately rents
fresh arrays per build (the brief allows a non-pooled reference); Task 4
replaces `MeshBufferPool`'s internals with capacity buckets and adds the
zero-allocation gate without changing the public shape.

### Winding

The internal frame is right-handed, Y up, forward `-Z`, X right (ADR-0004).
Quad corners are counter-clockwise seen from outside, with the fixed per-face
order below (cell-relative offsets, cell spanning `[c, c+1]`):

| Face | Outward normal | Corner order (v0, v1, v2, v3) |
| --- | --- | --- |
| +X | `(1, 0, 0)` | `(1,0,0) (1,1,0) (1,1,1) (1,0,1)` |
| -X | `(-1, 0, 0)` | `(0,0,0) (0,0,1) (0,1,1) (0,1,0)` |
| +Y | `(0, 1, 0)` | `(0,1,0) (0,1,1) (1,1,1) (1,1,0)` |
| -Y | `(0, -1, 0)` | `(0,0,0) (1,0,0) (1,0,1) (0,0,1)` |
| +Z | `(0, 0, 1)` | `(0,0,1) (1,0,1) (1,1,1) (0,1,1)` |
| -Z | `(0, 0, -1)` | `(0,0,0) (0,1,0) (1,1,0) (1,0,0)` |

Each quad is the two triangles `(0, 1, 2)` and `(0, 2, 3)` over its four
vertices; therefore `cross(v1 - v0, v2 - v0)` equals the stored outward
normal.

### Ambient occlusion encoding

`Ao` carries one `byte` per vertex, quantised from four levels:
`0 -> 0`, `1 -> 85`, `2 -> 170`, `3 -> 255`, where level 0 is the most
occluded and 255 is fully open. The level formula is the standard
three-neighbour rule, pinned by Task 3 tests:

`level = (side1 && side2) ? 0 : 3 - (side1 ? 1 : 0) - (side2 ? 1 : 0) - (corner ? 1 : 0)`.

Task 1's reference mesher writes 255 at every vertex until Task 3 computes
real AO; Task 3 updates the Task 2 golden hashes.

### Atlas mapping

S3 uses no padding. For an `AtlasLayout(TilesPerRow = T)`, the tile at index
`i` occupies exactly `[col/T, (col+1)/T] x [row/T, (row+1)/T]` with
`col = i % T`, `row = i / T`, tile 0 at the top-left (ADR-0004 pixel origin),
via `AtlasMap.TileMin` and `AtlasMap.TileUvSize = 1/T`. Face orientation picks
the block definition index: `+Y` and `-Y` use `AtlasIndexTop`, `+Z` and `-Z`
use `AtlasIndexFront`, `+X` and `-X` use `AtlasIndexSide`. (`-Y -> Top` keeps
the three-index model total; revisit only with real bottom art.)

Greedy merging (Task 2) keeps that mapping per block cell: a merged `W x H`
quad — `W` cells along the face's U axis and `H` along V, derived from the
winding corner table above (`+X` U=+Y,V=+Z; `-X` U=+Z,V=+Y; `+Y` U=+Z,V=+X;
`-Y` U=+X,V=+Z; `+Z` U=+X,V=+Y; `-Z` U=+Y,V=+X) — uses the same corner
pattern `(0,0), (W,0), (W,H), (0,H)` in tile units, i.e.
`uv = TileMin + TileUvSize * (cornerU * W, cornerV * H)`. A 1x1 quad therefore
reproduces the reference UVs exactly, and a merged quad repeats (tiles) the
tile once per covered cell, so its UVs may leave `[0,1]` and rely on texture
wrap; `GreedyMesher`'s class doc pins the same rule.

### Neighbour scope and missing chunks

The neighbour scope is the 26 directions with components in `{-1, 0, 1}`,
excluding zero (Chebyshev distance 1). `NeighbourSnapshot.Get(direction)`
returns the copied `ChunkSnapshot` for that direction or null when unloaded;
`GetCell(worldCell)` resolves a world cell through the owning neighbour and
returns `Air` for an unloaded neighbour, for the centre chunk and for cells
outside the 26-chunk neighbourhood. `World.CreateNeighbourSnapshot` builds the
view from currently loaded chunks; because it copies, later edits are not
observed.

### Dirty propagation rule

`ChunkEditPropagation.GetAffectedChunks(cell)` returns every chunk containing
a cell of `cell + [-1, 1]^3`, sorted by `(X, Y, Z)`: 1, 2, 4 or 8 chunks. The
box is exactly the reach of both effects of an edit: face visibility for
opaque neighbours (26 directions) and, from Task 3, ambient occlusion of any
cell whose vertex corner samples the edited cell (Chebyshev distance 1). Per
axis the box spans at most two chunks, so its eight corner cells already
enumerate every affected chunk.

### Performance budget

The meshing budget is p95 **<= 2.0 ms per full 16^3 chunk** (a solid or
terrain chunk built with `GreedyMesher`) measured on the dev machine by the
Task 4 benchmark harness: after 100 warm-up builds, 2,000 builds, printing
p50/p95/p99; the numbers are recorded in `docs/perf/s3.md`. Nightly regression
thresholds are deferred. No timing assertion enters `dotnet test`; Task 4's
unit gate is allocation-based (zero bytes after pool warm-up).

### Consequences

- Good: winding, AO encoding, atlas mapping, neighbour scope, dirty rule and
  budget each have one recorded statement and pure tests that need no engine.
- Good: section 5.10 is untouched; the additive types are listed here so later
  stages can rely on their exact shape.
- Good: the copy-based `NeighbourSnapshot` makes worker-thread meshing safe
  without locks, and missing chunks read as air so a seam never crashes.
- Bad: Task 1 intentionally allocates fresh arrays per build; only Task 4's
  pooling makes meshing allocation-free, so early benchmarks are misleading.
- Bad: `-Y -> Top` is provisional until bottom-face art exists; a bounded
  change, recorded here.
- Bad: `Cubeglass.Mesh` is C# 10 while `Cubeglass.CoreMath` stays C# 9; the
  split is deliberate (R21) but two language levels coexist in S3.
- Follow-up: Task 3 pins the AO formula in tests and updates goldens; Task 4
  implements pooling, the allocation gate and records the measured budget.

## Confirmation

- `dotnet/tests/CoreMath.Tests/VectorFloatTests.cs` pins component storage,
  `Zero`, exact equality, operators, invariant formatting and zero allocation.
- `dotnet/tests/Mesh.Tests/CulledMesherTests.cs` pins atlas UVs (16x16 grid),
  single block -> 6 quads, adjacent opaques -> no shared face, non-opaque
  neighbours never culling, border culling through the neighbour snapshot,
  solid 16^3 -> 6 x 256 quads, counter-clockwise winding (`cross(v1-v0,
  v2-v0)` equals the outward normal), the fixed index pattern, UVs inside the
  tile rect, byte-identical rebuilds, bounds, and single `Release`.
- `dotnet/tests/Voxel.Tests/NeighbourSnapshotTests.cs` and
  `ChunkEditPropagationTests.cs` pin all 26 directions, missing -> `Air`,
  copy isolation and the 1/2/4/8-chunk dirty sets in sorted order.
- `python -m depcheck --root .` keeps `Cubeglass.Mesh` free of
  `UnityEngine`, `UnityEditor`, `System.IO` and `System.Threading` and limited
  to the CoreMath + Voxel references.
- Task 4's benchmark prints p95 for the full chunk and `docs/perf/s3.md`
  records it against the 2.0 ms budget.

## Links

- Dossier sections: 3.3, 4.4, 5.10, 6 (S3), 7, 9.
- Related ADRs: [ADR-0003](0003-testing-strategy.md),
  [ADR-0004](0004-coordinate-unit-time-conventions.md),
  [ADR-0005](0005-voxel-purity-and-async-boundary.md),
  [ADR-0006](0006-chunk-storage-and-save-format.md).

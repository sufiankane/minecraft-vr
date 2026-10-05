# 0013. Cell-carrying world edit event

- Status: accepted
- Date: 2026-10-04
- Deciders: Sufyan Khan (owner)
- Consulted: .NET tech-debt wave (TD-016/TD-017), stage S7 adapter consumers
- Informed: all later-stage builders and reviewers

## Context and problem statement

`IWorld.ChunkChanged` (dossier section 5.9) is the only remesh signal in the
pure voxel module. It carries `ChunkCoord` alone. `ChunkViewManager` therefore
dirties the whole Chebyshev-1 neighbourhood of the changed chunk: a
correctness-safe superset of `ChunkEditPropagation.GetAffectedChunks`, which
already computes the exact set (1, 2, 4 or 8 chunks) from the edited cell.
ADR-0011 records this as a known bad consequence and names the follow-up: "a
cell-carrying edit signal in the Voxel layer (an additive `ChunkChanged`
variant or an `EditApplied` event)". TD-016/TD-017 track it in
`docs/notes/tech-debt.md`.

The same event is the edit path's notification point, so review finding M10
(TD-068) also lands here: a throwing subscriber currently aborts the remaining
subscribers, leaving derived mesh state stale. The pure module must not log
(ADR-0005), so any diagnostic must be exposed to the host instead.

In scope: the payload and the subscriber-fault semantics of the managed
`IWorld.ChunkChanged` event in `Cubeglass.Voxel`. Out of scope: the save
format, the C ABI, the shared-memory region (none changes) and the Unity
consumer updates, which are follow-up work in `unity/` (see Consequences).

## Decision drivers

- The adapter must be able to dirty the exact affected chunk set (TD-017),
  which needs the edited cell, not only the owning chunk.
- The event must keep its "exactly once per applied edit" semantics and the
  `Apply` result must not depend on subscribers.
- The voxel module stays pure (ADR-0005): no logging, no IO; a fault hook or
  counter is the diagnostic surface.
- The edit path must stay allocation-free (S2-WI7 allocation gate,
  `dotnet/tests/Voxel.Tests/AllocationTests.cs`), so fault isolation may not
  allocate per invocation.
- Consumers should have one event, not two: a second `ChunkEdited` event would
  either double-raise per edit or leave the old conservative path in place and
  make correct consumption optional.

## Considered options

- **Change `ChunkChanged` to `Action<ChunkEdit>`.** Chosen: one event, one
  raise, a superset payload (chunk, cell, previous and new block ids), source
  break visible at compile time in every consumer.
- **Add `ChunkEdited` and keep `ChunkChanged` unchanged.** Rejected: two
  events per edit or a stale legacy path; "additive" for the binary contract
  but worse for correctness and cost.
- **Keep `ChunkCoord` and document the conservative fan-out.** Rejected: the
  status quo TD-016 exists to remove; the exact set is already computed.
- **Let subscriber exceptions propagate.** Rejected: one faulty observer
  silently starves the others; M10 asks for defined semantics.

## Decision outcome

Chosen option: "Change `ChunkChanged` to `Action<ChunkEdit>`".

- `public readonly record struct ChunkEdit(ChunkCoord Chunk, Int3 Cell,
  BlockId Previous, BlockId New)` in `Cubeglass.Voxel`; `Previous` equals the
  accepted `EditCommand.Expected`.
- `IWorld.ChunkChanged` and `World.ChunkChanged` have the type
  `Action<ChunkEdit>`; `World.Apply` raises it exactly once per applied edit,
  after the chunk write, and never for a rejected edit.
- Per-subscriber isolation: `World` caches the invocation list when
  subscription changes (so an edit allocates nothing) and invokes each handler
  in its own try/catch. Each caught exception increments
  `World.SubscriberFaultCount` and is forwarded to `World.SubscriberFaulted`;
  a throwing fault hook is swallowed too. `Apply` still returns `Applied` and
  the remaining handlers still run.
- Hot paths use `ChunkEditPropagation.FillAffectedChunks(cell, span)` (returns
  the count; no allocation); `GetAffectedChunks` stays as the allocating
  convenience wrapper.

### Consequences

- Good, because consumers can now compute the exact dirty set from
  `ChunkEdit.Cell` (TD-017) and the block transition is observable without
  re-reading the world.
- Good, because a throwing subscriber can no longer starve the other
  subscribers or change the edit result; the host controls diagnostics through
  `SubscriberFaulted`/`SubscriberFaultCount` without the pure module doing IO.
- Good, because the isolation adds no per-edit allocation (cached invocation
  list) and the S2 allocation gate still passes.
- Bad, because the managed `IWorld` surface is source-breaking: the Unity
  consumers must be updated in the same release train. Required follow-up
  (not done in this wave; `unity/` is out of scope here):
  - `unity/Cubeglass/Packages/com.cubeglass.rendering/Runtime/ChunkViewManager.cs`
    - `:376`, `:743`, `:751` subscription/unsubscription of
      `HandleChunkChanged`;
    - `:723` `HandleChunkChanged(ChunkCoord changed)` must take `ChunkEdit`
      and should replace `FillRemeshNeighbourhood(changed, ...)` with
      `ChunkEditPropagation.FillAffectedChunks(edit.Cell, ...)`.
  - `unity/Cubeglass/Packages/com.cubeglass.input/Runtime/GameplayBridge.cs`
    - `:572` pragma comment and `:573` `EditObserver.ChunkChanged` event type.
- Version implication: this is a managed, in-repo contract only. It is not one
  of the four fingerprinted files in `contracts/`, so `CG_ABI_VERSION` stays 2,
  `contracts/abi-baseline.json`, the save format and the shared-memory region
  are unchanged. The breaking-ness is contained to the C# consumer set listed
  above.
- Follow-up: TD-017 (remesh only the exact affected chunks in
  `ChunkViewManager`) becomes implementable once the Unity consumer update
  lands.

## Confirmation

- `dotnet/tests/Voxel.Tests/WorldTests.cs` pins the payload
  (`ChunkChangedCarriesTheChunkCellAndBlockTransition`), the isolation
  (`AThrowingSubscriberDoesNotAbortTheEditOrLaterSubscribers`,
  `AFaultHookThatThrowsIsAlsoIsolated`) and unsubscribe behaviour
  (`UnsubscribedHandlerStopsReceivingChunkChanged`).
- `dotnet/tests/Voxel.Tests/ChunkEditPropagationTests.cs` pins the count,
  order and wrapper parity of `FillAffectedChunks`;
  `dotnet/tests/Voxel.Tests/AllocationTests.cs` pins its zero allocation.
- `AllocationTests.WorldGetAndApplyAllocateNothing` continues to prove `Apply`
  is allocation-free with no subscribers.
- `docs/ARCHITECTURE.md` section 4.2 and 9.3 quote the new payload, and the
  Unity update is listed above; `python -m depcheck --root .` still passes
  because no layer edge changed.

## Links

- Dossier section(s): 5.9 (world model), 5.10 (mesh hand-off), 11.3 ADR rule.
- Related ADRs: `ADR-0005` (purity), `ADR-0007` (Chebyshev-1 dirty rule),
  `ADR-0011` (records the follow-up this ADR implements).
- Debt rows: TD-016 (this decision), TD-017 (consumer follow-up), TD-068
  M10 (subscriber isolation).

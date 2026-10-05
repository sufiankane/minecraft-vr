# 0002. Shared-memory IPC between the hand service and the game

- Status: accepted
- Date: 2026-10-01
- Deciders: Sufyan Khan (owner)
- Consulted: Stage S0 build agents
- Informed: S5, S6, S8, S12 builders and reviewers

## Context and problem statement

The glasses are opened by exactly one process, because head pose and stereo
camera frames both come from the same VITURE SDK device handle (dossier section
3.4, decision D-1). The hand service (`cg-handservice`) owns that handle; the
game (`cubeglass.exe`, Unity) must still receive the newest head pose and hand
frames without blocking the render loop and without cross-process locks. The
dossier specifies the transport in section 5.6. This ADR records that decision.

No implementation lands in S0. The shared-memory writer is built in S12 and the
reader in S6; S0 records the decision and the contract so later stages build
against a frozen spec.

## Decision drivers

- One process must own the SDK device; two openers would fight over pose and
  cameras (dossier section 3.4, D-1).
- Readers must be wait-free and allocation-free on the render path (dossier NFR
  targets, section 3.4, section 5.6).
- A torn read must never be observed, and a dead writer must be reported as
  tracking-lost rather than as stale data (dossier section 5.6).
- The transport is a contract and must be frozen before implementation.

## Considered options

- **Shared memory with a seqlock per slot, single writer.** Chosen.
- **The hand service as an in-process plugin.** Rejected for S0/S12: the
  dossier records this as the alternative to revisit in S12 by ADR if shared
  memory proves slow; the contract is identical either way (section 3.4, D-1).
- **Sockets or named pipes.** Rejected: cannot guarantee wait-free, allocation-
  free reads on the render path.

## Decision outcome

Chosen option: **shared memory with seqlock slots and a single writer**, per
dossier section 3.4 (decision D-1) and section 5.6.

### Single writer

`cg-handservice` is the sole writer. It owns the VITURE SDK device and publishes
both the head pose and the hand frames. Readers (the Unity process) are
read-only. The writer stores a `writer_pid` in the header so a reader can
identify the current owner.

### Shared-memory region

Name: `Local\cubeglass.v1.state`. Layout is little-endian with 64-byte aligned
blocks:

```
Offset  Size  Field
0       8     magic  = 'CGSHM001'
8       4     abi_version (=1)
12      4     header_size
16      8     writer_pid
24      8     heartbeat_ns        (updated at least every 100 ms)
32      32    reserved
64      ...   HeadSlot   (seqlock)
                8  seq_a   (odd while writing)
               36  cg_head_sample
                8  seq_b   (equal to seq_a when stable)
256     ...   HandSlot   (seqlock)
                8  seq_a
                    cg_hand_frame
                8  seq_b
```

The head and hand payloads are the contract types `cg_head_sample` and
`cg_hand_frame` defined in dossier sections 5.1 and 5.6.

> **Amendment, 2026-10-04 (TD-009):** the `abi_version` field is **2**, not 1.
> S6 grew the hand slot with `cg_hand_frame` and bumped the region ABI
> (`kShmAbiVersion = 2` in `cpp/bridge/include/cg/bridge/shm_layout.hpp`;
> ADR-0010 records the same bump for the Unity bridge ABI). The `(=1)` value
> above is the S0 record and is kept for history; this amendment is the current
> value.

### Seqlock protocol

Reader: read `seq_a`; if odd, retry; copy the payload; read `seq_b`; accept only
if `seq_b` equals `seq_a`.

Writer: increment `seq_a` (making it odd), write the payload, store
`seq_b = seq_a + 1`, then set `seq_a = seq_a + 1`.

Atomic operations use release/acquire ordering. The contract test runs one
writer and several readers under ThreadSanitizer and checks for no torn reads.

### Heartbeat and staleness

The writer updates `heartbeat_ns` at least every 100 ms. A reader treats the
data as stale when `now - heartbeat_ns > 250 ms` and then reports
`TrackingLost`. This binds the reader above the dossier's 200 ms tracking-loss
cancellation rule for in-progress edits (dossier section 5.11) and above the
chaos-test requirement that the game reports tracking lost within 250 ms.

### Consequences

- Good: the render loop reads the newest state without locks or allocation, and
  a dead writer degrades to `TrackingLost` instead of hanging the game.
- Good: the transport is identical whether the service is a separate process or
  a future in-process plugin, so D-1 can be revisited in S12 without changing
  the game-side contract.
- Bad: the reader and writer must share one ABI and one struct layout; any
  change is a contract change requiring an ADR and a version bump.
- Follow-up: S12 builds the writer; S6 builds the reader and the fake writer;
  S12 runs the TSan torn-read test and the chaos test.

## Confirmation

The frozen spec is dossier section 5.6 and the `cg_bridge_*` functions in
section 5.12. Implementation is confirmed by the S6 bridge tests (torn-read
detection, stale-heartbeat handling) and the S12 TSan contract test. No part of
this ADR is implemented in S0.

## Links

- Dossier sections: 3.4 (decision D-1), 5.6, 5.1, 5.11, 5.12.
- Related ADRs: [ADR-0001](0001-layered-architecture.md),
  [ADR-0003](0003-testing-strategy.md).

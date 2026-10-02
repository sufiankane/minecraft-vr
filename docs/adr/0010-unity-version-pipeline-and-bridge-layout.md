# 0010. Unity version, stereo pipeline and the bridge layout

- Status: proposed (U-09 answer pending HIL; the rest is accepted for S6 software)
- Date: 2026-10-02
- Deciders: Sufyan Khan (owner)
- Consulted: Stage S6 build agents
- Informed: all later-stage builders and reviewers

## Context and problem statement

S6 delivers the stereo renderer and the Unity adapters. Three areas need one
recorded decision: the Unity version and render pipeline the project is pinned
to, the stereoscopic and coordinate-conversion design, and the changes the
Unity bridge makes to the frozen contracts and the dossier 5.6 shared-memory
region. Dossier sections 2.1 (F-04, U-09), 3.2/3.4, 5.6, 5.11..5.13 and 6/S6
apply.

This ADR changes contracts, so the version bump is part of the decision
(section 0): `contracts/cg_types.h` gains `cg_hand` and `cg_hand_frame` from
5.6 and `CG_ABI_VERSION` rises from 1 to 2 (R42); `contracts/cg_unity_bridge.h`
reproduces the 5.12 declarations verbatim. The shared-memory region keeps
`abi_version = 1` because the command channel is an additive use of previously
reserved header bytes (R43, detailed below).

Two dossier unknowns live here and are **not** answered by this ADR:
**U-09** (is per-eye distortion correction needed, and what are the real FOV
defaults?) is pending HIL. Distortion defaults to none and the FOV is a config
value until the owner's HIL run records the answer.

## Decision drivers

- One pinned Unity version and one render pipeline; no package that the
  Built-in RP path does not need.
- Coordinate conversion must happen in exactly one place, tested once
  (ADR-0004, S1 `Cubeglass.CoreMath`), so no adapter invents its own flip.
- Reads on the render path must be wait-free and allocation-free (5.6).
- The 5.6 region offsets are frozen; changes must be additive, documented and
  ignored by pre-existing readers.
- Unknowns are escalated, never guessed (U-09).

## Considered options

- **Unity 6000.6.3f1 with the Built-in Render Pipeline.** Chosen: the project
  uses two cameras with explicit viewport rects and per-eye matrices, which the
  Built-in RP supports directly; URP adds a package dependency, an asset
  pipeline and shader variants the S6 stereo path does not use.
- **Unity 6 with URP.** Rejected: no URP feature is required for SBS two-camera
  stereo, and it would change every existing material and add a package to the
  licence gate.
- **Side-by-side two cameras.** Chosen: one camera per eye with half-width
  viewports and per-eye offsets matches the glasses' SBS display and keeps the
  per-eye pose application explicit.
- **A single wide camera with an SBS blit shader.** Rejected: it hides the
  per-eye eye offsets and FOV in shader code, and late-latch would apply to a
  rendered texture instead of the camera transforms.
- **Per-eye distortion correction now.** Rejected: the lens distortion model is
  exactly U-09; defaulting to none is observable and measurable in the HIL
  checklist. If U-09 says correction is needed, a correction spec is added
  before the S6 tag.
- **P/Invoke export macros in the 5.12 header.** Rejected (R44): the header is
  frozen verbatim and has no export annotation; `cg_bridge` builds SHARED on
  Windows with `WINDOWS_EXPORT_ALL_SYMBOLS ON`, and STATIC on POSIX CI for
  compile/type checks.
- **A real writer in the bridge now.** Rejected: the writer is `cg-handservice`
  (S12) and owns the VITURE device. S6 ships a test-only native writer in the
  same DLL so C++ and C# tests can script samples without hardware; it is not
  on the production path.

## Decision outcome

Chosen options: Unity 6000.6.3f1 on the Built-in RP, SBS two-camera stereo
driven by a single conversion source, config-driven IPD/FOV with distortion
off pending U-09, and an additive bridge layout whose command channel and hand
ABI are recorded here.

### Unity version and render pipeline

The Unity project is pinned to **Unity 6000.6.3f1** with the **Built-in Render
Pipeline**. `unity/Cubeglass/Packages/manifest.json` must not gain
`com.unity.render-pipelines.universal`; materials stay Built-in. The S6
calibration scene, the two stereo cameras, the late-latch hook and the debug
overlay are all Built-in-RP features. The test runner uses the existing
`com.unity.test-framework` (EditMode + PlayMode) with local UPM packages.

### Stereoscopic design

The rig owns two cameras, one per eye. Each camera renders into its half of the
target: `rect = (0, 0, 0.5, 1)` for the left eye and `(0.5, 0, 0.5, 1)` for
the right eye, so the two viewports tile the target exactly at any
configuration. The eye offsets are `±IPD/2` along the rig's right axis and the
per-eye FOV is applied per camera. Late latch runs from a single documented
hook once per rendered frame and applies the **same** sample to both eyes;
there is no per-eye sample selection. No allocation is allowed on the
rig/render path.

### Configuration defaults

`config.json` (5.13) carries display mode, IPD, per-eye FOV, view distance and
the remaining tuning. Defaults live in code and are covered by tests:

| Setting | Default | Provenance |
|---|---|---|
| IPD | 64 mm | ADR default, config-overridable; pending HIL |
| Per-eye horizontal FOV | 45° | ADR default, config-overridable; pending HIL |
| Distortion | none | U-09 pending HIL |

If the HIL run shows that the glasses need a different FOV or distortion
correction, only the defaults/correction spec change; the config surface and
the rig math stay.

### Conversion single source

Internal (SDK/world) to Unity coordinates is converted in exactly one place:
`Cubeglass.CoreMath`'s `UnityConvert`/`ToUnityPose` (S1, ADR-0004). The Unity
bridge, the stereo rig and the late-latch code call it; no adapter or scene
script applies an ad-hoc axis flip. The S6 EditMode tests reuse the S1 golden
fixture values (for example internal yaw 90° maps to Unity −90°).

### Display configuration before Start

ADR-0009 makes every display call (`SetDisplayMode`, refresh queries) `NotReady`
while the pose source is running, because display calls must never be
concurrent with `PollPose`. S6 therefore applies display mode, refresh and
window settings **before** `IHeadPoseSource::Start` and after `Stop`; the Unity
adapter never calls display APIs while tracking is live. This is a sequencing
rule, not a new API.

### Contract additions and version bump (R42)

`contracts/cg_types.h` gains the 5.6 hand structs, unchanged from the dossier:

```c
typedef struct {
  uint8_t  present;          /* 0/1 */
  uint8_t  handedness;       /* 0 left, 1 right */
  uint8_t  reserved[2];
  float    confidence;       /* 0..1 */
  cg_vec3  joints[21];       /* head space, metres */
  cg_vec3  velocity;         /* wrist, m/s */
} cg_hand;

typedef struct {
  cg_time_ns capture_time;   /* camera frame time mapped to HostTime */
  cg_time_ns publish_time;
  cg_time_ns predicted_for;  /* the time joints are extrapolated to */
  uint32_t   sequence;
  cg_hand    hands[2];
} cg_hand_frame;
```

`CG_ABI_VERSION` becomes 2. The addition is additive for existing C consumers,
but the hand frame is an ABI-visible contract and the version counter is the
only signal a mixed-version pair can read, so it must move.

`contracts/cg_unity_bridge.h` is the 5.12 C ABI verbatim:

```c
cg_status cg_bridge_open(void** out_handle);                  /* maps shared memory */
cg_status cg_bridge_read_head(void* h, cg_head_sample* out);  /* wait-free */
cg_status cg_bridge_read_hands(void* h, cg_hand_frame* out);  /* seqlock read */
cg_status cg_bridge_send_command(void* h, uint32_t cmd);      /* recenter, start/stop service */
void      cg_bridge_close(void* h);
```

It must not gain export macros (R44). On Windows the `cg_bridge` target is
SHARED with `WINDOWS_EXPORT_ALL_SYMBOLS ON` and produces
`cg_unity_bridge.dll`; elsewhere it is STATIC, which still type-checks the C
ABI in CI.

### Shared-memory layout extension (R43)

The region name stays `Local\cubeglass.v1.state` and `abi_version` stays 1.
Bytes 32..39 of the 32-byte reserved window carry two `uint32_t` words: the
command word at 32 and its ack at 36. A pre-existing reader never interprets
the reserved window, so no reader can misparse the extension; no shm ABI bump
is needed. The remaining reserved tail is bytes 40..63.

The 5.6 offsets are unchanged, and the slot sizes are pinned relative to the
payloads rather than to the table's shorthand byte counts:

| Region | Offset | Size | Notes |
|---|---|---|---|
| `ShmHeader` | 0 | 64 | magic, abi, header size, pid, heartbeat, command/ack, reserved |
| `HeadSlot` | 64 | 64 | `seq_a` (8) + `cg_head_sample` (48) + `seq_b` (8) |
| reserved gap | 128 | 128 | head slot region is 192 bytes wide, 256 − 64 |
| `HandSlot` | 256 | 592 | `seq_a` (8) + `cg_hand_frame` (576) + `seq_b` (8) |

The dossier table's shorthand sizes omit compiler padding; the implementation
and its layout test document it. `cg_head_sample`'s fields end at byte 44
(host_time 8, pose 28, state 4, sequence 4) and int64 alignment rounds the
struct to 48 bytes (`state` sits at byte 36, the "36" in the 5.6 table).
`cg_hand_frame`'s fields end at byte 572 (capture 0, publish 8, predicted_for
16, sequence 24, hands 28) and 8-byte alignment rounds it to 576. These totals
are compile-time asserts, so a compiler/target that pads differently fails the
build instead of silently changing the ABI.

### Reader protocol, staleness and the test-only writer

Writer: store `seq_a` odd; write the payload; store `seq_b = seq_a + 1`; store
`seq_a = seq_a + 1`. Reader: read `seq_a` (retry while odd); copy the payload;
read `seq_b`; accept only when `seq_b == seq_a`. Counter accesses use
release/acquire ordering (`std::atomic` in the slot structs). A reader that
has no valid sample reports `CG_ERR_NOT_READY`; a reader whose
`now - heartbeat_ns` exceeds `kStaleAfterNs` (250 ms) reports
`CG_TRACK_LOST`/`CG_ERR_NOT_READY` rather than stale data. The writer updates
the heartbeat at least every 100 ms (5.6).

The bridge DLL also exports a **test-only native writer** (`cg_bridge_test_writer_*`,
header `cg/bridge/test_writer.h`). Rationale: the production writer is the S12
hand service and needs the VITURE device, so S6 C++ stress tests and Task 2 C#
EditMode tests script samples through the same memory layout with no hardware.
The test writer is documented as test-only in its header and is not referenced
by the production bridge functions.

## Consequences

- Good: the editor and pipeline are pinned, so stereo work and CI are
  reproducible; the Built-in RP keeps the dependency surface unchanged.
- Good: a single conversion source removes a whole class of eye/axis bugs; the
  S1 golden fixtures cover it.
- Good: the command channel and the hand ABI are additive, documented and
  versioned, and layout drift fails at compile time.
- Bad: `CG_ABI_VERSION` 2 means any stale C consumer must be rebuilt against
  the new header; in this repository nothing consumes 5.12 before S6.
- Bad: distortion is off until U-09 is answered, so the HIL checklist may
  reject the image and require a correction pass before the S6 tag.
- Follow-up: U-09 (distortion/FOV) is answered by the S6 HIL run
  (`docs/questions/S6-HIL.md`, Task 4); the S12 real writer replaces the
  test-only writer on the production path under the same layout.

## Confirmation

- `cpp/tests/bridge/layout_tests.cpp` pins every 5.6 offset, the command/ack
  offsets, the payload sizes including compiler padding, the little-endian
  `CGSHM001` bytes and `CG_ABI_VERSION == 2`; it is registered as the
  `bridge_layout` ctest entry.
- `contracts/cg_unity_bridge.h` is a verbatim copy of 5.12; Task 1b's reader
  and Task 2's C# wrapper are tested against the test-only writer.
- `python -m depcheck --root .` enforces the `bridge` layer's ONNX Runtime ban
  from `contracts/layers.json`.
- The C# P/Invoke wrapper uses blittable, explicitly laid-out structs
  (`[StructLayout(LayoutKind.Sequential)]`, `Pack = 1` only if needed) and is
  tested against the native writer.

## Links

- Dossier sections: 0 (rules), 2.1 (F-04, U-09), 3.2/3.4 (threads, D-1), 3.6,
  5.6, 5.11..5.13, 6 (S6), 7, 11.
- Related ADRs: [ADR-0002](0002-shared-memory-ipc.md),
  [ADR-0003](0003-testing-strategy.md),
  [ADR-0004](0004-coordinate-unit-time-conventions.md),
  [ADR-0008](0008-gameplay-contracts-and-tuning.md),
  [ADR-0009](0009-glasses-adapter-pose-semantics.md).
- SDD rulings: R42 (hand ABI addition, `CG_ABI_VERSION` 2), R43 (command word
  in reserved bytes 32..39), R44 (SHARED + `WINDOWS_EXPORT_ALL_SYMBOLS`, no
  export macros in the verbatim header).
- Escalation: `docs/questions/S6-HIL.md` (U-09; created in Task 4).

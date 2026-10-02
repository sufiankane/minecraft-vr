# 0009. Glasses adapter: status mapping, pose slot, recentre and predict semantics

- Status: proposed (U-01 and U-08 answers are pending HIL; the rest is accepted
  for S5 software)
- Date: 2026-10-02
- Deciders: Sufyan Khan (owner)
- Consulted: Stage S5 build agents
- Informed: all later-stage builders and reviewers

## Context and problem statement

Dossier sections 5.1 and 5.2 freeze the head pose contracts: `cg_types.h`
(`cg_status`, `cg_track_state`, `cg_head_sample`) and the C++
`IHeadPoseSource` port. Section 4.2 sketches a `Result<T>`/`Status` vocabulary
but does not define it in a contract file, and section 5.2's snippet names
`HostTime`, `Pose`, `Duration` and `TrackState` without declaring them. This
ADR records the additive C++ vocabulary and the semantics the S5 glasses
adapters implement behind the frozen port:

- `contracts/cpp/result.hpp`: `cg::StatusCode`, `cg::Status`,
  `cg::Result<T>`/`Result<void>`, `cg::FromCgStatus`;
- `contracts/cpp/ports.hpp`: `cg::Duration`, `cg::TrackState`, dossier 5.2
  verbatim (`HeadSample`, `IHeadPoseSource`);
- `cpp/glasses`: `PoseSlot`, `ManualClock`, `FakeHeadPoseSource`, and later
  `VitureHeadPoseSource` and `ReplayHeadPoseSource`.

`contracts/cg_types.h` is unchanged and `ports.hpp` reproduces 5.2 verbatim;
the new names are additive, so no contract version bump is required (SDD
ruling R34: additive contract vocabulary is recorded here rather than silently
assumed). `cg-glasses` is an adapter: threads, `windows.h`, file IO and the
SDK seam are permitted, but it must not link ONNX Runtime or engine code
(`contracts/layers.json`); every SDK call goes through `IVitureApi` (Task 2).

Two dossier unknowns live in this area and are **not** answered here. Per
`docs/questions/S5-HIL.md` they halt until hardware is available:

- **U-01 (pending HIL):** does Carina pose polling work in 3DoF mode on Luma
  Ultra on Windows, and what are the real pose rate and latency?
- **U-08 (pending HIL):** exact 3D-mode switching behaviour on Windows
  (display mode API, SBS signalling, refresh rate).

## Decision drivers

- Contracts frozen: the decisions must be additive, testable without hardware
  and recorded in one place.
- `TryGetLatest` must be wait-free on the render path: no locks, no allocation,
  no blocking, even while the polling thread publishes.
- One writer (the polling thread), many readers (game/render threads).
- Deterministic tests: the fake must be driven by a manual clock with no wall
  time, RNGs seeded per test.
- Recentre must reset heading without a visible roll or pitch jump (F-04,
  FR-08), and predict must hide one frame of pose latency without inventing
  motion.
- Every unknown must be marked pending HIL, never guessed.

## Considered options

- **`Result<T>` as `std::variant<T, Status>`.** Rejected: the variant's
  valueless-by-exception state and its stricter requirements buy nothing here;
  every type in use is nothrow-movable.
- **`Result<T>` as `std::optional<T>` plus a `Status`.** Chosen: trivially
  inspectable, works with move-only payloads, no heap for the contract types.
- **Throwing error handling.** Rejected: contract rule "no function throws"
  (section 5.1, Global Constraints).
- **Mutex-protected double buffer for the pose slot.** Rejected: readers would
  block behind the writer; the render path must not take a lock.
- **Seqlock with an unbounded reader retry loop.** Rejected: a reader could
  spin forever if the writer is preempted mid-publish; the retry is bounded
  (64).
- **Fallback to a previous payload copy after the bounded retries.** Rejected
  (R39): a second, unvalidated copy can still tear under a writer that never
  lets up, and a torn pose is worse than a missed frame. `TryRead` returns
  false instead; the caller keeps its previous frame.
- **Recentre by rewriting the slot's newest sample.** Rejected: a reader-side
  transform keeps `Publish` single-purpose and makes recentring observable
  immediately without a new sample (and without violating "no sample after
  `Stop`").
- **Recentre applied only to the next published sample.** Rejected for the
  fake: the newest sample would remain un-recentred until the next tick, which
  breaks the 0.1 degree contract test and flickers on a static head.
- **Unbounded prediction.** Rejected: extrapolating far past the last sample
  invents motion; the horizon is capped at 100 ms.

## Decision outcome

Chosen option: additive contract vocabulary, a bounded-retry seqlock slot, a
read-time yaw offset for recentring, and a 100 ms-capped linear prediction.

### Contract vocabulary (R34)

`cg::StatusCode` maps one to one onto `cg_status`; `FromCgStatus` performs that
mapping and is tested as a table:

| `cg_status` | `cg::StatusCode` |
| --- | --- |
| `CG_OK` | `Ok` |
| `CG_ERR_INVALID_ARG` | `InvalidArgument` |
| `CG_ERR_NOT_READY` | `NotReady` |
| `CG_ERR_DEVICE` | `Device` |
| `CG_ERR_TIMEOUT` | `Timeout` |
| `CG_ERR_UNSUPPORTED` | `Unsupported` |
| `CG_ERR_INTERNAL` | `Internal` |

`cg::Status` carries that word plus a non-owning `const char*` message (empty
by default) and `IsOk()`. `cg::Result<T>` holds either a moved `T` or a
`Status`; `Result<void>` holds only a `Status`. `operator*` is a
precondition-checked accessor like `std::optional`. `Ok(value)`, `Ok()` and
`Err<T>(status)` are the factories. No operation throws.

`cg::Duration` is a signed nanosecond count on the host timeline.
`cg::TrackState` is `Stable` (zero), `Unstable`, `Lost`, matching
`cg_track_state`. `HeadSample` and `IHeadPoseSource` are dossier 5.2 verbatim.

### Thread and slot model

`PoseSlot` is a single-writer/many-reader wait-free slot with seqlock
semantics and exactly one payload buffer. The payload is copied word-wise
through relaxed `std::atomic_ref<std::uint64_t>` accesses, so every payload
access is an atomic operation and the implementation is clean under TSan with
no annotations. The writer bumps an even/odd version counter around the payload
stores; a reader copies the payload and accepts it only when the version is
unchanged and even. The retry is bounded (`PoseSlot::kMaxReadAttempts`, 64):
`TryRead` returns false before the first publish and on exhaustion, and never
returns an unvalidated sample (R39). A transient false is allowed under
pathological writer pressure; the caller keeps its previous frame and the next
call normally succeeds. `Publish` and `TryRead` are `noexcept`, take no lock,
allocate nothing and have a bounded number of steps. `HeadSample` is asserted
trivially copyable, 8-byte sized and 8-byte aligned, and the atomic accesses
are asserted lock-free at compile time.

The fake is driven by `ManualClock`: `AdvanceSamples(n)` emits the next `n`
scripted samples on that clock, starting at sequence 1 with the sample time
equal to the clock instant (the clock then advances by one period). `Start`
and `Stop` are idempotent and thread-safe; after `Stop`, `AdvanceSamples` is a
no-op and the newest published sample stays readable; a later `Start` resumes
the existing sequence and timeline, so sequence and time remain strictly
increasing across a restart. The suite these rules are pinned by is
`cpp/tests/glasses/contract_tests.cpp`, parameterised over implementation
factories; Viture-over-`FakeVitureApi` and replay register their own factories
in Tasks 2 and 3.

### Viture SDK seam and loader (S5 Task 2a)

Every vendor call goes through `IVitureApi`
(`cpp/glasses/include/cg/glasses/viture_api.hpp`): `CreateDevice`,
`DestroyDevice`, `StartPose`, `PollPose` (blocking, capped at
`kViturePollTimeoutNs` = 100 ms), `ResetOriginCarina`, `SetDisplayMode`,
`GetRefreshHz`, `SdkVersion` and `RequestStop`. `CreateDevice`/`StartPose`/
`DestroyDevice` are lifecycle calls made on the wrapper's caller thread
(`Start`/`Stop`) and on the polling thread while reconnecting, serialised with
the polling loop; `PollPose`/`ResetOriginCarina`/display calls run on the
polling thread. `RequestStop` is the one cross-thread call, is thread-safe and
`noexcept`, and must make a blocked `PollPose` return promptly with `Timeout`;
`StartPose` clears it. Time enters the wrapper as an injected `IHostClock`
(`SteadyHostClock` in production, `ManualHostClock` in tests). No translation
unit includes a vendor header; tests drive `FakeVitureApi`
(`cpp/tests/glasses/fake_viture_api.hpp`), which scripts a `Result` per call,
counts calls and blocks a long poll until `RequestStop`.

`LoadVitureApi(dll_path)` (`cpp/glasses/src/viture_loader.cpp`) is the only TU
that opens the vendor library (`LoadLibraryW`/`GetProcAddress` on Windows,
`dlopen`/`dlsym` on POSIX). An empty path is `InvalidArgument`; a library that
cannot be opened, or an unresolved entry point, is `Unsupported` with the path
or symbol name in the message (the message is interned because `Status` is
non-owning). The returned API owns the library handle.

**Vendor symbol binding (pending HIL).** The exact exported symbol names, the
calling convention and the blocking behaviour of the VITURE SDK are not covered
by dossier facts F-01..F-10; F-02/F-04 describe the Carina poll and recentre
calls in prose only, and the display-mode surface is U-08. The function-pointer
table in `viture_loader.cpp` therefore uses documented placeholder names and
contract-shaped signatures. Until the HIL run answers this, loading the real
library fails with `Unsupported` naming the first unresolved placeholder; the
table and its thin adapter are rewritten in that single TU when the answers
exist. The loader is tested on the error paths only (missing library, empty
path), which is all that is testable without the vendor DLL.

### Polling policy (Task 2)

`VitureHeadPoseSource`
(`cpp/glasses/include/cg/glasses/viture_head_pose_source.hpp`) runs one
`std::jthread` per started session, named `cg-viture-poll` with
`SetThreadDescription` on Windows; an injectable setup hook (no-op default,
swallowed if it throws) raises its priority in production. `Start` performs
`CreateDevice` and `StartPose` on the caller thread, so a failed start returns
the SDK error synchronously and leaves the object ready for a clean retry,
then launches the thread; the thread owns `PollPose`, `ResetOriginCarina` and
the reconnect lifecycle calls.

**SDK time and the repurposed `host_time` field.** `cg_head_sample::host_time`
is a legacy C field name inherited from the dossier 5.2 struct: it does **not**
carry a host instant. `IVitureApi::PollPose` fills it with the SDK's monotonic
timestamp encoded in nanoseconds (F-05 counts SDK timestamps in seconds), and
the wrapper converts it once with `ToSeconds`, feeds
`ClockMapper.AddSample(seconds, clock.Now())` and publishes
`ClockMapper.Map(seconds)`. Reading the raw field as an existing `HostTime`
would map it a second time; the ADR-0004 rule that the SDK's double seconds are
converted once at the adapter boundary is what makes this field the sole time
seam.

**Fault and quiet policy.** A `Device`/`Timeout` (or any other) poll failure
destroys the device and waits an interruptible backoff of 100 ms doubling to a
2 s cap. After `kMaxReconnectAttempts` = 10 consecutive failed recreates the
thread stops recreating and probes every 2 s, reporting `Lost` until a poll
succeeds; a success resets the attempt counter, the backoff and the quiet
state. With no successful poll it publishes a synthetic sample carrying the
last pose and the current host time: `Unstable` after 500 ms
(`kUnstableAfter`) and `Lost` after 1000 ms (`kLostAfter`). Backoff and quiet
thresholds are measured on the injected `IHostClock`, so tests pin the
schedule with `ManualHostClock` and `Stop` interrupts a pending 2 s backoff
immediately.

**Recentre.** `Recenter` is posted to the polling thread (the only caller of
the seam), which narrows the newest pose to the SDK's `float[7]` layout
(`[px, py, pz, qw, qx, qy, qz]`), calls `ResetOriginCarina` and returns its
`Result` to the waiting caller. On success the wrapper arms an inverse-yaw
correction for samples whose sequence is at or below the reset sample, so the
newest pre-reset sample reads recentred as soon as `Recenter` returns (pitch
and roll untouched, position unchanged) while later samples arrive already
recentred from the SDK; the polling thread holds the next publish until a
reader observes the corrected sample, bounded by `kRecentreReadTimeout` = 1 s.

### Recentre

`Recenter` means "make the current heading yaw zero" (FR-08, F-04). Yaw is the
rotation about world +Y; positive yaw turns counter-clockwise seen from above
(ADR-0004). The fake keeps an inverse-yaw offset and composes it onto the pose
at read time, so the newest sample is recentred as soon as `Recenter` returns,
pitch and roll are untouched (the offset is a pre-multiplied pure-yaw
rotation), and repeated calls are idempotent. The contract suite pins
`|yaw| <= 0.1 deg` after recentre with pitch preserved within 0.1 deg. The real
adapter (Task 2) delegates to the SDK's `ResetOriginCarina`; U-01 (pending HIL)
will confirm what the SDK reports before and after that call.

### Predict

`TryGetLatest(out, predict)` returns the newest sample when `predict` is zero,
verbatim. For a positive `predict`, the fake extrapolates yaw and pitch
linearly by the last inter-sample rate, capped at 100 ms:
`dt = min(predict, 100 ms)`, `time = newest.time + dt`, `seq` and `state` are
copied from the newest sample, and the position is unchanged. Rates come from
the two most recent samples; before there are two, the rate is zero. Negative
`predict` values are treated as zero.

### U-01 (pending HIL)

Status: **unanswered**. `docs/questions/S5-HIL.md` is the escalation record;
the owner runs `cg-pose-probe` against the Luma Ultra and commits the measured
rate, latency and status semantics under `docs/notes/s5-hil/`. This section is
filled in from those artefacts (and the S5 note's 6DoF-and-discard-position
decision, if 3DoF pose fails) before `stage-5-complete`.

### U-08 (pending HIL)

Status: **unanswered**. The display mode API, SBS signalling and refresh-rate
behaviour are confirmed by the same hardware run (Task 3's `IDisplayControl`
is provisional until then). This section is filled in before
`stage-5-complete`.

## Consequences

- Good: the render path reads a consistent pose without locks or allocation,
  with a bounded reader worst case.
- Good: the fake is fully deterministic and needs no wall clock or hardware,
  so the contract suite runs everywhere.
- Good: the additive vocabulary is recorded once; `cg_types.h` and section 5.2
  are untouched.
- Bad: a reader can observe a transient false under a writer that saturates the
  slot; the late-latch caller keeps its previous frame (R39). The slot relies on
  a single writer, and the version-validated seqlock is a lock-free protocol
  rather than a single atomic snapshot.
- Bad: prediction is linear in yaw/pitch and frozen after 100 ms, so fast
  reversals inside one frame are not modelled; a real predictor is a later
  stage concern.
- Follow-up: U-01/U-08 answers, and the real adapter's status/timeout mapping,
  must be added to this ADR before `stage-5-complete` (S5 Task 5).

## Confirmation

- `cpp/tests/glasses/contract_tests.cpp` runs the parameterised contract suite
  (start idempotence, stop idempotence and totality, false before the first
  sample, strictly increasing sequence and time, finite unit quaternions,
  recentre, predict zero/cap/direction, restart ordering) plus the
  `Status`/`Result` mapping table, `PoseSlot` behaviour and fake script tests.
  The concurrent slot case tolerates transient false reads and asserts zero
  torn and zero out-of-order samples; it passes under `--gtest_repeat=100`
  against a tight writer.
- `cpp/tests/glasses/viture_loader_tests.cpp` pins the loader error paths: a
  missing library is `Unsupported` with the path in the message, and an empty
  path is `InvalidArgument`. The `IVitureApi` seam and `FakeVitureApi` are
  exercised by the Task 2b fault/contract suites under the same `glasses`
  ctest entry.
- `ctest --preset ci` registers the suite as `glasses`.
- `python -m depcheck --root .` enforces the `glasses` layer's ONNX Runtime ban.

## Links

- Dossier sections: 0 (rules), 2.1, 3.2/3.3, 4.2/4.4, 5.1, 5.2, 6 (S5), 7,
  11 (protocol), 12/A (ADR list).
- Related ADRs: [ADR-0003](0003-testing-strategy.md),
  [ADR-0004](0004-coordinate-unit-time-conventions.md),
  [ADR-0008](0008-gameplay-contracts-and-tuning.md).
- SDD ruling R34 (additive contract vocabulary).
- SDD ruling R39 (a pose slot read that exhausts its bounded retries returns
  false; the caller keeps its previous frame, never a torn sample).
- Escalation: `docs/questions/S5-HIL.md` (U-01, U-08; pending hardware).

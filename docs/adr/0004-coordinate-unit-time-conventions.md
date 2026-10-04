# 0004. Coordinate, unit and time conventions

- Status: accepted
- Date: 2026-10-01
- Deciders: Sufyan Khan (owner)
- Consulted: Stage S1 build agents
- Informed: all later-stage builders and reviewers

## Context and problem statement

Cubeglass implements the same core maths twice — C++ (`cg-core-math`) and C#
(`Cubeglass.CoreMath`) — and both implementations must produce identical results
so that one shared golden fixture can pin them. The code also meets two foreign
conventions at its edges: the VITURE SDK supplies poses as seven `float` values,
and Unity consumes left-handed coordinates. Without a recorded convention, each
implementation and each adapter would guess axis order, quaternion component
order, sign flips and time units independently, and mismatches would surface only
as pose drift on real glasses.

The dossier fixes the conventions in section 3.6 and the shared C types in
section 5.1. This ADR records them once for S1 and fixes the format of the two new
contract files introduced by the stage: `contracts/cg_types.h` (section 5.1
verbatim, carrying `CG_ABI_VERSION 1`) and `contracts/golden/transforms.json`
(`schema: 1`). This decision **adds** contract files; it changes no existing
contract file, so no contract version bump is required. Any later change to these
conventions requires a new ADR plus a bump of `CG_ABI_VERSION` or the fixture
`schema`.

## Decision drivers

- Two independent implementations must agree to within `1e-6` on every shared
  operation (dossier sections 4.4, 6/S1).
- Precision, layout and handedness may differ only at the two boundaries; core
  code must be uniform (dossier section 3.6).
- Hot paths — every value operation, every conversion and `ClockMapper` — must
  allocate nothing; this is measured by counter tests, not by inspection.
- SDK time is `double` seconds; the rest of the system needs one monotonic
  `int64` nanosecond timeline with offset error within 1 ms (dossier section
  6/S1 required tests).
- Test-only dependencies must be permissively licensed and declared to the
  licence gate (dossier sections 4.2, 9).

## Considered options

- **One recorded convention per item, enforced by the shared C types and one
  golden fixture consumed by both languages.** Chosen.
- **Per-module conventions with conversions at each call site.** Rejected: every
  call site is a sign/order error waiting to happen, and each conversion risks
  allocating on a hot path.
- **Store quaternions as `(x, y, z, w)` (common math-library order).** Rejected:
  the SDK and the dossier section 3.6 use `(w, x, y, z)`; matching that removes
  one reorder at the SDK boundary.
- **`float` internally to match the SDK and Unity.** Rejected: accumulated
  rotation error is visible across a frame; the C ABI already carries `float` at
  the boundary, where the precision loss is unavoidable anyway.

## Decision outcome

Chosen option: "one recorded convention per item, enforced by the shared C types
and one golden fixture consumed by both languages", because it puts each
convention in a single reviewable place and turns disagreement between the two
implementations into a red fixture test instead of a hardware-only defect.

### Axes and handedness

Right-handed, Y up, forward **-Z**, X right (dossier section 3.6, matching the
VITURE OpenGL pose). Identity rotation leaves the forward vector at `(0, 0, -1)`.

### Numeric precision and boundaries

Core value types (`Vec3`, `Quat`, `Pose`, `HostTime`) are `double` or `int64`.
`float` appears only at two boundaries: reading the VITURE SDK pose
(`float[7]`) is widened on read, and writing to Unity narrows to `float` in the
S6 adapter. The C ABI types `cg_vec3`, `cg_quat` and `cg_pose` in
`contracts/cg_types.h` are `float` structs; that header is a boundary contract,
not the internal representation.

### Quaternion storage

Stored as `(w, x, y, z)` in both languages and written as `[w, x, y, z]` in
fixtures. Quaternions are always unit: construction normalises (`FromComponents`
normalises before returning; degenerate input — squared norm `< 1e-24` or a
non-finite component — becomes identity). `Rotate-vector` uses
`t = 2 * Cross(q_xyz, v); return v + q.w * t + Cross(q_xyz, t);` and the Hamilton
product is not renormalised. `quat_inverse` is the conjugate (valid because unit).

### Units and pixel origin

Metres, seconds, radians; pixel coordinates are pixels with origin top-left.
Radians are used everywhere internally; degrees exist only in presentation.

### Time: `HostTime` and `ClockMapper`

`HostTime` is `int64` nanoseconds on one monotonic timeline. SDK `double` seconds
are converted once, at the adapter boundary, through `ClockMapper`.

`ClockMapper` keeps the most recent 32 `(sdk_seconds, host_time)` samples in a
fixed array (no allocation). For each sample the offset in seconds is
`offset_i = host_time_i / 1e9 - sdk_seconds_i`, and the estimate is the median of
the offsets in the current window:

- take a stack copy of the window and sort it by offset;
- odd sample count → the middle value;
- even sample count → the mean of the two middle values.

Mapping is `Map(sdk) = round((sdk + median_offset_seconds) * 1e9)` with
round-to-nearest, halfway away from zero (`llround` in C++), returning `int64`.
Before any sample the offset is zero, so `Map(sdk) = ToNanoseconds(sdk)`, and
`IsReady()` reports readiness from 8 samples onward. The error target is at most
1 ms offset error once ready; S1 pins jitter at `< 0.5 ms` and drift at
`< 1 ms` in tests. The window deliberately lags a step change in offset by up to
16 samples; the median makes it robust to outlier callbacks.

### Unity conversion

The formulas are:

- position `(x, y, z) -> (x, y, -z)`;
- quaternion `(w, x, y, z) -> (w, -x, -y, z)`.

They are implemented exactly once per language in the pure core module — C++
`cg::core_math` (`convert.hpp`/`convert.cpp`) and C#
`Cubeglass.CoreMath.UnityConvert` — and the S6 Unity adapter
(`cg-unity/Convert`) must call them and only narrow to `float`; it must not
re-derive the flip. The dossier's "exactly one place" rule (section 3.6) is
satisfied by that single implementation feeding the one adapter.

### Golden fixtures

`contracts/golden/transforms.json` has the shape
`{"schema": 1, "tolerance": 1e-6, "cases": [...]}`. Each case is
`{"name": <string>, "op": <string>, "input": {...}, "expected": ...}`. Numbers
are JSON numbers, never strings. Quaternions are `[w, x, y, z]`; poses are
`{"p": [x, y, z], "q": [w, x, y, z]}`.

| `op` | `input` fields | `expected` shape |
| --- | --- | --- |
| `vec3_dot` | `a`, `b` | number |
| `vec3_cross` | `a`, `b` | vec3 |
| `vec3_length` | `a` | number |
| `vec3_normalize` | `a` | vec3 |
| `quat_normalize` | `q` | quat |
| `quat_rotate` | `q`, `v` | vec3 |
| `quat_multiply` | `a`, `b` | quat |
| `quat_inverse` | `q` | quat |
| `quat_slerp` | `a`, `b`, `t` | quat |
| `pose_compose` | `a`, `b` | pose |
| `pose_inverse` | `a` | pose |
| `pose_transform_point` | `a`, `v` | vec3 |
| `sdk_pose_to_internal` | `sdk` (7 numbers, `[px,py,pz,qw,qx,qy,qz]`) | pose |
| `unity_position` | `p` | vec3 |
| `unity_rotation` | `q` | quat |
| `unity_pose` | `a` | pose |
| `clock_map` | `samples` (`[[sdk_seconds, host_ns], ...]`, oldest first), `query_sdk` | integer |

**Exact-value rule.** `clock_map` expectations are exact `int64` values and are
compared exactly (no tolerance): the samples and the `Map` result are integer
nanoseconds. Every other expectation is compared with the file's `tolerance`,
absolutely and element-wise for vectors, quaternions and poses. Both language
runners must read this one file, verify `schema == 1`, dispatch every listed
`op`, fail on an unknown `op`, and fail if an op defined in the fixture was never
visited. The fixture is the contract: a failing implementation is fixed, never
the fixture.

### Test-only dependencies

Two dependencies are added for the golden and property tests, and are declared in
`contracts/licence-allowlist.json` when the C++ manifest adds them (Task 2):

- **nlohmann-json — MIT.** Reason: parse `contracts/golden/transforms.json` in
  the C++ golden runner. The C# runner uses in-box `System.Text.Json`.
- **rapidcheck — BSD-2-Clause.** Reason: property-based tests for quaternion
  invariants (normalisation, rotation preserving length, `q * q⁻¹` identity).

Neither dependency enters a shipped artefact; both are test-only.

### Consequences

- Good: every convention has one recorded statement, and disagreement between
  the C++ and C# implementations fails the shared 28-case fixture.
- Good: conversions are two total, allocation-free functions; the adapter cannot
  invent its own flip.
- Bad: the `float` C ABI caps precision at the SDK boundary; this is inherent to
  the SDK input and accepted.
- Bad: the 32-sample median lags a genuine offset step by up to 16 samples; the
  jitter/drift budgets are set around that.
- Follow-up: the S6 adapter must call the CoreMath conversion functions. The
  section 9.7 contract-compatibility gate this ADR expected from the deferred S1
  follow-up did not land then; it was added on 2026-10-03 as
  `python -m depcheck contracts` and is enforced in the required `depcheck` job
  (see [ADR-0003](0003-testing-strategy.md)).

## Confirmation

- `contracts/golden/transforms.json` is consumed by the C++ golden runner
  (`cpp/tests/core-math/golden_test.cpp`, Task 5) and the C# runner
  (`dotnet/tests/CoreMath.Tests/GoldenFixtureTests.cs`, Task 8); both run all 28
  cases, `clock_map` exactly and the rest within `1e-6`.
- `contracts/cg_types.h` reproduced dossier section 5.1 with `CG_ABI_VERSION 1`
  at S1; it rose to **2** in ADR-0010 when the section 5.6 hand types joined the
  shared ABI. A single `git grep -c CG_ABI_VERSION contracts/cg_types.h` must
  still print 1 (one macro definition), and `python -m depcheck contracts`
  enforces the fingerprint/version coupling from 2026-10-04 onwards.
- `ClockMapper` jitter, drift and median tests in Tasks 4 and 7 pin the
  estimator behaviour against the 1 ms budget.
- The licence checker (`python -m depcheck licences`) enforces the allowlist
  entries once Task 2 adds nlohmann-json and rapidcheck.
- Review: the S6 adapter diff must not contain a second implementation of the
  Unity flip.

## Links

- Dossier sections: 3.6, 4.2, 4.4, 5.1, 6 (S1), 9.
- Related ADRs: [ADR-0001](0001-layered-architecture.md),
  [ADR-0002](0002-shared-memory-ipc.md),
  [ADR-0003](0003-testing-strategy.md).

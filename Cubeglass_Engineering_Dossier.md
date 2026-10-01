# CUBEGLASS: Engineering Dossier

Voxel sandbox for VITURE Luma Ultra on Windows PC, 3DoF head tracking, stereo-camera hand tracking.

| Field | Value |
|---|---|
| Document status | Baseline v1.0 (for agentic build) |
| Date | 2026-10-01 |
| Owner | Sufyan Khan |
| Audience | Autonomous or semi-autonomous coding agents, and human reviewers |
| Rule of precedence | Contracts (section 5) > Stage specs (section 6) > Everything else |

---

## 0. How to use this dossier (read first)

This dossier is a build contract. It is divided into **stages**. Each stage has a goal, scope, interfaces, tasks, tests and an **exit gate**. 

Hard rules for any agent working from it:

1. **One stage at a time.** Do not start stage N+1 until every exit-gate item of stage N is green in CI and the stage is tagged `stage-N-complete`.
2. **Contracts are frozen.** Section 5 defines the public interfaces and data formats. Changing a contract needs a new ADR (architecture decision record) and a version bump. Never change a contract silently to make code compile.
3. **Tests first.** For each unit of work, write the failing test, then the implementation, then refactor. Tests must be deterministic and runnable without hardware unless tagged `hil` (hardware-in-the-loop).
4. **No hardware in the default test run.** Every hardware dependency sits behind a port (interface) with a fake and a recorded-data replay implementation.
5. **No monoliths.** Each module has a single responsibility, a public API of at most one header or one interface file, and dependencies pointing inward only (section 3.3).
6. **Unknowns are spikes, not guesses.** Facts not verified are listed in section 2.2. Resolve them in the stage that owns them and record the answer in an ADR before building on it.
7. **Stop and ask** if a requirement conflicts with a contract, a gate cannot be met, or an unknown changes the architecture. Write the question in `docs/questions/` and halt that work item.

---

## 1. Product definition

### 1.1 Goal
A Minecraft-like voxel game rendered in stereo to VITURE Luma Ultra glasses connected to a Windows PC. The player looks around (3DoF head orientation), walks with a gamepad or keyboard, and places and breaks blocks. Final input is hand tracking from the glasses' side stereo cameras, with blocks "carried" in the hands.

### 1.2 Non-goals (v1)
- No Neckband, phone or Android build.
- No positional (6DoF) head movement in game logic. The architecture must allow it later without rewriting game code.
- No multiplayer, mobs, crafting or survival rules.
- No use of the front RGB pass-through camera for hand tracking (decision by owner).

### 1.3 Functional requirements

| ID | Requirement |
|---|---|
| FR-01 | The app outputs full side-by-side stereo (3840x1080) to the glasses and the view rotates with head orientation. |
| FR-02 | The player can walk with a gamepad or keyboard, with collision against blocks. |
| FR-03 | A procedurally generated, deterministic voxel world streams around the player. |
| FR-04 | The player can break and place blocks using a targeted block (gaze raycast first, hand ray later). |
| FR-05 | A hotbar of block types is available, selectable by input. |
| FR-06 | World edits persist between sessions. |
| FR-07 | Hand tracking (two hands, 21 joints each) drives pointing, placing, breaking and block selection. |
| FR-08 | A recentre action resets heading. |
| FR-09 | Hand tracking loss or glasses disconnection degrades safely (no stuck actions, no crash). |

### 1.4 Non-functional requirements (initial targets; validate in Stage 0 spike and tune by ADR)

| ID | Target |
|---|---|
| NFR-01 | Render at the highest 3D mode the glasses support (60 or 90 Hz). Frame time p99 below the frame budget on an RTX 5070. |
| NFR-02 | Head pose applied to the render as late as possible (late-latch). Pose read to photon path measured and reported. |
| NFR-03 | Hand pipeline: capture callback to published joints p95 at or below 15 ms (inference included); no frame queues, newest frame wins. |
| NFR-04 | Zero heap allocations per frame in steady state on hot paths (render loop, pose thread, hand pipeline). |
| NFR-05 | Core libraries have at least 90 percent line coverage and 100 percent of public contract behaviour covered. |
| NFR-06 | All persisted and shared data formats are versioned and backward-checked by golden-file tests. |
| NFR-07 | Clean shutdown within 2 seconds with no leaked threads or handles (verified with sanitizers and a soak test). |

---

## 2. Facts, assumptions and unknowns

### 2.1 Verified facts (from VITURE documentation supplied by owner)

| ID | Fact |
|---|---|
| F-01 | The VITURE Glasses SDK is a C SDK. Desktop (Windows) is supported. |
| F-02 | Luma Ultra is a "Carina" device. Pose is polled with `xr_device_provider_get_gl_pose_carina(handle, pose[7], predict_time_seconds, &status)` on a dedicated background thread. Pose layout `[px,py,pz,qw,qx,qy,qz]`, OpenGL convention (x right, y up, z backward). |
| F-03 | `xr_device_provider_set_dof_type_carina(handle, 0)` selects 3DoF. It must be called after `create` and before `initialize`. Default is 6DoF. Changing mode needs a full stop, shutdown, destroy and re-create. |
| F-04 | `xr_device_provider_reset_origin_carina(handle, float* pose)` recentres: position and yaw take the given pose, pitch and roll stay gravity-anchored. |
| F-05 | `xr_device_provider_register_callbacks_carina` registers: pose (25 Hz), vsync, IMU `[ax,ay,az,gx,gy,gz]`, and stereo camera callbacks. All timestamps are monotonic seconds (double). |
| F-06 | Stereo camera callback: `(char* left0, char* right0, char* left1, char* right1, double timestamp, int width, int height)`. 8-bit grayscale, 25 Hz. Buffers are valid only during the callback. |
| F-07 | Stereo cameras use auto exposure by default. Manual exposure ranges: time 0.01 to 8.0 ms, gain 0 to 15. Manual exposure can degrade VIO tracking. |
| F-08 | The front pass-through camera is a separate UVC device (1920x1080, 30 fps, MJPEG). Out of scope for hand tracking. |
| F-09 | Side-by-side 3D modes are 3840x1080 at 60 Hz or 90 Hz. 120 Hz modes are 2D only. |
| F-10 | The header exposes no camera intrinsics, extrinsics or field of view. |

### 2.2 Unknowns (each is owned by a stage and must be resolved by an ADR)

| ID | Unknown | Owner stage |
|---|---|---|
| U-01 | Does the Carina pose polling work in 3DoF mode on Luma Ultra on Windows? What is the real pose rate and latency? | S5 |
| U-02 | Do stereo camera frames arrive in 3DoF mode? If not, plan B is to run 6DoF and discard position. | S8 |
| U-03 | Stereo image resolution, row stride, pixel layout; contents of frame 0 versus frame 1. | S8 |
| U-04 | Lens model, field of view, baseline, camera-to-display extrinsics (must be self-calibrated). | S9 |
| U-05 | Do the camera and pose timestamps share a clock? What is the clock offset to the host's monotonic clock? | S8 |
| U-06 | Can hands be seen in both cameras (stereo overlap) in the volume where the player holds their hands? | S8 (go/no-go gate G-A) |
| U-07 | Do off-the-shelf hand-landmark models work on these grayscale images, or is fine-tuning needed? | S10 |
| U-08 | Exact 3D-mode switching behaviour on Windows (display mode API, SBS signalling, refresh rate). | S5, S6 |
| U-09 | Does the glasses' display need lens pre-distortion correction? Field of view per eye for the virtual cameras? | S6 |

---

## 3. Architecture

### 3.1 Context

```
+-----------+   USB-C (video + data)   +-----------------------------+
| Luma Ultra| <----------------------> |  Windows PC (RTX 5070)      |
|  glasses  |                          |                             |
| IMU/VIO   |  pose (polled)           |  [Glasses Adapter]          |
| 2 grey    |  stereo frames (25 Hz)   |  [Hand Service]  (C++)      |
|  cameras  |                          |  [Game]          (Unity/C#) |
+-----------+                          +-----------------------------+
```

### 3.2 Components

| Component | Language | Responsibility | Depends on |
|---|---|---|---|
| `cg-core-math` | C++ and C# (parallel, contract-tested) | Types, coordinate conversion, transforms, time, clock mapper | none |
| `cg-glasses` | C++ | Wraps the VITURE SDK: device lifecycle, pose polling thread, display mode, recentre, camera callback fan-out. Fakes and replay. | core-math, VITURE SDK |
| `cg-recorder` | C++ | Records stereo frames and poses to the dataset format and replays them as a source | core-math, glasses ports |
| `cg-calib` | Python (offline tool) | Computes calibration from recorded boards, writes `calibration.json` | dataset format |
| `cg-handcore` | C++ | Pure algorithms: rectify, crop, triangulate, skeleton constraints, filters, predictor. No I/O. | core-math |
| `cg-infer` | C++ | ONNX Runtime wrapper behind `IInferenceEngine`, with TensorRT RTX, DirectML and CPU providers | none (ORT) |
| `cg-handservice` | C++ | Real-time pipeline: frame source to hand frames, publishes through shared memory | handcore, infer, glasses ports |
| `cg-voxel` | C# (netstandard2.1, no Unity types) | Blocks, chunks, world, generation, raycast, edit commands, persistence | core-math (C#) |
| `cg-mesh` | C# (netstandard2.1) | Greedy mesher producing engine-neutral mesh data | voxel |
| `cg-gameplay` | C# (netstandard2.1) | Interaction logic, input actions, hotbar, gesture recogniser, state machines | voxel, core-math |
| `cg-unity` | C# (Unity 6 asmdefs) | Thin adapters: render rig, mesh upload, input providers, native plugin bridge, UI | everything above |

### 3.3 Dependency rule
Dependencies point inward only. Pure logic (`core-math`, `handcore`, `voxel`, `mesh`, `gameplay`) never references the engine, the VITURE SDK, ONNX Runtime, the file system or threads. Adapters at the edge implement ports defined by the inner layers. A CI check (include and reference lint) fails the build on a violation.

```
 adapters (cg-unity, cg-glasses, cg-infer, cg-handservice shell)
        |
        v
 ports (interfaces in contracts)
        |
        v
 pure logic (core-math, handcore, voxel, mesh, gameplay)
```

### 3.4 Process and thread model

| Process | Threads |
|---|---|
| `cubeglass.exe` (Unity) | Main thread (game, render); job workers (generation, meshing); I/O thread (save/load); native plugin bridge reads shared state without locks |
| `cg-handservice.exe` (C++) | SDK camera callback thread (copy only, return immediately); pipeline worker (GPU preprocess, inference, triangulation, publish); pose thread owned by the glasses adapter |

**Decision D-1:** The glasses are opened by exactly one process. Because the pose and the cameras both come from the same SDK device handle, the hand service owns the device and publishes both head pose and hand frames to the game through shared memory. (Alternative: the hand service as an in-process plugin. Revisit in S12 by ADR if shared memory proves slow; the contract is identical either way.)

### 3.5 Data flow

```
SDK pose thread --> PoseSlot ---------------------------------------> Game (late-latch)
SDK cam callback --> FrameSlot (newest wins) --> Pipeline worker
   --> rectify/crop (GPU) --> inference --> triangulate --> skeleton --> filter --> predict
   --> HandFrame --> SeqlockBuffer (shared memory) --> Game HandProvider --> Gesture --> Actions
```

### 3.6 Conventions (all code must follow; tested in S1)

- **Handedness and axes:** right-handed, Y up, forward is -Z, X right (matches the VITURE OpenGL pose). Unity (left-handed) conversion happens in exactly one place: `cg-unity/Convert`.
- **Unity conversion:** position `(x, y, z) -> (x, y, -z)`; quaternion `(w, x, y, z) -> (w, -x, -y, z)`. Covered by tests with known rotations; do not trust this note over the tests.
- **Units:** metres, seconds, radians. Pixel coordinates are in pixels with origin top-left.
- **Time:** `int64` nanoseconds on one monotonic timeline named `HostTime`. SDK `double` seconds are converted once, at the adapter boundary, using a `ClockMapper` (offset estimated from callbacks).
- **Quaternion storage:** `(w, x, y, z)` as in the SDK. Always normalised on input.
- **Joint order:** 21 joints in MediaPipe order (0 wrist, 1-4 thumb, 5-8 index, 9-12 middle, 13-16 ring, 17-20 little).
- **Hand space:** joint positions are in head space (origin between the eyes, same axes as above). World space is obtained by applying the head pose.

---

## 4. Engineering standards

### 4.1 Repository layout (monorepo, one build per language)

```
cubeglass/
  docs/                  # adr/, questions/, dossier, diagrams
  contracts/             # C headers, JSON schemas, shared-memory spec, golden files
  cpp/
    CMakeLists.txt  CMakePresets.json  vcpkg.json
    core-math/  glasses/  recorder/  handcore/  infer/  handservice/
    tests/ (per module)  tools/
  dotnet/
    Cubeglass.sln
    src/ Voxel/  Mesh/  Gameplay/  CoreMath/
    tests/ Voxel.Tests/ ...
  unity/
    Packages/ com.cubeglass.* (asmdefs)  Assets/  Tests/
  python/
    calib/ pyproject.toml  tests/
  data/                  # small fixtures only; large datasets via Git LFS or external storage
  .github/workflows/  .editorconfig  .clang-format  .clang-tidy
```

### 4.2 Tooling and language rules

| Area | Standard |
|---|---|
| C++ | C++20; CMake with presets; vcpkg manifest; `-Wall -Wextra -Wpedantic -Wconversion -Werror`; `clang-format`; `clang-tidy` (core guidelines, bugprone, performance); no raw owning pointers; RAII; no exceptions across module or ABI boundaries; `std::span`, `std::chrono`; errors via `Result<T>` (e.g. `tl::expected`) |
| C++ tests | GoogleTest and GoogleBenchmark; `rapidcheck` for property tests; sanitizers (ASan, UBSan, TSan) in CI on Linux/MSVC-equivalent where available; MSVC `/analyze` |
| C# | Nullable enabled; `TreatWarningsAsErrors`; Roslyn analyzers and `.editorconfig`; records and readonly structs for data; no `UnityEngine` in non-Unity assemblies; no allocation in hot paths (Span, pooled arrays); NUnit with `dotnet test`; FsCheck for property tests; Stryker.NET mutation testing on `Voxel` and `Gameplay` periodically |
| Unity | Assembly definitions per package; EditMode and PlayMode tests with Unity Test Framework; no business logic in MonoBehaviours (they only wire adapters); ScriptableObject config |
| Python | 3.11+; `ruff`, `mypy --strict`, `pytest`; used only for offline tools |
| Docs | Every public header and interface has Doxygen or XML docs describing preconditions, postconditions, thread-safety and error behaviour |

### 4.3 Software lifecycle

- **Branching:** trunk-based. Short-lived branches (maximum 1 day of work each), pull request required, squash-merge.
- **Commits:** Conventional Commits (`feat:`, `fix:`, `test:`, `refactor:`, `docs:`, `perf:`, `build:`, `ci:`). One logical change per commit.
- **Versioning:** SemVer for each contract and each library. Contract changes need an ADR and a changelog entry.
- **ADRs:** `docs/adr/NNNN-title.md` using the MADR template (context, options, decision, consequences). Mandatory for any change to a contract, dependency or architecture rule.
- **Review:** every PR needs a green CI, a checklist (section 11.3) completed in the PR description, and a reviewer separate from the author (a second agent or the owner).
- **Dependencies:** pinned versions, a licence check in CI, and a recorded reason for each dependency.
- **Releases:** tagged `vX.Y.Z`, build artefacts produced by CI only, release notes generated from commits.
- **Logging and telemetry:** structured logs (levels, module, timestamp); a lightweight metrics API (counters, histograms) with no allocation on hot paths; a debug overlay reads these.

### 4.4 Test strategy (pyramid)

| Level | Scope | Hardware | Runs |
|---|---|---|---|
| Unit | Single class or function, pure logic | none | every commit |
| Property-based | Invariants (e.g. raycast never returns an empty cell, save/load round trip) | none | every commit |
| Contract | Same behavioural suite run against every implementation of a port (real, fake, replay) | none (fake/replay) | every commit |
| Golden-file | Data formats, calibration maths, meshes | none | every commit |
| Integration | Two or more modules together using replay data | none | every commit |
| Performance | Benchmarks with budgets; regression threshold fails CI | none (GPU runner optional) | nightly and per release |
| Hardware-in-loop (`hil`) | Real glasses | glasses | manual, scripted, results committed as artefacts |
| Soak | 30-minute run with fakes: leaks, thread count, memory growth | none | nightly |

**Definition of Done (per work item):** acceptance tests written and passing; all lint and analyser gates clean; coverage target met for the module; docs updated; no TODO without a linked issue; contracts unchanged or ADR attached; benchmarks within budget.

---

## 5. Contracts

Contracts live in `contracts/`. They are the single source of truth. Both language implementations of shared types (C++ and C#) are tested against the same golden JSON fixtures.

### 5.1 Common types (C ABI, `contracts/cg_types.h`)

```c
#include <stdint.h>
#define CG_ABI_VERSION 1

typedef int64_t cg_time_ns;            /* HostTime, monotonic nanoseconds */

typedef struct { float x, y, z; }         cg_vec3;
typedef struct { float w, x, y, z; }      cg_quat;        /* unit quaternion */
typedef struct { cg_vec3 p; cg_quat q; }  cg_pose;        /* position metres, rotation */

typedef enum {
  CG_OK = 0,
  CG_ERR_INVALID_ARG = 1,
  CG_ERR_NOT_READY = 2,
  CG_ERR_DEVICE = 3,
  CG_ERR_TIMEOUT = 4,
  CG_ERR_UNSUPPORTED = 5,
  CG_ERR_INTERNAL = 6
} cg_status;

typedef enum { CG_TRACK_STABLE = 0, CG_TRACK_UNSTABLE = 1, CG_TRACK_LOST = 2 } cg_track_state;

typedef struct {
  cg_time_ns  host_time;     /* time the sample refers to */
  cg_pose     pose;          /* head pose; in 3DoF mode p is (0,0,0) */
  cg_track_state state;
  uint32_t    sequence;
} cg_head_sample;
```

Rules: all functions return `cg_status`; no function throws; out-parameters are written only on `CG_OK`; every handle has an explicit `create`/`destroy` pair; every callback states its thread and lifetime of pointers in documentation.

### 5.2 Port: head pose source (C++ interface, `contracts/cpp/ports.hpp`)

```cpp
namespace cg {
struct HeadSample { HostTime time; Pose pose; TrackState state; uint32_t seq; };

class IHeadPoseSource {
 public:
  virtual ~IHeadPoseSource() = default;
  // Starts delivery; idempotent. Thread-safe.
  virtual Result<void> Start() = 0;
  virtual void Stop() noexcept = 0;
  // Wait-free. Returns the newest sample; never blocks; never allocates.
  virtual bool TryGetLatest(HeadSample& out, Duration predict) const noexcept = 0;
  // Recentres heading to the current orientation (yaw only).
  virtual Result<void> Recenter() = 0;
};
}
```
Implementations: `VitureHeadPoseSource` (real), `FakeHeadPoseSource` (scripted trajectories), `ReplayHeadPoseSource` (from dataset). All pass the same contract test suite (monotonic sequence, `TryGetLatest` wait-free, recenter zeroes yaw within tolerance, stop is idempotent).

### 5.3 Port: stereo frame source

```cpp
namespace cg {
struct StereoImage {            // views are valid only during the callback
  const uint8_t* left;  const uint8_t* right;
  int width; int height; int stride;   // stride in bytes
};
struct StereoFrame {
  HostTime time; uint64_t seq;
  StereoImage f0;   // frame 0 pair
  StereoImage f1;   // frame 1 pair (semantics resolved by ADR from U-03)
};
class IStereoFrameSink { public: virtual ~IStereoFrameSink()=default;
  virtual void OnFrame(const StereoFrame&) noexcept = 0; };   // must return quickly

class IStereoFrameSource { public: virtual ~IStereoFrameSource()=default;
  virtual Result<void> Start(IStereoFrameSink*) = 0;
  virtual void Stop() noexcept = 0; };
}
```
Implementations: `VitureStereoSource`, `FakeStereoSource` (synthetic patterns), `ReplayStereoSource` (dataset). Contract tests: in-order sequence, timestamps non-decreasing, no sink call after `Stop` returns.

### 5.4 Port: inference engine

```cpp
namespace cg {
enum class Precision { FP32, FP16 };
struct TensorView { void* data; DType type; std::span<const int64_t> shape; Device device; };

class IInferenceEngine { public: virtual ~IInferenceEngine()=default;
  virtual Result<ModelHandle> Load(const ModelSpec&) = 0;     // path, precision, cache dir
  virtual Result<void> Run(ModelHandle, std::span<const TensorView> in,
                           std::span<TensorView> out) = 0;     // no allocation after warm-up
  virtual EngineInfo Info() const noexcept = 0;               // provider actually in use
};}
```
Implementations: TensorRT RTX provider, DirectML, CPU, and `FakeInferenceEngine` returning canned outputs. A provider fallback chain is configured, never hard-coded.

### 5.5 Pure algorithm contracts (`cg-handcore`)

| Function | Input | Output | Properties tested |
|---|---|---|---|
| `Rectify` | image, `CameraModel`, maps | rectified image | identity camera leaves image unchanged; round trip within tolerance |
| `Triangulate` | two 2D points, `StereoRig` | 3D point (metres), reprojection error | synthetic ground truth within 1 mm; invalid when disparity near zero |
| `FitSkeleton` | 21 raw joints, `HandProfile` | constrained joints | bone lengths preserved within tolerance; deterministic |
| `OneEuroFilter` | sample stream | filtered stream | step response and jitter limits; zero allocations |
| `Predictor` | joint history, target time | predicted joints | constant-velocity trajectory predicted exactly; bounded for large dt |
| `Hand2DTo3D` | per-camera landmarks | `HandObservation` with confidence | missing camera handled; confidence monotonic with agreement |

```cpp
struct Intrinsics { double fx, fy, cx, cy; double dist[8]; CameraModelType model; };
struct StereoRig  { Intrinsics left, right; Pose right_from_left; int width, height; };
struct RigToHead  { Pose head_from_left_camera; };
struct HandObservation { bool present; Handedness side; float confidence;
                         Vec3 joints[21]; /* head space, metres */ float reproj_error[21]; };
```

### 5.6 Hand frame (shared memory) and head pose slot

Shared memory name: `Local\cubeglass.v1.state`. Layout (little-endian, 64-byte aligned blocks). Writer is `cg-handservice`; readers are read-only.

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
Reader protocol: read `seq_a`; if odd retry; copy; read `seq_b`; accept only if equal to `seq_a`. A reader treats data as stale if `now - heartbeat_ns > 250 ms` and then reports `TrackingLost`. Writer protocol: increment `seq_a` (odd), write, store `seq_b = seq_a + 1`, set `seq_a = seq_a + 1`. Atomic operations use release/acquire ordering. The contract test runs a writer and several readers under TSan and checks no torn reads.

### 5.7 Calibration file (`calibration.json`, schema versioned)

```json
{
  "schema": 1,
  "device_serial": "string",
  "created_utc": "ISO-8601",
  "rig": {
    "image_size": [0, 0],
    "left":  {"model": "fisheye|pinhole", "fx":0,"fy":0,"cx":0,"cy":0,"dist":[]},
    "right": {"model": "fisheye|pinhole", "fx":0,"fy":0,"cx":0,"cy":0,"dist":[]},
    "right_from_left": {"p":[0,0,0],"q":[1,0,0,0]}
  },
  "head_from_left_camera": {"p":[0,0,0],"q":[1,0,0,0]},
  "hand_profile": {"bone_lengths_m": [], "tolerance": 0.15},
  "quality": {"reproj_rms_px": 0.0, "n_views": 0}
}
```
Validation: JSON Schema in `contracts/`; loader rejects unknown major schema; golden fixtures for good and bad files.

### 5.8 Dataset format (`.cgrec` directory)

```
session_YYYYMMDD_HHMMSS/
  manifest.json            # schema, device info, DOF mode, SDK version, sizes, counts
  frames/NNNNNNNN_l0.pgm, _r0.pgm, _l1.pgm, _r1.pgm   (or one packed .bin per frame)
  stereo.csv               # seq, host_time_ns, sdk_time_s, width, height
  pose.csv                 # host_time_ns, sdk_time_s, px,py,pz,qw,qx,qy,qz, status
  labels/ (optional)       # hand annotations for evaluation
```
Replay sources read this format and must reproduce original timing within 1 ms when run in real-time mode, and run as fast as possible in test mode.

### 5.9 Voxel domain contracts (C#, `Cubeglass.Voxel`)

```csharp
public readonly record struct BlockId(ushort Value);
public readonly record struct Int3(int X, int Y, int Z);

public interface IBlockRegistry {
    BlockDefinition Get(BlockId id);                  // solidity, opacity, atlas indices, hardness
    IReadOnlyList<BlockId> Placeable { get; }
}

public interface IWorld {
    BlockId Get(Int3 cell);                           // unloaded cell -> BlockId.Air or Unloaded flag
    bool IsLoaded(Int3 cell);
    EditResult Apply(in EditCommand cmd);             // the only mutation path
    event Action<ChunkCoord> ChunkChanged;            // used to mark meshes dirty
}

public readonly record struct EditCommand(Int3 Cell, BlockId Expected, BlockId New, long Tick);

public interface IWorldGenerator { Chunk Generate(ChunkCoord c, long seed); }   // pure, deterministic

public interface IRaycaster {
    RayHit? Cast(IWorld w, Ray ray, float maxDistance);   // DDA; returns cell and entry face normal
}

public interface IWorldStore {
    ValueTask SaveAsync(ChunkCoord c, ChunkDelta delta, CancellationToken ct);
    ValueTask<ChunkDelta?> LoadAsync(ChunkCoord c, CancellationToken ct);
}
```
Invariants (property-tested): `Apply` with a mismatching `Expected` is rejected; the same seed and coordinates yield identical chunks; save then load returns identical deltas; raycast hit cell is solid and the entry face points toward the ray origin; placing never occupies the player's collision box.

### 5.10 Mesh contract (`Cubeglass.Mesh`)

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
Invariants: no internal faces between two opaque blocks; the mesh of a fully solid chunk is exactly 6 quads per face region with greedy merging; mesh vertex count never exceeds budget; same input gives byte-identical output.

### 5.11 Input and gameplay contracts (C#)

```csharp
public interface IInputProvider {                   // gamepad, keyboard/mouse, hands
    InputFrame Sample(double timeSeconds);          // pure read of latest state
}
public readonly struct InputFrame {
    public Vector2f Move;  public float TurnSnap;  public bool RecenterPressed;
    public PointerRay? Pointer;                     // world-space ray, null if none
    public ButtonState Primary;                     // break
    public ButtonState Secondary;                   // place
    public int HotbarDelta;                         // -1, 0, +1
    public TrackingQuality Quality; }

public interface IGestureRecognizer {               // pure state machine
    GestureOutput Update(in HandsInput hands, double dt);   // pinch, fist, palette flick, with hysteresis
}
public interface IInteractionService {              // turns input frames into world edits
    InteractionResult Update(in InputFrame input, IWorld world, PlayerState player, double dt);
}
```
Behaviour rules: any tracking loss longer than 200 ms cancels in-progress break and place; edit commands are the only side effect; services hold no references to Unity types.

### 5.12 Native bridge to Unity (C ABI, `cg_unity_bridge.h`)

```c
cg_status cg_bridge_open(void** out_handle);                  /* maps shared memory */
cg_status cg_bridge_read_head(void* h, cg_head_sample* out);  /* wait-free */
cg_status cg_bridge_read_hands(void* h, cg_hand_frame* out);  /* seqlock read */
cg_status cg_bridge_send_command(void* h, uint32_t cmd);      /* recenter, start/stop service */
void      cg_bridge_close(void* h);
```
C# P/Invoke wrapper lives in one file, uses blittable structs only, and is tested against a fake shared-memory writer.

### 5.13 Configuration (`config.json`, versioned)
Display mode, IPD and per-eye field of view, view distance, key bindings, filter parameters, model paths, provider priority list, logging level. Loaded by one `ConfigService`; invalid values fail fast with a descriptive error; defaults live in code and are covered by tests.

---

## 6. Build plan and stages

Stage order is strict. Milestones: **M1** (end of S7) a playable 3DoF game with gamepad; **M2** (end of S12) live hand tracking data in the game; **M3** (end of S14) release candidate.

```
S0 -> S1 -> S2 -> S3 -> S4 -> S5 -> S6 -> S7 (M1)
                                  \-> S8 -> [G-A go/no-go] -> S9 -> S10 -> S11 -> S12 (M2) -> S13 -> S14 (M3)
```
S2 to S4 (pure game logic) and S5/S8 (device work) may run in parallel by separate agents because they share only contracts. S7 requires S3, S4, S5 and S6.

### S0: Foundation and engineering platform

**Goal.** A repository where every later stage can be built, tested and reviewed automatically.

**Depends on.** nothing

**Deliverables**
- Repository layout from section 4.1, with empty module skeletons that build.
- CMake presets, vcpkg manifest, .NET solution, Unity project skeleton, Python tooling.
- CI pipeline (build, lint, test, coverage, sanitizers, dependency-rule check, licence check).
- `.editorconfig`, `.clang-format`, `.clang-tidy`, analyzer rulesets, PR template (section 11.3), ADR template.
- ADR-0001 (language and layering), ADR-0002 (shared-memory IPC), ADR-0003 (testing strategy).

**Work items (in order)**
1. Create the monorepo and pin toolchains (compiler, CMake, .NET SDK, Unity 6 LTS version, Python).
2. Add one trivial library per language with one passing test, to prove each CI lane.
3. Implement the dependency-rule checker (script that fails on forbidden includes or references).
4. Configure coverage reporting and the benchmark job (nightly).
5. Write `CONTRIBUTING.md` containing the agent rules from section 0.

**Required tests**
- CI self-test: a deliberately failing lint, test and forbidden include each turn the pipeline red (kept as disabled negative tests).
- Fresh-clone build script succeeds on a clean machine image.

**Exit gate (all must pass)**
- [ ] Fresh clone builds and tests green with one command per language.
- [ ] CI blocks merging when any gate fails.
- [ ] All three ADRs merged.
- [ ] Tag `stage-0-complete`.

### S1: Core math, time and conventions

**Goal.** One tested implementation of coordinate conventions, transforms and clocks, shared by C++ and C#.

**Depends on.** S0

**Deliverables**
- `cg-core-math` (C++) and `Cubeglass.CoreMath` (C#): Vec3, Quat, Pose, transforms, interpolation, `HostTime`, `ClockMapper`.
- Conversion functions SDK pose to internal, internal to Unity.
- Shared golden fixtures `contracts/golden/transforms.json` consumed by both languages.

**Work items (in order)**
1. Write fixtures (input pose, expected output) before code, including axis-aligned and compound rotations.
2. Implement types and operations; normalised quaternions enforced on construction.
3. Implement `ClockMapper`: estimates the offset between SDK seconds and HostTime from paired samples using a robust (median or minimum-delay) estimator.
4. Implement slerp, pose composition and inverse.

**Required tests**
- Property tests: compose(inverse(p), p) equals identity; rotation preserves vector length; slerp endpoints.
- Golden tests: identical numerical results in C++ and C# within 1e-6.
- Unity conversion test: a forward-looking pose maps to the expected Unity forward vector.
- ClockMapper tests with simulated jitter and drift: offset error within 1 ms.

**Exit gate (all must pass)**
- [ ] Coverage at least 95 percent for both libraries.
- [ ] Both languages pass the same golden fixtures.
- [ ] No allocations in hot functions (benchmark with allocation counter).
- [ ] Tag `stage-1-complete`.

### S2: Voxel core (pure domain)

**Goal.** Deterministic world model, generation, raycast, edit commands and persistence with no engine dependencies.

**Depends on.** S1

**Deliverables**
- `Cubeglass.Voxel` implementing section 5.9.
- Block registry with a data-driven definition file.
- Terrain generator (seeded simplex noise, layered heights).
- Chunk store with delta persistence (versioned binary, run-length encoding).
- Player collision utility (AABB vs voxel grid).

**Work items (in order)**
1. Chunk and coordinate maths (world cell to chunk and local, negative coordinates handled correctly).
2. World with `Apply(EditCommand)` as the sole mutator, plus change events.
3. DDA raycaster.
4. Generator with determinism guarantees.
5. Delta serialisation with a format header and version; migration hook.
6. AABB collision and the rule that placement cannot overlap the player.

**Required tests**
- Property tests (FsCheck): world-to-chunk-to-world round trip including negative coordinates; raycast invariants from section 5.9; edit then undo returns the original; save/load round trip.
- Golden test: a fixed seed generates a known chunk hash.
- Benchmark: generate one chunk and 100k raycasts under budget; allocation-free raycast.
- Fuzz test: corrupted save data is rejected without exceptions escaping.

**Exit gate (all must pass)**
- [ ] All invariants in 5.9 have tests and pass.
- [ ] Coverage at least 90 percent; mutation score at least 70 percent on `Voxel`.
- [ ] Zero allocations in raycast and Get/Apply hot paths.
- [ ] Tag `stage-2-complete`.

### S3: Meshing

**Goal.** Engine-neutral greedy mesher with ambient occlusion and correct chunk-border handling.

**Depends on.** S2

**Deliverables**
- `Cubeglass.Mesh` implementing section 5.10.
- Texture atlas mapping data model.
- Mesh benchmark suite.

**Work items (in order)**
1. Naive face-culling mesher as the reference implementation (slow but obviously correct).
2. Greedy mesher; compare its visible-area output against the reference for random chunks.
3. Per-vertex ambient occlusion.
4. Border handling using neighbour snapshots; dirty propagation rules.
5. Buffer pooling.

**Required tests**
- Differential test: greedy and reference meshers cover identical surface area for thousands of random chunks.
- Golden mesh hashes for a few canonical chunks.
- Border test: editing a border block marks the neighbour dirty and the seam has no gaps.
- Benchmark: p95 meshing time for a full chunk under the budget set in the stage ADR.

**Exit gate (all must pass)**
- [ ] Differential and golden tests pass.
- [ ] No allocations after pool warm-up.
- [ ] Budget met on the dev machine; numbers recorded in `docs/perf/`.
- [ ] Tag `stage-3-complete`.

### S4: Input abstraction and gameplay logic

**Goal.** All behaviour that turns input into world edits, tested without Unity or hardware.

**Depends on.** S2, S3 (S1 for maths)

**Deliverables**
- `Cubeglass.Gameplay`: `InputFrame`, `IInputProvider`, `IInteractionService`, hotbar, player controller (walk, gravity, collision), break-time logic, recentre request.
- Scripted input provider for tests.
- Gesture recogniser interface and a first implementation using mock hand joints (full recogniser in S13).

**Work items (in order)**
1. Player movement integrator with fixed timestep and collision.
2. Interaction service: target selection, hold-to-break, place on face, hotbar cycling, tracking-loss cancellation.
3. State machines as explicit enums with transition tables.
4. Replayable input scripts (JSON) for regression tests.

**Required tests**
- Scenario tests from scripts: walk, break, place, cancel on tracking loss, cannot place inside player.
- Property test: no input sequence can produce an invalid world state.
- Determinism: the same script and seed give the same final world hash.

**Exit gate (all must pass)**
- [ ] All scenarios pass in plain `dotnet test`.
- [ ] Coverage at least 90 percent.
- [ ] Tag `stage-4-complete`.

### S5: Glasses adapter and head pose (hardware spike plus adapter)

**Goal.** A reliable `IHeadPoseSource` backed by the VITURE SDK, with fake and replay equivalents. Resolves U-01 and U-08.

**Depends on.** S1

**Deliverables**
- `cg-glasses`: SDK wrapper (RAII handles), `VitureHeadPoseSource`, `FakeHeadPoseSource`, `ReplayHeadPoseSource`.
- Display-mode control (3D SBS, refresh) behind `IDisplayControl`.
- Console tool `cg-pose-probe` for hardware verification.
- ADR: pose behaviour in 3DoF, measured rate, latency and status semantics.

**Work items (in order)**
1. Write the contract test suite for `IHeadPoseSource` first and run it against the fake.
2. Wrap the SDK C API in a thin RAII layer; map status codes to `Result`.
3. Implement the polling thread (dedicated, named, high priority), publishing to a wait-free slot; convert units and conventions at this boundary.
4. Implement recentre with `reset_origin_carina`.
5. Implement reconnect and error handling: device unplug, unstable status, timeouts.
6. Run `cg-pose-probe` on hardware: log rate, jitter, status and 3DoF behaviour; commit results.

**Required tests**
- Contract tests pass for fake and replay; run on real hardware under tag `hil`.
- Thread-safety test under TSan: producer thread plus many readers.
- Fault-injection tests: SDK returns errors, device removed, long stalls.
- Soak test with fake: 30 minutes, no leak.

**Exit gate (all must pass)**
- [ ] Contract suite green for all three implementations.
- [ ] HIL log committed; U-01 and U-08 answered in an ADR.
- [ ] Wait-free read verified by benchmark (no locks, no allocation).
- [ ] Tag `stage-5-complete`.

**Notes.** If pose does not work in 3DoF mode, the ADR must choose 6DoF-and-discard-position and the adapter must expose it through the same interface.

### S6: Stereo renderer and Unity adapters

**Goal.** Unity project that renders side-by-side stereo driven by the head pose source. Resolves U-09.

**Depends on.** S1, S5

**Deliverables**
- `cg-unity` packages: `Cubeglass.Unity.Rendering`, `Cubeglass.Unity.Bridge`, `Cubeglass.Unity.Input`.
- Native bridge library `cg_unity_bridge` and its P/Invoke wrapper (section 5.12) with a fake writer.
- Stereo camera rig with per-eye viewport, IPD and field of view from config, late-latch pose application.
- Test scene: calibration room with orientation markers and a debug overlay (frame time, pose rate, latency).

**Work items (in order)**
1. Implement the shared-memory reader and fake writer; seqlock tests.
2. Implement the rig: two cameras, half-width viewports, config-driven parameters.
3. Late-latch hook just before culling or render, reading the newest head sample.
4. Implement the Unity conversion (single place) using the S1 golden fixtures.
5. Window management: borderless fullscreen on the glasses display; fallback preview window for development.
6. Add synthetic pose input so the rig is testable without hardware.

**Required tests**
- EditMode tests: conversion fixtures; rig math (eye offsets, viewports).
- PlayMode tests: rig responds to scripted poses; no allocations per frame (profiler assertion).
- Bridge tests: torn-read detection, stale heartbeat handling.
- HIL: visual checklist (horizon level, yaw direction, pitch direction, depth sanity, recentre).

**Exit gate (all must pass)**
- [ ] All automated tests pass; HIL checklist signed off and logged.
- [ ] Frame time budget met in the test scene.
- [ ] U-09 answered in an ADR (distortion not needed, or specified).
- [ ] Tag `stage-6-complete`.

### S7: Vertical slice: playable 3DoF game (Milestone M1)

**Goal.** Integrate S2 to S6 into a playable game using gamepad or keyboard and gaze targeting.

**Depends on.** S3, S4, S5, S6

**Deliverables**
- World streaming controller (load, generate, mesh, unload by distance) with a frame-time budget.
- Unity mesh upload adapter and chunk renderers with pooling.
- Gamepad and keyboard/mouse `IInputProvider` implementations.
- In-world UI: reticle and hotbar, world-locked.
- Persistence wired to the store.
- Comfort options: snap turn, vignette while moving.

**Work items (in order)**
1. Implement the streaming scheduler as a pure class with a deterministic test harness, then the Unity adapter.
2. Upload meshes through a bounded queue (maximum N per frame).
3. Wire the interaction service to input and world.
4. Wire save on edit batches and on exit.
5. Profile and tune to the frame budget.

**Required tests**
- Integration test: headless simulation of a 5-minute scripted session; the final world hash matches the golden value.
- PlayMode smoke tests: boot, load world, break and place a block, save, reload, block persisted.
- Performance test: view distance of 8 chunks holds the frame budget p99 on the RTX 5070.
- HIL playtest checklist with the glasses (comfort, input, no stuck states).

**Exit gate (all must pass)**
- [ ] All tests green; HIL checklist passed; known issues triaged in the tracker.
- [ ] Performance numbers recorded in `docs/perf/m1.md`.
- [ ] Release candidate `v0.1.0` tagged and built by CI.
- [ ] Tag `stage-7-complete` (**Milestone M1**).

### S8: Stereo capture, recorder and feasibility (Go/No-go G-A)

**Goal.** Capture stereo frames reliably, record datasets, and decide whether stereo hand tracking is viable. Resolves U-02, U-03, U-05 and U-06.

**Depends on.** S1, S5

**Deliverables**
- `IStereoFrameSource` implementations: Viture, Fake, Replay (section 5.3).
- `cg-recorder` and the `.cgrec` format (section 5.8) with a validator.
- `cg-capture-probe` tool: logs resolution, stride, rate, frame-0 versus frame-1 behaviour, and behaviour in 3DoF and 6DoF.
- Dataset protocol document: recording scripts (hand positions, lighting, distances).
- Feasibility report and ADR for gate G-A.

**Work items (in order)**
1. Write contract tests for frame sources (order, timing, stop semantics).
2. Implement a lock-free single-slot "newest frame wins" handoff; the callback copies and returns within a budget.
3. Implement the recorder with a bounded writer queue and drop accounting (never block the callback).
4. Implement the validator and the replay source with timing fidelity.
5. Record sessions on hardware in 3DoF and 6DoF with hands in a defined volume grid; annotate a subset of frames with approximate hand presence per camera.
6. Measure the clock offset between SDK timestamps and HostTime.
7. Produce the feasibility report: fraction of frames where the hand is visible in both cameras by position, image quality, frame-0 versus frame-1 findings.

**Required tests**
- Contract tests for all three sources.
- Recorder stress test: the callback never blocks beyond budget; drops are counted and reported.
- Format round trip: record with the fake, then validate, and replay equals the original.
- Callback timing benchmark.

**Exit gate (all must pass)**
- [ ] U-02, U-03, U-05 and U-06 answered in ADRs with data attached.
- [ ] **Gate G-A:** stereo overlap covers a usable hand volume. Proposed criterion: both hands visible in both cameras in at least 90 percent of frames inside a defined 40 cm x 30 cm x 30 cm region at bent-arm distance. If not met, stop and raise `docs/questions/` with alternatives (6DoF mode, guidance on hand position, an external camera) before S9.
- [ ] Dataset of at least 20 minutes stored externally, with its manifest in the repo.
- [ ] Tag `stage-8-complete`.

### S9: Calibration

**Goal.** Reproducible camera calibration and hand profile. Resolves U-04.

**Depends on.** S8 (gate G-A passed)

**Deliverables**
- `cg-calib` (Python + OpenCV): board capture from `.cgrec`, stereo calibration, quality report, `calibration.json` writer (section 5.7).
- C++ `CalibrationLoader` with schema validation.
- Head-from-camera alignment procedure (guided routine and solver).
- Hand profile measurement (bone lengths) routine.

**Work items (in order)**
1. Generate synthetic calibration data from a known rig and verify the solver recovers it (ground-truth test).
2. Implement board detection, model selection (fisheye or pinhole) and the stereo solve; report reprojection RMS.
3. Implement the loader and golden fixtures.
4. Implement the alignment solver as a pure function (inputs: pointing correspondences) with synthetic tests.
5. Document the procedure.

**Required tests**
- Synthetic ground-truth recovery within tolerance (focal length within 0.5 percent, baseline within 0.5 mm).
- Schema validation tests (valid, invalid, wrong major version).
- Real data test: reprojection RMS below the threshold set in the stage ADR (proposed 0.5 px).

**Exit gate (all must pass)**
- [ ] All tests pass; a real calibration for the owner device is produced and stored.
- [ ] Calibration quality report committed.
- [ ] Tag `stage-9-complete`.

### S10: Hand inference engine and model evaluation

**Goal.** A fast, swappable inference layer plus an offline evaluation harness that decides which hand model is used. Resolves U-07.

**Depends on.** S8, S9

**Deliverables**
- `cg-infer`: `IInferenceEngine` with TensorRT RTX, DirectML, CPU and Fake implementations; provider fallback chain; engine cache.
- Model package format: ONNX file, model card (inputs, outputs, normalisation), hash.
- Offline evaluation tool: runs a model on `.cgrec` data and computes detection rate and keypoint stability (and accuracy on labelled frames).
- If needed: a fine-tuning pipeline in Python (separate folder) that exports ONNX.

**Work items (in order)**
1. Contract tests for the engine port using small test models (identity, add).
2. Implement the CPU provider first, then DirectML, then TensorRT RTX; log the provider actually used.
3. Implement device-resident tensors (IOBinding) and a zero-allocation run path after warm-up.
4. Build the evaluation harness; compare candidate models on grayscale stereo frames; document results.
5. Decide in an ADR: off-the-shelf versus fine-tuned model. If fine-tuned, specify data, labels and acceptance metrics.

**Required tests**
- Numerical equivalence: all providers produce results within tolerance on the same inputs.
- Fallback test: an unavailable provider falls back in the configured order.
- Benchmark: inference time p95 per frame on the RTX 5070, recorded per provider.
- Evaluation regression test: metrics on a fixed mini-dataset must not worsen.

**Exit gate (all must pass)**
- [ ] Engine contract green for all providers present on the machine.
- [ ] Chosen model meets proposed targets on the dataset: detection rate at least 95 percent in the hand volume; per-joint jitter below a threshold set in the ADR.
- [ ] Inference p95 within the NFR-03 budget (share allocated with other stages).
- [ ] Tag `stage-10-complete`.

### S11: Stereo geometry, skeleton and filtering (pure algorithms)

**Goal.** Accurate, smooth 3D hands from two sets of landmarks. No I/O, fully unit-testable.

**Depends on.** S9 (S10 for real inputs; use synthetic inputs first)

**Deliverables**
- `cg-handcore` implementing section 5.5: rectification maps, triangulation, outlier rejection, skeleton fitting, One Euro filter, predictor, hand-space transform.
- Synthetic scene generator for tests (3D hand pose, camera projection with noise).
- Replay-based accuracy harness.

**Work items (in order)**
1. Write the synthetic generator (projects a known 3D skeleton through the calibrated rig with configurable noise).
2. Triangulation with reprojection-error gating.
3. Skeleton constraint fitting using bone lengths and joint-angle limits.
4. Filters and predictor with allocation-free state.
5. Fusion rule: use stereo when both views agree; fall back to single-view relative pose with scale from the last good stereo result.
6. Hand-space output with timestamps.

**Required tests**
- Property tests on the synthetic generator: recovered 3D error under noise is bounded; filters remain stable.
- Tests for a missing camera, partial occlusion and NaN handling (no NaN may escape).
- Benchmarks: the whole algorithm chain under 1 ms per frame.
- Regression on recorded landmark outputs (from S10) with golden numbers.

**Exit gate (all must pass)**
- [ ] Synthetic error: mean joint error below 5 mm at zero noise and below the ADR bound at the specified noise level.
- [ ] No allocations; no NaN escapes (fuzz tested).
- [ ] Tag `stage-11-complete`.

### S12: Hand service runtime and bridge (Milestone M2)

**Goal.** The real-time pipeline process that publishes head pose and hand frames to the game.

**Depends on.** S5, S6, S8, S10, S11

**Deliverables**
- `cg-handservice` executable: composition root, configuration, lifecycle, health and heartbeat, structured logs, metrics.
- Pipeline stages as separate classes with explicit ports: FrameSlot, Preprocessor, Detector, Landmarker, Fusion, Publisher.
- Shared-memory writer (section 5.6) and Unity `HandProvider` adapter.
- Latency instrumentation: timestamps at each stage, exposed as histograms.
- Replay mode: the whole service runs from a `.cgrec` dataset without hardware.

**Work items (in order)**
1. Compose the pipeline with fakes first; run end to end with `ReplayStereoSource` and `FakeInferenceEngine`.
2. Replace fakes one at a time (real inference, then real device) with the contract tests unchanged.
3. Implement the backpressure policy: newest wins, drops counted.
4. Implement supervisor behaviour: restart on failure, heartbeat, graceful shutdown within 2 s.
5. Implement the Unity `HandProvider` mapping hand frames to the gameplay `HandsInput`.
6. Add the latency overlay.

**Required tests**
- End-to-end replay test: dataset in, expected hand frames out (golden within tolerance).
- Latency test: p95 callback-to-publish under NFR-03 on the RTX 5070 in replay mode with real inference.
- Chaos tests: kill the service during play; the game reports tracking lost within 250 ms and recovers when the service returns.
- Soak: 30 minutes, no leak, stable thread count.
- HIL: live hands visualised in the Unity test scene (joint spheres).

**Exit gate (all must pass)**
- [ ] All automated tests pass; the HIL live-hands checklist is signed off.
- [ ] Measured end-to-end hand latency documented in `docs/perf/m2.md`.
- [ ] Tag `stage-12-complete` (**Milestone M2**).

### S13: Gesture recogniser and hand-driven gameplay

**Goal.** Reliable gestures with few false positives, controlling pointing, placing, breaking and block selection.

**Depends on.** S4, S12

**Deliverables**
- Full `IGestureRecognizer` in `Cubeglass.Gameplay`: pinch (with hysteresis), fist or poke-to-break, palette selection, flick-to-cycle, per-user calibrated thresholds.
- Hand pointing ray model (wrist-to-fingertip ray, optional gaze blend).
- Carried-block visuals and palette UI in Unity.
- Labelled gesture dataset and an evaluation tool.

**Work items (in order)**
1. Record gesture sessions and label them (start and end frames per gesture).
2. Implement the recogniser as a pure state machine on joint streams; tune on the labelled set.
3. Define precision and recall targets; add a regression test that fails if they drop.
4. Integrate with the interaction service; verify cancellation on tracking loss.
5. Add a gamepad fallback that can be toggled at runtime.

**Required tests**
- Evaluation: gesture precision and recall each at least 95 percent on the labelled set (proposed target).
- Property tests: no gesture fires without its enabling conditions; no repeats within the refractory period.
- Scenario tests using recorded joint streams to drive the gameplay layer.
- HIL playtest with scripted tasks (place 10 blocks in a pattern, break 10 blocks) measuring success rate and time.

**Exit gate (all must pass)**
- [ ] Targets met on the recorded set and in HIL tasks (at least 90 percent task success).
- [ ] Tracking-loss scenarios verified.
- [ ] Tag `stage-13-complete`.

### S14: Hardening, packaging and release (Milestone M3)

**Goal.** A robust release candidate.

**Depends on.** S13

**Deliverables**
- Performance tuning report; memory and thread audits.
- Installer or portable package built by CI; versioned config and migration.
- User guide (setup, calibration, controls, troubleshooting) and developer guide.
- Crash handling and log bundle export.
- Dependency, model and dataset licence audit.

**Work items (in order)**
1. Profile against the budgets in section 8 and fix regressions.
2. Run extended soak, fault-injection and reconnect tests.
3. Write migration tests for saves and configs from earlier versions.
4. Final failure-mode review (section 10).
5. Produce release notes and tag the candidate.

**Required tests**
- Full regression across all stages.
- Upgrade test: install the previous tag, upgrade, and confirm saves and calibration still load.
- Two-hour HIL soak (comfort and stability).

**Exit gate (all must pass)**
- [ ] All gates from stages 0 to 13 remain green.
- [ ] No open defect of high severity; others documented.
- [ ] Tag `v1.0.0-rc1` (**Milestone M3**).

---

## 7. Cross-stage test matrix

| Test type | S1 | S2 | S3 | S4 | S5 | S6 | S7 | S8 | S9 | S10 | S11 | S12 | S13 |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| Unit | x | x | x | x | x | x | x | x | x | x | x | x | x |
| Property | x | x | x | x | x |  |  |  |  |  | x |  | x |
| Contract |  |  |  |  | x | x |  | x |  | x |  | x |  |
| Golden | x | x | x |  |  |  |  |  | x |  | x | x |  |
| Integration |  |  |  | x |  | x | x |  |  |  |  | x | x |
| Benchmark | x | x | x |  | x | x | x | x |  | x | x | x |  |
| HIL |  |  |  |  | x | x | x | x |  |  |  | x | x |
| Soak |  |  |  |  | x |  | x |  |  |  |  | x |  |

---

## 8. Performance budgets (initial; refine by ADR with measured data)

| Path | Budget | Measured by |
|---|---|---|
| Render frame (stereo) | Display frame time at chosen refresh (16.7 ms at 60 Hz, 11.1 ms at 90 Hz), p99 | Frame timing in the debug overlay and PlayMode perf test |
| Pose read in render loop | Wait-free, below 5 microseconds, zero allocations | Microbenchmark |
| Chunk meshing (full chunk) | Set in S3 ADR; must run on workers without frame spikes | Benchmark |
| Mesh uploads | Bounded per frame (N set in S7) | PlayMode test |
| Camera callback | Copy and return, below 1 ms | Callback benchmark |
| Hand pipeline (callback to publish) | p95 at or below 15 ms | Pipeline histograms |
| Triangulation, skeleton, filter chain | Below 1 ms per frame | Benchmark |
| Shutdown | Below 2 s | Lifecycle test |

Telemetry required from S5 onward: pose rate and jitter, dropped frames, per-stage pipeline latency, GPU provider in use, frame time percentiles.

---

## 9. Quality gates in CI

1. Build with warnings as errors on all languages.
2. Format and lint (clang-format, clang-tidy, analyzers, ruff, mypy).
3. Dependency-rule check (section 3.3).
4. Unit, property, contract, golden and integration tests.
5. Coverage thresholds per module.
6. Sanitizers: ASan and UBSan on C++ tests; TSan on concurrency tests.
7. Contract-compatibility check: contract files unchanged unless an ADR and version bump are in the PR.
8. Licence and vulnerability scan.
9. Nightly: benchmarks (regression threshold 10 percent), soak, mutation tests.
10. Release: artefacts built only in CI, with checksums.

---

## 10. Risk register

| ID | Risk | Likelihood | Impact | Mitigation | Owner stage |
|---|---|---|---|---|---|
| R-01 | Stereo cameras do not deliver frames in 3DoF mode | Medium | High | Run 6DoF and ignore position; adapter hides the choice | S8 |
| R-02 | Hands are outside the stereo overlap in natural poses | Medium | High | Gate G-A; gesture design around a work volume; alternatives documented | S8 |
| R-03 | Off-the-shelf hand models fail on grayscale wide-angle images | Medium | High | Evaluation harness; fine-tune with recorded and labelled data | S10 |
| R-04 | No factory calibration available, so calibration error limits accuracy | High | Medium | Self-calibration toolchain with quality gates | S9 |
| R-05 | End-to-end hand latency makes interaction feel laggy | Medium | Medium | Head-space hands, prediction, tolerant gestures, latency instrumentation | S11, S12 |
| R-06 | GPU contention between game and inference | Low | Medium | Separate process and queue priority, measure frame time under load | S12 |
| R-07 | Blackwell/ONNX Runtime provider incompatibility | Low | Medium | Provider fallback chain (TensorRT RTX, DirectML, CPU) | S10 |
| R-08 | SDK behaviour changes between versions | Medium | Medium | Wrapper isolates SDK; pinned SDK version; HIL smoke test per upgrade | S5 |
| R-09 | Motion discomfort | Medium | High | Late-latch, snap turn, vignette, stable frame rate, comfort ADR | S6, S7 |
| R-10 | Agent scope creep or contract drift | Medium | High | Section 0 rules, contract-compat CI check, small PRs, reviewer separate from author | all |

---

## 11. Agent operating protocol

### 11.1 Work item template (one per PR)

```
ID: S<stage>-WI<number>
Title:
Stage / milestone:
Contracts touched: (none | list + ADR link)
Inputs (files, fixtures, ADRs):
Acceptance tests to write first:
Out of scope:
Performance budget (if any):
Definition of done: see section 4.4
```

### 11.2 Prompt skeleton for a builder agent

```
You are implementing work item <ID> of the Cubeglass dossier.
Read: section 0 (rules), section 3 (architecture), the contracts you touch (section 5), and the stage spec.
Process: 1) restate acceptance criteria; 2) write failing tests; 3) implement the smallest change; 4) refactor; 5) run all gates locally; 6) fill the PR checklist.
Constraints: do not change contracts; do not add dependencies without an ADR; no engine or hardware types in pure modules; no allocations in hot paths; no TODO without an issue.
If blocked or if a requirement conflicts with a contract, stop and write docs/questions/<ID>.md.
Output: a PR with tests, docs and a short report of measured results.
```

### 11.3 Pull request checklist

- [ ] Work item ID and stage referenced.
- [ ] Tests written first and listed; all pass locally and in CI.
- [ ] No contract changes, or ADR linked and version bumped.
- [ ] Dependency rule respected.
- [ ] Public API documented (preconditions, thread-safety, errors).
- [ ] Hot paths allocation-free (evidence attached for benchmarks).
- [ ] Coverage meets the module target.
- [ ] No unresolved TODO, commented-out code or debug output.
- [ ] Performance numbers recorded when the work item has a budget.
- [ ] Reviewer is not the author.

### 11.4 Reviewer agent checks
Read the contract first, then the tests, then the code. Reject if the tests would still pass with the implementation deleted (tests that assert nothing), if behaviour is untested, if a class has more than one reason to change, if a function exceeds roughly 50 lines or has deep nesting, or if hardware or engine types appear in pure modules.

### 11.5 Escalation to the owner
Escalate for: gate G-A outcome; any proposed contract change; model choice and data collection requirements; hardware behaviour that contradicts section 2.1; any need for extra hardware.

---

## 12. Appendices

### A. Initial ADR list
| ADR | Decision |
|---|---|
| 0001 | Layered architecture, ports and adapters, pure logic modules |
| 0002 | Shared-memory IPC with seqlock between hand service and game |
| 0003 | Test strategy and pyramid |
| 0004 | Coordinate, unit and time conventions |
| 0005 | Unity version and rendering pipeline |
| 0006 | Voxel storage, chunk size and save format |
| 0007 | Pose behaviour in 3DoF (from S5 data) |
| 0008 | Stereo feasibility and gate G-A (from S8 data) |
| 0009 | Calibration method and thresholds |
| 0010 | Hand model and inference providers |

### B. Glossary
- **3DoF / 6DoF:** rotation only versus rotation plus position tracking.
- **VIO:** visual-inertial odometry, the glasses' tracking engine.
- **SBS:** side-by-side stereo image format.
- **Late-latch:** reading the newest head pose just before rendering.
- **Seqlock:** a lock-free reader/writer protocol using a sequence counter.
- **HIL:** hardware-in-the-loop test.
- **ADR:** architecture decision record.
- **Port / adapter:** an interface owned by inner logic, and its implementation at the system edge.
- **Newest wins:** a handoff policy that discards older unprocessed frames.

### C. Source documents the owner supplied
VITURE Glasses SDK pages (device provider, Carina header, camera provider); VITURE Unity SDK release notes (Neckband-targeted, used only as design reference for XR Hands-compatible joint data).

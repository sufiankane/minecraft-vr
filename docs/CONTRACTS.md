# Contract register and change runbook

`contracts/` is the single source of truth for every interface, layout and
fixture that crosses a module, language or process boundary (dossier §4.1,
§5). This document is the register of those artefacts, the process for changing
one, and the read/write matrix for each format. Verified at `docs/full-docs` /
`main` = `0d3ecb6` (#50); paths are relative to the repository root.

## 1. What a contract is, and the two rules

**Rule of precedence (dossier, "Rule of precedence"):** Contracts (§5) > Stage
specs (§6) > everything else. Where this register and the dossier disagree, §5
wins; where a stage plan and a contract disagree, the contract wins. An ADR is
the only place a contract is deliberately changed.

**Frozen-contract rule (dossier §0 rule 2):** contracts are frozen; changing
one needs a new ADR and a version bump, never a silent edit to make code
compile. For the C ABI surface the rule is enforced mechanically: `python -m
depcheck contracts` fingerprints `contracts/cg_types.h`,
`contracts/cg_unity_bridge.h`, `contracts/cpp/ports.hpp` and
`contracts/cpp/result.hpp`, compares them with `contracts/abi-baseline.json`,
and fails a real change unless `CG_ABI_VERSION` moved and the baseline was
regenerated (C-1, #42). Purely additive vocabulary that changes no existing
declaration is recorded in an ADR without a bump (ADR-0009, ruling R34).

A contract here is one of: the files in `contracts/`; the shared-memory region
layout; the save-file format; the toolchain and manifest pins that gate builds;
and the JSON fixtures both language implementations must agree on.

## 2. Contract register

| Path / artefact | Version | Authority | Consumers | Change process | Compatibility notes |
| --- | --- | --- | --- | --- | --- |
| `contracts/cg_types.h` | `CG_ABI_VERSION 2` | Dossier 5.1; ADR-0010 (R42 adds `cg_hand`/`cg_hand_frame`, bump 1→2) | `cpp/glasses` (`viture_api.hpp`), `cpp/bridge` (`shm_layout.hpp`, `bridge.cpp`), C++ tests; Unity `com.cubeglass.bridge` (`BridgeTypes.cs`) | ADR + `CG_ABI_VERSION` bump + `python -m depcheck contracts --update`; layout asserts updated | C structs, trivially copyable, no export macros; `cg_head_sample` 48 B (44 + 4 pad), `cg_hand` 272 B, `cg_hand_frame` 576 B (572 + 4 pad) |
| `contracts/cg_unity_bridge.h` | Frozen 5.12 surface under ABI 2 (no own macro) | Dossier 5.12; ADR-0010 (R44: no export macros) | Implemented by `cpp/bridge` → `cg_unity_bridge.dll`; P/Invoked from `NativeBridge.cs`; bridge EditMode + stress tests | ADR + baseline regen (it is one of the four fingerprinted files) | Five entry points; `cg_bridge_read_head` wait-free, `read_hands` seqlock; commands travel in the shm header, not here |
| `contracts/cpp/ports.hpp` | 5.2 verbatim + additive `Duration`, `TrackState` | Dossier 5.2; ADR-0009 (R34) | `cpp/glasses` sources; `cpp/tools/pose_probe`, `cpp/tools/soak`; glasses tests and benchmarks | Additive vocabulary: ADR only. Structural edit: version bump + baseline regen | `TryGetLatest` never blocks or allocates; predict capped at 100 ms; 30/30 `GlassesContract.*` cases across three factories |
| `contracts/cpp/result.hpp` | Additive vocabulary under ABI 2 | ADR-0009 (`Status`, `Result<T>`, `FromCgStatus`) | `cpp/glasses` headers/sources; `cpp/tools`; C++ tests | As above; included in the ABI baseline | One-to-one `cg_status` mapping, out-of-range → `Internal`; `noexcept`, non-owning message pointer |
| `contracts/golden/transforms.json` | `"schema": 1`, tolerance `1e-6`, 28 cases | ADR-0004; S1 plan op list | `cpp/tests/core-math/golden_test.cpp`; `dotnet/tests/CoreMath.Tests/GoldenFixtureTests.cs` | Add cases to both harnesses; the `schema` integer is the format version | Every dispatched op must be visited and no unknown op accepted; `clock_map` compares exact integers, the rest within tolerance |
| `contracts/layers.json` | Unversioned rule file | ADR-0001 (layering); `docs/ci.md` | `python/depcheck/rules.py`; CI `depcheck` job; `scripts/ci-local.ps1` | Edit the file; completeness is enforced (missing entry `layersEntryMissing`, stale `layersEntryStale`) | Inward-only rules per module; new modules are listed or put under `deferred` (`handcore` today) |
| `contracts/licence-allowlist.json` | Unversioned; 11 entries | `docs/ci.md`; dossier §9 | `python/depcheck/licences.py`; CI `licences` job | Add the package with a known SPDX id; an unknown/empty id fails | Declared deps come from `cpp/vcpkg.json` and `dotnet/Directory.Packages.props`; every declared package must be listed |
| `contracts/abi-baseline.json` | `"abiVersion": 2` | Dossier §9 item 7; ADR-0003/0004 corrections (#42, C-1) | `python/depcheck/contracts.py`; CI `depcheck` job | Bump `CG_ABI_VERSION` first, then `python -m depcheck contracts --update` | Normalised per-file SHA-256 + aggregate fingerprint; `--update` refuses while the version is unchanged |
| `cpp/bridge/include/cg/bridge/shm_layout.hpp` (shared-memory region) | Region ABI `2`; name stays `Local\cubeglass.v1.state` | Dossier 5.6; ADR-0002; ADR-0010 (R43 command/ack); #37 (I-2/I-3) | Writer: `cg-handservice` (future) and `cg_test_writer_*` (tests, separate `cg_bridge_test_support` library); readers: `cg_bridge_read_head/read_hands`, `cpp/tests/bridge` | Region ABI bump + layout/ABI tests; offsets frozen, additions only in reserved bytes | Magic `CGSHM001` (`0x3130304D48534743`) published last; header 64 B; heartbeat 24, command 32, ack 36; head slot 64, hand slot 256; region ≥ 848 B; stale when `now - heartbeat > 250 ms`; bounded 64-attempt reader retry |
| Save format (chunk delta v1) | Format version 1; magic `CGDL` (`0x43 0x47 0x44 0x4C`) | ADR-0006; R19 `0xFFFF` no-edit sentinel (`f51526e`, in #14) | `ChunkDeltaCodec` (C# `Cubeglass.Voxel`); Unity `FileWorldStore`; Voxel + persistence tests | New ADR + format-version bump + reader switch; golden byte fixtures | 16³ chunks, `BlockId` u16 (`Air 0`), little-endian RLE covering exactly 4096 cells; `0xFFFF` untouched, `0x0000` explicit Air edit; corrupt/unknown-version input rejected without throwing |
| Pipeline JSON schemas | **Not present at HEAD** (no `1.0.0` schema exists) | Dossier 5.7/5.8 promise "JSON Schema in `contracts/`"; none was ever committed | — | Would follow the ADR + fixture process if added | No `*.schema.json` and no `1.0.0` schema version exists anywhere in the tree; `git log -- contracts/` shows no schema file ever landed. Nearest committed schema integers: `transforms.json` `"schema": 1` and `calib.SCHEMA_VERSION == 1`. CI workflows are YAML; benchmark payloads are Google Benchmark / BenchmarkDotNet JSON parsed by `depcheck benchregress` |
| Toolchain pins: `dotnet/global.json`, `dotnet/Directory.Packages.props`, `cpp/vcpkg.json`, `cpp/CMakePresets.json` | SDK `10.0.401` (`latestPatch`); 7 central package versions (BenchmarkDotNet 0.15.8, FsCheck 3.4.0, Test.Sdk 18.10.1, NUnit 5.0.0, NUnit3TestAdapter 6.3.0, System.Text.Json 10.0.12, coverlet.collector 10.1.0); vcpkg baseline `eb2d3a3279fd019cb7733072d86900d0ad2a1aef`; CMake presets `"version": 6` | Dossier S0 WI1; `docs/toolchains.md`; ADR-0001/0010 | CI workflows, `scripts/ci-local.ps1`, `scripts/bootstrap-dev.ps1` | Change deliberately and update `docs/toolchains.md`; the vcpkg baseline also keys the CI cache | `cpp/vcpkg.json` carries an external `$schema` URL only; presets: `windows-msvc`, `windows-release`, `windows-analyze`, `linux-ci`, `linux-asan`, `linux-tsan`, `linux-coverage`, `benchmarks` |
| Unity manifests: `unity/Cubeglass/Packages/manifest.json`, `packages-lock.json`, `ProjectSettings/ProjectVersion.txt`, four local `package.json` | Editor `6000.6.3f1` (rev `45d8eee7de74`); local packages `0.1.0` (`unity: 6000.6`, placeholder `6000.0`); `com.unity.test-framework 1.8.0` | ADR-0010 (version + Built-in RP); ADR-0011 | Unity editor/CLI, release workflow, `ci-local` Unity lane | Pin changes are ADR-worthy; an editor change may re-churn committed scenes (rebuild + commit) | No URP package may be added; `packages-lock.json` pins resolved versions; `testables` enables the four local packages |

### ABI v2 payload sizes (verified)

| Struct | Field bytes | Tail padding | `sizeof` | Pinned by |
| --- | ---: | ---: | ---: | --- |
| `cg_head_sample` | 44 (8 time + 28 pose + 4 state + 4 sequence) | 4 | 48 | `cpp/tests/bridge/layout_tests.cpp`; Unity `BridgeTypes.cs` (`Size = 48`) |
| `cg_hand` | 272 (4 header + 21×12 joints + 12 velocity) | 0 | 272 | `layout_tests.cpp`; `BridgeTypes.cs` (`Size = 272`) |
| `cg_hand_frame` | 572 (3×8 times + 4 sequence + 2×272 hands) | 4 | 576 | `layout_tests.cpp`; `BridgeTypes.cs` (`Size = 576`) |
| `HeadSlot` (`seq_a` + sample + `seq_b`) | — | — | 64 | `layout_tests.cpp` (head slot at 64) |
| `HandSlot` (`seq_a` + frame + `seq_b`) | — | — | 592 | `layout_tests.cpp` (hand slot at 256; region ≥ 848 = 256 + 592, asserted in `bridge.cpp`) |

The dossier 5.6 table lists "36 `cg_head_sample`" — that is where `state`
starts, not the struct size; the compiler's 8-byte alignment adds the four tail
bytes. The layout tests document the padding explicitly.

### Shared-memory region facts (verified in `shm_layout.hpp` / `layout_tests.cpp`)

- Magic at byte 0: `0x3130304D48534743`, bytes `43 47 53 48 4D 30 30 31`
  ("CGSHM001"); region name `Local\cubeglass.v1.state` (`/cubeglass.v1.state`
  on POSIX). The name keeps its v1 form; the region **ABI is 2**.
- Header: 64 B — `magic` 0, `abi_version` 8, `header_size` 12, `writer_pid`
  16, `heartbeat_ns` 24, `command` 32, `ack` 36, reserved 40..63 (R43).
- Slots: `HeadSlot` at 64 (seqlock: 8-byte counter, payload, 8-byte counter),
  `HandSlot` at 256.
- Publication: writer zeroes/initialises the header, then publishes `magic`
  last with release ordering; readers acquire-load `magic` first (I-3). A
  50 ms retry window in `cg_bridge_open` is belt-and-braces only.
- Staleness: strict `now - heartbeat > 250 ms` (exactly 250 ms is fresh,
  future heartbeats are fresh; comparison is overflow-safe). A stale head read
  keeps its sample and reports `CG_TRACK_LOST`; a stale hand read reports
  `CG_ERR_NOT_READY` and no frame.
- Mixed-version pairs: rejected with `CG_ERR_UNSUPPORTED` naming both region
  versions (`LastHeaderError()`); the region ABI must move with the payload
  shape (I-2).

## 3. Change runbook

1. **Propose an ADR.** `docs/adr/NNNN-*.md` using `0000-template.md`, stating
   the contract change and the version bump; link the relevant dossier §5
   section and any SDD ruling (R-number). Merge it before or with the change.
2. **Bump the version.** C ABI: `CG_ABI_VERSION` in `contracts/cg_types.h`
   (currently 2). Region: `kShmAbiVersion` in `shm_layout.hpp` (currently 2).
   Save format: the format version field plus a new reader case. Additive-only
   vocabulary: the ADR records it; no bump (R34).
3. **Regenerate the baseline** from the repository root in the Python
   environment that has `depcheck` installed:
   `python -m depcheck contracts --update`. The tool refuses to write while
   the fingerprint changed but `CG_ABI_VERSION` is unchanged ("refusing to
   update ... bump CG_ABI_VERSION first"), so `--update` cannot silently
   rebase an unversioned contract edit; with no baseline present it
   bootstraps one. Comment/whitespace-only edits pass without a bump because
   the fingerprint is computed on normalised text.
4. **Update consumers and tests.** C++ layout `static_assert`s
   (`cpp/tests/bridge/layout_tests.cpp`); Unity explicit sizes in
   `BridgeTypes.cs`; golden fixture cases in both language harnesses; save
   byte fixtures and fuzz tests; shm reader/stress tests. Consumers per
   artefact are listed in the register table.
5. **Run the gates locally:** `python -m depcheck contracts` (ABI),
   `python -m depcheck` (`layers.json`), `python -m depcheck licences`
   (allowlist), then `scripts/ci-local.ps1` (six lanes; `-SkipUnity` for
   non-Unity changes). The contract-specific negative tests live in
   `python/tests/test_depcheck_contracts.py`.
6. **Gate in CI.** Merge only with the six required checks green
   (`docs/ci.md`): `cpp-windows`, `cpp-linux-asan`, `dotnet`, `python`,
   `depcheck` (which runs `python -m depcheck contracts`) and `licences`. A
   contract change without the ADR and version bump fails `depcheck` and
   blocks the merge.

## 4. Interop matrix

| Format / artefact | Writers | Readers | Fixture / test role |
| --- | --- | --- | --- |
| C ABI structs (`cg_types.h`) | C++ producers: `cg_test_writer_*` today (test-only `cg_bridge_test_support`); `cg-handservice` in the future (dossier 5.6) | C++ bridge and tests; Unity P/Invoke (`BridgeTypes.cs`, then `BridgePoseProvider`) | `layout_tests.cpp` compile-time offsets/sizes; Unity explicit `StructLayout` sizes |
| Shared-memory region | `cg_test_writer_*` (tests only, in `cg_bridge_test_support`; the production writer is the future hand service — TD-004/TD-067 split) | `cg_bridge_read_head/read_hands` (Unity through the DLL); `cpp/tests/bridge` | `bridge_layout`, `bridge_reader`, `bridge_stress` (200,000 samples, 8 readers, 0 mismatches); TSan lane |
| Command / ack words | `cg_bridge_send_command` (Unity side, `BridgeTypes`/`NativeBridge`) | `cg_test_writer_read_command`/`ack_command` in tests (`cg_bridge_test_support`); the future service in production | `shm_reader_tests.cpp` command/ack cases |
| `transforms.json` | Hand-authored repository data (no runtime writer) | C++ `golden_test.cpp`; C# `GoldenFixtureTests.cs` | 28 cases shared by both languages; unknown/unvisited ops fail |
| `layers.json` | Developers | `depcheck` rules (`python/depcheck/rules.py`) | Negative fixtures under `python/tests/` (`test_depcheck.py`) |
| `licence-allowlist.json` | Developers | `depcheck licences` (`python/depcheck/licences.py`) | SPDX-id and allowlist tests in `test_depcheck_licences.py` |
| `abi-baseline.json` | `depcheck contracts --update` | `depcheck contracts` + CI | Fingerprint/refusal tests in `test_depcheck_contracts.py` |
| Chunk delta save files | `ChunkDeltaCodec.Serialize` (tests; `FileWorldStore` in Unity writes temp+rename under the player's save dir) | `ChunkDeltaCodec.TryDeserialize` (tests; Unity load + boot replay) | Golden version-1 bytes and fuzz rejection in `ChunkDeltaCodecTests`; `PersistencePlayModeTests` round trip |
| Benchmark JSON | Google Benchmark (C++ nightly), BenchmarkDotNet `JsonExporter` (.NET nightly) | `depcheck benchregress` in `bench-compare` | the last five nightly summaries cached as `nightly-bench-history-*` (self-seeding; no committed baseline, TD-033/TD-054) |
| Unity manifests / `ProjectVersion.txt` | Developers and the Unity editor | Unity editor/CLI, release workflow, `ci-local` Unity lane | `packages-lock.json` pins resolved versions; scene hashes pin rebuilt scenes |

**Golden-fixture and test-writer roles.** `contracts/golden/transforms.json`
is the cross-language oracle: the same 28 cases pin the C++ and C#
implementations, and both harnesses must fail on an unknown op or an op no case
visits. `cg_test_writer_*` is the only in-repo shared-memory writer; since
TD-004/TD-067 it is compiled into the **test-only** `cg_bridge_test_support`
library (`cpp/bridge/src/test_writer.cpp`, `WINDOWS_EXPORT_ALL_SYMBOLS` on
Windows), not into the production `cg_unity_bridge.dll`, which exports only the
frozen 5.12 surface. The C++ bridge tests link the test-support library; the
Unity EditMode tests load it after `scripts/sync-unity-plugins.ps1
-IncludeTestSupport` (or `scripts/ci-local.ps1`) stages it, and the release
workflow removes it before the player build so it never ships. Scene hashes
(Game `2B97305E…`, Calibration `70F970CE…`) and the chunk/terrain hashes
(S2 `0x1EF678D6ADDA1CEC`, S7 `0xB38A50148C01A643`) are test-side golden values,
not contract files.

# Critical C++/native review — Cubeglass

- **Date:** 2026-10-04
- **Revision:** `docs/full-docs` @ `0d3ecb6` (same tree as `main`; previous review baseline was `c0734a2`)
- **Scope:** `contracts/`, `cpp/core-math`, `cpp/bridge`, `cpp/glasses`, `cpp/tests/**`, `cpp/tools/**`, CMake/presets/vcpkg
- **Standard:** life-support-adjacent. "Minor" = would be fixed before shipping anyway.
- **Mode:** read-only. No repository file was modified (this report excepted).
- **Method:** full line-level reading of the scope; hand-computation of 10 golden fixture values; contract-gate execution; execution of all five pre-built test binaries; inspection of CI workflows and presets. HIL/vendor SDK remains unavailable, as documented.

## Evidence run for this review

| Check | Command | Result |
|---|---|---|
| Contract ABI gate | `python -m depcheck contracts --root .` | pass (exit 0, no output) |
| core-math tests | `cg_core_math_tests.exe` | **71 passed, 0 failed** |
| bridge layout | `cg_bridge_layout_tests.exe` | 7 passed |
| bridge reader | `cg_bridge_reader_tests.exe` | 15 passed |
| bridge stress | `cg_bridge_stress_tests.exe` | 2 passed |
| glasses (contract+fault+thread+allocation+replay+display+loader) | `cg_glasses_tests.exe` | **106 passed, 0 failed** (~6 s) |
| Golden hand-check | sample below | all 10 sampled values correct |

Golden values verified by hand against the implementation formulas: `quat_rotate_pitch90`,
`quat_rotate_roll180`, `quat_rotate_yaw180`, `quat_multiply_yaw90_pitch90`,
`quat_rotate_compound`, `quat_slerp_mid_22_5deg`, `pose_compose_translates_child`,
`pose_inverse_yaw90`, `clock_map_odd_median` (median 0.0005 s → 4 000 500 000 ns),
`clock_map_even_median` (mean of middle two 0.0004/0.0005 → 5 000 450 000 ns). All match.

> **Evidence caveat (process).** `docs/notes/tech-debt.md` TD-045 records that hosted Actions
> jobs are billing-blocked since 2026-10-04 09:49Z. CI evidence for this commit therefore cannot
> be assumed; the local runs above are firsthand, but they are Debug builds only (see CXX-03).

## Counts

| Severity | New findings in this pass |
|---|---|
| **Critical** | **0** (none confirmed in the reviewed surface) |
| **Important** | **4** (CXX-01 … CXX-04) |
| **Minor** | **17** (CXX-05 … CXX-21) |
| Previously reported, accepted/deferred, confirmed still present | 8 (TD-001…TD-008; not recounted) |

### Important one-liners

- **CXX-01** — `VitureDisplayControl::Set/Get` is TOCTOU-racy against `VitureHeadPoseSource::Start`: the is-running predicate can pass, then `Start` launches the poller, so a display seam call can run concurrently with `PollPose`, violating the `IVitureApi` concurrency contract the whole seam is built on.
- **CXX-02** — `VitureHeadPoseSource::Recenter` arms `yaw_offset_deg_`/`recentre_until_seq_` *outside* `recentre_mutex_` (contradicting its own comment); a concurrent failing `HandleRecentre` can overwrite the fresh arm with `{0,0}`, so a recentre that returned Ok reads as a no-op until the device recovers.
- **CXX-03** — No CI lane ever tests a Release C++ build: `windows-msvc` is Debug, `linux-ci` (Release) is only built by nightly with no `ctest`. Optimizer/`NDEBUG`-only defects in the seqlock, `atomic_ref` aliasing, and conversion paths are unobserved.
- **CXX-04** — The soak RSS gate can false-PASS: a failed `ResidentBytes()` returns 0 and the final-vs-baseline formula turns a failed final query into "no growth"; on Linux `ru_maxrss` is a process-lifetime peak, so growth below an earlier high-water mark is invisible.

---

## Important findings

### CXX-01 — Display-control is-running check races `Start` (seam calls concurrent with `PollPose`)

- **Severity:** Important (concurrency, vendor-contract violation)
- **Location:** `cpp/glasses/src/display_control.cpp:19-48`; predicate declared in `cpp/glasses/include/cg/glasses/viture_head_pose_source.hpp:91-95`; predicate used in `cpp/glasses/src/viture_head_pose_source.cpp:76-95`; contract at `cpp/glasses/include/cg/glasses/viture_api.hpp:33-37`
- **Status:** confirmed by reading.
- **Scenario:** Thread A calls `VitureDisplayControl::Set(mode)`. `stopped_` is false and `SourceIsRunning()` returns false (source not started), so the guard passes. Thread B then calls `source.Start()`: `CreateDevice`, `StartPose`, `running_ = true`, polling `jthread` launched. Thread A then calls `api_.SetDisplayMode(...)` — concurrently with thread B's `PollPose()`. The `IVitureApi` contract (`viture_api.hpp:33-37`) states display calls "only run while the pose source is stopped … so they are never concurrent with `PollPose`". The production `VitureDisplayControl` is wired with `[&source]{ return source.Running(); }` (see `display_control_tests.cpp:120`), so this is the shipping pairing. Consequence depends on the vendor SDK (unknown until HIL, TD-042): at best a rejected call, at worst device-state corruption or a blocked display call on the polling thread's critical path.
- **Why the previous review did not catch it:** M-5 fixed the `Get`/`Set` ordering and the `Stop` latch only; the `Start` edge was not examined.
- **Fix:** make the predicate check and the seam call atomic with respect to `Start`/`Stop`: expose a session lock (e.g. `WithPoseSession(fn)` taking `lifecycle_mutex_` plus a "device alive" precondition), or move display calls onto the pose thread through the existing command path. Add a test that races `Set` against `Start` and asserts no seam call overlaps a poll.

### CXX-02 — `Recenter` arm is written outside the mutex and can be clobbered to a no-op

- **Severity:** Important (narrow interleaving; correctness of a safety-relevant pose correction)
- **Location:** `cpp/glasses/src/viture_head_pose_source.cpp:156-166` (arm) versus `:365-369` (failure-path resolution) and `:191-196` (reader)
- **Status:** confirmed by reading; probability low, impact user-visible.
- **Detail:** lines 156-157 store the new `yaw_offset_deg_`/`recentre_until_seq_` *before* `recentre_mutex_` is taken at 163. The comment at 158-162 claims "Arm and post under the same mutex … Arming outside would let a stale resolution clear the new arm" — the code does exactly what the comment says it must not.
- **Interleaving:** (1) `HandleRecentre(G)` has already called `ResetOriginCarina`, it failed, and it is returning; (2) `Recenter` executes 156-157 and is preempted before 159; (3) `HandleRecentre` acquires `recentre_mutex_`, sees `generation == recentre_generation_` (the poster has not incremented yet), and stores `yaw_offset = 0, until_seq = 0, pending = false` (365-369); (4) the poster acquires the mutex and stores `pending = true`, `++generation`, `requested = true`. Net state: `pending == true` with a zero offset, so `TryGetLatest` (192) reports "recentred" while applying no yaw correction.
- **Observable consequence:** `Recenter()` returned Ok, but every read until the next successful `ServiceRecentre` pass exposes the pre-recentre heading. Because the failed resolution implies the device just died, `ServiceRecentre` keeps returning early (`:317`) until a fresh session sample is published — the window is the whole outage, and if the outage lasts until `Stop` the correction is withdrawn and the caller's Ok is never honoured.
- **Fix:** move the two stores inside the `recentre_mutex_` critical section *after* `++recentre_generation_`, so a resolution holding the mutex can never clobber a newer arm; update the comment accordingly. Add a deterministic test with a fake `ResetOriginCarina` that fails while a `Recenter` is issued concurrently (a gate in the fake can hold the resolution open between `ResetOriginCarina` returning and the mutex acquisition).

### CXX-03 — Release configuration is never tested by CI

- **Severity:** Important (test/evidence integrity; optimizer-only defects unobserved)
- **Location:** `.github/workflows/ci.yml:51-60` (`cmake --preset windows-msvc` = Debug, `ctest --preset ci`); `cpp/CMakePresets.json:10-16` (`windows-msvc` `CMAKE_BUILD_TYPE=Debug`), `:22-28` (`linux-ci` Release), `:87-149` (test presets); `nightly.yml` uses `linux-ci` only to build tools/benchmarks
- **Status:** confirmed by reading workflows and presets.
- **Scenario:** all six CI checks build Debug: Windows MSVC (Debug), Linux ASan/UBSan (Debug), TSan (Debug), coverage (`-O0`), clang-format/clang-tidy. The only Release C++ build is the nightly benchmark/tool build, which never runs `ctest`, and the release workflow builds `cg_bridge` for packaging without running any C++ test. Every UB/optimization-sensitive construct in the scope — the `atomic_ref`-over-C-struct seqlock, the fence protocol, signed conversions, `std::function`/copy elision — is exercised only at `-O0`/Debug. A defect that appears only with `/O2`/`-O2` + NDEBUG (miscompilation or timing) ships unobserved. LCov coverage floors also run at `-O0`.
- **Fix:** add `ctest --preset linux-ci` (Release) and a Windows Release/RelWithDebInfo test run to the required jobs; keep the Debug lanes for sanitizers.

### CXX-04 — Soak RSS gate can pass while leaking (failed query; peak-RSS accounting)

- **Severity:** Important (test-integrity: safety gate false-PASS)
- **Location:** `cpp/tools/soak/glasses_soak.cpp:171-189` (`ResidentBytes()` returns 0 on failure), `:366-394` (delta and pass/fail), `:384` (`final_rss > baseline_rss ? … : 0`)
- **Status:** (a) confirmed; (b) confirmed mechanism, magnitude suspected.
- **(a) Query failure turns into PASS:** on Windows, `GetProcessMemoryInfo` failure returns 0 (line 175); on POSIX, `getrusage` failure returns 0 (line 181). If the *final* query fails, `final_rss == 0 < baseline_rss`, so `delta == 0` and the tool prints `PASS` (392) although RSS is unknown. If the *baseline* query fails, `baseline_rss == 0` and delta equals the whole RSS, producing a spurious FAIL. The tool never distinguishes "query failed" from a real reading.
- **(b) Peak masking:** on Linux/macOS `ru_maxrss` is the peak since process start, not the current RSS. The t=60 s baseline captures the peak up to then. Any later growth that does not exceed that earlier peak (e.g. a startup transient above the steady-state, then a slow leak below it) yields `delta == 0` and a false PASS. `docs/notes/s5-gate.md` §6 documents the RSS budget as the leak gate but not this limitation.
- **Fix:** make `ResidentBytes()` return `std::optional<uint64_t>` (or a status) and fail the run when a reading is unavailable; on Linux read current RSS from `/proc/self/statm` (or fork a fresh probe) instead of `ru_maxrss`, or deliberately allocate/retain a baseline watermark above transient peaks. Document the accounting rule next to the gate.

---

## Minor findings

### CXX-05 — Signed-overflow UB on externally supplied timestamps (replay CSV, device clock jumps)

- **Severity:** Minor (UB on hostile/glitched input; tool + adapter; no memory unsafety observed)
- **Location:** `cpp/glasses/src/replay_head_pose_source.cpp:236` (`row.host_time_ns - clock_.Now()`), `:302` (`row.host_time_ns - previous_time_`); `cpp/glasses/src/viture_head_pose_source.cpp:446` (`sample.time - previous_time_`)
- **Status:** confirmed by reading.
- **Scenario:** the replay loader accepts any strictly increasing `int64` host times (`Load`, `ParseInt64`; the only constraint is `host_time_ns > previous`). A CSV with rows `host_time_ns = INT64_MIN` then `INT64_MAX` passes validation; `INT64_MAX - (INT64_MIN)` overflows `int64` at `:302`, which is UB (UBSan would trap; in Release it wraps to `-1`, corrupting the rate computation and, via `clock_.Advance`, the playback clock). Similarly a device that jumps its clock from a very negative to a very positive epoch can overflow `:446` after the forward-jump guard (which only catches regressions).
- **Fix:** validate parsed host times against a sane window (e.g. reject absolute values > some bound, or require the delta to be representable) and compute deltas in `uint64`/checked arithmetic before converting.

### CXX-06 — First published sample (and first sample after a mapper reset) carries SDK-epoch time

- **Severity:** Minor (one-sample timestamp error; consumer-visible latency/ordering)
- **Location:** `cpp/glasses/src/viture_head_pose_source.cpp:231-247` (AddSample/Map/publish; reset path at `:236-239`); `cpp/core-math/include/cg/core_math/clock_mapper.hpp` (`OffsetSeconds` is 0 before any sample; `Reset` clears the window); `docs/adr/0004-coordinate-unit-time-conventions.md:108`
- **Status:** confirmed by reading.
- **Scenario:** the SDK timestamp is seconds in the device's own epoch. Before the mapper has an anchor, `Map(sdk)` returns `ToNanoseconds(sdk)`; the wrapper publishes that immediately as `HeadSample::time`. A consumer that interprets `time` as host monotonic time observes one sample tens of minutes to hours in the past (or future). The same happens for the first sample after a `mapper_.Reset()` on regression, and the reset path does not re-add the current sample to the freshly cleared window (`:236-239`), so the raw epoch leaks again. Offset resolves once two samples are present, so the window is one sample.
- **Fix:** either hold publication until `ClockMapper` has ≥1–2 anchors (add an `IsReady()`/sample count), or clamp the first mapped time to `clock_.Now()` and re-anchor; document the one-sample epoch in ADR-0004/0009.

### CXX-07 — `PoseSlot` payload-alignment `static_assert` checks the wrong direction

- **Severity:** Minor (assert is misleading; no runtime defect)
- **Location:** `cpp/glasses/include/cg/glasses/pose_slot.hpp:112`
- **Status:** confirmed by reading.
- **Detail:** `static_assert(alignof(HeadSample) <= alignof(std::uint64_t), "the seqlock payload must be 8-byte aligned")`. The atomic word accesses are on `payload_` (a `std::uint64_t[]`, always 8-aligned); the alignment of `HeadSample` is irrelevant to them. The assert fails if `HeadSample` ever gains `alignas(16)`, and it cannot detect an actually misaligned payload. The compile-time requirement that matters is `sizeof(HeadSample) % 8 == 0` (already asserted at :111) plus nothing about `HeadSample` alignment.
- **Fix:** delete the assert or restate it as `alignof(decltype(payload_)) >= alignof(std::uint64_t)` on the real member.

### CXX-08 — Fake/tool accept rates whose period rounds to 0 ns; `EmitOne` divides by zero

- **Severity:** Minor (test/tool-only; produces inf/NaN rates and a spin)
- **Location:** `cpp/glasses/src/fake_head_pose_source.cpp:70` (validates only finite > 0), `:139` (`period = ToNanoseconds(1.0/rate_hz)`), `:146`, `:166-167` (`/ dt_s`); `cpp/tools/soak/glasses_soak.cpp:152/304`; `cpp/tools/pose_probe/main.cpp` rate parsing
- **Status:** confirmed by reading.
- **Scenario:** `--rate 3e9` (or `FakeHeadPoseSourceConfig{3e9,…}`) gives `1/rate = 3.3e-10 s → ToNanoseconds → 0`. `dt_s` is then 0; `(yaw_deg_ - previous_yaw_deg_) / 0` is `0/0` for `Static` (NaN) or `±inf` for a sweep, stored into the rate atomics. `TryGetLatest(predict>0)` then computes NaN/inf angles; `Quat::FromAxisAngle` degrades to identity, silently changing the predicted pose. `AdvanceSamples` also stops advancing the clock, so a counted loop spins. The soak and probe accept the same rates (period 0 → unpaced producer).
- **Fix:** reject rates with `ToNanoseconds(1.0/rate) == 0` in `FakeHeadPoseSource::Start` (and the tools), or clamp the period to ≥1 ns.

### CXX-09 — Test-writer POSIX close destroys a region it did not create; `fstat` failure conflated; no heartbeat on create

- **Severity:** Minor (test-helper resource safety; POSIX CI only)
- **Location:** `cpp/bridge/src/test_writer.cpp:198-201` (fstat failure → `CG_ERR_UNSUPPORTED`), `:226-227` (mismatched open calls `cg_test_writer_close`), `:250-253` (`shm_unlink` unconditionally)
- **Status:** confirmed by reading.
- **Scenario:** `cg_test_writer_open()` on a region created by someone else (another test process, or a future POSIX service) that carries a foreign ABI calls `cg_test_writer_close()`, which `shm_unlink`s the region on POSIX — destroying the foreign object, not just refusing it. Also `cg_test_writer_create()` uses `shm_open(O_CREAT)` + `ftruncate(1024)` + memset, i.e. it truncates and zeroes an existing region it did not create. `fstat` failure is reported as `Unsupported` rather than `Internal`, masking the cause. And `initialize_region` never sets `heartbeat_ns`, so a freshly created region reads stale (LOST / NOT_READY) until a test sets it — a documented-in-tests but surprising default.
- **Fix:** track whether the handle owns the region, unlink only for created regions; return `Internal` for `fstat` failure; initialise the heartbeat to `steady_clock::now()` in `initialize_region`.

### CXX-10 — `RawRegionView::InitialiseHeader` publishes magic first, not last

- **Severity:** Minor (test fidelity; can hide an ordering regression)
- **Location:** `cpp/tests/bridge/shm_reader_tests.cpp:110-117`
- **Status:** confirmed by reading.
- **Detail:** the documented protocol (`shm_layout.hpp:17-25`, ADR-0010) publishes `magic` **last** with release, so an acquire load of a valid magic guarantees the other header fields are visible. The test helper stores `magic` first, then `abi_version` and `header_size`. `OpenRecoversWhenTheHeaderInitialisesWithinTheRetryWindow` (:318-342) only passes because the retry loop re-reads; if the production writer regressed to this order the test would still pass. The helper claims to use "the release semantics the bridge's atomic validation pairs with" but pairs the release with the wrong store.
- **Fix:** store abi/version/size first, magic last, in the helper; optionally assert the reader rejects a valid magic with a zero ABI on the first attempt.

### CXX-11 — Loader diagnostics can be stale; library handle leaks if `make_unique` throws

- **Severity:** Minor (diagnostics; OOM-only leak)
- **Location:** `cpp/glasses/src/viture_loader.cpp` (`OpenLibrary`/`LastLibraryError`, `LoadVitureApi` construction of `VendorVitureApi`)
- **Status:** confirmed by reading.
- **Detail:** if `Utf8ToWide` fails (invalid UTF-8 path), `OpenLibrary` returns null without calling `LoadLibraryW`; `LastLibraryError()` then reports whatever `GetLastError` holds (often `ERROR_NO_UNICODE_TRANSLATION` from the failed conversion, or a stale value), mislabelling the reason. Separately, `std::make_unique<VendorVitureApi>(handle, fns)` can throw `bad_alloc`; the `HMODULE`/`dlopen` handle is then never closed and the module stays loaded. `LoadVitureApi` is also not `noexcept`, so a `bad_alloc` escapes the "no function throws" contract for a caller that does not expect it.
- **Fix:** capture `GetLastError` immediately after the failing call (and inside `Utf8ToWide` return a distinct message); construct the object into a `unique_ptr` with a custom closer that owns the handle before allocation, or use `new (std::nothrow)` with explicit cleanup.

### CXX-12 — Prediction extrapolates through `Lost`/quiet samples

- **Severity:** Minor (product behaviour; safety-relevant comfort decision)
- **Location:** `cpp/glasses/src/viture_head_pose_source.cpp:196-230`
- **Status:** confirmed by reading.
- **Scenario:** after tracking is lost (`state == Lost`, or quiet synthetics published while the device is dead), `TryGetLatest(predict>0)` still extrapolates yaw/pitch with the last rates and advances `time` by up to 100 ms. A consumer that predicts on a lost pose injects motion that never happened; freezing at the last pose is the conservative behaviour.
- **Fix:** gate prediction on `state == Stable`, or document that prediction on a non-Stable sample is intentional and the consumer is responsible. Add a contract case asserting the chosen policy.

### CXX-13 — Reader can pair a new yaw offset with an old `until_seq` across two rapid recentres

- **Severity:** Minor (one-frame yaw error; requires two recentres within a reader's load window)
- **Location:** `cpp/glasses/src/viture_head_pose_source.cpp:191-196` (reader) versus `:156-157` / `:367-369` / `:383-385` (writers)
- **Status:** confirmed by reading.
- **Scenario:** the reader loads `recentre_until_seq_`, then `recentre_pending_`, then `yaw_offset_deg_` (relaxed). If a second `Recenter`/resolution updates offset and `until_seq` between the reader's first and third loads, the reader can validate `newest.seq <= old_until` and then read the *new* offset. The two offsets differ by the head yaw between the two targets, so the sample can be mis-corrected by up to that movement for one frame. Reader ordering is otherwise correct (every writer stores offset before the release stores the reader acquires).
- **Fix:** version the offset (pack offset+until into a single 64-bit/128-bit seqlock or read all three under a short mutex), or have the reader re-check `until_seq` after reading the offset and retry on change.

### CXX-14 — `Recenter()` can return Ok while a concurrent `Stop` withdraws the post

- **Severity:** Minor (API semantics; no corruption)
- **Location:** `cpp/glasses/src/viture_head_pose_source.cpp:98-119` (Stop withdraws after join), `:164-167` (post)
- **Status:** confirmed by reading.
- **Scenario:** `Recenter` passes the `running_` check, arms and posts; concurrently `Stop` tears down the device and, after joining, calls `WithdrawPendingRecentre` (`:115-119`, `:399-403`), clearing the pending correction. `Recenter` already returned Ok, so the caller believes the reset happened; it never did, and no seam call is made. The header documents the withdrawal, but the Ok return is ambiguous.
- **Fix:** return a distinguishable status when the post is withdrawn concurrently, or document that Ok means "posted" (not "applied") and have the caller observe the correction via the stream. At minimum add a test for the race.

### CXX-15 — Property tests run with an unpinned random seed

- **Severity:** Minor (test integrity: coverage varies run to run; failures not reproducible without the printed seed)
- **Location:** `cpp/tests/core-math/quat_property_test.cpp` (no `RC_PARAMS`/seed configuration); no `RC_PARAMS` anywhere in the repo or workflows
- **Status:** confirmed by reading; RapidCheck's `TestParams::seed` defaults to 0, whose documented meaning is "generate a random seed", and the gtest integration reads only `RC_PARAMS`.
- **Consequence:** each run exercises different inputs with different shrink paths. A rare counterexample produces a one-off CI failure; the S1 plan's "seeded `rc::Gen`" intent is not implemented. (The generator is finite-valued in this rapidcheck version, so NaN inputs are not a flake source.)
- **Fix:** pin `RC_PARAMS=seed=<fixed>` in the test target/CI (and vary it in a nightly lane), or construct `rc::detail::TestParams` explicitly in the suites.

### CXX-16 — Golden fixture is not fingerprinted; expected values are self-referential

- **Severity:** Minor (test integrity)
- **Location:** `contracts/golden/transforms.json`; `cpp/tests/core-math/golden_test.cpp`; `python/depcheck/contracts.py:34-39` (`CONTRACT_FILES` excludes it)
- **Status:** confirmed by reading.
- **Detail:** the golden runner compares the implementation against values read from the same repository file; nothing pins the file's content (unlike the four contract headers). An edit to both the formula and the fixture — or a careless fixture edit matched by a broken implementation — passes the C++ lane. The C# twin (`dotnet/tests/CoreMath.Tests`) is the only cross-check, so a dual edit would still pass. I verified 10 of the 28 values by hand against the formulas and they are correct at this revision.
- **Fix:** add `transforms.json` to the depcheck fingerprint set (or a checksum recorded in the test), and/or add independent hand-computed cases with provenance comments.

### CXX-17 — C++ suite never falsifies that the read-time recentre offset is dropped after `until_seq`

- **Severity:** Minor (test gap on a safety-relevant path)
- **Location:** `cpp/tests/glasses/contract_tests.cpp:141-168` (no `advance` after `Recenter`); `cpp/tests/glasses/fake_viture_api.hpp:189-195` (feed recentre modelled only for `use_script_` feeds)
- **Status:** confirmed by reading.
- **Detail:** in production, `ResetOriginCarina` changes the SDK feed, so the read-time offset must stop applying after `until_seq`; otherwise poses are double-recentred. The contract test observes only the same sample (armed path), and the fault tests that use the plain sample list never recentre the feed (`use_script_ == false`), so a regression that keeps applying `yaw_offset_deg_` forever passes the whole C++ suite. The fake *does* model feed recentring via `FeedScript`, but no test drives that combination.
- **Fix:** add a contract case on the scripted Viture harness: recentre, `advance` several samples, and assert yaw stays zero *without* the read-time offset (e.g. by checking `recentre_until_seq_` indirectly through a subsequent `ResetOriginCarina` count or a non-zero raw feed).

### CXX-18 — Contract harness silently under-advances on a 2 s timeout

- **Severity:** Minor (test flake tolerance; reduced coverage without a failure)
- **Location:** `cpp/tests/glasses/contract_tests.cpp:604-622`
- **Status:** confirmed by reading.
- **Detail:** `advance(count)` advances the clock, grants one poll, and waits up to 2 s for `seq >= target`; if the deadline elapses it just proceeds to the next step with no assertion. Later `ASSERT_TRUE(TryGetLatest(...))` may then observe the previous sample, producing either a flaky strict-order failure (`EXPECT_GT(sample.seq, previous.seq)`) or a silently reduced number of advances in tests that do not compare sequences. A stall in the source under test is thus converted into a delayed/flaky failure instead of a precise one.
- **Fix:** assert on timeout (`ADD_FAILURE`/`ASSERT`), or return a bool from `advance` and fail at the call site with the missed target.

### CXX-19 — `atomic_ref` over C-struct shared memory: formal object-model UB, no `-fno-strict-aliasing` note

- **Severity:** Minor (formal UB; universally implemented pattern; Release-level miscompilation risk in theory)
- **Location:** `cpp/bridge/src/bridge.cpp` (`copy_payload`/`store_payload` reinterpret `Payload*` to `std::uint64_t*`, then `atomic_ref` access), `cpp/bridge/src/test_writer.cpp` (same), `cpp/glasses/include/cg/glasses/pose_slot.hpp` (`memcpy` into `std::uint64_t payload_[]` is fine — this item is bridge-only)
- **Status:** confirmed by reading.
- **Detail:** the referenced objects have effective type `cg_head_sample`/`cg_hand_frame`; accessing their storage through `std::uint64_t` lvalues (and constructing `atomic_ref<uint64_t>` over them) is outside the object model. Alignment is guaranteed by the layout asserts, both sides use the same type, and compilers treat `atomic_ref` ops as opaque, so this is the standard cross-process seqlock pattern; but it is not strictly conforming, UBSan/TSan do not diagnose it, and no lane compiles the bridge with `-fno-strict-aliasing` or at `-O2` (see CXX-03).
- **Fix:** either declare the payload storage as an array of `std::atomic<std::uint64_t>` in the layout (and have the C writer publish through `memcpy`-free 8-byte words) or explicitly document the deviation and add `-fno-strict-aliasing` to the bridge target with a comment pointing at this finding.

### CXX-20 — Little-endian-only layout has no compile-time guard

- **Severity:** Minor (portability)
- **Location:** `cpp/bridge/include/cg/bridge/shm_layout.hpp` (`kShmMagic`, word-wise payload protocol); POSIX branch compiles on any Unix including big-endian targets
- **Status:** confirmed by reading.
- **Detail:** the region is little-endian by construction (`CGSHM001` bit-cast check, raw 8-byte word copies, float payloads). On a big-endian build the magic check fails and the payload would be misread; nothing asserts `std::endian::native == std::endian::little`. All current targets (x64/arm64) are LE, so this is a future-port note.
- **Fix:** add `static_assert(std::endian::native == std::endian::little, "the shared region is little-endian")` in `shm_layout.hpp`.

### CXX-21 — Bridge read path has no allocation counter gate

- **Severity:** Minor (test gap for NFR-04 on the render path)
- **Location:** `cpp/tests/bridge/*` (no replaced allocation operators); compare `cpp/tests/glasses/allocation_tests.cpp` and `cpp/tests/core-math/allocation_test.cpp`
- **Status:** confirmed by reading.
- **Detail:** the glasses and core-math hot paths have explicit 0-byte gates, but `cg_bridge_read_head`/`cg_bridge_read_hands` — called from the Unity render thread through the native plugin — have no allocation-measuring test. `copy_payload` is stack-array based, so the property is very likely true; it is just unverified. The S6 gate measured frame time, not allocations, on the C# side.
- **Fix:** add a small allocation-counter TU to the bridge reader test target, or a C# test with `GC.GetAllocatedBytesForCurrentThread` around 300 frames (the S6 method).

---

## Previously reported items — confirmed still present (accepted/deferred, not recounted)

These were reported in `2026-10-03-cpp.md` and are recorded as deferred in `docs/notes/s5-gate.md` §12 and
`docs/notes/tech-debt.md`; I re-verified each is still in the tree. They are listed only so the reader knows
they were not missed, not as new findings.

| TD | Item | Verified at |
|---|---|---|
| TD-001 | Fake prediction drops roll (rebuilds yaw·pitch) | `fake_head_pose_source.cpp:112-120` |
| TD-002 | Process-wide `PoseSlot::SetTestPublishHook` read non-atomically | `pose_slot.hpp:52-63` |
| TD-003 | No magic/ABI re-validation on `read_head`/`read_hands` | `bridge.cpp` read paths |
| TD-004 | `cg_test_writer_*` exported from the production bridge DLL | `cpp/bridge/CMakeLists.txt` (test_writer in `cg_bridge`, `WINDOWS_EXPORT_ALL_SYMBOLS`) |
| TD-005 | `HostSample.seq` monotonicity invariant implicit | `viture_head_pose_source.cpp` `seq_` |
| TD-006 | `cg_bridge_send_command` ignores its handle and reopens by name | `bridge.cpp` `store_command` |
| TD-007 | Soak is a leak gate only; not registered with ctest | `cpp/tools/CMakeLists.txt` |
| TD-008 | Windows region-size guard page-granular (`VirtualQuery`) | `bridge.cpp` `cg_bridge_open` |

Also fixed-and-confirmed: I-2 (region ABI 2 + baseline `abiVersion: 2`), I-3 (magic published last with
release), M-5 (`stopped_`/`sbs_` acquire/release), M-6 (future heartbeat fresh, unsigned age), M-8 (replay
`Load` resets `next_seq_`/`slot_`), M-10 (`/permissive- /utf-8`), M-11 (`ToNanoseconds` saturation).

## Verified sound (coverage of this pass)

**Seqlocks / concurrency**

- `PoseSlot::Publish` uses the canonical protocol: relaxed odd store → release fence → relaxed payload
  stores → release fence → release even store; the hook sits after the odd store and before the payload.
- `PoseSlot::TryRead` uses acquire begin, relaxed word loads, acquire fence, relaxed re-check; the counter
  is monotonic (+2 per publish), closing the ABA window; the 64-attempt bound returns false (documented
  false fallback); `published_` gates the first read; `Reset` semantics documented.
- Bridge writer uses the same protocol plus `seq_b = seq_a + 1`; the reader re-reads `seq_a` after `seq_b`
  (the ABA guard ADR-0010 promises), bounds at 64/128 attempts, and copies through 8-aligned words
  (`static_assert` sizes/offsets pin 48/576/592/848 and little-endian magic).
- `is_stale` is exclusive at 250 ms, treats future heartbeat as fresh, and computes the age in unsigned
  arithmetic to avoid overflow (`INT64_MIN`/`INT64_MAX` cases tested).
- Staleness semantics are consistent with ADR-0010: head keeps the sample with `CG_TRACK_LOST`, hands
  return `NOT_READY`.
- `VitureHeadPoseSource` lifecycle: `lifecycle_mutex_` serializes `Start`/`Stop`; `jthread` + `stop_token`
  plus `RequestStop` give total `Stop` under a blocking poll (fault test proves ≤250 ms with a 5 s poll);
  Start/Stop idempotent; reconnect backoff 100 ms→2 s cap and post-cap 2 s probing match ADR-0009 and are
  falsified by `ReconnectCapStopsRecreatingAndRecoversOnLostProbe` (create_calls == 11).
- Recentre handoff: post from any thread, service on the poll thread with a generation guard; the reader
  acquires `until_seq`/`pending` before reading the relaxed offset; exactly-once application is tested
  after a reconnect (`RecentreWorksAfterSuccessfulRecreate`).
- `TryGetLatest` allocates nothing (1M-read counter gate) and the slot is wait-free; quiet-state timing
  (500 ms Unstable / 1000 ms Lost) and monotonic `seq_`/`time` are pinned by tests.
- `VitureDisplayControl`: `Stop` latch and SBS cache use acquire/release; seam failures propagate without
  poisoning the cache; no seam call after `Stop`.

**Memory / resources**

- No leaks found on any `cg_bridge_open` error path (Windows and POSIX); `new (std::nothrow)` failure
  unmaps and closes; `cg_bridge_close(nullptr)` and double-close are safe; `LastHeaderError` is
  thread-local and cleared on success.
- `VitureLoader` closes the library when symbol resolution fails, uses `memcpy` to convert symbol
  addresses (no function-pointer cast UB), and interns diagnostics in a bounded, mutex-protected pool.
- `ReplayHeadPoseSource::Load` validates every field (finite positions/quaternions, integer ranges,
  strictly increasing host time) and clears the slot/sequence on reload; `Slerp`, `Quat::FromComponents`,
  and `PoseFromSdk` fall back to identity on non-finite input without producing NaN poses.
- `ClockMapper` median is correct for odd/even windows, rejects non-finite samples, saturates through
  `ToNanoseconds`, and both sources reset it on mapped-time regression.

**Contracts / ABI / build**

- `depcheck contracts` passes; the baseline hashes match the normalized contract text; the version-bump
  guard cannot be bypassed by `--update` without a bump.
- `offsetof`/size `static_assert`s pin every 5.6 table entry; `atomic<uint64_t>` lock-freedom asserted;
  `CG_ABI_VERSION == 2` pinned in the layout tests.
- Warning coverage is real where enabled: MSVC `/W4 /WX /permissive- /utf-8`, GCC
  `-Wall -Wextra -Wpedantic -Wconversion -Werror`, ASan+UBSan, TSan (`-Wno-tsan` is documented and limited
  to GCC's fence-instrumentation warning), coverage with atomic profile updates.
- The five test binaries are green at this revision (201 tests total), including torn-counter injection,
  saturated-writer retry-exhaustion, TSan-oriented stress, and the manual-clock fault suite.

## Claims that need a run to settle

1. **CXX-01/CXX-02 races** — need a deterministic harness (gated fake `ResetOriginCarina`; a `Start` raced
   against `Set`). TSan cannot catch either: the state is mutex/atomic-protected at the C++ level; the
   defect is a protocol ordering window. I confirmed both by interleaving analysis only.
2. **CXX-03 Release exposure** — needs a Release/RelWithDebInfo `ctest` run on GCC and MSVC; the
   `atomic_ref`-over-struct aliasing and fence code have never executed above `-O0`/Debug in CI.
3. **CXX-04(b)** — needs a Linux soak with an injected sub-peak leak to measure the masking magnitude;
   (a) is confirmed by code path.
4. **HIL (TD-042/043)** — the real vendor `PollPose` block time and whether `RequestStop` can interrupt
   it (the adapter only sets a flag; `Stop` totality relies on the contractual 100 ms poll cap), plus
   U-01 (SDK seconds epoch/rate) and U-08 (display concurrency). If the HIL poll can exceed the cap,
   `Stop` (and hence Unity teardown) is unbounded.
5. **CXX-19** — whether GCC/MSVC at `-O2` ever exploits the aliasing formally; not observable with the
   current lanes (no UBSan diagnosis for this class, no Release tests).

## Report path

`.superpowers/reviews/2026-10-04-critical-cpp.md`

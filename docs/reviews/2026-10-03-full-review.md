# Cubeglass — Full Programme Code Review (2026-10-03/04)

**Scope:** the entire repository at the end of the S7 software merge (`c0734a2`), reviewed by four independent domain reviewers and then fixed in waves, each fix wave re-reviewed by a fresh reviewer before merge. This document is the consolidated record: methodology, findings, dispositions, verification, and the residual owner actions.

**Method**

- Four parallel read-only reviews: C++/contracts native, .NET pure modules, Unity adapters, and CI/tooling/documentation. Reviewers were given the dossier contracts, ADRs and the stage gate notes, and were asked to verify even known-deferred items.
- Every Critical/Important finding was fixed in a scoped wave (branches `review/*`) with TDD where testable, then re-reviewed by a separate agent; two runtime flakes discovered during the fix phase were root-caused rather than retried.
- Minors were fixed when cheap or recorded as deferred with a reason. Nothing was silently dropped.

**Findings summary**

| Domain | Critical | Important | Minor | Fix PRs |
|---|---|---|---|---|
| C++ / native / contracts | 0 | 3 | 12 | #37, #39, #41 |
| .NET pure modules | 0 | 1 | 4 | #38 |
| Unity adapters | 0 | 3 | 9 | #40 |
| CI / tooling / docs | 2 | 6 | 10 | #42 |
| **Total** | **2** | **13** | **35** | |

All 15 Critical/Important findings are fixed and verified; 0 remain open. Deferred minors are listed at the end.

## Critical and Important findings — disposition

### C++ / native

| # | Severity | Finding | Disposition |
|---|---|---|---|
| N-1 | Important | `Recenter` blocked the polling thread up to ~1 s, stalling the 500 Hz pose stream | Fixed in #37: recentre is posted and applied by the poll loop; reader-visible yaw-zero preserved; contract/fault tests updated |
| N-2 | Important | Shared-memory region ABI stayed 1 while the hand payload grew (`cg_hand_frame`); mixed-version pairs undetectable | Fixed in #37: region ABI 2, reader rejects mismatches naming both versions, layout/tests/ADR pinned |
| N-3 | Important | Writer header publication had no release/acquire ordering; a partly-initialised header could be accepted | Fixed in #37: magic published last with release, reader acquire-loads magic first; init retry documented as belt-and-braces |
| N-4 | Important (found while fixing) | `VitureFault.RecentreWorksAfterSuccessfulRecreate` flaked (5/200 repeats): bounded slot read could starve under a back-to-back publisher, and a post racing device loss was consumed without effect | Fixed in #39: capture retries while a publish is in flight; a claimed post stays armed until a live device publishes, then applies exactly once; 200/200 repeats clean, also under 4-way load |
| N-5 | Important (found while fixing) | Fault suite still used wall-clock waits; different tests flaked on slow/coverage runners (`StartPoseFailure…`, `ResetFailure…`) | Fixed in #41: all waits converted to self-advancing manual-clock predicates; two fake-observable publication races fixed; 300× fault repeats, 50× full binary, 16×250 under load 0 failures |

Minors: fake predict drops roll; global slot test hook; no header re-validation on read; test writer shipped in the production DLL (deliberate, ADR-0010); display latch/SBS ordering (fixed); future-heartbeat/page-granular guard (fixed); recentre seq invariant (documented); replay `Load` state carry-over (fixed); `send_command` ignores handle; MSVC `/permissive-`/`/utf-8` (added); `ClockMapper::Map` overflow saturation (fixed); soak gate scope. Remaining deferred items are recorded in `docs/notes/s5-gate.md` §12.

### .NET pure modules

| # | Severity | Finding | Disposition |
|---|---|---|---|
| D-1 | Important | `DdaRaycaster` accepted `+∞`/NaN/negative `maxDistance`; with an axis-aligned ray the traversal could never terminate (effective hang) | Fixed in #38: invalid distances throw (`ArgumentOutOfRangeException`), traversal step cap as defence-in-depth; +∞/NaN/negative tests plus a finite-input termination property |

Minors: `PlayerController` non-finite Move/TurnSnap sanitisation (fixed, tests); interaction FsCheck replay seed pinned (fixed); raycaster property now asserts nearest-hit minimality against a brute-force oracle (fixed); per-edit dirty-set allocation recorded as deferred (small, edit path only; `IReadOnlyList` shape frozen). Coverage floors held (Voxel 94.0%, Gameplay 98.2%).

### Unity adapters

| # | Severity | Finding | Disposition |
|---|---|---|---|
| U-1 | Important | HUD hotbar anchored at `PlayerRoot` feet (~31° below eye, outside the 13.1° half-vFOV) — off-screen in the SBS view | Fixed in #40: eye-height + documented forward offset inside the per-eye FOV, math + PlayMode visibility tests; monocular-left IMGUI documented, stereo HUD deferred to M2 |
| U-2 | Important | Release Game scene had no `WindowManager`; FR-01 (SBS 3840×1080/90 Hz) had no in-product caller | Fixed in #40: added to the builder, scene regenerated (hash `2B97305E…`), composition test |
| U-3 | Important | Calibration `SyntheticPoseDrive` applied `TurnSnap` without `* dt` (60–90× sensitivity) and ignored the snap edge; a follow-up sign inversion mirrored calibration look vs the game | Fixed in #40: per-second integration, snap edge wired, look sign matches `PlayerController`, discriminating tests |
| U-4 | Important (data integrity) | Failed disk writes still reported flush complete | Fixed in #40: flush result surfaces failures, `SaveBatches` logs loudly; forced-failure test asserts the previous save stays byte-identical |
| U-5 | Important | A throwing dirty-remesh leaked a pooled chunk view | Fixed in #40: release on throw, exactly-once guard tested |
| U-6 | Important (diagnostics) | Missing native bridge plugin fell back to synthetic tracking silently | Fixed in #40: explicit warning + `PluginUnavailable` flag and tests |

Minors: calibration budget test hermeticity (fixed), IMGUI paint untested headless (documented), plus the known S6/S7 deferrals (Input System, config.json runtime load, cell-carrying ChunkChanged, Chebyshev-1 remesh superset, boot double-apply diagnostics, Replace-fallback wording).

### CI / tooling / documentation

| # | Severity | Finding | Disposition |
|---|---|---|---|
| C-1 | Critical | Dossier §9.7 contract-compatibility gate was unimplemented while ADR-0003 implied otherwise | Fixed in #42: `depcheck contracts` fingerprints the frozen ABI (`cg_types.h`, bridge header, ports/result) against `contracts/abi-baseline.json`; updates require a real version bump (comment-bypass closed); CI step in the required `depcheck` job; ADR-0003/0004 corrected |
| C-2 | Critical | Performance gates were advisory: nightly had no regression comparison; the mesh p95 harness always exited 0 | Fixed in #42: p95 `--budget-ms` exits non-zero (8 ms CI budget wired into nightly; 2.0 ms local/release budget documented); nightly compares cpp+.NET benchmark JSON against a captured baseline at the dossier 10 %, naming offenders; multi-report CLI tested |
| I-1 | Important | `release.yml` "release absent" path exited 1; the first successful dispatch would fail after building | Fixed in #42 (probe, capture, clear `$LASTEXITCODE`; verified across present/absent/failed-upload paths) |
| I-2 | Important | No vulnerability scanning; licence allowlist ids unvalidated | Fixed in #42: nightly NuGet `--vulnerable` + `pip-audit` + SPDX-id validation with tests |
| I-3 | Important | Actions on floating tags; game-ci CLI floating | Fixed in #42: every action SHA-pinned with version comments, `cliVersion` pinned, Dependabot added |
| I-4 | Important | clang-format missed headers; clang-tidy scope misdocumented | Fixed in #42: format covers all `cpp/**` (60 files clean); tidy scope stated honestly; expansion deferred |
| I-5 | Important | `depcheck` silently exempted modules missing from `layers.json` | Fixed in #42: missing/stale entries fail; `handcore` explicitly deferred per ADR-0001; tests |
| I-6 | Important | Unity suites never ran in CI or release builds | Fixed in #42: release workflow runs EditMode+PlayMode under the same licence before building; per-PR limitation documented |

Minors: workflow permissions/timeouts/concurrency (added), ci-local exit-code robustness, .gitignore coverage, docs drift (toolchains/TSan/Unity CLI), `nightly-baseline.json` established on first run (documented), pip hash-pinning deferred with reasons, MSVC `/analyze`, Gameplay mutation testing and clang-tidy expansion deferred.

## Verification of the fix programme

- PRs #37–#42 all merged with the six required checks green; each fix wave re-reviewed by a separate agent (verdicts: all addressed; the infra wave required one fix round for the multi-report CLI and the ABI regex).
- Full local gates at the final heads: cpp `ctest` 5/5 (incl. TSan in CI), .NET 471/471 with coverage floors held, Python 78 + ruff/mypy, `depcheck`/`contracts`/`licences`, Unity EditMode 112/112 and PlayMode 44/44, `ci-local -SkipUnity` ALL LANES PASS.
- Scene hashes re-pinned: Game `2B97305E…`, Calibration `70F970CE…`.
- Local Windows player build reproduced after all fixes: `unity/Cubeglass/build/StandaloneWindows64/Cubeglass/` (96.9 MB payload; data/level files rebuilt 2026-10-04 05:38; launcher exe byte-identical). This is the artefact for the owner playtests; the CI release build remains the tagged-release gate.

## Residual owner actions

1. **CI release build (v0.1.0):** add repository secrets `UNITY_LICENSE` (contents of a `.ulf`; Personal flow: `unity-request-activation-file` → license.unity3d.com → `.ulf`), `UNITY_EMAIL`, `UNITY_PASSWORD`. Dispatch the Release workflow; it now stages managed+native plugins, runs EditMode/PlayMode under that licence, builds the Windows x64 player, and attaches it to a `v0.1.0` release if the tag exists.
2. **HIL playtests (stage tags S5/S6/S7 and `v0.1.0` are withheld until these land):**
   - S5: `docs/questions/S5-HIL.md` — run `cg-pose-probe` with glasses attached, commit `docs/notes/s5-hil/pose_probe.csv` + log; answers U-01/U-08 in ADR-0009.
   - S6: `docs/questions/S6-HIL.md` — calibration-scene visual checklist + U-09 (distortion/FOV).
   - S7: `docs/questions/S7-HIL.md` — play the local build (or the CI artefact) on the glasses: comfort, input, no stuck states, break/place/save/reload, view distance 8 at 90 Hz on the RTX 5070.
3. **First nightly run:** establishes `docs/perf/nightly-baseline.json` (commit the artefact) after which the 10 % regression gate is active; mutation/soak/vulnerability jobs execute there.

## Deferred minors index

C++: `docs/notes/s5-gate.md` §12. .NET: `docs/notes/s7-gate.md` (per-edit allocation). Unity: `docs/notes/s6-gate.md`/`s7-gate.md` (stereo HUD, Input System, config.json, compaction one-tick race, cell-carrying ChunkChanged, remesh superset, Replace-fallback wording, boot double-apply diagnostics). Infra: `docs/ci.md` (pip hashes, tidy expansion) and the infra fix reports under `.superpowers/reviews/` (scratch, not committed).

*Nothing in this review requires a contract version bump beyond the ones recorded in ADR-0009/0010; the ABI gate added here now enforces that going forward.*

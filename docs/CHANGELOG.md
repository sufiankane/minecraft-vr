# Changelog

Consolidated engineering history of Cubeglass, kept in the spirit of
[Keep a Changelog](https://keepachangelog.com/) but organised by build stage
(S0–S7) instead of semantic version: nothing has shipped yet, and the only
planned release tag is `v0.1.0` (M1), withheld per ruling R50.

Every entry below is derived from `git log --oneline --decorate`, `git tag -l`,
`docs/notes/s0-gate.md`…`s7-gate.md`, `docs/reviews/2026-10-03-full-review.md`,
`docs/releases/v0.1.0.md` and `docs/ci.md`. Entries cite the squash-merge commit
and PR number shown in the log; no dates or PRs are invented. The verified tree
for this document is `docs/full-docs` / `main` = `0d3ecb6` (#50).

## [Unreleased] - 2026-10-05 tech-debt resolution waves

**Head:** after PR #61. Four parallel worktree waves resolved the register:
C++/native (#57), CI/gates (#58), Unity + .NET (#59), follow-ups (#60:
test-support DLL split, MSVC /analyze lane, Unity service-account release auth),
plus the register consolidation (#61).

### Changed
- 59 register rows closed with outcomes and commit refs; 17 residual rows added
  (TD-069..TD-085); open debt is now **22 rows - High 4, Medium 6, Low 12** (TD-077 closed: the last skipped Unity test became a junction-based probe; PR #63), of
  which the four High rows are owner-gated (HIL playtests and the v0.1.0 tag).
- Contract evolution: ADR-0013 cell-carrying ChunkChanged (managed-only), the
  bridge test-support DLL split (production exports verified by dumpbin), and
  the hosted release service-account auth path.

### Verified
- Every wave merged with all six hosted checks green (runs 37285368799,
  37291376806, 37293376168, 37301193538, 37302016541); local gates included
  Debug+Release ctest 8/8, .NET 534/534 with coverage floors, Python 117 with
  hash-pinned requirements, Unity EditMode 199 + PlayMode 51.
## [Unreleased] - 2026-10-04 second critical review

**Head:** `fix/critical-review` (merged after this entry's commit). The second,
adversarial review treated the code as a life-saving device: four domain
reviewers, every Critical/Important fixed and re-verified by the reviewer who
raised it.

### Added
- Full documentation set: `docs/ARCHITECTURE.md`, `docs/CONTRACTS.md`,
  `docs/CHANGELOG.md`, `docs/ONBOARDING.md`, `docs/adr/README.md` (#51).
- Second critical review record: `docs/reviews/2026-10-04-critical-review.md`.
- Tech-debt rows TD-048..TD-061 (textual-scan limits, gate discipline, loader
  and shm trust, scans, baseline lifecycle, artefact signing, synthetic-Stable,
  registry parse validation, deferred C++/Unity minors, attach-job verification).

### Fixed
- Contract gate: code-anchored `KNOWN_FINGERPRINTS`/`KNOWN_ABI_VERSIONS` close
  the baseline-rewrite bypass; digraph includes, line continuations, string
  literals, fully-qualified namespaces and macro includes are caught; recursive
  module discovery (`41073ac`, `bd89ee3`).
- Native: display/lifecycle TOCTOU, recentre arming race and one-frame pairing
  snapshot, Release test lane, soak RSS gate fail-closed, loader path/search
  hardening (`2a6c7ac..cae93d4`, `9b6e51b`).
- .NET: unknown-id totality, streaming config validation, collision/placement
  integer safety plus eight minors (`a8a272b..d7058b6`).
- Unity: bridge probe retry/fallback reporting, flush + atomic store swap,
  NaN/Inf guards, unknown-id mirror, budget ceiling, five follow-ups
  (`168b7eb..bc541b4`).
- CI/release: shared Unity results gate on both routes, checksum artefacts,
  scoped write tokens with attach jobs, nightly zero-report guard, pinned gcovr,
  release-lane timeouts (`c6f1283..9690761`).

### Verified
- Local gates at the fix head: C++ Debug 6/6 and `windows-release` 6/6
  (+300x race repeats), .NET 524/524, Python 117/117, Unity EditMode 152/152 and
  PlayMode 50/50 with scene hashes unchanged, `ci-local -SkipUnity` ALL LANES
  PASS. Hosted checks remained billing-blocked (TD-045).
## [Unreleased] — post-S7 (#37–#50)

**Head:** `0d3ecb6` (#50); no tag. This block covers everything after the S7
software merge `c0734a2`: the review waves #37–#43 and the post-review CI and
release fixes #44–#50.

### Added
- Self-hosted Windows release route for Hub-activated machines
  (`runs-on: [self-hosted, windows]`, no Unity secrets), test-gated by the
  EditMode/PlayMode suites before the player build (#44).
- ADR-0012 "CI Unity licensing and the release build routes", status accepted:
  dispatch-only release (R49) with `hosted`/`self-hosted` `build_target`
  routes (#46).
- Programme tech debt register `docs/notes/tech-debt.md` (#47); extended by
  TD-045–TD-047 in #50 (hosted Actions billing block, admin-merge bypass,
  session-scoped runner).

### Changed
- `release.yml` hosted route rebuilt around the pinned Unity CLI
  (`1.0.0-beta.11`) and editor `6000.6.3f1`, with `UNITY_LICENSE` (offline
  `.ulf`) or `UNITY_SERIAL` credential modes (#45); the managed licensing
  client is installed before activation (#48); the self-hosted job verifies the
  pinned .NET SDK (`dotnet --list-sdks` vs `dotnet/global.json`, 10.0.401)
  instead of `actions/setup-dotnet` (#49). `docs/ci.md` and
  `docs/releases/v0.1.0.md` kept in step.

### Fixed
- Hosted-route licence activation on a fresh runner (missing managed
  licensing client) (#48).
- Self-hosted SDK verification without elevation (#49).

### Verified
- Self-hosted route end to end: run 37198879954 (2026-10-04) passed the SDK
  check, plugin staging, EditMode+PlayMode release gate, player build,
  packaging and upload, producing `Cubeglass-v0.1.0-win-x64` (~36 MB); the
  release attach was skipped because `v0.1.0` does not exist yet (owner-gated,
  R50).
- The hosted route remains unverified while the account's Actions billing block
  is open (TD-045); no repository change can bypass it.
- `stage-7-complete` and `v0.1.0` remain withheld (S7 HIL + CI player build).

### Review waves (#37–#43) — Critical/Important fixes per domain

The full programme review at `c0734a2` (record added in #43,
`docs/reviews/2026-10-03-full-review.md`) ran four parallel read-only domain
reviews. Findings summary: **2 Critical, 13 Important, 35 Minor**; all 15
Critical/Important were fixed in scoped waves and re-reviewed. Five further
Important-severity items were found while fixing (N-4, N-5, U-4, U-5, U-6) and
are fixed as well; minors were fixed when cheap or recorded as deferred.

| Domain | Fixed by | Critical / Important items |
| --- | --- | --- |
| C++ / native / contracts | #37, #39, #41 | N-1 non-stalling recentre (posted to the poll loop); N-2 region ABI 2 with mixed-pair rejection; N-3 magic published last with release ordering; N-4 recentre survives reconnect/recreate; N-5 fault suite made clock-deterministic |
| .NET pure modules | #38 | D-1 `DdaRaycaster` rejects `+∞`/NaN/negative `maxDistance`; step cap as defence-in-depth |
| Unity adapters | #40 | U-1 HUD eye-height anchor inside the per-eye FOV; U-2 `WindowManager` added to the Game scene; U-3 calibration drive `* dt`, snap edge and sign fixed; U-4 failed writes surface as flush failures; U-5 remesh throw releases the pooled view; U-6 missing plugin warns explicitly |
| CI / tooling / docs | #42 | C-1 `depcheck contracts` ABI gate + `abi-baseline.json`; C-2 enforced perf gates (p95 `--budget-ms`, nightly 10% regression compare); I-1 release-absent path; I-2 vulnerability scanning + SPDX id validation; I-3 SHA-pinned actions + Dependabot; I-4 clang-format over all `cpp/**`; I-5 `layers.json` completeness; I-6 Unity suites in the release gate |

Wave verification: PRs #37–#42 merged with the six required checks green;
cpp 5/5 (incl. TSan in CI), .NET 471/471 with coverage floors held, Python 78
plus ruff/mypy, `depcheck`/`contracts`/`licences`, Unity EditMode 112/112 and
PlayMode 44/44, `ci-local -SkipUnity` ALL LANES PASS. Scene hashes re-pinned
(Game `2B97305E…`, Calibration `70F970CE…`). Deferred C++ minors are indexed
in `docs/notes/s5-gate.md` §12.

## S7 software — M1 vertical slice (#32–#36, head `c0734a2`)

**Tag:** withheld. `stage-7-complete` (M1) waits on the S7 HIL playtest and the
CI player build; the `v0.1.0` tag is owner-gated (R50).

- **Added:** deterministic streaming scheduler and golden session (#32); pooled
  chunk views and streaming runtime (#33); input mapping, gameplay bridge,
  world UI, comfort vignette and Unity-space alignment (#34); batched
  persistence and boot replay (#35); game scene, performance record, release
  pipeline and HIL escalation (#36); ADR-0011 (accepted).
- **Changed:** committed scenes rebuilt (`Game.unity` SHA-256 `2B97305E…`,
  `Calibration.unity` `70F970CE…`); `EditorBuildSettings` pins `[Game,
  Calibration]`; the HUD moved to an eye-height anchor inside the per-eye FOV.
- **Fixed:** cross-session edit loss (a delta loaded at boot was not seeded
  into the merged map) and flush accounting (a max-version drain could report
  complete before a coalesced older chunk was written); pre-fix failure
  evidence at `6abf9ac`, merged in #35 with `EditsSurviveAcrossSessions` and
  `FlushWaitsForEveryChunkWhenCoalescingReordersTheQueue`.
- **Verified:** golden 5-minute session world hash `0xB38A50148C01A643`,
  independent of frame chunking; 1,445 resident chunk views at view distance 8;
  frame p95 1.576 ms / p99 2.020 ms against the ADR-0010 11.1 ms budget; Unity
  EditMode 112/112, PlayMode 44/44; 471 .NET tests; CI runs #32
  37109972292, #33 37114583092, #34 37122822029, #35 37127666886.

## S6 software — Unity stereo runtime (#28–#31, head `91576de`)

**Tag:** withheld. `stage-6-complete` waits on the visual checklist and U-09 on
the glasses (ruling R45 let S7 proceed).

- **Added:** shared-memory bridge contract and the 5.12 C ABI (#28); Unity
  bridge package and native bridge test surface (#29); stereo rig, late-latch,
  Unity input/window/overlay (#30); calibration scene, frame budget and HIL
  escalation (#31); ADR-0010 (proposed pending U-09).
- **Changed:** `CG_ABI_VERSION` 1 → 2 with `cg_hand`/`cg_hand_frame` (R42);
  command/ack words occupy reserved header bytes 32..39 (R43); the
  `com.cubeglass.bridge` and `com.cubeglass.rendering` packages joined the
  Unity project.
- **Verified:** bridge stress 200,000 published samples against 8/4 readers,
  0 mismatches; `ctest` 5/5; Unity EditMode 59/59, PlayMode 5/5; calibration
  frame p95 0.654 ms vs the 11.1 ms budget; CI runs #28 37011797355,
  #29 37021006447, #30 37032693109.

## S5 software — glasses contracts and pose slot (#24–#27, head `67b9044`)

**Tag:** withheld. `stage-5-complete` waits on the HIL probe (U-01/U-08);
S6 proceeded meanwhile (ruling R39/R45).

- **Added:** glasses contract vocabulary, wait-free `PoseSlot` and fake source
  (#24); VITURE SDK seam and polling head-pose source (#25); replay source,
  display control and TSan lane (#26); `cg-pose-probe`, soak lane, wait-free
  benchmark and software evidence (#27); ADR-0009 (proposed pending HIL).
- **Changed:** additive C++ contract vocabulary (`Duration`, `TrackState`,
  `Status`/`Result`) in `contracts/cpp/`; `CG_ABI_VERSION` stayed 1 through
  S5 (the shared-memory region itself arrives in S6).
- **Verified:** `cg_glasses_tests` 92 tests / 12 suites; the parameterised
  contract suite 30/30 across `fake`, `replay` and `viture-fake`; TSan lane;
  0 allocations over 1M reads; `BM_TryGetLatest` median ≈ 6.5 ns/op; 30-minute
  soak: 900,001 samples, RSS delta 0.00 MiB against the 1 MiB budget.

## S4 — gameplay core (#20–#23, head `9ddde05`)

**Tag:** `stage-4-complete` → `9ddde05` (#23).

- **Added:** input contracts, player controller and scripted input (#20);
  interaction service, scenario replays and gesture-ready contracts (#21);
  gesture recogniser over mock hands (#22); coverage floor and exit-gate
  evidence (#23); ADR-0008.
- **Verified:** seven scenario replays (`walk`, `break`, `place`,
  `place-inside-player`, `tracking-loss-cancel`, `hotbar-mid-break`,
  `recentre`) match their pinned world hashes; `Cubeglass.Gameplay` coverage
  98.61% (floor 90); 439/439 .NET tests; CI run 36968690060.

## S3 — meshing (#16–#19, head `d393599`)

**Tag:** `stage-3-complete` → `d393599` (#19).

- **Added:** mesh contracts, float vectors and reference mesher (#16); greedy
  mesher with differential and golden tests (#17); per-vertex AO and border
  seam handling (#18); pooled buffers, benchmarks, p95 budget and gate
  evidence (#19); ADR-0007.
- **Verified:** differential oracle over 2,000 seeded chunks (10,952,194
  exposed faces); five FNV-1a golden mesh hashes unchanged across pooling;
  p95 vs the 2.0 ms ADR-0007 budget (solid 0.438 / terrain 0.280 /
  checkerboard 1.115 recorded); `Cubeglass.Mesh` coverage 97.26%; CI run
  36955165377.

## S2 — voxel core (#11–#15, head `7d585e7`)

**Tag:** `stage-2-complete` → `7d585e7` (#15).

- **Added:** voxel coordinates, ADRs and the layer exception (#11); registry,
  chunk, world and store (#12); DDA raycaster and deterministic terrain
  generation (#13); delta persistence and collision (#14); budgets, mutation
  score and exit-gate evidence (#15); ADR-0005, ADR-0006.
- **Changed:** ADR-0006 amended pre-release with the `0xFFFF` no-edit sentinel
  so mined (explicit Air) edits survive a save/load round trip (ruling R19,
  commit `f51526e`, shipped in #14).
- **Verified:** origin-chunk golden hash `0x1EF678D6ADDA1CEC` (seed 42);
  `Cubeglass.Voxel` coverage 92.68% (floor 90); Stryker mutation score 83.05%
  (break 70); raycast/Get/Apply allocation deltas 0; CI run 36940886728.

## S1 — core math, time and conventions (#7–#10, head `6031fff`)

**Tag:** `stage-1-complete` → `6031fff` (#10).

- **Added:** shared C types, ADR-0004 and the 28-case transform golden fixture
  (#7); C++ core math and golden harness (#8); C# `Cubeglass.CoreMath` and
  harness (#9); allocation gates, benchmarks, 95% coverage floors and gate
  evidence (#10).
- **Verified:** both language harnesses green on the same
  `contracts/golden/transforms.json` (schema 1, tolerance 1e-6); C++ `core-math`
  coverage 99.19%, C# `Cubeglass.CoreMath` 95.76% (floors 95); allocation
  deltas 0 in both languages; CI run 36922538193.

## S0 — foundation and engineering platform (#1–#6, head `a2131c6`)

**Tag:** `stage-0-complete` → `a2131c6` (#6); initial commit `98d69f6`.

- **Added:** repository layout, CMake presets, vcpkg manifest, .NET solution,
  Unity skeleton and Python tooling (#1); negative-gate self-tests (#2);
  coverage gates and nightly benchmarks (#3); ADR-0001–0003, `CONTRIBUTING.md`
  and the PR checklist (#4); fresh-clone validation and `scripts/ci-local.ps1`
  (#5); final review fixes (#6).
- **Fixed:** the Unity lane in a pristine clone (create
  `unity/Cubeglass/Assets` before the CLI run, relative project path) — a
  scaffold prerequisite, not a weakened gate.
- **Verified:** `ci-local.ps1` ALL LANES PASS from state-free clones, including
  a clone path containing spaces; six required check contexts block merges
  (PR #3/#4 merged only green); negative-gate dispatch run 36875444355 rejects
  its fixtures and inversion run 36875811720 proves a gate can go red.

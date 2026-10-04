# Programme tech debt register

- **Snapshot:** `docs/tech-debt` from `main` `fc4a6ce` (2026-10-04), after the
  2026-10-03/04 programme review and fix waves (#37–#45).
- **Source set:** `.superpowers/reviews/2026-10-03-{cpp,dotnet,unity,infra}.md`,
  `docs/reviews/2026-10-03-full-review.md`, `docs/notes/s0-gate.md`…`s7-gate.md`
  (deferred sections), `docs/ci.md`, `docs/perf/README.md`,
  `docs/releases/v0.1.0.md`, `.superpowers/reviews/fixes-*.md`, and
  `git log --oneline -40`.

## Purpose and how to use it

Purpose: one durable list of the debt that deliberately survived the S0–S7
review waves, so it stays owned and triggered instead of being rediscovered.
Scope: the open items of the four domain reviews, the stage-gate deferred
sections, `docs/ci.md`/`docs/perf/README.md` deferrals and the fix reports.
Rows marked **review leftover** are findings a gate note did not carry; they are
registered here so they are not lost. Historical stage-minor lists that already
have a reason recorded in a gate note are not duplicated.

Rules:

- **Add rows, never delete them.** One row per item, next free `TD-NNN`; do not
  renumber survivors.
- When an item is fixed, **move the row to [Closed](#closed)** with the PR
  number and a one-line outcome, and delete it from the open table.
- Re-grade severity when the trigger fires; correct a stale row in place rather
  than adding a duplicate.
- Severity legend: **High** blocks the release/HIL gates; **Medium** is a
  shipped-product risk or a gate that is not yet enforcing; **Low** is docs,
  tests or tooling polish.

Verified at this snapshot: no open TSan/Unity CLI documentation drift
(`docs/ci.md`, `CONTRIBUTING.md`, `docs/toolchains.md` were synced in #42); the
F1 XML-contract nit is closed in #43 (`ceil`→`floor` step-cap wording); the six
required CI job contexts still match `docs/notes/s0-gate.md:172`.

## Open debt

| ID | Area | Item | Sev | Source | Why deferred | Suggested trigger / owner action |
| --- | --- | --- | --- | --- | --- | --- |
| TD-001 | C++/native | `FakeHeadPoseSource` prediction rebuilds yaw·pitch and drops roll | Low | cpp review M-1; s5-gate §12 | Test-only fake; the contract suite sweeps no roll case | Trigger: pose-source or Unity-rig work. Action: port the replay delta-compose and add a rolled-pose case |
| TD-002 | C++/native | `PoseSlot::SetTestPublishHook` is process-wide inline-static state, read non-atomically | Low | cpp review M-2; s5-gate §12 | Only safe while armed with no publish in flight; current test users are serial | Trigger: any parallel/overlapping slot test. Action: instance member or atomic hook with an "arm before publish" rule |
| TD-003 | C++/native | `cg_bridge_read_head`/`read_hands` never re-validate magic/ABI/header_size after open | Low | cpp review M-3; s5-gate §12 | Region-name collision is unlikely; not on the open path | Trigger: S12 writer or bridge hardening. Action: cheap acquire re-check returning `CG_ERR_UNSUPPORTED`/`CG_ERR_NOT_READY` on mismatch |
| TD-004 | C++/native | `cg_test_writer_*` raw-injection exports ship inside the production `cg_unity_bridge.dll` | Medium | cpp review M-4; s5-gate §12; ADR-0010 | Deliberate for S6 C# tests (R44); the frozen header forbids export macros | Trigger: any release beyond the owner playtest / M2 packaging. Action: split a test DLL or compile-gate the writer; keep production to the 5.12 exports |
| TD-005 | C++/native | `HostSample.seq` monotonicity across reconnects is implicit, and the exactly-once recentre correction relies on it | Low | cpp review M-7; s5-gate §12 | Single-publisher assumption; no deterministic falsifier exists | Trigger: next recentre/reconnect change. Action: document the invariant on `seq_` and add a publish→recentre→reconnect→publish exactly-once test |
| TD-006 | C++/native | `cg_bridge_send_command` ignores its handle and reopens the region by name on every call | Low | cpp review M-9; s5-gate §12 | The payload region is read-only, so a writable header view is needed; Unity is the only caller | Trigger: S12 command/ack layer. Action: stash the mapping on the handle, or document "validated but not used for the write" |
| TD-007 | C++/native | Soak is a leak gate only: no thread/handle assertions, no latency threshold, not registered with `ctest` | Medium | cpp review M-12; s5-gate §12 | NFR-07's second half is unautomated; S5 is a single manual 30-minute record | Trigger: NFR-07 automation. Action: assert thread/handle counts and register a short nightly `ctest` soak |
| TD-008 | C++/native | Windows region-size guard uses `VirtualQuery` `RegionSize`, exact only to 4 KiB | Low | fixes-native (M-6 remainder) | An exact `NtQuerySection` check was out of the fix-wave scope | Trigger: S12 writer/bridge hardening. Action: check the section size exactly |
| TD-009 | C++/native | ADR-0002's historical layout table still prints `abi_version (=1)` | Low | ADR-0002 l.55-61; ADR-0010 l.183-184; fixes-native I-2 | ADR-0002 is the S0 historical record; the region is now ABI 2 while the name stays `Local\cubeglass.v1.state` | Trigger: next docs/ADR pass. Action: annotate the table as historical or update it to ABI 2 |
| TD-010 | C++/native | `cg_warnings` is linked `PRIVATE` on every target, so header consumers do not inherit `/W4 /WX` | Low | fixes-native (M-10 remainder) | `/permissive-` and `/utf-8` were the requested fix; visibility change was out of scope | Trigger: next CMake/toolchain change. Action: expose it as an `INTERFACE` dependency of the contract-facing targets |
| TD-011 | .NET | `ChunkEditPropagation.GetAffectedChunks` allocates 1–2 `ChunkCoord[]` per edit (108–144 B) | Low | dotnet review F5; s7-gate §12; fixes-dotnet | Frozen `IReadOnlyList` shape; edit path (not per-frame); tests-only caller today | Trigger: API-break window or edit-heavy GC. Action: caller-owned buffer or `Span`-with-count |
| TD-012 | Unity | The HUD is one left-eye screen-space IMGUI pass; the right eye sees no HUD, with no stereo depth | Medium | s7-gate §12; unity I-1 disposition; fixes-unity | Eye-height placement is fixed; per-eye geometry is M2+ work | Trigger: M2 HUD. Action: per-eye HUD geometry; HIL confirms left-eye readability/right-eye comfort |
| TD-013 | Unity | Input System mapping/package never landed; new-input-only builds return `Neutral` | Medium | s6-gate §9; unity M-1; `Packages/manifest.json` | S6 said S7, S7 did not carry it; legacy Input Manager works | Trigger: input work or a new-input-only target. Action: add `com.unity.inputsystem` and the mapping, or record it dropped |
| TD-014 | Unity | `config.json` is never loaded at runtime and no config file ships | Medium | s6-gate §9; unity M-1 | `StereoRigConfig.LoadFromJson` is EditMode-tested only; the player uses serialized defaults (64 mm IPD, 45°) | Trigger: when calibration/config must ship. Action: wire the 5.13 config into `GameBoot`, or mark dropped |
| TD-015 | Unity | An edit in the same frame as a `ChunkViewManager` compaction can land in the pre-compaction world for one tick | Low | s7-gate §12; dotnet review known/deferred | The next tick picks up the rebuilt world; the settled path is pinned | Trigger: only if a stuck/ghost edit is observed. Action: re-resolve the live world inside the edit path |
| TD-016 | Unity | `ChunkChanged` carries the chunk, not the edited cell | Low | s7-gate §12 | The adapter cannot dirty an exact chunk set; the 27-chunk fan-out is conservative | Trigger: remesh cost matters. Action: cell-carrying edit signal |
| TD-017 | Unity | The Chebyshev-1 remesh superset rebuilds identical neighbour meshes | Low | s7-gate §12 | Correctness-safe; paired with TD-016 | Trigger: remesh cost matters. Action: after TD-016, remesh only changed chunks |
| TD-018 | Unity | Boot replay applies a stored delta then the merged map (double apply); overlay counters can mislead | Low | s7-gate §12 | Idempotent (the second apply is a no-op); diagnostics only | Trigger: next persistence/overlay change. Action: count effective edits or label the counters |
| TD-019 | Unity | `FileWorldStore`'s publish-fallback log wording suggests an unexpected `File.Replace` failure | Low | s7-gate §12 | The behaviour is correct; wording only | Trigger: next store edit. Action: reword the fallback log |
| TD-020 | Unity | IMGUI paint paths (HUD/overlay/vignette) have no automated coverage; headless never runs `OnGUI` | Medium | unity M-10; fixes-unity item 7 | Headless-lane limitation, documented; the U-1 HUD defect slipped through it | Trigger: M2 HUD or a Game View CI lane. Action: keep the layout assertions and HIL both-eyes item; add a paint smoke if a lane exists |
| TD-021 | Unity | Failed world-store writes are surfaced but not retried or re-dirtied | Medium | fixes-unity deferrals | Retry/backoff is future work; a quit right after an IO error can drop that batch | Trigger: durability work. Action: retry once and re-dirty, or document the loss window in the overlay/release notes |
| TD-022 | Unity | The calibration look direction is pinned only at the `SyntheticPoseDrive` level | Low | fixes-unity follow-up concern | A real on-glasses look test is a HIL item | Trigger: S6 HIL. Action: confirm the direction on the glasses in the calibration checklist |
| TD-023 | Unity | Fallback residuals: `PluginUnavailable` is warned and set but no runtime UI reads it; the plain `NotReady` fallback stays silent | Low | fixes-unity item 6; unity M-6 | The flag was added for diagnostics; overlay wiring was out of scope (verified: only tests read it) | Trigger: overlay/diagnostics work. Action: surface the flag and decide the no-writer UX |
| TD-024 | Unity | `GameplayBridge.SaveRequested` is a write-only diagnostic (only tests read it) | Low | unity M-2 (**review leftover**, no gate note) | Documented as a consume-and-clear handshake; persistence flushes independently | Trigger: next bridge/persistence edit. Action: delete it, document it as set-only, or have `SaveBatches` consume it |
| TD-025 | Unity | Scene hash pins are git-blob hashes and the pinning recipe is not documented | Low | unity M-5 (**review leftover**, no gate note); s7-gate §7 | A Windows checkout hashes CRLF bytes and will false-mismatch | Trigger: next re-pin or Unity version bump. Action: document `git cat-file`/rebuild-then-hash, or add a blob-hash script |
| TD-026 | Unity | `MotionVignette` draws one full-screen rect across the SBS pair | Low | unity M-7 (**review leftover**, no gate note); `MotionVignette.cs:108` | The comfort intent is per-eye; single-rect darkening is still acceptable | Trigger: M2 comfort pass. Action: draw per eye (`camera.pixelRect`) or document the behaviour |
| TD-027 | Unity | Package descriptions are stale ("S7 adapter placeholder"; rendering omits streaming/persistence) | Low | unity M-9 (**review leftover**, no gate note) | Metadata polish only | Trigger: next package touch. Action: update the `description` fields |
| TD-028 | CI/tooling | Python installs are version-pinned but not hash-pinned; `gcovr` is installed inline | Low | docs/ci.md deferred note; infra M-9; fixes-infra-b | Needs a compiled lock for every transitive plus the inline install; nightly `pip-audit` covers CVEs meanwhile | Trigger: dedicated supply-chain task. Action: `--require-hashes` lock (pip-compile/uv) |
| TD-029 | CI/tooling | clang-tidy runs only over `cpp/core-math/src`; glasses/bridge/tests/tools are deferred | Medium | docs/ci.md; infra I-4 disposition | Widening surfaces 159 warnings-as-errors; the burn-down is a task | Trigger: quality sweep. Action: enable module by module (start `cpp/bridge/src`) and keep the ci.md scope current |
| TD-030 | CI/tooling | MSVC `/analyze` is not run anywhere | Low | infra M-3; fixes-infra-b deferred | clang-tidy is the only C++ static analysis and does not cover all modules | Trigger: toolchain/CI hardening. Action: add `/analyze /WX` or record an accepted deviation |
| TD-031 | CI/tooling | Mutation testing covers Voxel only; no Gameplay score | Low | infra M-4; fixes-infra-b deferred | Nightly Stryker targets `Cubeglass.Voxel`; Gameplay is the rule hotspot | Trigger: gameplay churn or a nightly slot. Action: add a Gameplay Stryker config |
| TD-032 | CI/tooling | Coverage floors pass a zero-line module at 100%, and CI picks the first report if several exist | Low | infra M-2; docs/perf/README; docs/ci.md | No current module takes the path; the docs are honest about it | Trigger: next coverage-check change. Action: fail when floor>0 and lines==0; fail on multiple reports |
| TD-033 | CI/tooling | The nightly baseline is not committed, so the 10% regression gate compares nothing | Medium | docs/perf/README; fixes-infra-a concern 2; full review owner action 3 | Capture-on-first-run design; an owner must commit the artefact | Trigger: after the first nightly run. Action: commit `docs/perf/nightly-baseline.json` |
| TD-034 | CI/tooling | The 10% nightly comparison runs on noisy shared runners and can false-fail | Low | docs/perf/README (8 ms p95 CI-budget rationale); fixes-infra-b concern 4 | Hosted runners are noisier than the dev machine; the threshold is dossier-mandated | Trigger: after TD-033 lands. Action: watch the nightly; tune `--threshold` or use a stable runner if noise exceeds the margin |
| TD-035 | CI/tooling | The self-hosted release path has never run end-to-end (CLI/editor install, cold import, PlayMode timing) | Low | fixes-selfhosted "not verified"; docs/ci.md route 2 | No real self-hosted run; first-run duration and `--non-interactive` placement are unexercised | Trigger: first dispatch on the licensed machine. Action: expect timeout/`-nographics` tuning, then record the duration |
| TD-036 | CI/tooling | With `gh` absent the release attach is skipped with a warning; the player stays a workflow artefact | Low | docs/ci.md route 2; fixes-selfhosted | The runner may not have `gh`; the attach is optional | Trigger: runner setup review. Action: install `gh` or make the skip decision explicit; verify both branches once |
| TD-037 | CI/tooling | clang-format/clang-tidy versions are not pinned in CI, and the negative gate uses a distro build | Low | infra M-1; fixes-infra-b "still open" | Hosted runner tools are deliberately unlocked with version prints; format output drifts between versions | Trigger: next format/negative-gate change. Action: pin the tool or record the runner version as intentional in both jobs |
| TD-038 | CI/tooling | Release artefacts carry no SHA-256 checksums (dossier gate 10) | Medium | infra M-10; fixes-infra-b "still open" | The release has not run yet; packaging predates the gate | Trigger: first v0.1.0 dispatch. Action: emit and upload `Cubeglass-windows-x64.zip.sha256` and reference it in the release notes |
| TD-039 | CI/tooling | The contract gate fingerprints exactly the four named contract files | Low | fixes-infra-a concern 3; docs/ci.md | A new file under `contracts/` would not be covered until `CONTRACT_FILES` is extended | Trigger: a new contract file. Action: add it to the gate and regenerate the baseline |
| TD-040 | CI/tooling | The Unity CLI installer script is fetched from Unity's CDN unpinned (the `-Target` binary is SHA-256-verified) | Low | fixes-infra-b concern 3; docs/ci.md release section | The script text cannot be pinned; the binary it installs is verified | Trigger: release supply-chain review. Action: vendor/pin the installer or record the accepted risk |
| TD-041 | Release/HIL | `v0.1.0` has never been built or tagged: no Unity secrets, no successful release dispatch | High | full review owner action 1; docs/ci.md; s7-gate §11; docs/releases/v0.1.0.md | The tag is withheld (R50) until the HIL playtest and the CI player build succeed | Owner: add `UNITY_LICENSE`/`UNITY_EMAIL`/`UNITY_PASSWORD` (or use the self-hosted route), dispatch, and create the tag when green |
| TD-042 | Release/HIL | S5 HIL log missing: `cg-pose-probe` on the glasses, commit `docs/notes/s5-hil/pose_probe.csv` + `.log` | High | s5-gate §8; S5-HIL.md; full review owner action 2 | Answers ADR-0009 U-01/U-08; `stage-5-complete` is withheld | Owner: run the probe command in `docs/notes/s5-gate.md` §8 and commit the artefacts |
| TD-043 | Release/HIL | S6 HIL log missing: calibration visual checklist plus U-09 (distortion/FOV) | High | s6-gate; S6-HIL.md; full review owner action 2 | `stage-6-complete` is withheld; ADR-0009/0010 stay proposed | Owner: run the calibration checklist on the glasses and commit `docs/notes/s6-hil/checklist.md` (+ screenshots) |
| TD-044 | Release/HIL | S7 HIL log missing: on-glasses playtest (comfort, input, break/place/save/reload, view distance 8 @ 90 Hz on the RTX 5070) | High | s7-gate §11; S7-HIL.md; docs/releases/v0.1.0.md | `stage-7-complete` (M1) and the release candidate wait on it | Owner: play the built player and commit `docs/notes/s7-hil/checklist.md` plus the perf log/screenshots |
| TD-045 | CI/tooling | Hosted Actions jobs are refused at start: "recent account payments have failed or your spending limit needs to be increased" (first seen 2026-10-04 09:49Z; blocked per-PR CI, Nightly and the hosted release) | High | Release run 37193381925 annotation; main CI 37193378194 | Account-level billing block; no repository change can bypass it | Owner: GitHub Billing and plans -> Plans and usage -> raise the Actions spending limit / settle the failed payment, then `gh run rerun` the failed runs |
| TD-046 | Process | Two CI-only PRs were admin-merged while hosted checks could not start (#49 self-hosted SDK verify; this docs PR). Branch protection was bypassed once per PR | Medium | This register; release run 37198879954 | No way to obtain the six checks while TD-045 is open; changes were CI/docs-only and the self-hosted run verified the result | Trigger: TD-045 resolved. Action: return to check-gated merges and re-verify any admin-merged commit through the normal CI lane |
| TD-047 | CI/tooling | The self-hosted runner `cubeglass-local` runs as a session-scoped background process; it stops when this Kilo session ends | Medium | Runner setup 2026-10-04 | Quickest path to a verified build; service install needs elevation and the user's credentials | Owner (optional): `C:\actions-runner\svc.cmd install`, started as the same Windows user, for persistence across logins |

Open counts at this snapshot: **44 rows — High 4, Medium 10, Low 30.**

## Closed

| PR | Area | Closed item | One-line outcome |
| --- | --- | --- | --- |
| #37 | C++/native | N-1 `Recenter` blocked the pose stream for up to ~1 s | Recentre is posted and applied by the poll loop; reader-visible yaw-zero preserved; contract/fault tests updated |
| #37 | C++/native | N-2 shared-region ABI stayed 1 while `cg_hand_frame` grew | Region ABI 2; mixed-version pairs rejected naming both versions; layout/tests/ADR pinned |
| #37 | C++/native | N-3 writer header publication had no release/acquire ordering | Magic published last with a release store; reader acquire-loads magic first; retry kept as belt-and-braces |
| #39 | C++/native | N-4 recentre flake after a device recreate (5/200 repeats) | A claimed post stays armed until a live device publishes, then applies exactly once; 200/200 repeats clean |
| #41 | C++/native | N-5 fault suite used wall-clock waits and flaked on slow runners | All waits are self-advancing manual-clock predicates; 0 failures under stress |
| #38 | .NET | D-1 `DdaRaycaster` accepted `+∞` and could hang | Invalid distances throw; traversal step cap; `+∞`/NaN/negative tests plus a finite-input termination property |
| #40 | Unity | U-1 hotbar anchored at the feet, outside the per-eye frustum | Eye-height anchor inside the per-eye FOV; math + PlayMode visibility tests; monocular HUD documented |
| #40 | Unity | U-2 release Game scene never applied the display/window configuration | `WindowManager` added to the builder; scene regenerated (`2B97305E…`); composition test |
| #40 | Unity | U-3 calibration applied a per-second look rate without `dt` and ignored the snap edge | Per-second integration, snap edge wired, look sign matches the game, discriminating tests |
| #40 | Unity | U-4 failed writes reported the flush as complete | `FlushResult` surfaces failures; `SaveBatches` logs; a forced-failure test keeps the previous save byte-identical |
| #40 | Unity | U-5 a throwing dirty remesh leaked a pooled view | Release-on-throw with an exactly-once guard tested |
| #40 | Unity | U-6 a missing/wrong native plugin fell back silently | Explicit warning + `PluginUnavailable` flag + tests (overlay surfacing left as TD-023) |
| #42 | Infra | C-1 dossier §9.7 contract-compatibility gate was absent | `depcheck contracts` fingerprints the frozen ABI against `contracts/abi-baseline.json`; a bump is required; required job |
| #42 | Infra | C-2 performance gates were advisory only | p95 `--budget-ms` exits non-zero (8 ms CI budget); nightly compares cpp+.NET JSON at the dossier 10%, naming offenders |
| #42 | Infra | I-1 release attach failed whenever `v0.1.0` did not exist | The probe captures and clears the native exit state; a missing release is a normal outcome |
| #42 | Infra | I-2 no vulnerability scanning; licence allowlist ids unvalidated | Nightly NuGet `--vulnerable` + `pip-audit`; SPDX-id validation with tests |
| #42 | Infra | I-3 actions on floating tags; game-ci CLI floating | Every action SHA-pinned with version comments, `cliVersion` pinned, Dependabot added |
| #42 | Infra | I-4 clang-format missed headers; clang-tidy scope misdocumented | Format covers all `cpp/**` (60 files); tidy scope stated honestly (expansion left as TD-029) |
| #42 | Infra | I-5 `depcheck` silently exempted modules missing from `layers.json` | Missing/stale entries fail (`layersEntryMissing`/`layersEntryStale`); `handcore` deferred explicitly; tests |
| #42 | Infra | I-6 Unity suites never ran in CI or release builds | Tagged releases run EditMode + PlayMode under the licence before building; the per-PR lane stays local and documented |
| #43 | Process | Programme review record + review nits | `docs/reviews/2026-10-03-full-review.md` added; ADR-0004 wording corrected; the DDA `ceil`→`floor` doc nit closed |
| #44 | Release | Self-hosted release route for a Hub-licensed machine | Build, Unity test gate, package and attach with no Unity secrets |
| #45 | Release | Hosted release with the pinned Unity CLI | Serial or offline `.ulf` licence, pinned CLI/editor, test-gated build |

Fixed minors kept for traceability (not open, not rowed above): C++ M-5, M-6
(future-heartbeat half; page-granular remainder is TD-008), M-8, M-10
(`/permissive-`/`/utf-8`; visibility remainder is TD-010), M-11, plus the TSan
fake-observable fixes (#37/#41); .NET F2, F3, F4 (#38); Unity budget
hermeticity, remesh leak, plugin warning, HUD/game-window/calibration fixes
(#40); infra M-5, M-6, M-7, M-8 and the round-2 multi-report/ABI-comment fixes
(#42).

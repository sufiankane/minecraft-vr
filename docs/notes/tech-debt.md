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
| TD-041 | Release/HIL | `v0.1.0` has never been tagged: the HIL playtest is outstanding and no release dispatch is green (hosted attempt 37233165883 failed at Unity serial activation, see TD-069; the self-hosted route built and packaged the player in run 37198879954) | High | full review owner action 1; docs/ci.md; s7-gate §11; docs/releases/v0.1.0.md; td-ci wave observation | The tag is withheld (R50) until the HIL playtest and a green release dispatch succeed; the failed hosted attempt and its cause are recorded here | Owner: complete HIL, add the hosted licence sign-in (TD-069) or dispatch the self-hosted route, then create the tag when green |
| TD-042 | Release/HIL | Resolved 2026-10-06: HIL log committed (`docs/notes/s5-hil/pose_probe.csv` + `.log`, 60 s, 4,533 samples, exit 0); ADR-0009 U-01/U-08 answered in the 2026-10-06 amendment | Resolved | s5-gate §8; S5-HIL.md | `stage-5-complete` eligible after green CI | — |
| TD-043 | Release/HIL | Resolved 2026-10-06: calibration checklist committed (`docs/notes/s6-hil/checklist.md` + `calibration-sbs.png`); all eight items pass; ADR-0010 amended: U-09 answered (distortion none, FOV 45°, IPD 64 mm) | Resolved | s6-gate; S6-HIL.md | `stage-6-complete` eligible after green CI | — |
| TD-044 | Release/HIL | S7 HIL log missing: on-glasses playtest (comfort, input, break/place/save/reload, view distance 8 @ 90 Hz on the RTX 5070) | High | s7-gate §11; S7-HIL.md; docs/releases/v0.1.0.md | `stage-7-complete` (M1) and the release candidate wait on it | Owner: play the built player and commit `docs/notes/s7-hil/checklist.md` plus the perf log/screenshots |
| TD-047 | CI/tooling | The self-hosted runner `cubeglass-local` runs as a session-scoped background process; it stops when this Kilo session ends | Medium | Runner setup 2026-10-04; td-ci TD-047 | #58 added `scripts/install-runner-service.ps1` (elevation-checked, idempotent) and the `docs/ci.md` runbook, but running it remains an owner action needing the user's credentials | Owner: run `scripts/install-runner-service.ps1` as the same Windows user for persistence across logins |
| TD-061 | Release | The new attach jobs (hosted/self-hosted) are statically verified only; the self-hosted attach needs a second runner pickup, which depends on the session-scoped runner (TD-047) | Medium | security review round 1 concern; td-ci wave observation | Observed 2026-10-04: hosted dispatch 37233165883 failed at licence activation before `attach-hosted` ran (TD-069); the self-hosted attach still waits on the service install (TD-047) | Trigger: first green release dispatch. Action: verify attach + checksum end to end on both routes |
| TD-069 | Release/HIL | Owner must create the Unity Cloud service-account key and set `UNITY_SERVICE_ACCOUNT_ID`/`UNITY_SERVICE_ACCOUNT_SECRET` (+ `UNITY_SERIAL`); the hosted workflow wiring landed in #60. First hosted dispatch is the end-to-end proof; the offline `.ulf` route remains the fallback | Medium | run 37233165883; #60 | Activation needs a signed-in session; the secrets are owner-held | Owner: Unity Cloud -> Administration -> Service accounts -> create key, add secrets, dispatch the hosted release |
| TD-070 | C++/native | Sub-page region-size rounding: Windows can only measure a page-rounded section cheaply, so the mapped view length is accepted as the size; the exact-window semantics are tested only on the POSIX lane | Low | td-cpp.md concern 3; TD-008 closure | `NtQuerySection` was out of scope; the accepted rounding semantics are documented in `bridge.cpp`/`shm_layout.hpp` | Trigger: S12 writer/bridge hardening. Action: exact section-size check or record the accepted page rounding on both platforms |
| TD-071 | Security | Shared-memory identity handshake (nonce/HMAC) remains out of scope; `writer_pid` is advisory, and the region cap is POSIX-only (Windows caps the mapped length) | Medium | td-cpp.md concern 4; TD-052 closure | A same-user process can still spoof pose/tracking until the S12 hand service; the design pass was deliberately deferred | Trigger: S12 hand service. Action: nonce/HMAC identity handshake + size cap; degrade to `TrackLost` on mismatch |
| TD-072 | C++/native | Allocation gate compiles the bridge sources into the test binary because a Windows DLL resolves its own `operator new`; it measures the real sources, not the shipped DLL allocator | Low | td-cpp.md concern 5; CXX-21/TD-059 residual | Duplicating the sources is deliberate; the DLL's allocator path stays unobserved by the gate | Trigger: next bridge packaging/test change. Action: document the caveat in the gate or add a DLL-level probe |
| TD-073 | CI/tooling | `NOLINTNEXTLINE(a, b)` directives must stay single-line: clang-format's 120-column wrap splits the check list and clang-tidy then suppresses nothing (this broke #58's tidy gate) | Low | td-ci-fix-tidy.md concern 2 | The CI tidy gate now catches the wrapped form, but the durable rule is not documented for contributors | Trigger: any tidy/NOLINT or format change. Action: add the one-line rule to AGENTS/CONTRIBUTING and re-run tidy after formatting |
| TD-074 | CI/tooling | Linux cpp job pins exact `clang-format-23`/`clang-tidy-23` debs from apt.llvm.org: the pool can stop serving that build, and the archive binary and PyPI wheel distributions must move together on a bump | Low | td-ci-fix-clang.md concerns 3-5 | An `apt-cache madison` guard fails with instructions; a bump is a deliberate URL+hash+key+docs edit | Trigger: LLVM bump or apt pool change. Action: move the archive URL/hash/cache key, apt version + deb hashes, PyPI pin and docs together |
| TD-075 | Unity | Input System live device reads (plug/unplug, `Gamepad.current`/`Keyboard`/`Mouse.delta`) are not exercisable headless, and `activeInputHandler` changed 0 to 2 (Both) so the provider compiles | Medium | td-unity item 3, "What could not be verified", concern 2 | Equivalence is pinned at the Unity-free `InputSystemMapping` snapshot; only a device/HIL run exercises real reads | Trigger: next input work or HIL. Action: exercise input on the glasses and confirm the legacy default + Both setting are intended |
| TD-076 | Unity | `SceneCanonicalizer` now throws on structurally identical same-named roots instead of ordering them by pseudo-random old ids | Low | td-unity concern 3 | Deliberate fail-loud for renumber-order independence; a future builder producing twins must rename one root | Trigger: next scene-builder/canonicalizer change. Action: document the twin rule or emit a clearer rename diagnostic |
| TD-078 | Security | TD-053 residual: OSV-Scanner has no vcpkg extractor and the Unity UPM registry has no open-source scanner, so `cpp/vcpkg.json` and UPM packages are covered by pins/tests, not an advisory scan | Medium | td-ci TD-053 outcome + concern 1 | Documented deviation; a real scan needs another tool (e.g. a vcpkg SBOM export) and a workflow install step | Trigger: next supply-chain change. Action: add a vcpkg SBOM/advisory step or record the accepted UPM risk |
| TD-079 | Release | TD-055 residual: release artefacts carry build-provenance attestations and checksums but remain unsigned; cryptographic signing with an owner-held key is a vendor decision | Low | td-ci TD-055 outcome | Key custody/signing infrastructure is out of M1 scope; attestation covers build identity | Trigger: first public release. Action: decide the signing key/vendor and sign, or record attestation as sufficient |
| TD-080 | Release | `--clobber` retention: release asset attach deletes and replaces an asset with the same name (`gh --clobber` / REST delete-existing), so a re-run silently overwrites it | Low | td-ci TD-036 outcome; TD-055 row | Deliberate for re-runs; the replacement policy is an owner decision | Trigger: first public release. Action: confirm the clobber policy or move to immutable versioned asset names |
| TD-081 | C++/native | TD-051 residual: the DLL SHA-256 pin is optional and is not a signature; the POSIX `dlopen` dependency search and executable-directory trust exemption remain documented, not mitigated | Low | td-cpp TD-051 outcome + concern 7 | Signature verification needs a vendor key policy; the SDK is Windows-only | Trigger: first HIL/release packaging. Action: decide an Authenticode/hash-required policy and dependency loading from the SDK dir |
| TD-082 | Security | TD-064 residual (M-18): `actions/cache` restore-keys can restore a vcpkg tree built from a different commit (prefix fallback) | Low | td-ci concern 3 | The installed tree is keyed on the manifest hash with prefix fallback; strict exact-key caching costs cold builds | Trigger: next dependency/cache change. Action: drop restore-keys or include the exact commit/manifest hash |
| TD-083 | CI/tooling | Gameplay mutation lane is wired but its first score is unknown; the first hosted nightly run may be red until the threshold is tuned | Low | td-ci concern 4; TD-031 outcome | The report artefact names the score; threshold tuning follows the first run | Trigger: first hosted nightly run. Action: read the report, tune `--break-at`/thresholds or fix the survivors |
| TD-084 | Unity | M-10 real display behaviour (apply fullscreen mode, change refresh, move to a second physical display) is unit-tested through helpers only; it was not exercised in batch mode | Low | td-unity "What could not be verified" | Needs hardware; the read-back match/mismatch wiring is tested | Trigger: S7/HIL or next display work. Action: exercise the window/display paths on hardware and record the result |
| TD-085 | Unity | No framebuffer comparison headless: TD-012/TD-020 assert geometry/layout/paint calls only, not rendered pixels | Low | td-unity "What could not be verified" | Batch-mode limitation; the HUD defect class is covered by the HIL both-eyes checks | Trigger: M2 HUD or a Game View CI lane. Action: add a screenshot/pixel smoke where a lane exists |

Open counts at this snapshot: **22 rows - High 4, Medium 6, Low 12.**

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
| wave | C++/native | Critical review CXX-01..CXX-04 (display TOCTOU, recentre arming, Release lane, soak RSS gate) | Fixed (`2a6c7ac..cae93d4`), re-verified; remaining minors TD-059 |
| wave | .NET | Critical review I1-I3 (unknown ids, streaming config, collision overflow) + M1-M8 | Fixed (`a8a272b..d7058b6`), re-verified; Unity mirror fixed in the Unity wave |
| wave | Unity | Critical review I-1..I-3 (probe retry, atomic saves, NaN guards) + M1/M2/M4/M6/M7/M11 + follow-ups R-1..R-5 | Fixed (`168b7eb..bc541b4`), re-verified; remaining minors TD-060 |
| wave | Security/CI | Critical review C-1 (contract-gate bypass) + I-2..I-6, I-9, I-10 + digraph/doc corrections | Fixed across three rounds (`c6f1283..bd89ee3`), re-verified; residuals TD-048..TD-058 |
| wave | Docs | Second critical review record and full documentation set | `docs/reviews/2026-10-04-critical-review.md`, `docs/ARCHITECTURE.md`, `docs/CONTRACTS.md`, `docs/CHANGELOG.md`, `docs/ONBOARDING.md`, `docs/adr/README.md` |
| wave | Fixes | Unknown-id crash (security I-7) and unenforced frame budget (I-11) | Fixed in the .NET/Unity waves; no open row |
| wave | C++/native | Fixed minor set CXX-05/06/07/08/09/10/13/15/20 | Fixed (`57c5558..cae93d4`), verified; CXX-19 and the rest -> TD-059 |
| wave | .NET | Fixed minor set M1-M8 | Fixed (`a8a272b..d7058b6`), verified; M9/M10 -> TD-068 |
| wave | Unity | Fixed minor set M-1/M-2/M-4/M-6/M-7/M-11 plus follow-ups R-1..R-5 | Fixed (`168b7eb..bc541b4`), verified; M-3/M-5/M-8/M-9/M-10 -> TD-060 |
| wave | Security/CI | Fixed set C-1, I-3..I-7, I-9 (partial), I-10 | Fixed (`c6f1283..bd89ee3`), verified; security minors M-1..M-21 -> TD-062..TD-067 |
| audit | Traceability | AUD-001..AUD-012 audit follow-up: reports committed, mypy scope widened, counts corrected, ignores/attributes fixed, stale worktree removed | `docs/reviews/2026-10-04-audit-log.md` resolution section; evidence in `docs/reviews/evidence/` |
| billing | CI/tooling | TD-045 hosted Actions billing block | Resolved 2026-10-04 evening: billing restored, hosted jobs queue again; main run 37231450589 all six green |
| #54 | Process | TD-046 admin merges while checks were unavailable | Resolved: #54 merged check-gated (all six green, run 37231080745). The bypass masked one real defect: admin-merged #53 failed on main (run 37230445088, GCC C++20 mixed-pair compile error in a new test) until #54 fixed it and re-ran every lane || n/a | CI/tooling | TD-035 self-hosted release route unverified | Verified end to end in run 37198879954 (2026-10-04): SDK check, plugin staging, EditMode/PlayMode gate, player build, package, upload; artefact `Cubeglass-v0.1.0-win-x64` (~36 MB) |
| #57 | C++/native | TD-001 `FakeHeadPoseSource` prediction rebuilt yaw/pitch and dropped roll | `aba38be`: prediction composes the recentre + yaw/pitch delta onto the recorded rotation; a scripted roll survives and a non-finite roll offset is rejected (tests) |
| #57 | C++/native | TD-002 `PoseSlot::SetTestPublishHook` was process-wide inline-static state | `edefb5d`: hook and context are instance members, the arm-before-publish rule is documented, and the test seam clears on every path |
| #57 | C++/native | TD-003 `read_head`/`read_hands` never re-validated magic/ABI/header_size after open | `1cb5e88`: classification is re-run on every read; torn/foreign headers return `CG_ERR_NOT_READY`/`CG_ERR_UNSUPPORTED` (tests) |
| #57 | C++/native | TD-005 `HostSample.seq` monotonicity and exactly-once recentre relied on an implicit invariant | `f2a42b8`: the never-reset strictly-increasing `seq_` invariant and the exactly-once recentre rule are documented where they live and pinned by reconnect/recreate tests |
| #57 | C++/native | TD-006 `cg_bridge_send_command` reopened the region by name on every call | `1cb5e88`: `cg_bridge_open` holds a persistent writable 64-byte header view; a name-gone test still observes `CG_OK` |
| #57 | C++/native | TD-007 soak was a leak gate only (no thread/handle/latency assertions, not in ctest) | `167f0e9`: soak asserts thread and handle/fd counts against a post-`Start` baseline plus a p95 latency gate; `soak_smoke` runs under ctest |
| #57 | C++/native | TD-008 Windows region-size guard was exact only to 4 KiB | `1cb5e88`: POSIX checks the exact `fstat` window and Windows accepts the mapped view length (documented); sub-page residual -> TD-070 |
| #57 | C++/native | TD-009 ADR-0002 still printed `abi_version (=1)` | `2f98488`: ADR-0002 gains a dated amendment stating ABI 2 and pointing at `kShmAbiVersion`/ADR-0010 |
| #57 | C++/native | TD-010 `cg_warnings` was linked PRIVATE, so header consumers missed `/W4 /WX` | `11f1038`: `cg_warnings` propagates PUBLIC from core-math/glasses/bridge; a consumer TU shows `/W4 /WX` in `compile_commands.json` |
| #57 | C++/native | TD-029 clang-tidy ran only over `core-math/src` | `6d66d1f`: scope widened to core-math+glasses+bridge (14 sources) with real fixes and ~57 justified NOLINT lines; CI aligned in `c4e90f1` (#58) |
| #57 | C++/native | TD-051 loader residuals (unsigned vendor DLL, POSIX `dlopen` search, exe-dir trust) | `3b1d3ea`: optional `CG_VITURE_DLL_SHA256` pin refuses a hash mismatch before open with tests; not a signature; residual -> TD-081 |
| #57 | C++/native | TD-052 shared-memory trust boundary (writer identity unverified, region uncapped) | `1cb5e88`: POSIX size window + exact Windows view, fixed `sizeof` payload copies, zero `writer_pid` treated as unpublished; identity residual -> TD-071 |
| #57 | C++/native | TD-059 remaining C++ minors CXX-11/12/14/16/17/18/21 + CXX-19 | `3b1d3ea`/`f2a42b8`/`51a78b2`/`1cb5e88`: all fixed with tests; allocation-gate measurement caveat -> TD-072 |
| #58 | CI/tooling | TD-028 Python installs version-pinned but not hash-pinned; `gcovr` inline | `446a3d1`/`c71fcc1`: all three requirement files regenerated with `--generate-hashes` + `--require-hashes`, pip-audit pinned, meta-test guards |
| #58 | CI/tooling | TD-031 mutation testing covered Voxel only | `1804923`/`3696550`/`8d717b7`: Gameplay Stryker lane wired (Stryker 5 `--break-at 70`, scoped to the Gameplay test project) with a JSON artefact; threshold tuning -> TD-083 |
| #58 | CI/tooling | TD-032 zero-line module passed at 100%; the first report was picked silently | `1d4c0a2`: zero coverable lines fails unless `--allow-empty`; `--report` resolves exactly one Cobertura file (tests) |
| #58 | CI/tooling | TD-033 nightly baseline uncommitted, so the regression gate compared nothing | `1804923`: nightly compares against the median of <=5 cached history summaries, bootstraps cleanly and uploads the history artefact; nothing committed |
| #58 | CI/tooling | TD-034 10% nightly comparison noisy on shared runners | `1804923`: median-of-N comparison subsumes the noise; the 10% rationale is documented in `docs/perf/README.md` |
| #58 | CI/tooling | TD-036 release attach was skipped when `gh` was absent | `e2aa347`: `publish-release-assets.ps1` prefers `gh` and falls back to the REST API with delete-existing upload; 13 PowerShell tests; both attach jobs use it |
| #58 | CI/tooling | TD-037 clang-format/clang-tidy versions not pinned in CI | `d97133f`/`df6973a`: CI installs and asserts exactly LLVM 23.1.2 (Windows archive SHA-256-verified and cached; Linux exact apt.llvm.org build) |
| #58 | CI/tooling | TD-038 release artefacts carried no SHA-256 checksums | Verified stale: fixed by #52; both release routes write and upload `*.sha256` (re-verified in this wave) |
| #58 | CI/tooling | TD-039 contract gate fingerprinted exactly four files | `1d4c0a2`: all five ABI artefacts fingerprinted; `EXEMPT_CONTRACT_FILES` records config/generated reasons; an uncovered new file fails (tests) |
| #58 | CI/tooling | TD-040 Unity CLI installer fetched unpinned from the CDN | `e2aa347`: the hosted installer is verified against the committed SHA-256 of the pinned CLI script (override variable for upgrades); the self-hosted route asserts `unity --version` |
| #58 | CI/tooling | TD-048 depcheck textual scan limitations (interpolation holes, built names, `#if 0`) | `1d4c0a2`: interpolation holes are scanned as code (nesting to depth 8, escaping, format masking); remaining limits documented and tested |
| #58 | Process | TD-049 contract-gate version-bump discipline was review-enforced only | `1d4c0a2`: `depcheck contracts --since REF` compares the merge-base baseline and fails a changed surface without a `CG_ABI_VERSION` bump; CI checks out full history |
| #58 | .NET | TD-050 dotted-prefix Voxel allow admitted `CancellationTokenSource` | `1d4c0a2`: `exact:<type>` entries added; Voxel migrated to `exact:System.Threading.CancellationToken`; the sibling is rejected by tests |
| #58 | Security | TD-053 no advisory scan for vcpkg ports or Unity packages | `1804923`/`04326f7`: OSV-Scanner v2.6.0 scans the hash-pinned requirements and `depcheck nuget` parses the JSON report; no vcpkg/UPM extractor -> TD-078 |
| #58 | CI/tooling | TD-054 nightly baseline uncommitted and lifecycle undefined | `1804923`: superseded by the self-seeding cached-history design (TD-033); no baseline artefact is committed and re-capture is automatic |
| #58 | Release | TD-055 artefacts checksummed but unsigned; `--clobber` retained | `e2aa347`: both attach jobs attest build provenance alongside checksums; signing keys stay a vendor decision -> TD-079; clobber retention -> TD-080 |
| #58 | CI/tooling | TD-063 watch-gate coverage residuals (negative gates, empty-file lists, RSS-only soak, Voxel-only mutation, bounded fuzz) | `d97133f`/`c4e90f1`: expect-red gates for contracts/licences/coverage/benchregress with diagnostics; zero-file selectors throw; M-8/M-9 subsumed by TD-007/TD-031 |
| #58 | Security | TD-064 supply-chain pinning residuals (Python not hash-pinned; cache restore-keys) | `446a3d1`/`c71fcc1`: requirements hash-pinned and enforced; the M-18 cache restore-key loophole is untouched -> TD-082 |
| #58 | Security | TD-065 golden values self-referential; licence gate never read the licence text | `1d4c0a2`: `depcheck golden` cross-checks all 28 transforms with an independent Python implementation; the licence gate warns on undeclared allowlist entries |
| #58 | Operability | TD-066 no licence-expiry runbook; TSan lowered `vm.mmap_rnd_bits` for the rest of the job | `4250d73`/`c29db7c`: expiry/rotation runbook (Unity Student seat 2027-10-07) + ADR-0012 amendment; the sysctl is captured and restored best-effort under `if: always()` |
| #59 | .NET | TD-011 `GetAffectedChunks` allocated 1-2 `ChunkCoord[]` per edit | `9945e79`: `FillAffectedChunks(Int3, Span<ChunkCoord>)` writes the sorted set allocation-free; the wrapper shape is unchanged; zero-alloc gate |
| #59 | Unity | TD-012 HUD was one left-eye screen-space IMGUI pass | `c676cae`/`54a9fb2`: world-space per-eye reticle + hotbar with both-eye visibility, per-eye vignette and both-eye/allocation-free refresh tests |
| #59 | Unity | TD-013 Input System mapping/package never landed | `3529ff4`: `com.unity.inputsystem@1.20.0` added, provider over the Unity-free mapping core with legacy fallback; `activeInputHandler=Both`; live-device reads -> TD-075 |
| #59 | Unity | TD-014 `config.json` never loaded at runtime | `fcc507e`: `Assets/config.json` ships defaults, validated (clamp/ignore/reject) with built-ins < inspector < file precedence, loaded by GameBoot/StereoRig/StreamingRuntime |
| #59 | Unity | TD-015 same-frame edit vs compaction could land pre-compaction | `9bccca6`: `CompactWorldNow`/`RefreshWorld` keep the edit alive; a same-frame remesh test pins it |
| #59 | Unity | TD-016 `ChunkChanged` carried the chunk, not the edited cell | `281794f`/`9bccca6`/`34fa8d0`: cell-carrying `Action<ChunkEdit>` (ADR-0013) raised once per applied edit; all .NET and Unity consumers updated |
| #59 | Unity | TD-017 Chebyshev-1 remesh superset rebuilt identical neighbours | `9bccca6`: the precise dirty set drives the remesh queue (1 vs 27 EditMode, 7 vs 9 PlayMode) and the superset/`NotifyEdit` path is removed |
| #59 | Unity | TD-018 boot replay double-applied a stored delta; overlay counters could mislead | `9bccca6`: the stored delta is applied once and the merged map skipped (`EditsSkippedAlreadyApplied`); counters fixed, two-boot test |
| #59 | Unity | TD-019 `FileWorldStore` fallback log wording implied an unexpected failure | `c676cae`: fallback log wording corrected |
| #59 | Unity | TD-020 IMGUI paint paths had no automated coverage | `c676cae`: virtual paint seams with fake events and recording subclasses assert layout without exceptions; headless caveat documented |
| #59 | Unity | TD-021 failed world-store writes were not retried or re-dirtied | `0b61195`: bounded retry with backoff, `WriteFailed` + `SaveBatches` re-dirty; forced-failure-then-success tests |
| #59 | Unity | TD-022 calibration look direction pinned only at `SyntheticPoseDrive` | `3529ff4`: `CalibrationRigDirectionTests` pins the camera yaw sign for a scripted turn |
| #59 | Unity | TD-023 `PluginUnavailable` was set but unread; the plain fallback stayed silent | `c676cae`: overlay plugin row shows `PluginUnavailable`/fallback reason with a text-helper test |
| #59 | Unity | TD-024 `GameplayBridge.SaveRequested` was a write-only diagnostic | `9bccca6`: removed; the `EditApplied`/`PreciseEdits` stream drives `SaveBatches`, asserted in PlayMode |
| #59 | Unity | TD-025 scene hash pins are git-blob hashes with no documented recipe | `6379bca`: `SceneHashVerifier` menu/batch with LF-normalised SHA-256 and the recipe in s7-gate §7.1; pins unchanged |
| #59 | Unity | TD-026 `MotionVignette` drew one full-screen rect across the SBS pair | `c676cae`: per-eye rects at 3840x1080 with tiling/no-overlap assertions |
| #59 | Unity | TD-027 package descriptions were stale | `3529ff4`: both package descriptions updated to shipped behaviour |
| #59 | Release | TD-056 non-Windows store path `File.Replace` was never-lands-but-safe | `0b61195`: POSIX `rename(2)` overwrite parity with `MoveFileEx(REPLACE_EXISTING)` documented in code |
| #59 | Unity | TD-057 synthetic pose fallback still reported `Stable` while active | `861bf7a`: fallback reports `TrackingState.Lost`; gameplay/overlay tests updated |
| #59 | .NET | TD-058 `BlockRegistry.Parse` accepted out-of-range atlas indices and opaque air | `a85c464`: opaque air throws `FormatException`; negative atlas indices covered; the upper bound stays a mesh-time check by design |
| #59 | Unity | TD-060 deferred Unity minors M-3/M-5/M-8/M-9/M-10 | `9bccca6`/`6379bca`: bounded async delta load, generated palette material, scene verifier (M-8), id-scrubbed canonicalizer (M-9), one-frame read-back + `MoveMainWindowTo` (M-10); hardware display residual -> TD-084 |
| #59 | Security | TD-062 save-path robustness residuals (`.cgdl` size cap, device names, reparse points, `GITHUB_ENV` hygiene) | `0b61195`: `.cgdl` size cap, reserved device/trailing-dot-space name rejection, reparse-point refusal for world dir + delta reads, all tested; ignored symlink test -> TD-077 |
| #59 | .NET | TD-068 deferred .NET minors M9/M10 | `158777e`/`281794f`: `AbiVersion.Value` tracks `CG_ABI_VERSION` with a drift test; cached subscriber list with per-handler isolation + `SubscriberFaulted` |

Fixed minors kept for traceability (not open, not rowed above): C++ M-5, M-6
(future-heartbeat half; page-granular remainder is TD-008), M-8, M-10
(`/permissive-`/`/utf-8`; visibility remainder is TD-010), M-11, plus the TSan
fake-observable fixes (#37/#41); .NET F2, F3, F4 (#38); Unity budget
hermeticity, remesh leak, plugin warning, HUD/game-window/calibration fixes
(#40); infra M-5, M-6, M-7, M-8 and the round-2 multi-report/ABI-comment fixes
(#42).
| #60 | C++/native | TD-004 test-writer exports in the production bridge DLL | Fixed: split into cg_bridge_test_support.dll; dumpbin verifies the production DLL exports only the five bridge symbols |
| #60 | CI/tooling | TD-030 MSVC /analyze | Fixed: CG_ENABLE_ANALYZE lane over core-math/glasses/bridge/test-support; findings fixed; clean from-scratch analyze build |
| #60 | Release | TD-067 (duplicate of TD-004) | Closed with TD-004 |
| #63 | Unity | TD-077 ignored symlink-refusal test | Fixed: replaced by a junction-based DeltaPathReparsePointIsRefusedOnLoad probe; EditMode 200/200 with zero skips |
| TD-086 | Release/HIL | S8 HIL deferred 2026-10-07: left-hand probe + `a1-left` session done (U-02/U-03/U-05 answered with committed artefacts), but the right/both-hand passes, the 20-minute dataset and the G-A labels are pending, so U-06/G-A stay open | Medium | s8-gate; S8-HIL.md | `stage-8-complete` withheld | Owner: finish the remaining scripts and labels per docs/questions/S8-HIL.md |

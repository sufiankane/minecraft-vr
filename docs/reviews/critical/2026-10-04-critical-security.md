# Critical security, supply-chain, test-rigour and operability review — Cubeglass

- **Date:** 2026-10-04
- **Tree reviewed:** `docs/full-docs` @ `0d3ecb6` (same tree as `main` `0d3ecb6`), working tree had uncommitted sibling docs edits (`CONTRIBUTING.md`, `README.md`, untracked `docs/ARCHITECTURE.md`, `docs/CHANGELOG.md`, `docs/ONBOARDING.md`); none reviewed as committed content.
- **Mode:** read-only. Nothing was modified or fixed. The only file written is this report (`.superpowers/` is git-ignored).
- **Intent read:** dossier §4.x, §9, §10, §11, §12; ADR-0003, 0005, 0009, 0010, 0012; previous review `.superpowers/reviews/2026-10-03-infra.md` (its ABI-gate/perf-gate items are fixed; only persisting residuals are re-raised here, labelled).
- **Evidence run locally:** `python -m pytest` → **78 passed**; `python -m depcheck --root .`, `depcheck contracts`, `depcheck licences` → all pass at HEAD; `git ls-remote` tag verification for all 7 pinned actions; `Test-Path docs/perf/nightly-baseline.json` → **False**; targeted source reads with line citations below. `actionlint` is **not present** in the repo or on PATH (docs only mention it; no workflow runs it — see M-6).
- **Severity counts:** **Critical 1, Important 11, Minor 21.**

---

## 1. Findings — Critical

### C-1. The required contract-compatibility gate is bypassable by hand-editing `contracts/abi-baseline.json`; no ABI-version bump is needed and all six required checks stay green

- **Severity:** Critical (gate integrity; enables silent ABI drift → P/Invoke type confusion).
- **Location:** `python/depcheck/contracts.py:167-196` (check), `:126-150` (baseline load), `contracts/abi-baseline.json`; gate runs in the required `depcheck` job (`.github/workflows/ci.yml:324-345`).
- **Attack/failure scenario (confirmed by code):**
  1. Author changes a frozen contract file (e.g. reorders/retitles a field in `contracts/cg_types.h`) without touching `CG_ABI_VERSION`.
  2. `check_contracts` computes `state.fingerprint` from the four contract files and compares **only** `state.fingerprint == baseline.fingerprint` (`contracts.py:180`). The baseline's own `files` hashes are only used for the diagnostic text (`changed_files`, `:159-164`); they are never recomputed or cross-checked against `state.files`.
  3. The author overwrites the `fingerprint` string in `contracts/abi-baseline.json` with the new value (computed with the same normaliser from `contracts.py:83-123`, trivially reproducible). `abiVersion` stays 2 and `files` may be left stale.
  4. `state.fingerprint == baseline.fingerprint` → `[]` violations → required `depcheck` job green, with **no version bump and no visible signal**. `--update` refuses in this case (`:210-215`), but direct JSON editing bypasses that guard because nothing validates the baseline file against itself or git history.
  5. A mixed Unity/native pair can then ship against a mismatched layout; the shm header check only rejects differing `abi_version` values (`cpp/bridge/src/bridge.cpp` open path), so a same-version layout drift is not caught at runtime either.
- **Status:** **Confirmed** (logic is four lines; no test or negative fixture covers a hand-edited baseline — `python/tests/test_depcheck_contracts.py` only pins the API path).
- **Mitigation:** make the gate independent of the PR's editable copy: compare `compute_state(root)` against the baseline read from the **merge base** (`git show <base>:contracts/abi-baseline.json`), and allow the in-PR baseline to change only when `CG_ABI_VERSION` strictly increases; additionally recompute the baseline fingerprint from its own `files` map and require `state.files == baseline.files` so a bare `fingerprint` edit is self-inconsistent. Add a negative fixture for a hand-edited baseline.

---

## 2. Findings — Important

### I-1. Shared memory has no writer authentication and the reader trusts payload content

- **Severity:** Important (trust boundary; same-user attacker → logic corruption / DoS, latent hand-data injection).
- **Location:** `cpp/bridge/src/bridge.cpp:267-320` (open/map, size check `:286`), `:153-207` (head/hands seqlock, no payload validation), `contracts/cg_types.h` (fixed layout), `unity/Cubeglass/Packages/com.cubeglass.rendering/Runtime/LateLatchPose.cs:170-179`, `unity/.../com.cubeglass.bridge/Runtime/NativeBridge.cs:169-179`.
- **Scenario (confirmed/suspected split):** any process of the same Windows user can create `Local\cubeglass.v1.state` first and fill it with a valid magic/ABI/header. The reader never checks `writer_pid` (the field exists and is written, never verified) and never validates samples. Confirmed consequences: arbitrary **yaw/pitch rotation** and tracking-state spoofing of the player camera (LateLatch consumes rotation + state), and a hostile writer can keep `seq_a` odd or bouncing to force bounded timeouts. Hands are **currently inert** (no runtime caller of `TryReadHands`; only tests) — latent for S13. Quaternion NaN/zero is neutralised both sides (`dotnet/src/CoreMath/Quat.cs:48-53`, `cpp/core-math/include/cg/core_math/quat.hpp:38-40`), so no memory unsafety is reachable; position floats are unvalidated but head position is ignored by `LateLatchPose`. `MapViewOfFile(...,0,0,0)` maps the **whole** writer-created section with no upper bound (`bridge.cpp:272`; POSIX maps full `fstat` size `:305-314`), so a hostile 10 GB section inflates reader VA — memory exhaustion is suspected but not measured.
- **Mitigation:** authenticate the writer (validate `writer_pid` maps to the expected signed executable, or a per-session nonce handshake); cap the accepted section size; sanitise at the boundary (finite components, normalised quaternion, enum state, monotonic sequence) and degrade to `TrackLost` with a once-per-session warning instead of consuming hostile data; re-validate header magic/ABI on read (TD-003).

### I-2. Vendor DLL is loaded unsigned/unhashed with the default search order and an untested positive path

- **Severity:** Important (code-execution surface / ABI mismatch UB).
- **Location:** `cpp/glasses/src/viture_loader.cpp:80-85` (`LoadLibraryW(path)`), `:118-119` (`dlopen`), `:204-214` (`GetProcAddress`→`memcpy`), `:323-341` (`LoadVitureApi`); path source `cpp/tools/pose_probe/main.cpp:626-632` (`--dll` or `CG_VITURE_DLL`); placeholder binding documented in `docs/adr/0009-glasses-adapter-pose-semantics.md:178-187`; tests only cover error paths (`cpp/tests/glasses/viture_loader_tests.cpp:21-35`).
- **Scenario:** if `--dll`/`CG_VITURE_DLL` is a bare or relative name, `LoadLibraryW` searches application dir, system dirs, **current directory and `PATH`** — a same-user process can plant `viture_glasses_sdk.dll` in the working directory (the probe is typically run from a repo/Downloads dir) and get code execution as the user, and for the future S12 service in the game process. No hash/signature check, no absolute-path requirement, no `LOAD_LIBRARY_SEARCH_*` flags. The symbol names/signatures are acknowledged placeholders; a vendor or impostor export with the wrong arity can corrupt state (x64 unifies calling convention, so this is type confusion rather than stack smash), and the loader's success path has no test at all.
- **Status:** Exploitability of the path hijack **confirmed by code** where a relative path is used; vendor-signature mismatch **suspected** until HIL.
- **Mitigation:** reject non-rooted paths; `LoadLibraryExW` with `LOAD_LIBRARY_SEARCH_SYSTEM32 | LOAD_LIBRARY_SEARCH_APPLICATION_DIR | LOAD_LIBRARY_SEARCH_USER_DIRS` and no cwd; verify an Authenticode signature or a committed SHA-256 of the vendor DLL; bump/verify an SDK version gate before binding; add a test that loads a fake library built by the test suite and proves success + the missing-symbol diagnostic.

### I-3. Release job holds repo-write token in every step and persists it into `.git/config` while running an unpinned CDN script

- **Severity:** Important (supply-chain: one compromised remote script gets repo write).
- **Location:** `.github/workflows/release.yml:94-95` (`permissions: contents: read` top), `:111-112` and `:405-406` (job `contents: write`), `:114-116`/`:408-410` (checkout without `persist-credentials: false`), `:222-223` (unpinned `https://public-cdn.cloud.unity3d.com/hub/prod/cli/install.ps1` downloaded and executed); TD-040 accepts the unpinned script but not token reach.
- **Scenario:** `actions/checkout` defaults to `persist-credentials: true`, writing an `http.extraheader` bearer token into the workspace `.git/config`. Any later step — including the remote installer script and every build script — can read and exfiltrate a `contents: write` token for this repository. The token is needed only by the final `gh release upload` step.
- **Status:** **Confirmed** (GitHub Actions default + workflow text).
- **Mitigation:** `persist-credentials: false` on both checkouts; job `contents: read` with write granted only to the attach step (or a separate job); vendor/hash the installer (TD-040); consider an Environment with required reviewers for release.

### I-4. Release artefacts have no checksums or signatures, and the attach step can clobber published assets

- **Severity:** Important (release integrity; dossier gate 10 unmet).
- **Location:** `.github/workflows/release.yml:346-374` (hosted `Compress-Archive` → `gh release upload … --clobber`), `:578-615` (self-hosted same), no checksum step anywhere; TD-038 ("Trigger: first v0.1.0 dispatch" — the trigger has now fired: run 37198879954 produced the artefact).
- **Scenario:** a user downloading `Cubeglass-v0.1.0-win-x64.zip` cannot verify it is the CI-built bytes; an unsigned `.exe` gives SmartScreen no publisher and no integrity story. `--clobber` lets any writer who can dispatch the workflow (any ref) silently replace a previously published release asset with a build from a modified branch.
- **Status:** **Confirmed.**
- **Mitigation:** emit `<asset>.sha256` files, list the digest in the release notes, sign the player (at least an Authenticode cert when available); drop `--clobber` or gate the attach on `github.ref` being `refs/heads/main`/the release tag.

### I-5. The self-hosted release test gate is weaker than the hosted one and can pass with a suite that did not really run

- **Severity:** Important (test gate that can silently not enforce).
- **Location:** `.github/workflows/release.yml:535-545` (self-hosted: only `$LASTEXITCODE`), versus `:288-320` (hosted: parses NUnit XML, requires `total > 0`, fails on `failed > 0`, `skipped > 0`, missing required assemblies).
- **Scenario:** on the route that has actually been verified end-to-end (ADR-0012 run 37198879954), a `unity test` invocation that exits 0 while running zero tests of a required assembly (e.g. an accidental filter/`testables`/asmdef change, or an ignored-test regression) passes the release gate. Skipped tests also do not fail the self-hosted route. The hosted route would catch all of these but has never run (TD-045).
- **Status:** **Confirmed.**
- **Mitigation:** factor the hosted XML assertions (or `scripts/ci-local.ps1`'s `Assert-UnityResults`) into a shared step and run it in both jobs; keep the `-output <results.xml>` flag in the self-hosted route.

### I-6. The nightly performance-regression gate currently compares nothing, and its .NET half can silently skip

- **Severity:** Important (perf gate not enforcing; persisting part of fixed C-2).
- **Location:** `.github/workflows/nightly.yml:165-185`; `docs/perf/nightly-baseline.json` **does not exist at HEAD** (`Test-Path` false); `python/depcheck/__main__.py:39` passes with "baseline established" when missing; TD-033. The .NET comparison is wrapped in `if [ ${#bdn[@]} -gt 0 ]` (`nightly.yml:177-185`) with `nullglob`, so an empty/renamed BDN artefact directory silently disables **all** .NET regression checks (the C++ one always runs).
- **Scenario:** a 10 %+ regression in any .NET benchmark (Voxel/Mesh/CoreMath) is invisible until someone commits a baseline; if the artefact glob ever breaks, only the C++ half fails. Byte-identical benchmark names are required for comparison, so renamed/added benchmarks are never checked.
- **Status:** **Confirmed.**
- **Mitigation:** commit the captured baseline (owner action) and make its absence fail once the capture exists; make the BDN empty-glob a hard error; consider comparing on stable runners and extending the p95 `--budget-ms` gate to the game frame budget (I-11).

### I-7. A corrupted/hostile save can carry unknown block ids into the renderer, which throws out of the frame tick

- **Severity:** Important (file-parser → runtime robustness; same-user/disk-corruption vector).
- **Location:** `dotnet/src/Voxel/ChunkDeltaCodec.cs:132-166` (accepts any `blockId` except `0xFFFF`, no registry knowledge — by design), `unity/.../rendering/Runtime/FileWorldStore.cs:275-290` (returns the delta unvalidated), `ChunkViewManager.cs:640-653` (applies it to the world), `dotnet/src/Mesh/GreedyMesher.cs:162-165` → `AmbientOcclusion`/`MesherBuild` calling `IBlockRegistry.Get`, `unity/.../SliceBlockRegistry.cs:55-67` (**throws `KeyNotFoundException`**), `StreamingRuntime.cs:197-206` (`Tick` has no catch) → `ChunkViewManager.cs:421-464`/`:475-...` rethrow after releasing the view.
- **Scenario:** a `.cgdl` file with `blockId` 6..0xFFFE (tampered `%LOCALAPPDATA%\…\saves\default\*.cgdl`, or bit rot) parses successfully, is applied, and then every mesh attempt for that chunk throws; Unity logs the managed exception each frame, the chunk never renders, and because loaded deltas are merged into `SaveBatches` and re-persisted, the invalid id survives further saves. The game is wedged for that chunk until the save is deleted; no user-visible recovery. (Symptom severity is suspected — Unity catches MonoBehaviour exceptions and continues — but the throw path is confirmed.)
- **Status:** **Confirmed path; suspected runtime impact.**
- **Mitigation:** validate stored edits against the active registry at load (reject unknown ids with a warning and substitute Air, or refuse the delta), make `Get` total / add `TryGet`, and catch per-chunk build failures in `OnUpload`/`ProcessDirtyRemeshes` so one bad chunk cannot abort a tick.

### I-8. Required CI is currently unavailable and two commits in the reviewed history were admin-merged with branch protection bypassed

- **Severity:** Important (operational security control not enforcing).
- **Location:** `docs/notes/tech-debt.md:86-87` (TD-045 hosted Actions billing block; TD-046 admin merges of #49/#50), `docs/ONBOARDING.md:517-519` ("While TD-045 is open, merges are not check-gated").
- **Scenario:** since 2026-10-04 09:49Z the six required job contexts cannot start on GitHub-hosted runners. The reviewed HEAD contains an admin-merged CI change (#49) and an admin-merged docs change (#50). The only independent verification of #49's tree is the self-hosted release run 37198879954; the docs change has no check. A future admin merge of a non-docs commit would land code that no required check has seen.
- **Status:** **Confirmed** by the register and onboarding docs (branch-protection state itself lives outside the repo and cannot be audited from this checkout).
- **Mitigation:** restore billing; re-run the blocked workflows and re-verify the admin-merged commits through the normal lane (TD-046 trigger); stop admin merges until then; add an Environment with required reviewers on the release workflow so a release cannot be produced from an unreviewed ref; consider an external status check (e.g. self-hosted runner lane) as a temporary required check.

### I-9. Vulnerability scanning has blind ecosystems and a marker-text fail condition

- **Severity:** Important (supply chain).
- **Location:** `.github/workflows/nightly.yml:193-229`: `dotnet list ... --vulnerable` detection depends on the English string `has the following vulnerable packages` (`:215`), which a tool/localisation change silently defeats while exiting 0; `pip-audit -r python/requirements-dev.txt` (`:226-227`) does not cover the inline `gcovr==8.6` install in the required job (`ci.yml:150`), `python/pyproject.toml` project installs, the vcpkg dependencies `benchmark`, `gtest`, `nlohmann-json`, `rapidcheck` (`cpp/vcpkg.json:6-11`) or the Unity package set (`unity/Cubeglass/Packages/manifest.json:1-52`).
- **Status:** **Confirmed.**
- **Mitigation:** parse `dotnet list package --vulnerable --format json` (or fail on any non-zero and match a stable token), add an OSV/vcpkg-plus-Unity scan pinned to the same baseline/lock, and include `gcovr` in the audited requirement set.

### I-10. The dependency-rule gate has confirmed parsing bypasses and completeness blind spots

- **Severity:** Important (required gate bypass; lower blast radius than C-1).
- **Location:** `python/depcheck/rules.py:46` (`_INCLUDE_RE` — a comment between `#include` and the header, `#include_next`, `#import`, or a macro include all evade), `:47-50`/`:163-178` (`_USING_RE` matches only using-directive lines, so fully-qualified `System.IO.File…` in a pure module is never seen), `:226-237`/`:240-250` (completeness scans only `cpp/*/CMakeLists.txt` and `dotnet/src/*/*.csproj`, so a header-only C++ module or a project nested one directory deeper escapes `layers.json`).
- **Scenario:** `#include /* gap */ <windows.h>` or `#include_next <thread>` in `cpp/core-math` passes the required `depcheck` job; a `System.IO.File` call in `Cubeglass.Voxel` (no `using`) passes purity while violating ADR-0005's intent. Pinned tests only cover the documented patterns.
- **Status:** **Confirmed** (regexes and scans read).
- **Mitigation:** strip comments and use a token-level scan (or clang AST) for includes; add a banned-token check for fully-qualified forbidden namespaces and `Task.Run`/`async`/`File.` in Voxel; recurse both completeness scans and require any directory containing sources under `cpp/`/`dotnet/src` to be configured.

### I-11. The product's 90 Hz frame budget is recorded evidence only; the only enforced performance numbers are the mesh p95 and the currently inert nightly comparison

- **Severity:** Important (test-system gap: the core comfort/safety claim has no automated falsifier).
- **Location:** `unity/.../Tests/PlayMode/FrameBudgetPlayModeTests.cs:30-32` ("evidence, not a hard CI gate, so the test does not fail when the budget is missed"), `GameFrameBudgetPlayModeTests.cs:37-41` (same), `docs/adr/0010-unity-version-pipeline-and-bridge-layout.md:101-105`, `docs/perf/README.md:43-57` (mesh p95 8 ms shared-runner / 2.0 ms local; baseline missing per I-6).
- **Scenario:** a change that doubles stereo frame time passes every required check, the Release gate, and the nightly (baseline absent / p95 unaffected); only the HIL playtest would notice. A hostile reviewer can keep hurting 90 Hz while staying green.
- **Status:** **Confirmed.**
- **Mitigation:** add a generous absolute ceiling (e.g. fail above a multiple of 11.1 ms) or a committed frame-budget baseline with the same capture-once discipline as benchmarks, keeping HIL as the acceptance gate.

---

## 3. Findings — Minor

| ID | Location | Finding | Status | Mitigation |
|---|---|---|---|---|
| M-1 | `unity/.../FileWorldStore.cs:287` | `File.ReadAllBytes` on every `.cgdl` with no size cap: a hostile/corrupt multi-GB save forces a proportional allocation (OOM/process kill). The codec's exact-length rule does not help because the buffer is already allocated. | Confirmed | Reject files above a small maximum (format bounds allow ≤ ~28 KB per chunk delta) before reading. |
| M-2 | `FileWorldStore.cs:613-631`, `:141-142`, `:486-515` | World-name validation rejects separators/invalid chars but not Windows reserved device names (`CON`, `NUL`, `COM1`…), trailing dots/spaces (name collisions on NTFS); no reparse-point/symlink check on the world directory or temp path, so a junction redirects writes/TOCTOU. | Confirmed | Reject reserved names and trailing dot/space; resolve and verify the final path stays under the saves root; use `FileOptions`/`GetFinalPathNameByHandle` if hard guarantees are needed. |
| M-3 | `unity/.../StereoRigConfig.cs:104-112` (`Near`/`Far`/`TargetRefresh` at `:40-43`) | `JsonUtility.FromJsonOverwrite` accepts any numbers; nothing enforces `0 < near < far`, finite values, or a sane refresh. Latent: `LoadFromJson` has no runtime caller today (TD-014). | Confirmed (latent) | Validate and clamp in `LoadFromJson`/setters; keep the test-only seam honest so wiring TD-014 cannot ship an inverted projection. |
| M-4 | `cpp/bridge/src/test_writer.cpp:100-125`; `cpp/bridge/CMakeLists.txt` (`WINDOWS_EXPORT_ALL_SYMBOLS`) | `cg_test_writer_*` raw-injection exports ship inside the production `cg_unity_bridge.dll`; `cg_test_writer_create` memsets an existing region created by another process without checking magic, a production footgun. | Confirmed (TD-004) | Split the test writer into a test-only DLL or compile-gate it; at minimum require magic/ABI before wiping a region. |
| M-5 | `docs/ci/negative-gates.md:26-30` | Negative gates self-test only format/pytest/depcheck. Contract, licence, coverage and benchregress rejection behaviour is pinned only by unit tests inside the same Python job (soft coupling). | Confirmed | Add frozen negative fixtures for the contract gate (including the C-1 hand-edit case) and benchregress. |
| M-6 | `ci.yml:69-71`, `:83-85` | Both clang-format and clang-tidy exit 0 on an empty file list ("No C++ sources to check"), so a selector regression silently disables the format/static-analysis gate. `actionlint` is documented (`docs/ci.md:157-162`, ADR-0012:187-188) but wired nowhere. | Confirmed | Fail on zero files; add an actionlint job (consider pinning the action SHA) or a pinned actionlint binary. |
| M-7 | `python/depcheck/coverage_check.py:49-72`; `ci.yml:232-246` | A module matching with zero coverable lines passes with a WARNING (TD-032); several reports in a results dir pick the first. A silently emptied test assembly that still emits a report cannot be distinguished from a covered one except by test count. | Confirmed (TD-032) | Fail when `floor > 0 and total_lines == 0`; fail on multiple reports; assert a minimum test count per required assembly in the Unity gate (I-5). |
| M-8 | `cpp/tools/soak/glasses_soak.cpp:9-10, 72`; `nightly.yml:84` | Soak is an RSS-leak gate only (>1 MiB growth fails); no thread/handle assertions, no latency threshold, not registered with `ctest` (TD-007). | Confirmed (TD-007) | Add thread/handle-count asserts and a short ctest soak variant. |
| M-9 | `dotnet/stryker-config.json:13-17`; `nightly.yml:247` | Mutation testing covers `Cubeglass.Voxel` only at break 70 (TD-031), runs nightly only, so mutation score regressions never block a merge and Gameplay (the rule hotspot) is unscored. | Confirmed (TD-031) | Add a Gameplay Stryker config; consider a per-PR budget on changed files. |
| M-10 | `docs/ci.md:284-287`; `release.yml:535-545` | Unity EditMode/PlayMode suites never run on PRs (ADR-0012 makes per-PR CI secret-free by design). A PR that breaks the C# bridge/rig only fails at release dispatch (or never, per I-5). | Confirmed (residual of I-6 from the 2026-10-03 review) | Run the Unity suite on the self-hosted runner on PRs/labels, or at least require a successful dispatch before tagging; keep ci-local as the documented local lane. |
| M-11 | `docs/ci.md:36-40`; `ci.yml:150`; `scripts/bootstrap-dev.ps1` | Python deps are version-pinned but not hash-pinned; `gcovr==8.6` is installed inline; bootstrap uses unpinned `winget install` for developer machines (TD-028). | Confirmed (TD-028) | `--require-hashes` lock via pip-compile/uv; pin the installer artifacts or accept and record. |
| M-12 | `contracts/golden/transforms.json`; `dotnet/tests/CoreMath.Tests/GoldenFixtureTests.cs:10-14`; `python/depcheck/contracts.py:34` | The shared golden fixture is a cross-language **consistency** oracle (C++ and C# run the same cases), not an independent one: a coordinated change of the fixture and both implementations passes every gate, and the fixture is not in `CONTRACT_FILES`. `GoldenSessionTests` (`dotnet/tests/Streaming.Tests/GoldenSessionTests.cs:11-24`) is a generated regression pin, not a spec oracle. | Confirmed | Mark the fixture as dossier-transcribed in review; optionally pin a hash of the fixture in an ADR-adjacent check or extend the contract gate to cover it when semantics change. |
| M-13 | `python/depcheck/licences.py`; `docs/ci.md:45-57` | The licence gate proves declared-name → allowlist-entry + SPDX-id shape only; it never inspects the actual package licence, its transitive set, the Unity manifest or vcpkg baseline, so a mis-allowlisted copyleft dependency passes. | Confirmed | Add a periodic manual/automated licence inventory (e.g. `dotnet list package --include-transitive`, vcpkg x-manifest licenses, Unity manifest) as a review artefact. |
| M-14 | `docs/releases/v0.1.0.md:58-59` | Verification numbers are stale: "471 .NET tests, 45 Python tests"; the Python suite is **78** at this HEAD. | Confirmed | Re-state the numbers when the release is actually cut; add a note that fix waves changed the counts. |
| M-15 | `docs/adr/0012-ci-unity-licensing-and-release-routes.md:153-159`; no mention of an expiry date anywhere in the repo | No licence-expiry/rotation runbook. The owner's Unity Student seat expires **2027-10-07**; after that both hosted modes fail (offline `.ulf` and serial) and the self-hosted Hub activation fails its build guard. Runtime players are unaffected. | Confirmed (repo absence); date from task input | Add a register row/calendar reminder with the renewal steps and a fallback (renew Student, buy Plus, or move to a floating licence server). |
| M-16 | `docs/notes/tech-debt.md:88` (TD-047); `release.yml:401` | The self-hosted runner runs as a session-scoped background process: release capability dies with the Kilo session, and an interruption mid-build leaves the staged (git-ignored) plugin tree behind. | Confirmed (TD-047) | Install the runner as a service (`svc.cmd install`) as the register suggests; the job already cleans/re-stages plugins. |
| M-17 | `ci.yml:126-130` | The TSan step lowers `vm.mmap_rnd_bits=28` system-wide on the runner (reduced ASLR entropy) for the remainder of the job. Negligible on an ephemeral hosted runner; do not copy this into a self-hosted job. | Confirmed | Scope the change to the test process environment/container, or document it. |
| M-18 | `ci.yml:34-40`; `release.yml:164-169` | `actions/cache` restore-keys fall back to a prefix, so a vcpkg-installed tree produced under a different manifest revision can be restored; poisoning requires repo write and the bridge links no third-party libs, so impact is low. | Suspected (low) | Key strictly on the manifest hash or verify the restored tree's baseline stamp; avoid caches feeding release if feasible. |
| M-19 | `release.yml:492-493` (self-hosted `GITHUB_ENV` append); serial activation step `:262-276` | Computed paths are appended to `GITHUB_ENV` without newline sanitisation (runner-local only); the optional serial is passed as a command-line argument, visible to local process inspection during the hosted activation window. | Confirmed (low) | Prefer env/stdin for the serial; validate path values before writing `GITHUB_ENV`. |
| M-20 | `dotnet/tests/Voxel.Tests/ChunkDeltaFuzzTests.cs:16-18` | Fuzzing is seeded and bounded (1000 random arrays + 2048 flips, deterministic); there is no coverage-guided/corpus evolution and no fuzz corpus for the shm reader, the replay CSV parser or the registry JSON. | Confirmed | Schedule a longer fuzz run or add a libFuzzer/AFL corpus for the binary and CSV parsers. |
| M-21 | `docs/notes/tech-debt.md:82-85` (TD-041..TD-044) | The three HIL logs and the v0.1.0 player build are still pending; the release notes are an RC. The reviewed HEAD's product claims (comfort, 90 Hz on hardware, input, save/reload) are therefore unverified on the target device. | Confirmed (register) | Owner actions already listed in the register; treat as the top playtest risk, not a code defect. |

---

## 4. Threat-model deep dives

### 4.1 Shared memory (`Local\cubeglass.v1.state`)

Layout is fixed at offsets (`contracts/cg_types.h`; ABI 2; 64-byte header, head slot at 64, hand slot at 256, minimum 848 bytes). The reader maps read-only and never computes an index or offset from data, so **no out-of-bounds read is reachable** — this closes the classic hostile-offset class.

What a hostile same-user writer **can** do (all confirmed unless noted):

| Vector | Reachable? | Effect |
|---|---|---|
| Spoof `magic`/`abi`/`header_size` | Yes, reader validates these (rejects wrong ABI; 50 ms retry) | None; only `CG_ERR_UNSUPPORTED`/`NOT_READY`. |
| Torn/bouncing `seq_a`/`seq_b` | Yes | Bounded retries (64) → timeout; no hang (reads never block). |
| Wrong-shaped payload (NaN/Inf, huge floats) | Yes | Rotation NaN normalised to identity on both sides; position unvalidated but ignored by rendering; hands unconsumed. Future risk at S13. |
| Arbitrary head rotation / tracking state | Yes (`writer_pid` unverified) | Camera view and comfort state (TrackLost/Stable) fully controlled. |
| Heartbeat far future | Yes | Defeats the 250 ms stale rule (state stays Stable). |
| Huge section (no size cap) | Suspected | Reader maps gigabytes of VA; potential allocation pressure/DoS rather than commit. |
| Command channel (`cg_bridge_send_command`) | Yes from writer's perspective | The game can be made to see command ack values; commands are write-only from the reader, not consumed yet. |

The reader's seqlock is correct (double `seq_a` acquire check, `seq_b`, release/acquire; `bridge.cpp:153-207`), and the writer-side test tool publishes magic last with release order. Bounded waits: 50 ms open, 64 retries, 100 ms poll cap. The residual risk is **authenticity, not memory safety**: same-user spoofing (which for a normal desktop game is not a privilege boundary, but for a device-style product could mislead safety diagnostics) and the unbounded section size.

### 4.2 Vendor DLL (`CG_VITURE_DLL`)

- Path source: `--dll` / `CG_VITURE_DLL`, default empty → `InvalidArgument` (no silent bare-name default). Passing a relative path opens the default `LoadLibraryW` search (cwd + PATH) — code execution as the user; no signature/hash pin.
- Symbol table is placeholder until HIL (ADR-0009); a wrong-arity or differently-named export surfaces as `Unsupported` for a missing symbol, but a same-named wrong-arity export is undetectable and is UB/type confusion. Calling convention is not pinned (x64 Windows unifies it; 32-bit is not a target).
- Positive binding path is untested (`viture_loader_tests.cpp:21-35` only error paths), so a regression in the `GetProcAddress`/`memcpy` glue would only show at HIL.
- The probe tool is the only current caller; the S12 service will inherit this boundary in the game process.

### 4.3 P/Invoke vs native writer

- Sizes/offsets match the C layout to the byte: `BridgeHeadSample` 48, `BridgeHand` 272, `BridgeHandFrame` 576 with hands at 28/300 (`unity/.../BridgeTypes.cs`, `cg_types.h`), pinned by `ShmLayout` C++ tests (`cpp/tests/bridge/layout_tests.cpp` — offsets, padding, constants) and by C# field-wise round-trip tests through the real native writer (`NativeBridgeTests.HeadRoundTripPreservesEveryField`, `HandsRoundTripPreservesEveryJointAndVelocity`).
- Residual: no machine check ties the C# explicit offsets to the C header in required CI — the round-trip tests only run in the Unity suite (M-10/I-5). `CG_ABI_VERSION` is checked at open, but the mirrored C# `AbiVersion.Value` tracks **core-math's** ABI 1, not `CG_ABI_VERSION` 2 (separate constants, separately pinned, no cross-language check) — confusing but not a defect today.

### 4.4 File-parsing surfaces

| Surface | Bounds/validation | Residual risk |
|---|---|---|
| Save codec `ChunkDeltaCodec` | magic/version/reserved, `entryCount ≤ 4096`, exact length (no trailing bytes), `runLength` clipped to remaining cells, never throws, fuzz+property+golden tests | Unknown block ids pass into the world (I-7); caller allocates the whole file first (M-1). |
| Block registry JSON (`dotnet/src/Voxel/Content/blocks.json` + `BlockRegistry.Parse`) | Embedded, commit-time data; parser rejects malformed/duplicate/missing-Air; atlas indices only checked `>= 0` (no upper bound against the atlas) | A bad committed entry yields out-of-atlas UVs (not runtime-untrusted). The Unity mirror is a separate code-built registry kept in sync by a drift test. |
| Replay pose CSV (`cpp/glasses/src/replay_head_pose_source.cpp`) | Each field parsed with `from_chars` + `isfinite`; rejects non-monotonic/invalid timestamps; no NaN/Inf; quaternion components finite (degenerate → identity via `PoseFromSdk`) | Unbounded row vector and line length: a multi-GB CSV allocates proportionally (local asset/tool vector). No fuzz corpus. |
| Config JSON (`StereoRigConfig.LoadFromJson`) | `JsonUtility.FromJsonOverwrite`, IPD/FOV clamped on set | Near/Far/Refresh unvalidated (M-3); no runtime caller yet. |
| Gameplay scenario JSON | Test-only (`dotnet/tests/Gameplay.Tests/scenarios/*.json`) | None at runtime. |
| World names | Rejects separators, `GetInvalidFileNameChars`, `.`/`..` | Reserved device names, trailing dot/space, symlink/junction redirection (M-2). |

### 4.5 SDK / pose / gesture trust

- SDK stream → `VitureHeadPoseSource`: clock mapped with a median offset (jump-resistant); reconnect re-seeds so published time cannot regress; prediction capped (`std::clamp`); quaternion degenerate input → identity. Wall-clock jumps are irrelevant (`SteadyHostClock`, `steady_clock` monotonic).
- Shm → Unity: `LateLatchPose` consumes provider samples each frame, sanitises through `Quat.FromComponents`, ignores head position; tracking state is passed through (spoofable, I-1). Hand frames are read by nothing at runtime yet.
- Gesture data does not exist in the codebase yet (S13), so no gesture-trust surface is reachable today.

---

## 5. CI/CD security assessment

**Triggers and secrets.** `ci.yml` triggers `pull_request` + `push` to `main`; `nightly.yml` `schedule` + `workflow_dispatch`; `negative-gates.yml` `workflow_dispatch` only; `release.yml` `workflow_dispatch` only. No `pull_request_target`, no `workflow_run`. Secrets appear **only** in the two release jobs (`release.yml:121-122, :262-263`), never in PR-triggered workflows; they are passed through `env` and never interpolated into `run:` text. Fork PRs therefore get no secrets and a read-only token.

**Permissions.** `ci.yml:8-9`, `nightly.yml:8-9`, `negative-gates.yml:10-11` are `contents: read`. Release jobs hold `contents: write` job-wide (I-3). `github.token` is passed explicitly only to the attach steps (`release.yml:359, :591`).

**Third-party action pinning — verified.** All 7 pins resolve to the claimed tags (`git ls-remote`, 2026-10-04):

| Action | Pinned SHA | Tag resolves to | Verdict |
|---|---|---|---|
| actions/checkout | `3d3c42e5…90b1` | `refs/tags/v7.0.1` = same | ✓ |
| actions/cache | `55cc8345…52a9` | `refs/tags/v6.1.0` = same | ✓ |
| ilammy/msvc-dev-cmd | `0b201ec7…2756` | annotated tag `v1.13.0` object `a102174a…` peels to the pinned commit | ✓ |
| actions/setup-python | `5fda3b95…b97` | `refs/tags/v7.0.0` = same | ✓ |
| actions/setup-dotnet | `a98b5685…c68` | `refs/tags/v6.0.0` = same | ✓ |
| actions/upload-artifact | `043fb46d…6a0a` | `refs/tags/v7.0.1` = same | ✓ |
| actions/download-artifact | `3e5f45b2…1e7c` | `refs/tags/v8.0.1` = same | ✓ |

`game-ci` is gone; the Unity CLI is installed by a pinned `-Target` binary but via an **unpinned CDN script** (TD-040, I-3). Outside release, the workflows use only `actions/*` plus the pinned msvc-dev-cmd action. Dependabot is configured for actions/nuget/pip (`dependabot.yml`) — no vcpkg ecosystem exists.

**Runner trust (self-hosted).** Release jobs check out the **dispatched ref** (`fetch-depth: 0`) and execute repository scripts and a Unity build method from it on the owner's machine, under the runner user, with a job-wide `contents: write` token. Dispatch requires write permission, so this is a "compromised collaborator/account" threat, not an anonymous one, but the mitigation hierarchy is weak: no Environment protection/required reviewers, no allow-list of refs, no `persist-credentials: false`. The machine also holds the owner's Hub licence (per-user) and is a general dev box (TD-047, M-16). Recommended: Environment with required reviewers + branch/tag allow-list, no secrets used by the job (already true), a dedicated low-privilege runner user (already the Hub user), and workflow-run approval for outside collaborators (not applicable to a single-owner private repo but cheap).

**Logging/masking.** Secret values are not echoed; `::add-mask` is implicitly applied by GitHub for secret-backed values in logs. The offline `.ulf` is written to `%RUNNER_TEMP%` on the hosted runner and left there until the ephemeral runner is destroyed — acceptable. The optional serial reaches the CLI as an argument (M-19). The self-hosted route uses no Unity secrets at all, which is a genuine security advantage.

**Artefact integrity.** See I-4: no checksums/signatures, `--clobber`, no per-ref attach guard.

**Admin-merge bypass.** See I-8.

---

## 6. Supply chain

- **Pinning:** vcpkg `builtin-baseline` pinned (`cpp/vcpkg.json:5`), NuGet centrally versioned (`Directory.Packages.props`), pip version-pinned (`python/requirements-dev.txt`) but not hash-pinned (TD-028/M-11); `gcovr==8.6` inline; .NET SDK pinned with `latestPatch` roll-forward (`dotnet/global.json`); Unity editor 6000.6.3f1 and CLI 1.0.0-beta.11 pinned in release. Caches keyed on the manifest hash with prefix fallback (M-18).
- **Vulnerability scanning:** NuGet (transitive) + pip `--require-hashes`-less audit; no vcpkg, no Unity packages, no inline tooling; English-marker fail condition (I-9).
- **Licence allowlist:** name→SPDX-shape only, no licence verification and no transitive/Unity coverage (M-13); declared set is permissive and test-only deps (FsCheck, gtest, BenchmarkDotNet) are correctly allowlisted.
- **Contract gate:** strong intent (normalised fingerprints, updater refuses without a version bump) but **bypassable** (C-1) and covers exactly four files (TD-039); the golden fixture and `abi-baseline.json` itself are outside the fingerprint. The ADR requirement is review-only by design (ADR-0003).
- **Gate bypass resistance overall:** `depcheck`'s include/using parsing has confirmed evasions (I-10); the contract gate has C-1; the licence gate is declarative; the coverage gate passes vacuous modules (M-7). The negative-gates workflow proves only three gates still reject broken input (M-5).

---

## 7. Test-system rigour

### 7.1 Suite inventory — what runs where, and what can silently not run

| Suite | Runs where | What it proves | Silent-skip risk |
|---|---|---|---|
| C++ gtest: core-math (unit+golden), glasses (source/fault/contract/replay), bridge (layout/reader/stress) — ctest 5 targets | Required `cpp-windows` (`ctest --preset ci`), required `cpp-linux-asan` (ASan/UBSan), TSan preset | Math/convert golden parity, pose filtering/recentre/clock, shm layout/seqlock/torn rejection, CSV replay, threading races | Low. Zero-match impossible (targets are explicitly added); TSan `sysctl` step fails if it cannot run (M-17). |
| C++ property tests (rapidcheck) / allocation counters | Same ctest binaries | Properties + no-allocation hot paths | Low; generator count implicit in target. |
| .NET NUnit: CoreMath, Voxel, Mesh, Gameplay, Streaming | Required `dotnet` job, per-project `dotnet test` + coverage | Units, property (FsCheck fixed seeds), fuzz (ChunkDelta), golden hashes, allocation, scenario JSON | **Medium:** a new test project added to the sln is built by the solution build but not tested unless a CI step is added; removing/filtering a suite that still emits a coverage report is only caught by the floor (M-7). |
| Python pytest (78 tests) | Required `python` job (`ruff`, strict `mypy`, pytest + coverage) | depcheck rules/contracts/licences/coverage/benchregress, fixture parsing, malformed inputs | Low; pytest exits 5 on zero collection. |
| Unity EditMode (112) + PlayMode (44) | **Only** release dispatch (both routes) and local `ci-local.ps1`; never PRs | Bridge P/Invoke round-trips/stress, rig math, scene composition, persistence, frame-budget evidence | **High on self-hosted:** exit-code-only gate (I-5); no per-assembly/zero/skipped check. |
| Fuzz | `ChunkDeltaFuzzTests` (seeded, mutation/truncation/random), no corpus fuzz for shm/CSV/registry | Codec total-function property | Low but narrow (M-20). |
| Golden | Shared `contracts/golden/transforms.json` (C++ + C#), mesh/scene/streaming hashes | Cross-language conversion consistency; regression pins | Fixture is editable with both sides (M-12); scene hashes are git-blob based (TD-025). |
| Mutation | Nightly Stryker, `Cubeglass.Voxel`, break 70 | Test-suite kill power for Voxel | Nightly-only; Voxel-only (M-9). |
| Soak | Nightly 30 min `glasses_soak` (RSS growth > 1 MiB fails) | Leak bound | Not in ctest; no handle/thread asserts (M-8); nightly itself is currently blocked (I-8). |
| Benchmarks | Nightly bench-cpp, bench-dotnet + p95 `--budget-ms 8`; benchregress vs baseline | Throughput regression; mesh p95 | Baseline missing → compares nothing; .NET half glob-skippable (I-6). |
| Coverage floors | Required cpp/dotnet/python jobs via `depcheck coverage` | 95 % core-math; 90 % other modules; missing/mismatched module fails | Zero-line module warning passes (M-7). |
| Negative gates | `negative-gates.yml`, dispatch-only | That format/pytest/depcheck reject deliberately broken fixtures, with diagnostic-signature matching | Only 3 gates; workflow can rot unrun by design (M-5). |
| Required CI as a whole | GitHub-hosted | Merge gate | **Currently not running** (I-8). |

### 7.2 Falsifiability and flake handling

- **Falsifiability:** negative gates assert both a non-zero exit *and* the expected diagnostic, so a crashed tool is not mistaken for a rejection — good. Unit tests are mostly discriminating (e.g. the harness fixes from #37-#43 include pre-fix failing witnesses). Weak spots: frame-budget tests cannot fail on the budget (I-11); the golden fixture is not an independent oracle (M-12); the contract-gate tests never exercise a hand-edited baseline (C-1).
- **Flake handling:** no `[Ignore]`/`[Explicit]`/`pytest.skip`/`xfail` anywhere in the test trees (grep); FsCheck and the fuzzers use fixed seeds; the C++ fault suite uses manual clocks/self-advancing predicates (#41); the Unity PlayMode budget tests are wall-clock but evidence-only; the nightly 10 % threshold on shared runners is acknowledged as noisy (TD-034). No retry/rerun machinery exists, which is the right choice for a safety-style gate (flakes surface instead of being masked) as long as the suite is deterministic, which it now largely is.
- **Fail-closed properties:** pytest zero-collection (exit 5), coverage missing/mismatched module, ctest empty preset, `unity test` output-missing, package-cache/restore failures, hostile shm headers — all fail closed.

### 7.3 What can silently not run, in one list

1. Self-hosted Unity suite's actual content (I-5).
2. `docs/perf/nightly-baseline.json` comparison and the whole .NET regression half (I-6).
3. Nightly as a whole while hosted Actions are blocked (I-8).
4. clang-format/clang-tidy if the file selector yields zero files (M-6).
5. Coverage floors for a module with zero instrumentable lines (M-7).
6. Licence/vulnerability coverage of undeclared ecosystems (I-9, M-13).
7. Contract gate after a baseline fingerprint hand-edit (C-1).
8. Any .NET test project added without a matching CI step (M-10 family).
9. Negative gates unless someone dispatches them (M-5).

---

## 8. Operability under failure

| Condition | Current behaviour (confirmed unless noted) | User-visible outcome / recovery | Gap |
|---|---|---|---|
| Disk full during save | `FileWorldStore.WriteOne` catches, counts `FailedWrites`, temp is deleted, destination untouched; `FlushResult` surfaces failure and `SaveBatches` logs (`FileWorldStore.cs:486-530`, `SaveBatches.cs:298-360`). | Warning in the log; previous save intact; next batch retries. | No on-screen message (TD-021/023); repeated failure silently accumulates lost edits until quit. |
| Permission denied (saves dir) | Same catch path; `Directory.CreateDirectory` throws in the constructor (`:141-142`) — **not** caught there. | Constructor exception surfaces at boot; store not created; game presumably runs without persistence. | Boot path should degrade gracefully with a visible reason; suspected UX gap (constructor not read in caller context). |
| Corrupted save chunk | `LoadAsync` → `TryDeserialize` false → treated as "no delta", chunk regenerates from seed; warning logged (`ChunkViewManager.cs:625-637`). Unknown **ids** are the unhandled case (I-7). | Edited chunk silently reverts to terrain; bad id path spams exceptions and never renders. | No user-visible "save recovered/ignored" notice; no quarantine file. |
| Killed process during save | Temp+`File.Replace` means the destination is atomic: the old save survives the kill; the in-flight batch is lost (≤32 edits / 2 s). Fallback delete+move has a small non-atomic window (`FileWorldStore.cs:499-515`). | At most the last batch is lost with no crash marker on next boot. | No journal/recovery notice; document the loss window. |
| Clock jumps (NTP/manual) | Steady clocks throughout (`SteadyHostClock`, Unity frame time, native `steady_clock`); `ClockMapper` median offset; stale rule uses monotonic ns. | No visible effect. | None found; verified sound. |
| Runner interruption | Hosted: job dies, ephemeral runner discarded; self-hosted: job dies, workspace may hold staged git-ignored plugins, caches persist (M-16); the release tag/asset is only written at the end, so a killed run publishes nothing. | Re-run dispatch rebuilds. | Self-hosted runner stops when the session ends (TD-047). |
| Licence expiry (2027-10-07) | Hosted activation or self-hosted build guard fails with an actionable message; no player is produced; the previous artefact stays available. | Release pipeline blocked until renewed. | No runbook/alert (M-15). |
| Vendor DLL missing/symbol mismatch | `LoadVitureApi` → `Unsupported` naming the path/symbol; headless/replay paths unaffected; Unity warns + `PluginUnavailable` (TD-023). | Pose features degrade; game remains playable. | No in-game visibility of the flag (TD-023). |
| Shm region absent/stale | `CG_ERR_NOT_READY` after a 50 ms open retry; stale >250 ms forces `TrackLost`; Unity falls back to synthetic/plain pose (TD-023). | Silent fallback for `NotReady`. | Surface the no-writer state (TD-023). |

---

## 9. Verified-sound areas

- **Action pinning** — all 7 SHAs resolve to the claimed tags, including the annotated `ilammy` tag's peeled commit (table in §5); no floating tags anywhere; game-ci removed.
- **Workflow trigger safety** — dispatch-only release and negative gates; no `pull_request_target`/`workflow_run`; PR/nightly/negative jobs run `contents: read` with no secrets; release secrets only in dispatch jobs, passed via `env`.
- **shm reader protocol** — fixed-offset layout, read-only mapping, correct seqlock with double-check, bounded retries/waits, ABI/magic/size rejection, stale rule, stress-tested with 8/4 concurrent readers; no data-derived offsets.
- **P/Invoke layout** — byte-exact explicit offsets and sizes pinned by C++ layout tests and C# field-wise round-trip tests through the real native writer.
- **Save codec** — strict header/version/length/count validation, exact cell coverage, no trailing bytes, never throws, mutation/truncation/random fuzz + property + fixed fixture; atomic publication on the normal path.
- **Pose/clock pipeline** — quaternion degenerate/NaN → identity on both sides; monotonic steady clocks; jump-resistant clock mapper; capped prediction; reconnect cannot publish time regressions.
- **Determinism/flake** — seeded property/fuzz generators, manual clocks in fault tests, no ignored/quarantined tests; negative-gate jobs assert diagnostics, not just exit codes.
- **Local gate reproduction** — 78/78 pytest, `depcheck`, `depcheck contracts`, `depcheck licences` all pass at HEAD; CI-local mirrors the required lanes.
- **Unity route design** — pinned CLI/editor/SDK, seat returned with `if: always()`, no account credentials anywhere, self-hosted needs no Unity secrets.

## 10. What needs a run (cannot be settled by reading)

1. `cpp-windows` ctest and the Unity suites at this HEAD on a clean machine (the six required checks have not run since TD-045; only the self-hosted release exercised the tree).
2. `dotnet test` + coverage floors and `clang-format`/`clang-tidy` after the C-1/I-10 mitigations if implemented.
3. A hostile-writer shm harness (create the region with a huge size, NaN payloads, bouncing sequence, spoofed `writer_pid`) to confirm I-1's suspected memory-pressure and behaviour.
4. A fake vendor DLL exporting the documented symbols to exercise the loader success path and wrong-arity behaviour (I-2).
5. A corrupted-save fixture with `blockId` 6..0xFFFE against a real player build to confirm the per-frame exception/symptom (I-7).
6. A dispatch of the **hosted** release route (never verified end-to-end) and a self-hosted run with the full XML assertions (I-5).
7. A committed nightly baseline to turn the regression gate on and measure its noise (I-6/TD-034).
8. Renewal/expiry rehearsal for the Unity licence before 2027-10-07 (M-15).

## 11. Existing register cross-reference (open debt that overlaps this review)

TD-003 (header re-validation — I-1), TD-004 (test writer shipped — M-4), TD-007 (soak — M-8), TD-021/023 (save/plugin UX — §8), TD-028 (hash pinning — M-11), TD-029/030/031 (static analysis/mutation scope — M-9), TD-032 (coverage vacuity — M-7), TD-033/034 (baseline/noise — I-6), TD-038 (checksums — I-4), TD-039 (contract file coverage — C-1/I-10), TD-040 (Unity installer — I-3), TD-041..044 (HIL/release — M-21), TD-045/046 (CI blocked/admin merges — I-8), TD-047 (runner persistence — M-16). Nothing above duplicates a **fixed** item from the 2026-10-03 infra review; I-6/I-10 style residuals are explicitly labelled where the earlier fix left a persisting hole.

## 12. Finding index

- **Critical (1):** C-1 contract-gate baseline fingerprint hand-edit.
- **Important (11):** I-1 unauthenticated shm writer; I-2 unsigned/unpinned DLL load; I-3 release write-token + unpinned CDN script; I-4 no artefact checksums/signatures + `--clobber`; I-5 weak self-hosted Unity gate; I-6 inert nightly perf gate + skippable .NET half; I-7 unknown block ids from saves throw in the mesh path; I-8 CI unavailable + admin-merge bypass; I-9 scanning blind spots + marker-text gate; I-10 dependency-rule parser bypasses; I-11 frame budget unenforced.
- **Minor (21):** M-1…M-21 as tabulated.

# Cubeglass — Second Critical Review (2026-10-04)

**Standard applied.** This review was run as if the software were a life-saving
medical device: every defect is assumed able to injure the wearer (disorientation,
lost tracking), corrupt user data, or brick the product; "Minor" means it
still gets fixed before shipping. Four independent domain reviewers worked in
parallel over the whole tree at `main` `edf66b1`, then each fix wave was
re-verified by the reviewer who raised the finding.

**Method.** Read-only reviews with adversarial probes (the .NET reviewer built
scratch harnesses in `%TEMP%`; the security reviewer reproduced the contract-gate
bypass on a scratch copy). Every Important/Critical fix landed with a test that
fails pre-fix. The original reviewers verified each wave and two items were sent
back for a second round (the contract-gate bypass and the include-scan evasions).

## Findings and outcome

| Domain | Critical | Important | Minor | Status |
|---|---:|---:|---:|---|
| C++ / native (`2026-10-04-critical-cpp.md`) | 0 | 4 | 17 | all Importants fixed and verified; selected minors fixed; rest in TD |
| .NET (`2026-10-04-critical-dotnet.md`) | 0 | 3 | 10 | all fixed and verified; Unity mirror fixed in the Unity wave |
| Unity / integration (`2026-10-04-critical-unity.md`) | 0 | 3 | 11 | all fixed; five follow-ups from verification fixed too |
| Security / CI / tests (`2026-10-04-critical-security.md`) | 1 | 11 | 21 | the Critical and all Importants fixed across two rounds and verified |
| **Total** | **1** | **21** | **59** | **0 open Critical/Important** |

### The Critical

- **Contract gate bypass** (`python/depcheck/contracts.py`): the §9.7 ABI gate
  compared fingerprints to a baseline an attacker can edit, so a consistent
  rewrite of `abi-baseline.json` admitted silent layout drift toward P/Invoke
  type confusion. Fixed by anchoring expected fingerprints in **code**
  (`KNOWN_FINGERPRINTS`, `KNOWN_ABI_VERSIONS`); the baseline is bookkeeping and
  cannot admit drift. The attack test now fails pre-fix; `--update` refuses
  without a code-constant edit. Verified.

### Notable Important findings (all fixed; commit in parentheses)

- **Wearer-facing**: NaN/Inf could reach camNear/Far/FOV/IPD and rig transforms,
  producing blank or garbage frames (`5a06797`, `efc51dc`); the pose-bridge probe
  ran once at boot and never retried, silently synthesising tracking behind the
  wearer's back (`168b7eb`, `2dc2edc`); recentre arming could be clobbered by a
  concurrent failure, losing a recentre during an outage (`2a6c7ac`, `b97b92d`);
  the recentre correction could pair a new offset with an old validity sequence
  for one frame, glitching yaw (`b97b92d`).
- **Data integrity**: `FileWorldStore` did not `Flush(true)` before replacing and
  used delete-then-move, exposing a missing save window; now flush + atomic swap
  with a verified old-or-new guarantee under concurrent loads (`6587501`).
- **Crashes / hangs**: unknown block ids from a corrupted save threw out of the
  frame tick (`5c23159`, `cd34db8`); `VoxelCollision` hung on an extreme box and
  `CanPlace` wrapped at `int.MaxValue` (`a8a272b`); an invalid `StreamingConfig`
  could wrap the desired-set loop (`e39fe6c`); hostile DLL names could be planted
  in the working directory (`9b6e51b`).
- **Gate vacuity**: the self-hosted release gate checked only the process exit
  code, so zero tests/skips could pass — the hosted NUnit assertions are now
  shared and called from both routes (`e6ae13d`); the nightly benchmark
  comparison could silently compare nothing (`a78d8fd`).
- **Concurrency (native)**: a display call could race `Start`/`PollPose` in the
  vendor seam (`2a6c7ac`); the soak RSS gate could false-PASS on a failed query
  and missed sub-peak growth on Linux (`04f75dc`).
- **Detection**: include/using scans were evadable (comment splicing, line
  continuations, string literals, fully-qualified names, macro includes); now
  literal-aware with recursive module discovery (`ee95e40`, `41073ac`).

### Fix waves and verification

- `fix/critical-review` — waves: C++ `2a6c7ac..cae93d4`, .NET `a8a272b..d7058b6`,
  Unity `168b7eb..bc541b4`, CI/security `c6f1283..18884f7` and `41073ac..9690761`.
- Verification: every original reviewer re-checked their wave; the security
  reviewer's first re-check rejected the C-1 fix and the scan evasions and a
  second round closed them; the Unity reviewer's follow-ups (R-1..R-5) were
  fixed and re-run.
- Local gates at the branch head: C++ Debug 6/6 + `windows-release` 6/6 (+300×
  race repeats), .NET 524/524, Python 114/114 + ruff/mypy, depcheck/contracts
  (coverage 92.96 %, floor 90), licences, Unity EditMode 152/152 + PlayMode
  50/50 with scene hashes unchanged, `ci-local -SkipUnity` ALL LANES PASS.
- CI checks could not run on GitHub at this time (billing block, TD-045); the
  self-hosted release route was verified earlier in run 37198879954.

## Residuals and new debt

All residuals are registered in `docs/notes/tech-debt.md` (`TD-048` onward):
shared-memory trust boundary, DLL signature/search residuals, vcpkg/Unity
advisory scans, nightly baseline lifecycle, `--clobber`, non-Windows Replace
fallback, synthetic-`Stable` requirement for S12, textual-scan limitations
(interpolation holes, string-built names), `#if 0` false positives, POSIX
dependency-search suppression, registry parse validation, refused-swap save
loss, C++ CXX-11/12/14/16/17/18/21, Unity M-3/M-5/M-8/M-9/M-10.

**Verdict: no open Critical or Important findings; the software is fit to
proceed to the owner HIL gates, where the U-08-dependent Stop-blocking bound and
the on-glasses comfort/vision checks are the remaining external evidence.**

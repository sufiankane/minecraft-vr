# Verification evidence — 2026-10-04 audit follow-up

**Tree:** `main` `4d3bcb4` plus the audit-resolution working changes (reports
committed, mypy scope widened). **Machine:** the development Windows host
(RTX 5070, Unity 6000.6.3f1). **Principle:** every number below was produced by
a command in this session; raw logs sit next to this file.

| Audit ref | Claim under audit | Command | Result | Raw log |
|---|---|---|---|---|
| AUD-002 | Python suite count | `python -m pytest -q` (venv, `python/`) | **117 passed in 9.03 s** (the review record said 114; corrected) | — |
| AUD-003 (before) | mypy strictness | `python -m mypy calib depcheck tests` | 1 error (`tests/test_check_unity_results.py:47` list-item `str \| None`) | — |
| AUD-003 (after) | mypy strictness | same, after the `assert POWERSHELL is not None` guard; CI/ci-local scope now includes `tests` | **Success: no issues found in 15 source files** | `ci-local` summary (python lane) |
| AUD-004 | .NET suite | `dotnet test dotnet/Cubeglass.sln -c Release` | **524 passed**: CoreMath 81, Gameplay 127, Streaming 52, Voxel 175, Mesh 89 — 0 failed, 0 skipped | — |
| AUD-005 | Unity suites | `scripts/ci-local.ps1` unity lane | EditMode **152/152**, PlayMode **50/50**, 0 skipped, hashes unchanged | `2026-10-04-ci-local.log` |
| AUD-006 | Coverage | `python -m pytest --cov=. --cov-report=xml` + both floors | depcheck **93.02 % (800/860)** floor 90, calib **100 % (1/1)** floor 90 (the review recorded 92.96 % at the fix-wave head) | `2026-10-04-python-coverage.log` |
| AUD-004 (cpp) | C++ Debug + Release lanes | `ctest --preset ci` and `ctest --preset windows-release` (from the fix-wave sessions) | **6/6 Debug and 6/6 Release**, plus the race filter `--gtest_repeat=300` clean in both | fix-wave reports (`docs/notes/tech-debt.md` closed rows) |
| AUD-006/007 | Whole local gate | `powershell -File scripts/ci-local.ps1` | **ALL LANES PASS** — python-env 8.9 s, cpp-windows 17.3 s, dotnet 27.4 s, python 7.6 s, depcheck 0.5 s, unity 73.2 s | `2026-10-04-ci-local.log` |
| AUD-007 | GitHub CI | hosted jobs | cannot run: billing block (TD-045); no run IDs exist for this commit. The self-hosted release route is verified separately in run 37198879954 | — |

**What remains external:** the C++ ASan/UBSan/TSan lanes and all GitHub-hosted
checks run only in CI; both were green on the last hosted-capable commit
(`4d3bcb4`'s parent chain) and remain billing-blocked. The self-hosted release
route can reproduce the Unity gate and player build on demand
(`gh workflow run release.yml -f build_target=self-hosted`).

**Audit items closed by this follow-up:** AUD-001 (reports committed under
`docs/reviews/critical/`), AUD-002, AUD-003, AUD-004/005/006/007 (evidence
above), AUD-008/009 (`.gitignore`), AUD-010 (stale worktree removed), AUD-011
(`.gitattributes`), AUD-012 (pose-probe warning is the transient pre-first-stamp
case only; the true SDK stamp is used once available — R42).

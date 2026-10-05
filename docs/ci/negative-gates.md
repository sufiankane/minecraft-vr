# Negative gates (expect red)

This document describes `.github/workflows/negative-gates.yml`. Its jobs are
**self-tests for the CI gates**: each one feeds a deliberately broken fixture to
a real gate and passes only when that gate correctly rejects it. A green run
here is evidence that the positive gates still fail on bad input.

## Why dispatch-only

The workflow declares only:

```yaml
on:
  workflow_dispatch:
```

It has no `push` or `pull_request` trigger and therefore never runs in normal
CI. This is deliberate: the fixtures under `scripts/negative/` are *supposed* to
be broken, and if they were wired into the required checks the red semantics
would be inverted. Keeping the workflow dispatch-only means the negative
self-tests can never block a legitimate merge, while still being runnable on
demand from any branch.

## Jobs and fixtures

| Job | Gate under test | Fixture | Green when |
| --- | --- | --- | --- |
| `expect-red-format` | pinned `clang-format --dry-run --Werror` (LLVM 23.1.2) under the repo `.clang-format` | `scripts/negative/fixtures/fail-format.cpp` | `clang-format` exits non-zero with `clang-format-violations` |
| `expect-red-test` | `python -m pytest` | `scripts/negative/fixtures/fail-test.py` | `pytest` exits non-zero with `1 failed` |
| `expect-red-depcheck` | `python -m depcheck --root <root>` | `scripts/negative/depcheck-root/` | `depcheck` exits non-zero with `forbidIncludes` |
| `expect-red-contracts` | `python -m depcheck contracts --root <root>` | `scripts/negative/contracts-root/` | the gate exits non-zero naming `KNOWN_FINGERPRINTS` |
| `expect-red-licences` | `python -m depcheck licences --root <root>` | `scripts/negative/licences-root/` | the gate exits non-zero with `ghost-package` and `unknown SPDX licence id` |
| `expect-red-coverage` | `python -m depcheck coverage` | `scripts/negative/fixtures/coverage-below.xml`, `coverage-zero-lines.xml` | the gate exits non-zero with `observed 50.00%` and with `0 coverable lines` (TD-032) |
| `expect-red-benchregress` | `python -m depcheck benchregress --baseline-from` | `scripts/negative/fixtures/bench-baseline.json`, `bench-current.json` | the gate exits non-zero naming `cpp/BM_A` with `+30.0%` |

`scripts/negative/depcheck-root/` is a miniature scan root: it contains its own
`contracts/layers.json` (listing a `core-math` `forbidIncludes` rule) and
`cpp/core-math/forbidden-include.cpp`, which violates that rule by including
`<thread>`. The real `python -m depcheck --root .` gate is untouched.

`scripts/negative/contracts-root/` carries byte-identical copies of the five
real contract artefacts with one extra `#define` appended to `cg_types.h`. The
failure must therefore be the hardcoded `KNOWN_FINGERPRINTS` anchor — not a
missing file and not a missing baseline — which is what proves the anchor, and
not the bookkeeping baseline, rejected the drift.

`scripts/negative/licences-root/` declares `ghost-package` in `cpp/vcpkg.json`
(it is absent from the allowlist) and gives the declared `NUnit` the bogus SPDX
id `BSD` (not a known identifier), so one run exercises both licence-gate
failure modes.

Each job inverts the gate result explicitly and also asserts on the gate's
diagnostic, so a crashed or misconfigured tool (which also exits non-zero) can
never be mistaken for a rejection. For example:

```bash
set +e
output=$(python -m depcheck coverage --report scripts/negative/fixtures/coverage-below.xml --module core-math --floor 90 2>&1)
status=$?
set -e
if [ "$status" -eq 0 ]; then
  echo "::error::depcheck coverage accepted the below-floor fixture; the gate is not enforcing."
  exit 1
fi
if ! echo "$output" | grep -q "observed 50.00%"; then
  echo "::error::depcheck coverage exited $status but reported no 50.00% observation; treating as a tool error, not a rejection."
  exit 1
fi
```

If a gate unexpectedly *passes* its bad fixture, the job exits `1` and the
workflow run turns red — that is the signal that the gate has stopped
enforcing. If the gate fails but without its expected diagnostic signature
(e.g. a missing tool or import/config error), the job also exits `1`, because a
non-zero exit alone does not prove a rejection. When both conditions hold, the
job exits `0` (the job is green) with an `OK:` line.

`expect-red-format` installs the pinned clang-format from the hash-pinned
`python/requirements-ci.txt` and asserts its version before the fixture check,
so the self-test cannot silently switch to the distro clang-format. The
positive `cpp-windows` job has the matching assertion and both its format and
tidy selectors now **fail loudly when they find zero files** (TD-063), so an
empty selector can no longer produce a vacuous green.

## How to run

From a checkout, on any branch:

```bash
gh workflow run negative-gates.yml --ref <branch>
gh run watch --exit-status
```

Expected result: every `expect-red-*` job **succeeds**. A red job means the
corresponding gate failed to reject its fixture.

## Reproduce locally

```powershell
# Format gate must fail
clang-format --dry-run --Werror scripts/negative/fixtures/fail-format.cpp

# Test gate must fail
python -m pytest scripts/negative/fixtures/fail-test.py

# Dependency gate must fail with a "path:line rule" violation
python -m depcheck --root scripts/negative/depcheck-root

# Contract gate must fail against the anchored fingerprints
python -m depcheck contracts --root scripts/negative/contracts-root

# Licence gate must fail with both diagnostics
python -m depcheck licences --root scripts/negative/licences-root

# Coverage gate must fail below the floor and on a zero-line module
python -m depcheck coverage --report scripts/negative/fixtures/coverage-below.xml --module core-math --floor 90
python -m depcheck coverage --report scripts/negative/fixtures/coverage-zero-lines.xml --module Cubeglass.Voxel --floor 90

# Regression gate must fail and name the benchmark
python -m depcheck benchregress --baseline-from scripts/negative/fixtures/bench-baseline.json --format google --prefix cpp --current scripts/negative/fixtures/bench-current.json
```

Using the project virtual environment, substitute
`python/.venv/Scripts/python.exe` for `python`.

## Notes

- The fixtures live under `scripts/negative/`, outside every build glob and
  outside the `python/` test suite (`testpaths = ["tests"]`), so they never
  enter normal builds or the positive `python` job.
- The workflow must stay dispatch-only. Do not add `push` or `pull_request`
  triggers.

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
| `expect-red-format` | `clang-format --dry-run --Werror` under the repo `.clang-format` | `scripts/negative/fixtures/fail-format.cpp` | `clang-format` exits non-zero |
| `expect-red-test` | `python -m pytest` | `scripts/negative/fixtures/fail-test.py` | `pytest` exits non-zero |
| `expect-red-depcheck` | `python -m depcheck --root <root>` | `scripts/negative/depcheck-root/` | `depcheck` exits non-zero |

`scripts/negative/depcheck-root/` is a miniature scan root: it contains its own
`contracts/layers.json` (listing a `core-math` `forbidIncludes` rule) and
`cpp/core-math/forbidden-include.cpp`, which violates that rule by including
`<thread>`. The real `python -m depcheck --root .` gate is untouched.

Each job inverts the gate result explicitly and also asserts on the gate's
diagnostic, so a crashed or misconfigured tool (which also exits non-zero) can
never be mistaken for a rejection. For example:

```bash
set +e
output=$(clang-format --dry-run --Werror scripts/negative/fixtures/fail-format.cpp 2>&1)
status=$?
set -e
if [ "$status" -eq 0 ]; then
  echo "::error::clang-format accepted the mis-formatted fixture; the format gate is not enforcing."
  exit 1
fi
if ! echo "$output" | grep -q "clang-format-violations"; then
  echo "::error::clang-format exited $status but emitted no diagnostic; treating as a tool error, not a rejection."
  exit 1
fi
```

If a gate unexpectedly *passes* its bad fixture, the job exits `1` and the
workflow run turns red — that is the signal that the gate has stopped
enforcing. If the gate fails but without its expected diagnostic signature
(e.g. a missing tool or import/config error), the job also exits `1`, because a
non-zero exit alone does not prove a rejection. The required signatures are
`clang-format-violations`, `1 failed`, and `forbidIncludes` respectively. When
both conditions hold, the job exits `0` (the job is green) with an `OK:` line,
and the `expect-red-depcheck` job also prints the `path:line rule` violation it
observed.

## How to run

From a checkout, on any branch:

```bash
gh workflow run negative-gates.yml --ref <branch>
gh run watch --exit-status
```

Expected result: `expect-red-format`, `expect-red-test` and
`expect-red-depcheck` all **succeed**. A red job means the corresponding gate
failed to reject its fixture.

## Reproduce locally

```powershell
# Format gate must fail
clang-format --dry-run --Werror scripts/negative/fixtures/fail-format.cpp

# Test gate must fail
python -m pytest scripts/negative/fixtures/fail-test.py

# Dependency gate must fail with a "path:line rule" violation
python -m depcheck --root scripts/negative/depcheck-root
```

Using the project virtual environment, substitute
`python/.venv/Scripts/python.exe` for `python`.

## Notes

- The fixtures live under `scripts/negative/`, outside every build glob and
  outside the `python/` test suite (`testpaths = ["tests"]`), so they never
  enter normal builds or the positive `python` job.
- The workflow must stay dispatch-only. Do not add `push` or `pull_request`
  triggers.

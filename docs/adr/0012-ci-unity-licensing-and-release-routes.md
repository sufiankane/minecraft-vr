# 0012. CI Unity licensing and the release build routes

- Status: accepted
- Date: 2026-10-04
- Deciders: Sufyan Khan (owner)
- Consulted: Stage S7 build and infra agents
- Informed: all later-stage builders and reviewers

## Context and problem statement

The v0.1.0 Windows x64 player is built by `.github/workflows/release.yml`,
which is dispatch-only (R49): the `build_target` input picks one of two jobs
and the other job is skipped. The `hosted` job (default) runs on GitHub-hosted
`windows-latest`; the `self-hosted` job runs on the owner's Windows machine,
whose Unity editor is already activated through the Unity Hub.

Every route needs an activated Unity editor before the release test gate or
the player build can run, and the activation modes differ in what they require
and what they can leak. The owner's account is Unity Personal, Hub-activated:
the pinned Unity CLI (`1.0.0-beta.11`) rejects
`unity license activate --personal` when it runs with service-account tokens,
and manual activation is deprecated for Hub-activated accounts. An unattended
route therefore cannot reuse the owner's own activation, which is why the
self-hosted route exists. The decision was implemented in PRs #44 and #45 and
is recorded here before the first v0.1.0 dispatch.

In scope: the two dispatch routes, the credential mode each accepts, the test
gate both run, and the rule that per-PR CI stays secret-free. Out of scope:
the Unity version and editor pin (ADR-0010) and the test pyramid itself
(ADR-0003).

## Decision drivers

- Unattended activation must not need account email/password credentials, and
  a job should hold only the secret its licence mode needs.
- The owner's Unity Personal licence is Hub-activated, so the hosted route
  needs an activation mode that works from a secret alone.
- The player must be built by the pinned Unity CLI and editor (ADR-0010), not
  by a third-party build action.
- Tagged releases are test-gated: the EditMode and PlayMode suites run before
  the player build on both routes (I-6).
- Per-PR CI must stay secret-free, so Unity tests run in the release workflow
  and locally through `scripts/ci-local.ps1`, never on pull requests.
- A serial activation is a seat and must be returned even when the job fails
  or is cancelled.

## Considered options

- **Hosted runner, pinned Unity CLI, `UNITY_LICENSE` alone (offline `.ulf`).**
  Chosen for Personal/Student licences: the release gate writes the secret to
  `Unity_lic.ulf` and runs `unity license activate --file`; no account
  credentials are involved.
- **Hosted runner, pinned Unity CLI, `UNITY_SERIAL` alone.** Chosen for
  Plus/Pro/Education serials: the release gate runs
  `unity license activate --serial` and returns the seat at the end of the
  job.
- **`game-ci/unity-builder` with the licence secrets.** Rejected: it puts a
  third-party build action between the repository and the Unity build and
  needs the `UNITY_LICENSE`/`UNITY_EMAIL`/`UNITY_PASSWORD` account-token trio
  that the offline and serial modes do not.
- **`unity license activate --personal` with Unity Cloud service accounts.**
  Rejected: the CLI refuses `--personal` when it runs with service-account
  tokens, so it cannot activate the owner's Personal licence unattended.
- **Floating licence server (`unity license activate --floating`).** Rejected
  for now: it needs a reachable Unity licence server that this project does
  not run, so the workflow does not wire it in.
- **Self-hosted Windows runner on the licensed machine.** Chosen as the second
  route: the machine's own Hub activation is the licence, so the job needs no
  Unity secrets; `runs-on: [self-hosted, windows]` selects the runner.

## Decision outcome

Chosen: a dispatch-only release with two routes (R49). The hosted route runs
the pinned Unity CLI itself and accepts exactly one credential mode -
`UNITY_LICENSE` alone (offline `.ulf`) or `UNITY_SERIAL` alone (serial), never
an email/password pair. The self-hosted route runs on the Hub-activated
machine and needs no Unity secrets. Both routes run the EditMode and PlayMode
suites before the player build, and per-PR CI stays secret-free.

### Hosted route (`windows-x64-player`, `windows-latest`)

The `hosted` job is the default (`build_target: hosted`) and runs on
`windows-latest`. Its preflight requires one credential mode and fails before
any build step with an actionable message when neither secret is set:

- `UNITY_LICENSE` alone - the contents of a Personal/Student `.ulf` exported
  from [license.unity3d.com/manual](https://license.unity3d.com/manual); the
  gate writes it to `Unity_lic.ulf` and activates with
  `unity license activate --file`.
- `UNITY_SERIAL` alone - a Plus/Pro/Education serial; the gate activates with
  `unity license activate --serial`.

When both secrets happen to be set, the offline `.ulf` mode is used; the
preflight is a one-credential-per-mode guard, not a two-secret login.

The job installs the pinned Unity CLI (`1.0.0-beta.11`) and editor
(`6000.6.3f1`), activates the licence as above, runs
`unity test unity/Cubeglass --mode EditMode --non-interactive` and
`--mode PlayMode --non-interactive`, and fails when results are missing, a
required test assembly is absent, any test fails or any test is skipped. Only
then does it build the player with
`unity run unity/Cubeglass --non-interactive -- -executeMethod Cubeglass.Editor.BuildPlayer.BuildWindows64`,
package
`Cubeglass-windows-x64.zip`, upload it as the `Cubeglass-windows-x64` artefact
and attach it to the `v0.1.0` release when that tag exists (R50: the tag is
owner-gated).

The serial mode is a seated activation: a final step runs
`unity license return --yes` with `if: always()` and
`continue-on-error: true`, so the seat is returned even when the job failed or
was cancelled, and the offline `.ulf` mode (which has nothing to return) does
not fail the job from that step.

### Self-hosted route (`build-self-hosted`, `[self-hosted, windows]`)

The `self-hosted` job is the opt-in (`build_target: self-hosted`). The
machine's own Unity Hub activation - Unity Personal on this owner's machine -
is the licence: the job has no Unity secrets, runs no
`unity license activate`, and installs no editor. It runs the same EditMode and PlayMode
suites before `unity run` and packages `Cubeglass-v0.1.0-win-x64.zip`. The
build guard fails with a message naming the Hub activation when `unity run`
exits non-zero or the player executable is missing.

Runner setup (one-off): the GitHub runner is installed with the labels
`self-hosted` and `windows`, started as the Windows user that activated Unity
through the Hub (the Personal licence lives in that user's profile under
`%LOCALAPPDATA%\Unity\licenses`, which a service under a system account cannot
see). That user's PATH needs the Unity CLI (`unity`), the runner agent and
Visual Studio Build Tools with the C++ x64 toolset; `gh` is needed only for
the release attach, which the job skips with a warning when it is absent.

### Test gating and per-PR CI

Both routes are test-gated (I-6): the EditMode and PlayMode suites run before
the player build, and a failing suite fails the release before any player is
packaged. Per-PR `ci.yml` stays secret-free: no required job receives a Unity
credential or installs an editor, and the only per-commit Unity lane is
`scripts/ci-local.ps1`, run locally. The release workflow is therefore where
the Unity suites become mandatory in CI.

## Consequences

- Good: no third-party build action sits between the repository and the
  pinned Unity CLI, and no route uses account email/password credentials.
- Good: the hosted preflight is a one-credential-per-mode guard - one secret
  per mode (`UNITY_LICENSE` or `UNITY_SERIAL`), and a dispatch with neither
  fails fast with an actionable message instead of producing a release
  without a player.
- Good: the self-hosted route works with a Hub-activated Personal licence and
  needs no secrets at all.
- Good: both routes test-gate the release, and per-PR CI never sees Unity
  secrets.
- Bad: one of the two hosted secrets must exist and be rotated in repository
  settings; a serial is a seat, so the job must return it (the final step
  above does).
- Bad: the self-hosted route depends on one machine's Hub activation, PATH and
  per-user licence; a runner started under another account cannot see the
  licence and fails the build guard.
- Tech debt: serial/licence rotation, runner setup and the unwired
  floating-licence option are tracked in the tech debt register,
  `docs/notes/tech-debt.md`.

## Confirmation

- `.github/workflows/release.yml` is the source of truth for both jobs; the
  hosted job is `windows-x64-player` and the self-hosted job is
  `build-self-hosted`, selected by mutually exclusive `if:` conditions on
  `inputs.build_target`.
- `docs/ci.md` ("Release workflow") mirrors the routes and the two credential
  modes; `docs/releases/v0.1.0.md` links this ADR from the build and run
  section.
- The workflow stays valid under the existing `actionlint` invocation in
  `docs/ci.md`.
- The v0.1.0 dispatch is the first end-to-end run of both routes and records
  the outcome in the release notes.

## Links

- Workflows: [release.yml](../../.github/workflows/release.yml),
  [ci.yml](../../.github/workflows/ci.yml).
- Docs: [ci.md](../ci.md), [v0.1.0.md](../releases/v0.1.0.md).
- Related ADRs: [ADR-0003](0003-testing-strategy.md) (test pyramid and gate
  model), [ADR-0010](0010-unity-version-pipeline-and-bridge-layout.md) (Unity
  version and editor pin).
- Rulings and review items: R49 (dispatch-only release with two routes), R50
  (owner-gated `v0.1.0` tag), I-1 (missing-release probe fix), I-6 (tagged
  releases test-gated).

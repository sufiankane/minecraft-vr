# S8 dataset protocol (2026-10-07)

Purpose: collect the evidence that answers U-02, U-03, U-05 and U-06 and lets
gate **G-A** be decided (dossier S8): *both hands visible in both cameras in at
least 90 % of frames inside a defined 40 cm x 30 cm x 30 cm region at
bent-arm distance*. The dataset is stored externally; the loader manifests and
timing CSVs are committed, and the feasibility report (`docs/adr/0014-*`) is
written from these artefacts.

## Format and tools

- Sessions are `.cgrec` schema 2 (`manifest.json`, `stereo.csv`, `frames/`;
  dossier 5.8). Frame files: `--storage bin` (one padded binary per frame,
  fewer files) for the grid scripts; `pgm` for short sessions when
  JPEG-viewable frames are wanted.
- Recorder: `cg-recorder --source viture --dll <abs>\glasses.dll
  --dof 3dof|6dof --storage bin --seconds <n> --out <external>\<session>`.
  The geometry comes from the first frame; a few drops are counted in the
  manifest and are acceptable as long as the grid holds are covered.
- Probe: `cg-capture-probe` produces the U-02/U-03 evidence (rate, gaps,
  geometry/stride, f0-vs-f1, and PGM snapshots). Run it before the recordings
  in both DOF modes.
- Preconditions: SpaceWalker closed; the glasses connected directly; no
  display mode needed (frames only). Between sessions, stop recording before
  moving the hands.

## Volume definition (G-A region)

Centred on the face midline at bent-arm distance, with the front face 35 cm
from the eye line:

- x (width): -20 cm .. +20 cm
- y (height): -15 cm .. +15 cm relative to the eye line
- z (depth): 35 cm .. 65 cm

Grid nodes: 5 x 4 x 3 = 60 nodes (every 10 cm in x, every 10 cm in y, every
15 cm in z). A node is "held" for 3 s with the hand palm-forward, fingers
spread, wrist straight.

## Recording scripts

| Script | Mode | Lighting | Content | Duration |
|---|---|---|---|---|
| A1 | 3DoF | daylight | grid, left hand only (60 nodes x 3 s) | 3 min |
| A2 | 3DoF | daylight | grid, right hand only | 3 min |
| A3 | 3DoF | daylight | grid, both hands (left and right at mirrored nodes) | 3 min |
| B1 | 3DoF | desk lamp | coarse grid (every second node, both hands) | 1.5 min |
| B2 | 3DoF | desk lamp | motion: slow/fast sweeps, crossings, rotations | 3 min |
| B3 | 3DoF | desk lamp | near/far reaches across the depth range | 1.5 min |
| C1 | 6DoF | daylight | coarse grid, both hands | 1.5 min |
| C2 | 6DoF | daylight | motion: sweeps and crossings | 2 min |

Total: **20 minutes** of recordings (the gate's minimum). Record A1-A3 as one
`--seconds 540` session if convenient, B as `--seconds 360`, C as
`--seconds 210`; separate sessions per script are also fine and easier to
label.

## Label policy (approximate hand presence)

- Annotate **one frame per node per pass**: the middle of each 3 s hold. The
  recorder's `stereo.csv` gives the sequence for any wall-clock instant; the
  node table in `docs/notes/s8-hil/` maps hold order to grid coordinates.
- Label file per annotated session: `labels/annotations.csv` with
  `seq,l0,r0,l1,r1,notes` where each stream column is `1` when the tested
  hand(s) are clearly visible in that stream and `0` otherwise; `notes`
  carries the lighting/pose oddities.
- G-A metric: fraction of annotated grid frames where **both hands are
  visible in both cameras**. The stream-to-camera mapping comes from the
  probe's f0-vs-f1 finding (U-03); the report states the mapping it used.

## Clock offset (U-05)

- Immediately after a recording session, run
  `cg-pose-probe --source viture --dll <abs>\glasses.dll --seconds 60 --out
  docs/notes/s8-hil/pose_probe-offset.csv` with the recorder stopped. The
  ClockMapper offset in that CSV bounds the SDK-monotonic-to-host offset for
  the same device state (S5 measured 0.65 ms median / 1.03 ms p95); the S8
  note records the fresh measurement and whether the camera and pose
  timestamps share the clock (both are the SDK's monotonic seconds).
- `stereo.csv`'s `sdk_time_s` is that same SDK clock, so replay timing and
  later fusion can use one constant offset per session.

## Artefact layout

In the repo (committed):

- `docs/notes/s8-hil/probe-3dof.log`, `probe-6dof.log`, `snap-*/` PGMs
- `docs/notes/s8-hil/manifests/<session>/manifest.json` and `stereo.csv`
  (small: ~18k rows per 10 min at 30 fps)
- `docs/notes/s8-hil/labels/annotations-<session>.csv`
- `docs/notes/s8-hil/clock-offset.md`
- `docs/notes/s8-hil/sessions.md` (index: dir, script, mode, seconds, frames,
  drops, external path)

External (not committed): the `frames/` payloads (multi-GB); the external
root path is recorded in `sessions.md`.

## Session index template

| Session | Script | Mode | Seconds | Frames | Dropped | External path |
|---|---|---|---|---|---|---|
| | | | | | | |

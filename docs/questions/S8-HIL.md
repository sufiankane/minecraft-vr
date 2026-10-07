# S8 HIL runbook — stereo capture and gate G-A (owner)

This is the owner-run hardware session that answers U-02, U-03, U-05 and U-06
and decides gate **G-A** (dossier S8). The software half is complete and
merged; everything below runs the built tools against the real glasses. Follow
`docs/notes/s8-dataset-protocol.md` for the recording scripts and the label
policy.

## Build and stage (already done on this machine)

- Tools: `cpp\build\windows-msvc\bin\cg-capture-probe.exe`,
  `...\cg-recorder.exe` (rebuild with the MSVC preset if needed).
- Vendor DLLs: copy `viture sdk\x86_64\*.dll` next to those exes (the loader's
  path policy accepts a DLL sibling of the running executable; a path inside
  the repo working directory is otherwise rejected).
- Close **SpaceWalker** before every run (it holds the device exclusively).
- No display mode or SBS setup is needed: this session consumes camera frames
  only. Keep the glasses connected directly to the laptop.

## Step 1 — probes (U-02, U-03)

```powershell
$dll = (Resolve-Path "cpp\build\windows-msvc\bin\glasses.dll").Path
cd cpp\build\windows-msvc\bin
.\cg-capture-probe.exe --dll $dll --dof 3dof --seconds 60 --out "$env:USERPROFILE\Documents\cgrec\s8\snap-3dof" > "$env:USERPROFILE\Documents\cgrec\s8\probe-3dof.log" 2>&1
.\cg-capture-probe.exe --dll $dll --dof 6dof --seconds 60 --out "$env:USERPROFILE\Documents\cgrec\s8\snap-6dof" > "$env:USERPROFILE\Documents\cgrec\s8\probe-6dof.log" 2>&1
Write-Output "3dof exit=$LASTEXITCODE"
```

What matters in the logs: `frames` > 0 in 3DoF (U-02), the `geometry=` line
(resolution/stride, U-03), `sequence_gaps`, and `f0_equals_f1` /
`left0_equals_right0` (stream layout, U-03). The `snap-*` folders hold the
first-frame PGMs of the four streams.

## Step 2 — recordings (20 minutes total)

Create the external dataset root (any drive with a few GB free):

```powershell
$root = "$env:USERPROFILE\Documents\cgrec\s8\sessions"   # or D:\cgrec\s8
New-Item -ItemType Directory -Force $root | Out-Null
```

Run the scripts from `docs/notes/s8-dataset-protocol.md` (A1-A3 as one 9 min
session, B as 6 min, C as 3.5 min; separate shorter sessions also work):

```powershell
cd cpp\build\windows-msvc\bin
.\cg-recorder.exe --source viture --dll $dll --dof 3dof  --storage bin --seconds 540 --out "$root\a-3dof-grid"
.\cg-recorder.exe --source viture --dll $dll --dof 3dof  --storage bin --seconds 360 --out "$root\b-3dof-motion"
.\cg-recorder.exe --source viture --dll $dll --dof 6dof  --storage bin --seconds 210 --out "$root\c-6dof"
```

Hold each grid node for ~3 s as described in the protocol; the recorder counts
drops in its manifest (a handful is fine, a steady stream means the disk is
too slow). `--storage pgm` instead of `bin` gives viewable per-frame PGMs when
you want to eyeball a short session.

## Step 3 — clock offset (U-05)

With the glasses still attached and no recording running:

```powershell
.\cg-pose-probe.exe --source viture --dll $dll --seconds 60 --out "$root\pose-offset.csv" > "$env:USERPROFILE\Documents\cgrec\s8\pose-offset.log" 2>&1
```

## Step 4 — labels (G-A)

Annotate one frame per grid node per pass (the middle of each hold) following
the protocol's label policy:

- `labels/annotations-<session>.csv` next to each session, columns
  `seq,l0,r0,l1,r1,notes` (1 = the tested hand clearly visible in that stream).
- Keep it approximate: the report only needs the visible/not-visible decision.

## Step 5 — hand back

Copy these into the repo and tell the assistant the session index:

- `docs/notes/s8-hil/probe-3dof.log`, `probe-6dof.log`, `snap-*/` PGMs
- `docs/notes/s8-hil/pose-offset.log` + a short `clock-offset.md` with the
  offset range you read from the CSV
- For each session: `docs/notes/s8-hil/manifests/<session>/manifest.json` and
  `stereo.csv`, plus the label CSV under `docs/notes/s8-hil/labels/`
- `docs/notes/s8-hil/sessions.md` filled from the protocol's index template
  (external paths included)

The assistant then writes `docs/notes/s8-gate.md` + `docs/adr/0014-*`, computes
the G-A fraction from the labels and, if it is ≥90 %, tags
`stage-8-complete`; otherwise the runbook pauses for the escalation note in
this file (6DoF-only operation, hand-position guidance, or an external camera,
per the dossier's no-go path).

## Status (2026-10-07)

**Done on hardware** (owner's laptop, firmware `12.0.01.101_20260605`, SDK
2.4.0, SpaceWalker closed; artefacts committed under `docs/notes/s8-hil/`):

- Probes in both modes, 60 s each: **1,493 frames = 25.0 Hz, zero sequence
  gaps**, 640x480 packed, `l0`/`r0` present and `l1`/`r1` **null** in both
  3DoF and 6DoF (answers U-02: frames arrive in 3DoF; U-03: one stereo pair,
  packed rows, `f0 != f1`; first stamp is a startup artifact).
- `a1-left` recording: left-hand grid, 180 s, **4,494 frames, 0 drops**
  (5.2 GB stored externally; manifest + `stereo.csv` committed). Sample
  frames show the raised hand, palm to camera, sharp and well exposed.
- Clock offset (U-05): the camera and pose stamps share the SDK monotonic
  clock; a fresh 30 s pose probe measured 0.75 ms median / ~1 ms p95 to the
  host clock (`clock-offset.md`).

**Owner decision:** the right-hand and both-hands passes, the full 20-minute
script set and the per-node labels are **deferred**; the owner asked to
process the left-hand take only. U-06 and gate **G-A remain open** and
`stage-8-complete` stays withheld. The two defects the run exposed (null
`l1`/`r1` crash, startup-artifact rate) are fixed and merged (PR #74).

**Resume:** run the remaining protocol scripts (A2/A3 right/both hands, B, C)
with the beep pacer and complete the labels; the procedure above is unchanged.

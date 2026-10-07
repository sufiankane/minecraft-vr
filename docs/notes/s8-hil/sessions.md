# S8 HIL sessions (2026-10-07)

## Probes (U-02/U-03)

Both modes recorded identically for 60 s:

| Probe | Frames | Rate | Geometry | Streams | Gaps |
|---|---|---|---|---|---|
| 3DoF | 1,493 | 25.0 Hz | 640x480 stride 640 (packed) | l0/r0 yes, l1/r1 **null** | 0 |
| 6DoF | 1,493 | 25.0 Hz | 640x480 stride 640 (packed) | l0/r0 yes, l1/r1 **null** | 0 |

Answers: **U-02** stereo frames arrive in 3DoF (no 6DoF fallback needed).
**U-03** the callback delivers a single stereo pair `l0`/`r0` at 640x480,
packed rows, `f0 != f1` streams differ (real stereo); the `l1`/`r1` pair is
absent (null) on this device/firmware, and the first stamp of a session is a
startup artifact (see `clock-offset.md`).

## Recordings

| Session | Script | Mode | Seconds | Frames | Dropped | Storage | Location |
|---|---|---|---|---|---|---|---|
| `a1-left` | A1 left-hand grid (owner-scoped to left only), second take | 3dof | 180 | 4,492 | 0 | bin | `C:\Users\sufia\Documents\cgrec\s8\sessions\a1-left` (external, 5.2 GB) |

Committed per session: `manifests/<session>/manifest.json` + `stereo.csv`.
Sample frames for visual inspection: `samples/a1-frame{1,900,1800,2700,3600,4400}.png`.
The samples show the raised left hand across the near/mid/far planes; this
take was recorded in the evening, so the hand reads as a dark silhouette
against the room (the first take was daylight and brighter). The camera
handles both; the labels only need the presence decision.

## Scope note (owner decision, 2026-10-07)

The owner ran the **left-hand** pass (twice, to improve the take) and asked to
process it rather than run the right-hand/both-hand passes. The capture
pipeline is proven end-to-end on hardware; the formal G-A statement (both
hands) and U-06 remain open per `docs/questions/S8-HIL.md`, tracked by
TD-086. `stage-8-complete` stays withheld.

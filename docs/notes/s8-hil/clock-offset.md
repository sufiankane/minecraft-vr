# S8 clock offset (U-05)

Run: 2026-10-07, glasses attached directly, SpaceWalker closed, firmware
`12.0.01.101_20260605`, SDK 2.4.0.

Pose probe (`cg-pose-probe --source viture`, 30 s, artefact
`pose-offset.csv`):

- 3,359 samples over 29.98 s (112 Hz), stable 3,000 / unstable 359 / lost 0.
- `ClockMapper` offset (`host_time_ns/1e9 - sdk_time_s`): median **0.747 ms**,
  minimum -0.034 ms. The first stamps carry the known ~1.01 s startup
  transient (p95 0.39 s; identical to the S5 run, which converged to
  0.65 ms median), so timing analysis skips the first frames.

Camera clock (capture probe trace, same session):

- The camera callback timestamps advance as SDK monotonic **seconds**
  (`ts=236xxxx.xxx`), the same clock family as the pose samples
  (`t=236299.5 s`) and the host's steady clock (machine uptime).
- The very first camera stamp of a session is a startup artifact (observed
  `235.5 s` against a `235450 s` clock); the probe anchors its rate on host
  arrival times for that reason.

Conclusion (answers U-05): **camera and pose timestamps share the SDK
monotonic clock; the offset to the host monotonic clock is sub-millisecond
(≈0.75 ms median, ≈1 ms p95) and constant per session**, so one constant per
session maps SDK time to host time for fusion and replay.

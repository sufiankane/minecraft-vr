# S5 HIL run notes (2026-10-06)

- Host: owner's HP laptop; glasses on its USB-C chain
  (`USB\VID_35CA&PID_1104` behind a USB 2.0-class hub on `XHC3/PRT1`; the
  stream works through it); firmware `12.0.01.101_20260605`; SDK 2.4.0;
  SpaceWalker closed; `VitureXrRuntime` service stopped.
- Command:
  `cg-pose-probe --source viture --dll <benchmarks bin>\glasses.dll --display
  --seconds 60 --out docs\notes\s5-hil\pose_probe.csv` with stdout+stderr
  redirected to `pose_probe.log`.
- Result: exit 0; 4,533 samples / 59.995 s (75.5 Hz measured poll rate;
  an 8 s probe run measured 203 Hz); status stable 4,529 / unstable 4 /
  lost 0; 1 failed poll (the warm-up `-3` right after `start`); jitter
  p50 2.37 ms / p95 11.25 ms / p99 11.78 ms; latency proxy
  (`host_time_ns/1e9 - sdk_time_s`) p50 0.65 ms / p95 1.03 ms after the first
  4 samples (which carry a ~1.01 s stale callback stamp).
- Display (U-08): initial 90 Hz (left by the previous session); `set SBS 90
  ok` -> readback 90 Hz after a 500 ms settle; `set 2D 90 ok` -> 90 Hz;
  `restore 2D 60` returned `-3` (a rapid third switch).
- The wrapper warm-up grace fix and the full analysis live in ADR-0009
  (amendment 2026-10-06); the pinning test is
  `VitureFault.TransientPollErrorsInsideTheGraceKeepTheSession`.
- Environment note: the vendor demo and the loader both log
  `get exp flag status: Host Command Invalid` and `get_film_mode ... -7` on
  this device; both are benign (they appear in working runs).

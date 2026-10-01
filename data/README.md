# `data/`

Shared test and calibration data for Cubeglass.

- **Small fixtures only.** Files checked in here must be small enough to review
  and clone cheaply (text, JSON, tiny images, short recordings).
- **Large datasets do not belong in git.** Publish them through Git LFS or
  external storage and reference them by URL plus a checksum; commit only the
  small manifest or pointer needed to fetch them.
- **No generated data.** Keep build outputs and captured captures out of this
  directory; see `.gitignore`.

The directory is intentionally empty of fixtures for now — it exists so later
stages have a single agreed location for shared test data.

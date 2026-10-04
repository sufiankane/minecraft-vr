"""Nightly benchmark regression gate.

The nightly lane produces benchmark JSON (Google Benchmark for the C++ target,
BenchmarkDotNet's JSON exporter for the .NET projects). This module turns those
payloads into a name -> nanoseconds mapping, captures a baseline file when none
is committed yet, and fails when a benchmark present in both the baseline and
the current run regresses by more than the threshold (15 percent by default,
matching dossier section 9 item 9).

The committed baseline lives at ``docs/perf/nightly-baseline.json`` and has the
shape ``{"benchmarks": {"<prefix>/<name>": <nanoseconds>}}``. Until the first
nightly run has been captured and committed, the compare step passes with a
"baseline established" message and uploads the captured file as an artefact.

Only the standard library is used. Google Benchmark values are taken from the
``iteration`` rows and converted to nanoseconds using ``time_unit``;
BenchmarkDotNet statistics are already reported in nanoseconds.
"""

from __future__ import annotations

import json
from dataclasses import dataclass
from pathlib import Path
from typing import cast

DEFAULT_THRESHOLD_PERCENT = 15.0
BASELINE_KEY = "benchmarks"

_UNIT_TO_NS: dict[str, float] = {
    "ns": 1.0,
    "us": 1.0e3,
    "\u00b5s": 1.0e3,
    "ms": 1.0e6,
    "s": 1.0e9,
}


class BenchError(Exception):
    """Raised when a benchmark payload or baseline cannot be interpreted."""


@dataclass(frozen=True)
class Measurement:
    """One benchmark measurement, normalised to nanoseconds."""

    name: str
    nanoseconds: float


def _load_json(path: Path) -> object:
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except FileNotFoundError as error:
        raise BenchError(f"missing benchmark JSON: {path}") from error
    except json.JSONDecodeError as error:
        raise BenchError(f"{path}: invalid JSON ({error})") from error


def _as_object(value: object) -> dict[object, object]:
    return cast(dict[object, object], value) if isinstance(value, dict) else {}


def _unit_to_ns(unit: object) -> float:
    if not isinstance(unit, str) or unit not in _UNIT_TO_NS:
        raise BenchError(f"unsupported Google Benchmark time_unit: {unit!r}")
    return _UNIT_TO_NS[unit]


def parse_google_benchmark(payload: object) -> list[Measurement]:
    """Parse a Google Benchmark JSON document into nanosecond measurements.

    Aggregate rows (``run_type`` other than ``iteration``) are ignored; several
    iteration rows for the same name are averaged, which keeps the parser valid
    when repetitions are added later.
    """

    entries = _as_object(payload).get("benchmarks")
    if not isinstance(entries, list):
        raise BenchError("Google Benchmark JSON has no 'benchmarks' array")
    grouped: dict[str, list[float]] = {}
    for raw in cast(list[object], entries):
        entry = _as_object(raw)
        if entry.get("run_type", "iteration") != "iteration":
            continue
        name = entry.get("name")
        real_time = entry.get("real_time")
        if not isinstance(name, str) or not isinstance(real_time, (int, float)) or isinstance(real_time, bool):
            continue
        grouped.setdefault(name, []).append(float(real_time) * _unit_to_ns(entry.get("time_unit", "ns")))
    if not grouped:
        raise BenchError("Google Benchmark JSON contains no iteration benchmarks")
    return [Measurement(name, sum(values) / len(values)) for name, values in sorted(grouped.items())]


def parse_bdn_json(payload: object) -> list[Measurement]:
    """Parse a BenchmarkDotNet JSON exporter document into measurements.

    BenchmarkDotNet statistics are reported in nanoseconds; the benchmark name
    is the exporter's ``FullName`` (namespace-qualified type and method).
    """

    entries = _as_object(payload).get("Benchmarks")
    if not isinstance(entries, list):
        raise BenchError("BenchmarkDotNet JSON has no 'Benchmarks' array")
    measurements: list[Measurement] = []
    for raw in cast(list[object], entries):
        entry = _as_object(raw)
        statistics = _as_object(entry.get("Statistics"))
        mean = statistics.get("Mean")
        if not isinstance(mean, (int, float)) or isinstance(mean, bool):
            continue
        full_name = entry.get("FullName")
        if isinstance(full_name, str) and full_name:
            name = full_name
        else:
            type_name = entry.get("Type")
            method = entry.get("Method")
            if not isinstance(type_name, str) or not isinstance(method, str):
                continue
            name = f"{type_name}.{method}"
        measurements.append(Measurement(name, float(mean)))
    if not measurements:
        raise BenchError("BenchmarkDotNet JSON contains no usable 'Statistics.Mean' entries")
    return sorted(measurements, key=lambda measurement: measurement.name)


def load_measurements(paths: list[Path], fmt: str) -> dict[str, float]:
    """Merge one or more benchmark JSON files, keyed by benchmark name."""

    parser = {"google": parse_google_benchmark, "bdn": parse_bdn_json}.get(fmt)
    if parser is None:
        raise BenchError(f"unknown benchmark format: {fmt}")
    merged: dict[str, float] = {}
    for path in paths:
        for measurement in parser(_load_json(path)):
            merged[measurement.name] = measurement.nanoseconds
    return merged


def load_baseline(path: Path) -> dict[str, float] | None:
    """Load the committed baseline; ``None`` when the file does not exist."""

    if not path.is_file():
        return None
    entries = _as_object(_load_json(path)).get(BASELINE_KEY)
    if not isinstance(entries, dict):
        raise BenchError(f"{path}: expected a '{BASELINE_KEY}' object")
    baseline: dict[str, float] = {}
    for key, value in cast(dict[object, object], entries).items():
        if not isinstance(value, (int, float)) or isinstance(value, bool):
            raise BenchError(f"{path}: benchmark {key!r} has a non-numeric value")
        baseline[str(key)] = float(value)
    return baseline


def write_capture(path: Path, measurements: dict[str, float]) -> None:
    """Merge ``measurements`` into ``path`` (creating it) and write it back."""

    existing = load_baseline(path) if path.is_file() else {}
    merged = dict(existing or {})
    merged.update(measurements)
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(
        json.dumps({BASELINE_KEY: dict(sorted(merged.items()))}, indent=2) + "\n",
        encoding="utf-8",
    )


def regressions(
    baseline: dict[str, float],
    current: dict[str, float],
    threshold_percent: float = DEFAULT_THRESHOLD_PERCENT,
) -> list[str]:
    """Return one message per benchmark that regressed by more than the threshold.

    Only benchmarks present in both maps are compared; a benchmark new in the
    current run is reported by the caller as uncaptured, never as a regression.
    """

    violations: list[str] = []
    for name in sorted(set(baseline) & set(current)):
        recorded = baseline[name]
        measured = current[name]
        if recorded <= 0.0:
            continue
        change_percent = (measured - recorded) / recorded * 100.0
        if change_percent > threshold_percent:
            violations.append(
                f"{name}: {recorded:.3f} ns -> {measured:.3f} ns "
                f"(+{change_percent:.1f}%, threshold {threshold_percent:g}%)"
            )
    return violations

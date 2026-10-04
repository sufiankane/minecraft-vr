from __future__ import annotations

import json
from pathlib import Path

import pytest

from depcheck.__main__ import main
from depcheck.benchregress import (
    BenchError,
    load_baseline,
    load_measurements,
    parse_bdn_json,
    parse_google_benchmark,
    regressions,
    write_capture,
)

GOOGLE_PAYLOAD = {
    "benchmarks": [
        {"name": "BM_A", "run_type": "iteration", "real_time": 100.0, "time_unit": "ns"},
        {"name": "BM_A", "run_type": "iteration", "real_time": 300.0, "time_unit": "ns"},
        {
            "name": "BM_A",
            "run_type": "aggregate",
            "aggregate_name": "mean",
            "real_time": 200.0,
            "time_unit": "ns",
        },
        {"name": "BM_B", "run_type": "iteration", "real_time": 2.0, "time_unit": "us"},
    ]
}

BDN_PAYLOAD = {
    "Benchmarks": [
        {
            "Namespace": "Cubeglass.CoreMath.Benchmarks",
            "Type": "AbiVersionBenchmarks",
            "Method": "Value",
            "FullName": "Cubeglass.CoreMath.Benchmarks.AbiVersionBenchmarks.Value",
            "Statistics": {"Mean": 4.5},
        }
    ]
}


def write_json(path: Path, payload: object) -> Path:
    path.write_text(json.dumps(payload), encoding="utf-8")
    return path


def test_google_parser_converts_units_and_averages_repeats() -> None:
    measurements = {measurement.name: measurement.nanoseconds for measurement in parse_google_benchmark(GOOGLE_PAYLOAD)}
    assert measurements == {"BM_A": 200.0, "BM_B": 2000.0}


def test_bdn_parser_reads_mean_nanoseconds() -> None:
    measurements = parse_bdn_json(BDN_PAYLOAD)
    assert len(measurements) == 1
    assert measurements[0].name == "Cubeglass.CoreMath.Benchmarks.AbiVersionBenchmarks.Value"
    assert measurements[0].nanoseconds == 4.5


def test_unknown_google_time_unit_is_an_error() -> None:
    with pytest.raises(BenchError, match="time_unit"):
        parse_google_benchmark({"benchmarks": [{"name": "X", "real_time": 1.0, "time_unit": "furlongs"}]})


def test_regression_over_threshold_names_the_offender() -> None:
    violations = regressions({"cpp/BM_A": 100.0}, {"cpp/BM_A": 111.0})
    assert len(violations) == 1
    assert "cpp/BM_A" in violations[0]
    assert "+11.0%" in violations[0]
    assert "threshold 10%" in violations[0]


def test_regression_at_the_threshold_boundary_passes() -> None:
    assert regressions({"cpp/BM_A": 100.0}, {"cpp/BM_A": 110.0}) == []


def test_only_benchmarks_present_in_both_are_compared() -> None:
    violations = regressions({"cpp/Old": 100.0}, {"cpp/New": 100.0})
    assert violations == []


def test_load_measurements_merges_multiple_files(tmp_path: Path) -> None:
    first = write_json(
        tmp_path / "a.json",
        {"benchmarks": [{"name": "BM_A", "real_time": 1.0, "time_unit": "ms"}]},
    )
    second = write_json(
        tmp_path / "b.json",
        {"benchmarks": [{"name": "BM_B", "real_time": 5.0, "time_unit": "ns"}]},
    )
    assert load_measurements([first, second], "google") == {"BM_A": 1_000_000.0, "BM_B": 5.0}


def test_capture_merges_with_an_existing_file(tmp_path: Path) -> None:
    capture = tmp_path / "baseline.json"
    write_capture(capture, {"cpp/BM_A": 100.0})
    write_capture(capture, {"cpp/BM_B": 200.0})
    assert load_baseline(capture) == {"cpp/BM_A": 100.0, "cpp/BM_B": 200.0}


def test_cli_establishes_and_captures_a_missing_baseline(
    tmp_path: Path, capsys: pytest.CaptureFixture[str]
) -> None:
    current = write_json(tmp_path / "current.json", GOOGLE_PAYLOAD)
    baseline = tmp_path / "baseline.json"
    capture = tmp_path / "capture.json"

    code = main(
        [
            "benchregress",
            "--baseline",
            str(baseline),
            "--format",
            "google",
            "--prefix",
            "cpp",
            "--current",
            str(current),
            "--capture",
            str(capture),
        ]
    )
    captured = capsys.readouterr().out
    assert code == 0
    assert "baseline established" in captured
    assert load_baseline(capture) == {"cpp/BM_A": 200.0, "cpp/BM_B": 2000.0}
    assert not baseline.exists()


def test_cli_fails_and_names_a_regressed_benchmark(
    tmp_path: Path, capsys: pytest.CaptureFixture[str]
) -> None:
    current = write_json(
        tmp_path / "current.json",
        {"benchmarks": [{"name": "BM_A", "real_time": 130.0, "time_unit": "ns"}]},
    )
    baseline = write_json(tmp_path / "baseline.json", {"benchmarks": {"cpp/BM_A": 100.0}})

    code = main(
        [
            "benchregress",
            "--baseline",
            str(baseline),
            "--format",
            "google",
            "--prefix",
            "cpp",
            "--current",
            str(current),
        ]
    )
    captured = capsys.readouterr().out
    assert code == 1
    assert "cpp/BM_A" in captured
    assert "+30.0%" in captured


def test_cli_passes_within_threshold(tmp_path: Path, capsys: pytest.CaptureFixture[str]) -> None:
    current = write_json(
        tmp_path / "current.json",
        {"benchmarks": [{"name": "BM_A", "real_time": 110.0, "time_unit": "ns"}]},
    )
    baseline = write_json(tmp_path / "baseline.json", {"benchmarks": {"cpp/BM_A": 100.0}})

    code = main(
        [
            "benchregress",
            "--baseline",
            str(baseline),
            "--format",
            "google",
            "--prefix",
            "cpp",
            "--current",
            str(current),
        ]
    )
    captured = capsys.readouterr().out
    assert code == 0
    assert "within 10%" in captured

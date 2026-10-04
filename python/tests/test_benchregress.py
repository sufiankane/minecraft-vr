from __future__ import annotations

import json
from pathlib import Path

import pytest

from depcheck.__main__ import main
from depcheck.benchregress import (
    BenchError,
    load_baseline,
    load_history,
    load_measurements,
    median_history,
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


def test_cli_accepts_multiple_current_files_and_names_the_regression(
    tmp_path: Path, capsys: pytest.CaptureFixture[str]
) -> None:
    first = write_json(
        tmp_path / "first.json",
        {"benchmarks": [{"name": "BM_A", "real_time": 100.0, "time_unit": "ns"}]},
    )
    second = write_json(
        tmp_path / "second.json",
        {"benchmarks": [{"name": "BM_B", "real_time": 120.0, "time_unit": "ns"}]},
    )
    baseline = write_json(
        tmp_path / "baseline.json",
        {"benchmarks": {"dotnet/BM_A": 100.0, "dotnet/BM_B": 100.0}},
    )

    code = main(
        [
            "benchregress",
            "--baseline",
            str(baseline),
            "--format",
            "google",
            "--prefix",
            "dotnet",
            "--current",
            str(first),
            str(second),
        ]
    )
    captured = capsys.readouterr().out
    assert code == 1
    assert "dotnet/BM_B" in captured
    assert "+20.0%" in captured
    assert "dotnet/BM_A" not in captured


def test_cli_first_run_captures_multiple_reports_and_prefixes(
    tmp_path: Path, capsys: pytest.CaptureFixture[str]
) -> None:
    cpp = write_json(
        tmp_path / "cpp.json",
        {"benchmarks": [{"name": "BM_Cpp", "real_time": 5.0, "time_unit": "ns"}]},
    )
    dotnet_first = write_json(tmp_path / "dotnet-first.json", BDN_PAYLOAD)
    dotnet_second = write_json(
        tmp_path / "dotnet-second.json",
        {"Benchmarks": [{"FullName": "X.Y.Z", "Statistics": {"Mean": 9.0}}]},
    )
    baseline = tmp_path / "missing-baseline.json"
    capture = tmp_path / "capture.json"

    code = main(
        [
            "benchregress",
            "--baseline",
            str(baseline),
            "--format",
            "bdn",
            "--prefix",
            "dotnet",
            "--current",
            str(dotnet_first),
            str(dotnet_second),
            "--capture",
            str(capture),
        ]
    )
    captured = capsys.readouterr().out
    assert code == 0
    assert "baseline established" in captured
    assert load_baseline(capture) == {
        "dotnet/Cubeglass.CoreMath.Benchmarks.AbiVersionBenchmarks.Value": 4.5,
        "dotnet/X.Y.Z": 9.0,
    }

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
            str(cpp),
            "--capture",
            str(capture),
        ]
    )
    captured = capsys.readouterr().out
    assert code == 0
    assert "baseline established" in captured
    assert load_baseline(capture) == {
        "dotnet/Cubeglass.CoreMath.Benchmarks.AbiVersionBenchmarks.Value": 4.5,
        "dotnet/X.Y.Z": 9.0,
        "cpp/BM_Cpp": 5.0,
    }


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


def test_median_history_is_robust_to_a_single_outlier_run(tmp_path: Path) -> None:
    history = [
        write_json(tmp_path / f"run-{index}.json", {"benchmarks": {"cpp/BM_A": value}})
        for index, value in enumerate([98.0, 100.0, 102.0, 180.0])
    ]
    summaries = [load_baseline(path) or {} for path in history]
    median = median_history(summaries)
    assert median == {"cpp/BM_A": 101.0}


def test_median_history_averages_the_middle_pair(tmp_path: Path) -> None:
    first = write_json(tmp_path / "a.json", {"benchmarks": {"cpp/BM_A": 100.0}})
    second = write_json(tmp_path / "b.json", {"benchmarks": {"cpp/BM_A": 111.0}})
    assert median_history([load_baseline(first) or {}, load_baseline(second) or {}]) == {
        "cpp/BM_A": 105.5
    }


def test_load_history_missing_directory_is_empty(tmp_path: Path) -> None:
    assert load_history(tmp_path / "not-there") == []


def test_load_history_keeps_only_the_window_most_recent(tmp_path: Path) -> None:
    paths = []
    for index, value in enumerate([10.0, 20.0, 30.0, 40.0, 50.0, 60.0]):
        path = write_json(tmp_path / f"run-{index}.json", {"benchmarks": {"cpp/BM_A": value}})
        paths.append(path)
    # Force a deterministic modification order for the mtime tiebreak.
    for index, path in enumerate(paths):
        path.touch()
    window = load_history(tmp_path, window=3)
    assert [summary["cpp/BM_A"] for summary in window] == [40.0, 50.0, 60.0]


def test_cli_history_bootstraps_on_the_first_run(
    tmp_path: Path, capsys: pytest.CaptureFixture[str]
) -> None:
    current = write_json(tmp_path / "current.json", GOOGLE_PAYLOAD)
    history = tmp_path / "history"
    capture = history / "bench-1.json"

    code = main(
        [
            "benchregress",
            "--history",
            str(history),
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
    assert "no prior history" in captured
    assert load_baseline(capture) == {"cpp/BM_A": 200.0, "cpp/BM_B": 2000.0}


def test_cli_history_fails_against_the_median_and_names_the_benchmark(
    tmp_path: Path, capsys: pytest.CaptureFixture[str]
) -> None:
    history = tmp_path / "history"
    history.mkdir()
    for index, value in enumerate([99.0, 100.0, 101.0]):
        write_json(history / f"run-{index}.json", {"benchmarks": {"cpp/BM_A": value}})
    current = write_json(
        tmp_path / "current.json",
        {"benchmarks": [{"name": "BM_A", "real_time": 130.0, "time_unit": "ns"}]},
    )

    code = main(
        [
            "benchregress",
            "--history",
            str(history),
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
    assert "median of 3" in captured
    assert "+30.0%" in captured


def test_cli_history_passes_within_threshold_and_reports_the_median(
    tmp_path: Path, capsys: pytest.CaptureFixture[str]
) -> None:
    history = tmp_path / "history"
    history.mkdir()
    for index, value in enumerate([100.0, 100.0, 100.0, 180.0, 100.0]):
        write_json(history / f"run-{index}.json", {"benchmarks": {"cpp/BM_A": value}})
    current = write_json(
        tmp_path / "current.json",
        {"benchmarks": [{"name": "BM_A", "real_time": 105.0, "time_unit": "ns"}]},
    )

    code = main(
        [
            "benchregress",
            "--history",
            str(history),
            "--format",
            "google",
            "--prefix",
            "cpp",
            "--current",
            str(current),
            "--history-window",
            "5",
        ]
    )
    captured = capsys.readouterr().out
    assert code == 0
    assert "within 10%" in captured
    assert "median of 5" in captured


def test_cli_history_window_ignores_older_runs(
    tmp_path: Path, capsys: pytest.CaptureFixture[str]
) -> None:
    history = tmp_path / "history"
    history.mkdir()
    old = write_json(history / "run-0.json", {"benchmarks": {"cpp/BM_A": 200.0}})
    for index in range(1, 5):
        write_json(history / f"run-{index}.json", {"benchmarks": {"cpp/BM_A": 100.0}})
    # The old outlier must sort before the window's four runs.
    old.touch()
    current = write_json(
        tmp_path / "current.json",
        {"benchmarks": [{"name": "BM_A", "real_time": 105.0, "time_unit": "ns"}]},
    )

    code = main(
        [
            "benchregress",
            "--history",
            str(history),
            "--format",
            "google",
            "--prefix",
            "cpp",
            "--current",
            str(current),
            "--history-window",
            "4",
        ]
    )
    captured = capsys.readouterr().out
    assert code == 0
    assert "median of 4" in captured


def test_cli_baseline_from_requires_an_existing_baseline(
    tmp_path: Path, capsys: pytest.CaptureFixture[str]
) -> None:
    current = write_json(
        tmp_path / "current.json",
        {"benchmarks": [{"name": "BM_A", "real_time": 100.0, "time_unit": "ns"}]},
    )
    code = main(
        [
            "benchregress",
            "--baseline-from",
            str(tmp_path / "missing.json"),
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
    assert "missing.json" in captured
    assert "does not exist" in captured


def test_cli_baseline_from_compares_against_the_named_file(
    tmp_path: Path, capsys: pytest.CaptureFixture[str]
) -> None:
    baseline = write_json(tmp_path / "pinned.json", {"benchmarks": {"cpp/BM_A": 100.0}})
    current = write_json(
        tmp_path / "current.json",
        {"benchmarks": [{"name": "BM_A", "real_time": 130.0, "time_unit": "ns"}]},
    )
    code = main(
        [
            "benchregress",
            "--baseline-from",
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

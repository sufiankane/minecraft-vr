from __future__ import annotations

from pathlib import Path

import pytest

from depcheck.__main__ import main
from depcheck.coverage_check import CoverageError, CoverageResult, check_coverage

FIXTURES = Path(__file__).parent / "fixtures" / "coverage"

ABOVE = FIXTURES / "above_floor.xml"
BELOW = FIXTURES / "below_floor.xml"
MISSING = FIXTURES / "module_missing.xml"
DOTNET_PACKAGE = FIXTURES / "dotnet_package.xml"
DOTNET_CLASS = FIXTURES / "dotnet_class.xml"
TEST_NAME_ONLY = FIXTURES / "test_name_only.xml"


def test_above_floor_passes() -> None:
    result = check_coverage(ABOVE, "core-math", 90)
    assert isinstance(result, CoverageResult)
    assert result.meets_floor is True
    assert result.covered_lines == 5
    assert result.total_lines == 5
    assert result.rate == pytest.approx(100.0)


def test_below_floor_fails() -> None:
    result = check_coverage(BELOW, "core-math", 90)
    assert result.meets_floor is False
    assert result.covered_lines == 1
    assert result.total_lines == 5
    assert result.rate == pytest.approx(20.0)


def test_module_match_is_case_insensitive() -> None:
    assert check_coverage(ABOVE, "Core-Math", 90).meets_floor is True
    assert check_coverage(ABOVE, "CORE-MATH", 90).meets_floor is True


def test_module_matching_nothing_fails() -> None:
    result = check_coverage(MISSING, "core-math", 90)
    assert result.matched_units == 0
    assert result.meets_floor is False
    assert result.rate == pytest.approx(0.0)


def test_missing_module_message_names_module_floor_and_rate() -> None:
    summary = check_coverage(MISSING, "core-math", 90).summary()
    assert "core-math" in summary
    assert "matched nothing" in summary
    assert "90" in summary
    assert "0.00%" in summary


def test_below_floor_message_names_module_floor_and_observed_rate() -> None:
    summary = check_coverage(BELOW, "core-math", 90).summary()
    assert "core-math" in summary
    assert "90" in summary
    assert "20.00%" in summary
    assert "FAIL" in summary


def test_module_does_not_match_files_that_merely_contain_the_name() -> None:
    result = check_coverage(TEST_NAME_ONLY, "depcheck", 90)
    assert result.matched_units == 0
    assert result.meets_floor is False


def test_dotnet_package_without_coverable_lines_passes() -> None:
    result = check_coverage(DOTNET_PACKAGE, "Cubeglass.CoreMath", 90)
    assert result.matched_units >= 1
    assert result.total_lines == 0
    assert result.rate == pytest.approx(100.0)
    assert result.meets_floor is True
    assert "no coverable lines" in result.summary()


def test_dotnet_class_matches_assembly_qualified_module() -> None:
    result = check_coverage(DOTNET_CLASS, "Cubeglass.CoreMath", 90)
    assert result.covered_lines == 2
    assert result.total_lines == 2
    assert result.meets_floor is True


def test_dotnet_windows_path_matches_short_module() -> None:
    result = check_coverage(DOTNET_CLASS, "CoreMath", 90)
    assert result.covered_lines == 2
    assert result.total_lines == 2
    assert result.meets_floor is True


def test_missing_report_raises_coverage_error(tmp_path: Path) -> None:
    with pytest.raises(CoverageError) as raised:
        check_coverage(tmp_path / "does-not-exist.xml", "core-math", 90)
    assert "does-not-exist.xml" in str(raised.value)


def test_malformed_report_raises_coverage_error(tmp_path: Path) -> None:
    broken = tmp_path / "broken.xml"
    broken.write_text("<coverage><packages>", encoding="utf-8")
    with pytest.raises(CoverageError):
        check_coverage(broken, "core-math", 90)


def test_cli_above_floor_exits_zero(capsys: pytest.CaptureFixture[str]) -> None:
    code = main(["coverage", "--report", str(ABOVE), "--module", "core-math", "--floor", "90"])
    captured = capsys.readouterr()
    assert code == 0
    assert "PASS" in captured.out
    assert "core-math" in captured.out


def test_cli_below_floor_exits_one(capsys: pytest.CaptureFixture[str]) -> None:
    code = main(["coverage", "--report", str(BELOW), "--module", "core-math", "--floor", "90"])
    captured = capsys.readouterr()
    assert code == 1
    assert "FAIL" in captured.out
    assert "20.00%" in captured.out


def test_cli_module_missing_exits_one(capsys: pytest.CaptureFixture[str]) -> None:
    code = main(["coverage", "--report", str(MISSING), "--module", "core-math", "--floor", "90"])
    captured = capsys.readouterr()
    assert code == 1
    assert "core-math" in captured.out
    assert "matched nothing" in captured.out


def test_cli_missing_report_exits_one(tmp_path: Path, capsys: pytest.CaptureFixture[str]) -> None:
    code = main(["coverage", "--report", str(tmp_path / "none.xml"), "--module", "core-math", "--floor", "90"])
    captured = capsys.readouterr()
    assert code == 1
    assert "none.xml" in captured.out


def test_cli_floor_accepts_fractional_value(capsys: pytest.CaptureFixture[str]) -> None:
    code = main(["coverage", "--report", str(ABOVE), "--module", "core-math", "--floor", "99.5"])
    captured = capsys.readouterr()
    assert code == 0
    assert "99.5" in captured.out

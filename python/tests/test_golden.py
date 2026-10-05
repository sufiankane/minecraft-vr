from __future__ import annotations

import json
from pathlib import Path
from typing import Any

import pytest

from depcheck.__main__ import main
from depcheck.golden import GOLDEN_OPS, GoldenError, verify_golden

REPO_ROOT = Path(__file__).parents[2]
FIXTURE = REPO_ROOT / "contracts" / "golden" / "transforms.json"


def _load() -> Any:
    return json.loads(FIXTURE.read_text(encoding="utf-8"))


def _write(tmp_path: Path, document: object) -> Path:
    path = tmp_path / "transforms.json"
    path.write_text(json.dumps(document, indent=2) + "\n", encoding="utf-8")
    return path


def test_repository_fixture_matches_the_independent_reference() -> None:
    assert verify_golden(FIXTURE) == []


def test_fixture_has_the_documented_case_count() -> None:
    cases = _load()["cases"]
    assert isinstance(cases, list)
    assert len(cases) == 28


def test_reference_implements_every_op_in_the_fixture() -> None:
    cases = _load()["cases"]
    assert isinstance(cases, list)
    fixture_ops = {case["op"] for case in cases}
    assert fixture_ops == set(GOLDEN_OPS)


def test_a_corrupted_expected_value_is_reported(tmp_path: Path) -> None:
    document = _load()
    cases = document["cases"]
    assert isinstance(cases, list)
    cases[0]["expected"] = 999.0
    failures = verify_golden(_write(tmp_path, document))
    assert len(failures) == 1
    assert "vec3_dot_basic" in failures[0]
    assert "999" in failures[0]


def test_a_corrupted_pose_component_is_reported(tmp_path: Path) -> None:
    document = _load()
    cases = document["cases"]
    assert isinstance(cases, list)
    for case in cases:
        if case["op"] == "pose_compose":
            case["expected"]["p"][0] = 42.0
            break
    else:
        raise AssertionError("fixture has no pose_compose case")
    failures = verify_golden(_write(tmp_path, document))
    assert len(failures) == 1
    assert "pose_compose" in failures[0]


def test_an_unknown_op_is_an_error(tmp_path: Path) -> None:
    document = _load()
    cases = document["cases"]
    assert isinstance(cases, list)
    cases[0]["op"] = "not_an_op"
    with pytest.raises(GoldenError, match="unknown op"):
        verify_golden(_write(tmp_path, document))


def test_an_unused_reference_op_is_reported(tmp_path: Path) -> None:
    document = _load()
    cases = document["cases"]
    assert isinstance(cases, list)
    document["cases"] = [case for case in cases if case["op"] != "clock_map"]
    failures = verify_golden(_write(tmp_path, document))
    assert any("no fixture case exercises" in failure for failure in failures)
    assert any("clock_map" in failure for failure in failures)


def test_missing_fixture_is_an_error(tmp_path: Path) -> None:
    with pytest.raises(GoldenError, match="not found"):
        verify_golden(tmp_path / "missing.json")


def test_malformed_fixture_is_an_error(tmp_path: Path) -> None:
    path = tmp_path / "broken.json"
    path.write_text("{not json", encoding="utf-8")
    with pytest.raises(GoldenError, match="not valid JSON"):
        verify_golden(path)


def test_cli_reports_a_matching_fixture(capsys: pytest.CaptureFixture[str]) -> None:
    code = main(["golden", "--root", str(REPO_ROOT)])
    captured = capsys.readouterr().out
    assert code == 0
    assert "match the independent Python reference" in captured


def test_cli_reports_a_corrupted_fixture(tmp_path: Path, capsys: pytest.CaptureFixture[str]) -> None:
    document = _load()
    cases = document["cases"]
    assert isinstance(cases, list)
    cases[1]["expected"] = [9.0, 9.0, 9.0]
    path = _write(tmp_path, document)
    code = main(["golden", "--fixture", str(path)])
    captured = capsys.readouterr().out
    assert code == 1
    assert "vec3_cross_basis" in captured

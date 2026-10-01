from __future__ import annotations

import subprocess
import sys
from pathlib import Path

import pytest

from depcheck.__main__ import main

FIXTURES = Path(__file__).parent / "fixtures" / "depcheck"
PROJECT_ROOT = Path(__file__).parents[1]


def run_cli(root: Path, capsys: pytest.CaptureFixture[str]) -> tuple[int, list[str]]:
    code = main(["--root", str(root)])
    captured = capsys.readouterr()
    lines = [line for line in captured.out.splitlines() if line]
    return code, lines


def test_compliant_cpp_has_no_violations(capsys: pytest.CaptureFixture[str]) -> None:
    code, lines = run_cli(FIXTURES / "cpp_compliant", capsys)
    assert code == 0
    assert lines == []


def test_compliant_dotnet_has_no_violations(capsys: pytest.CaptureFixture[str]) -> None:
    code, lines = run_cli(FIXTURES / "dotnet_compliant", capsys)
    assert code == 0
    assert lines == []


def test_forbidden_cpp_include_is_reported_with_line(capsys: pytest.CaptureFixture[str]) -> None:
    code, lines = run_cli(FIXTURES / "cpp_violation", capsys)
    assert code == 1
    assert "cpp/core-math/src/bad.cpp:3 forbidIncludes" in lines


def test_forbidden_dotnet_namespace_is_reported_with_line(capsys: pytest.CaptureFixture[str]) -> None:
    code, lines = run_cli(FIXTURES / "dotnet_namespace", capsys)
    assert code == 1
    assert "dotnet/src/Voxel/Bad.cs:3 forbidNamespaces" in lines


def test_disallowed_project_reference_is_reported_with_line(capsys: pytest.CaptureFixture[str]) -> None:
    code, lines = run_cli(FIXTURES / "dotnet_reference", capsys)
    assert code == 1
    assert "dotnet/src/Voxel/Cubeglass.Voxel.csproj:8 allowedProjectReferences" in lines


def test_missing_module_directory_is_skipped(capsys: pytest.CaptureFixture[str]) -> None:
    code, lines = run_cli(FIXTURES / "missing_module", capsys)
    assert code == 0
    assert lines == []


def invoke_module(root: Path) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        [sys.executable, "-m", "depcheck", "--root", str(root)],
        cwd=PROJECT_ROOT,
        capture_output=True,
        text=True,
        check=False,
    )


def test_module_invocation_exits_zero_when_clean() -> None:
    result = invoke_module(FIXTURES / "cpp_compliant")
    assert result.returncode == 0
    assert result.stdout.strip() == ""


def test_module_invocation_exits_one_on_violation() -> None:
    result = invoke_module(FIXTURES / "cpp_violation")
    assert result.returncode == 1
    assert "cpp/core-math/src/bad.cpp:3 forbidIncludes" in result.stdout

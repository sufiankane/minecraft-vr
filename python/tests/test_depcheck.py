from __future__ import annotations

import subprocess
import sys
from pathlib import Path

import pytest

from depcheck.__main__ import main

FIXTURES = Path(__file__).parent / "fixtures" / "depcheck"
PROJECT_ROOT = Path(__file__).parents[1]
REPO_ROOT = Path(__file__).parents[2]


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


def test_forbidden_system_io_namespace_in_voxel_is_reported(capsys: pytest.CaptureFixture[str]) -> None:
    code, lines = run_cli(FIXTURES / "dotnet_pure_io", capsys)
    assert code == 1
    assert "dotnet/src/Voxel/Bad.cs:1 forbidNamespaces" in lines


def test_allowed_namespaces_exact_and_child_are_not_reported(capsys: pytest.CaptureFixture[str]) -> None:
    code, lines = run_cli(FIXTURES / "dotnet_allow_namespace", capsys)
    assert code == 0
    assert lines == []


def test_forbidden_sibling_namespace_is_still_reported(capsys: pytest.CaptureFixture[str]) -> None:
    code, lines = run_cli(FIXTURES / "dotnet_allow_namespace_sibling", capsys)
    assert code == 1
    assert lines == [
        "dotnet/src/Voxel/Bare.cs:1 forbidNamespaces",
        "dotnet/src/Voxel/Mixed.cs:1 forbidNamespaces",
        "dotnet/src/Voxel/TasksX.cs:1 forbidNamespaces",
    ]


def test_bare_parent_namespace_is_still_reported_under_an_allow_list(capsys: pytest.CaptureFixture[str]) -> None:
    code, lines = run_cli(FIXTURES / "dotnet_allow_namespace_sibling", capsys)
    assert code == 1
    assert "dotnet/src/Voxel/Bare.cs:1 forbidNamespaces" in lines


def test_allow_entry_does_not_cover_a_longer_sibling_name(capsys: pytest.CaptureFixture[str]) -> None:
    code, lines = run_cli(FIXTURES / "dotnet_allow_namespace_sibling", capsys)
    assert code == 1
    assert "dotnet/src/Voxel/TasksX.cs:1 forbidNamespaces" in lines


def test_missing_allow_list_keeps_forbidden_namespaces_reported(capsys: pytest.CaptureFixture[str]) -> None:
    code, lines = run_cli(FIXTURES / "dotnet_allow_namespace_missing", capsys)
    assert code == 1
    assert lines == ["dotnet/src/Voxel/Bad.cs:1 forbidNamespaces"]


def test_disallowed_project_reference_is_reported_with_line(capsys: pytest.CaptureFixture[str]) -> None:
    code, lines = run_cli(FIXTURES / "dotnet_reference", capsys)
    assert code == 1
    assert "dotnet/src/Voxel/Cubeglass.Voxel.csproj:8 allowedProjectReferences" in lines


def test_missing_module_directory_is_skipped(capsys: pytest.CaptureFixture[str]) -> None:
    code, lines = run_cli(FIXTURES / "missing_module", capsys)
    assert code == 0
    assert lines == []


def test_global_qualified_usings_are_reported_with_line(capsys: pytest.CaptureFixture[str]) -> None:
    code, lines = run_cli(FIXTURES / "dotnet_global_using", capsys)
    assert code == 1
    assert "dotnet/src/CoreMath/Bad.cs:3 forbidNamespaces" in lines
    assert "dotnet/src/CoreMath/GlobalUsings.cs:1 forbidNamespaces" in lines


def test_generated_build_output_is_ignored(tmp_path: Path, capsys: pytest.CaptureFixture[str]) -> None:
    root = tmp_path / "root"
    contracts = root / "contracts"
    contracts.mkdir(parents=True)
    (contracts / "layers.json").write_text(
        '{"dotnet": {"Cubeglass.CoreMath": {"forbidNamespaces": ["System.IO", "System.Threading"]}}}',
        encoding="utf-8",
    )
    source = root / "dotnet" / "src" / "CoreMath"
    source.mkdir(parents=True)
    (source / "Real.cs").write_text("using System.IO;\n", encoding="utf-8")
    for directory in ("obj", "bin"):
        generated = source / directory / "Debug" / "Generated.cs"
        generated.parent.mkdir(parents=True, exist_ok=True)
        generated.write_text("using System.Threading;\n", encoding="utf-8")

    code, lines = run_cli(root, capsys)
    assert code == 1
    assert lines == ["dotnet/src/CoreMath/Real.cs:1 forbidNamespaces"]


def invoke_module(root: Path) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        [sys.executable, "-m", "depcheck", "--root", str(root)],
        cwd=PROJECT_ROOT,
        capture_output=True,
        text=True,
        check=False,
    )


def test_module_invocation_from_repo_root_exits_zero() -> None:
    result = subprocess.run(
        [sys.executable, "-m", "depcheck", "--root", "."],
        cwd=REPO_ROOT,
        capture_output=True,
        text=True,
        check=False,
    )
    assert result.returncode == 0
    assert result.stdout.strip() == ""


def test_module_invocation_exits_one_on_violation() -> None:
    result = invoke_module(FIXTURES / "cpp_violation")
    assert result.returncode == 1
    assert "cpp/core-math/src/bad.cpp:3 forbidIncludes" in result.stdout

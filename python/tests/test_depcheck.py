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


def test_stale_configured_modules_are_reported(capsys: pytest.CaptureFixture[str]) -> None:
    code, lines = run_cli(FIXTURES / "missing_module", capsys)
    assert code == 1
    assert lines == [
        "cpp/core-math:0 layersEntryStale",
        "cpp/handcore:0 layersEntryStale",
        "dotnet/src/Gameplay:0 layersEntryStale",
    ]


def _write_layers(root: Path, payload: str) -> None:
    contracts = root / "contracts"
    contracts.mkdir(parents=True, exist_ok=True)
    (contracts / "layers.json").write_text(payload, encoding="utf-8")


def _add_cpp_module(root: Path, name: str) -> None:
    module = root / "cpp" / name
    module.mkdir(parents=True, exist_ok=True)
    (module / "CMakeLists.txt").write_text(
        f"cmake_minimum_required(VERSION 3.20)\nproject(cg_{name} CXX)\n", encoding="utf-8"
    )


def _write_cpp_source(root: Path, relative: str, text: str) -> None:
    path = root / relative
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(text, encoding="utf-8")


def _write_cmake(root: Path, relative: str) -> None:
    path = root / "cpp" / relative / "CMakeLists.txt"
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text("cmake_minimum_required(VERSION 3.20)\n", encoding="utf-8")


def _add_dotnet_project(root: Path, project: str) -> None:
    directory = root / "dotnet" / "src" / project.rsplit(".", 1)[-1]
    directory.mkdir(parents=True, exist_ok=True)
    (directory / f"{project}.csproj").write_text("<Project />", encoding="utf-8")


def test_cpp_module_without_layers_entry_fails(
    tmp_path: Path, capsys: pytest.CaptureFixture[str]
) -> None:
    _write_layers(tmp_path, '{"cpp": {}}')
    _add_cpp_module(tmp_path, "recorder")
    code, lines = run_cli(tmp_path, capsys)
    assert code == 1
    assert lines == ["cpp/recorder:0 layersEntryMissing"]


def test_dotnet_project_without_layers_entry_fails(
    tmp_path: Path, capsys: pytest.CaptureFixture[str]
) -> None:
    _write_layers(tmp_path, '{"dotnet": {}}')
    _add_dotnet_project(tmp_path, "Cubeglass.Infer")
    code, lines = run_cli(tmp_path, capsys)
    assert code == 1
    assert lines == ["dotnet/src/Infer:0 layersEntryMissing"]


def test_stale_cpp_and_dotnet_layers_entries_fail(
    tmp_path: Path, capsys: pytest.CaptureFixture[str]
) -> None:
    _write_layers(
        tmp_path,
        '{"cpp": {"core-math": {"forbidIncludes": []}}, "dotnet": {"Cubeglass.HandService": {}}}',
    )
    code, lines = run_cli(tmp_path, capsys)
    assert code == 1
    assert lines == [
        "cpp/core-math:0 layersEntryStale",
        "dotnet/src/HandService:0 layersEntryStale",
    ]


def test_complete_module_tree_passes(tmp_path: Path, capsys: pytest.CaptureFixture[str]) -> None:
    _write_layers(
        tmp_path,
        '{"cpp": {"core-math": {"forbidIncludes": []}}, "dotnet": {"Cubeglass.CoreMath": {}}}',
    )
    _add_cpp_module(tmp_path, "core-math")
    _add_dotnet_project(tmp_path, "Cubeglass.CoreMath")
    code, lines = run_cli(tmp_path, capsys)
    assert code == 0
    assert lines == []


def test_deferred_cpp_module_may_be_absent(tmp_path: Path, capsys: pytest.CaptureFixture[str]) -> None:
    _write_layers(
        tmp_path,
        '{"cpp": {"handcore": {"forbidIncludes": []}}, "deferred": {"cpp": ["handcore"], "dotnet": []}}',
    )
    code, lines = run_cli(tmp_path, capsys)
    assert code == 0
    assert lines == []


def test_cpp_tests_and_tools_directories_are_not_modules(
    tmp_path: Path, capsys: pytest.CaptureFixture[str]
) -> None:
    _write_layers(tmp_path, '{"cpp": {}}')
    for name in ("tests", "tools", "build-linux", "build"):
        _add_cpp_module(tmp_path, name)
    code, lines = run_cli(tmp_path, capsys)
    assert code == 0
    assert lines == []


def test_comment_between_include_and_header_is_still_caught(
    tmp_path: Path, capsys: pytest.CaptureFixture[str]
) -> None:
    _write_layers(tmp_path, '{"cpp": {"core-math": {"forbidIncludes": ["windows.h"]}}}')
    _write_cmake(tmp_path, "core-math")
    _write_cpp_source(tmp_path, "cpp/core-math/src/bad.cpp", "#include /* deliberate gap */ <windows.h>\n")

    code, lines = run_cli(tmp_path, capsys)
    assert code == 1
    assert lines == ["cpp/core-math/src/bad.cpp:1 forbidIncludes"]


def test_include_next_is_caught(tmp_path: Path, capsys: pytest.CaptureFixture[str]) -> None:
    _write_layers(tmp_path, '{"cpp": {"core-math": {"forbidIncludes": ["thread"]}}}')
    _write_cmake(tmp_path, "core-math")
    _write_cpp_source(tmp_path, "cpp/core-math/src/bad.cpp", "#include_next <thread>\n")

    code, lines = run_cli(tmp_path, capsys)
    assert code == 1
    assert lines == ["cpp/core-math/src/bad.cpp:1 forbidIncludes"]


def test_macro_include_is_flagged(tmp_path: Path, capsys: pytest.CaptureFixture[str]) -> None:
    _write_layers(tmp_path, '{"cpp": {"core-math": {"forbidIncludes": []}}}')
    _write_cmake(tmp_path, "core-math")
    _write_cpp_source(tmp_path, "cpp/core-math/src/bad.cpp", "#include GENERATED_HEADER\n")

    code, lines = run_cli(tmp_path, capsys)
    assert code == 1
    assert lines == ["cpp/core-math/src/bad.cpp:1 macroInclude"]


def test_import_directive_is_flagged_when_not_a_header_literal(
    tmp_path: Path, capsys: pytest.CaptureFixture[str]
) -> None:
    _write_layers(tmp_path, '{"cpp": {"core-math": {"forbidIncludes": []}}}')
    _write_cmake(tmp_path, "core-math")
    _write_cpp_source(tmp_path, "cpp/core-math/src/bad.cpp", "#import LEGACY_HEADER\n")

    code, lines = run_cli(tmp_path, capsys)
    assert code == 1
    assert lines == ["cpp/core-math/src/bad.cpp:1 macroInclude"]


def test_commented_out_includes_are_not_flagged(tmp_path: Path, capsys: pytest.CaptureFixture[str]) -> None:
    _write_layers(tmp_path, '{"cpp": {"core-math": {"forbidIncludes": ["windows.h"]}}}')
    _write_cmake(tmp_path, "core-math")
    _write_cpp_source(
        tmp_path,
        "cpp/core-math/src/ok.cpp",
        "// #include <windows.h>\n/* #include <windows.h> */\n#include /* not a comment */ <cstdint>\n",
    )

    code, lines = run_cli(tmp_path, capsys)
    assert code == 0
    assert lines == []


def test_nested_cpp_module_without_layers_entry_fails(
    tmp_path: Path, capsys: pytest.CaptureFixture[str]
) -> None:
    _write_layers(tmp_path, '{"cpp": {}}')
    _write_cmake(tmp_path, "group/module")

    code, lines = run_cli(tmp_path, capsys)
    assert code == 1
    assert lines == ["cpp/group/module:0 layersEntryMissing"]


def test_nested_cpp_module_with_layers_entry_passes(
    tmp_path: Path, capsys: pytest.CaptureFixture[str]
) -> None:
    _write_layers(tmp_path, '{"cpp": {"group/module": {"forbidIncludes": []}}}')
    _write_cmake(tmp_path, "group/module")

    code, lines = run_cli(tmp_path, capsys)
    assert code == 0
    assert lines == []


def test_comment_inside_using_is_still_caught(tmp_path: Path, capsys: pytest.CaptureFixture[str]) -> None:
    _write_layers(tmp_path, '{"dotnet": {"Cubeglass.Voxel": {"forbidNamespaces": ["System.IO"]}}}')
    source = tmp_path / "dotnet" / "src" / "Voxel"
    source.mkdir(parents=True)
    (source / "Cubeglass.Voxel.csproj").write_text("<Project />", encoding="utf-8")
    (source / "Bad.cs").write_text("using /* deliberate gap */ System.IO;\n", encoding="utf-8")

    code, lines = run_cli(tmp_path, capsys)
    assert code == 1
    assert lines == ["dotnet/src/Voxel/Bad.cs:1 forbidNamespaces"]


def test_commented_out_using_is_not_flagged(tmp_path: Path, capsys: pytest.CaptureFixture[str]) -> None:
    _write_layers(tmp_path, '{"dotnet": {"Cubeglass.Voxel": {"forbidNamespaces": ["System.IO"]}}}')
    source = tmp_path / "dotnet" / "src" / "Voxel"
    source.mkdir(parents=True)
    (source / "Cubeglass.Voxel.csproj").write_text("<Project />", encoding="utf-8")
    (source / "Ok.cs").write_text("// using System.IO;\n/* using System.IO; */\n", encoding="utf-8")

    code, lines = run_cli(tmp_path, capsys)
    assert code == 0
    assert lines == []


def test_nested_dotnet_project_without_layers_entry_fails(
    tmp_path: Path, capsys: pytest.CaptureFixture[str]
) -> None:
    _write_layers(tmp_path, '{"dotnet": {}}')
    project = tmp_path / "dotnet" / "src" / "Group" / "Infer"
    project.mkdir(parents=True)
    (project / "Cubeglass.Infer.csproj").write_text("<Project />", encoding="utf-8")

    code, lines = run_cli(tmp_path, capsys)
    assert code == 1
    assert lines == ["dotnet/src/Group/Infer:0 layersEntryMissing"]


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
    (source / "Cubeglass.CoreMath.csproj").write_text("<Project />", encoding="utf-8")
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

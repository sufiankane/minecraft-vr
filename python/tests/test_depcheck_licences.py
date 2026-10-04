from __future__ import annotations

import subprocess
import sys
from pathlib import Path

import pytest

from depcheck.__main__ import main

FIXTURES = Path(__file__).parent / "fixtures" / "depcheck_licences"
REPO_ROOT = Path(__file__).parents[2]


def run_cli(root: Path, capsys: pytest.CaptureFixture[str]) -> tuple[int, list[str]]:
    code = main(["licences", "--root", str(root)])
    captured = capsys.readouterr()
    lines = [line for line in captured.out.splitlines() if line]
    return code, lines


def test_complete_allowlist_passes(capsys: pytest.CaptureFixture[str]) -> None:
    code, lines = run_cli(FIXTURES / "complete", capsys)
    assert code == 0
    assert lines == []


def test_declared_package_missing_from_allowlist_fails_and_is_listed(
    capsys: pytest.CaptureFixture[str],
) -> None:
    code, lines = run_cli(FIXTURES / "missing", capsys)
    assert code == 1
    assert lines == ["benchmark"]


def test_missing_allowlist_file_treats_every_package_as_unknown(
    tmp_path: Path, capsys: pytest.CaptureFixture[str]
) -> None:
    root = tmp_path / "root"
    (root / "cpp").mkdir(parents=True)
    (root / "cpp" / "vcpkg.json").write_text('{"dependencies": ["gtest"]}', encoding="utf-8")
    (root / "dotnet").mkdir(parents=True)
    (root / "dotnet" / "Directory.Packages.props").write_text("<Project />", encoding="utf-8")

    code, lines = run_cli(root, capsys)
    assert code == 1
    assert lines == ["gtest"]


def test_missing_vcpkg_manifest_fails_and_names_the_path(
    tmp_path: Path, capsys: pytest.CaptureFixture[str]
) -> None:
    root = tmp_path / "root"
    (root / "dotnet").mkdir(parents=True)
    (root / "dotnet" / "Directory.Packages.props").write_text("<Project />", encoding="utf-8")

    code, lines = run_cli(root, capsys)
    assert code == 1
    assert any("vcpkg.json" in line for line in lines)


def test_missing_dotnet_manifest_fails_and_names_the_path(
    tmp_path: Path, capsys: pytest.CaptureFixture[str]
) -> None:
    root = tmp_path / "root"
    (root / "cpp").mkdir(parents=True)
    (root / "cpp" / "vcpkg.json").write_text('{"dependencies": []}', encoding="utf-8")

    code, lines = run_cli(root, capsys)
    assert code == 1
    assert any("Directory.Packages.props" in line for line in lines)


def test_root_option_defaults_to_current_directory(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch, capsys: pytest.CaptureFixture[str]
) -> None:
    (tmp_path / "cpp").mkdir()
    (tmp_path / "cpp" / "vcpkg.json").write_text('{"dependencies": []}', encoding="utf-8")
    (tmp_path / "dotnet").mkdir()
    (tmp_path / "dotnet" / "Directory.Packages.props").write_text("<Project />", encoding="utf-8")
    (tmp_path / "contracts").mkdir()
    (tmp_path / "contracts" / "licence-allowlist.json").write_text("{}", encoding="utf-8")
    monkeypatch.chdir(tmp_path)
    code = main(["licences"])
    captured = capsys.readouterr()
    assert code == 0
    assert captured.out == ""


def test_repository_root_is_compliant() -> None:
    result = subprocess.run(
        [sys.executable, "-m", "depcheck", "licences", "--root", "."],
        cwd=REPO_ROOT,
        capture_output=True,
        text=True,
        check=False,
    )
    assert result.returncode == 0
    assert result.stdout.strip() == ""


def write_manifest_root(root: Path, allowlist: str) -> None:
    (root / "cpp").mkdir(parents=True, exist_ok=True)
    (root / "cpp" / "vcpkg.json").write_text('{"dependencies": ["gtest"]}', encoding="utf-8")
    (root / "dotnet").mkdir(parents=True, exist_ok=True)
    (root / "dotnet" / "Directory.Packages.props").write_text("<Project />", encoding="utf-8")
    (root / "contracts").mkdir(parents=True, exist_ok=True)
    (root / "contracts" / "licence-allowlist.json").write_text(allowlist, encoding="utf-8")


def test_unknown_spdx_licence_id_fails_and_names_the_package(
    tmp_path: Path, capsys: pytest.CaptureFixture[str]
) -> None:
    root = tmp_path / "root"
    write_manifest_root(root, '{"gtest": "BSD"}')
    code, lines = run_cli(root, capsys)
    assert code == 1
    assert lines == [
        (
            "contracts/licence-allowlist.json: package \"gtest\" has unknown SPDX licence id "
            "'BSD'; use a known SPDX identifier"
        )
    ]


def test_empty_spdx_licence_id_fails(tmp_path: Path, capsys: pytest.CaptureFixture[str]) -> None:
    root = tmp_path / "root"
    write_manifest_root(root, '{"gtest": ""}')
    code, lines = run_cli(root, capsys)
    assert code == 1
    assert any("unknown SPDX licence id" in line for line in lines)


def test_known_spdx_licence_ids_pass(tmp_path: Path, capsys: pytest.CaptureFixture[str]) -> None:
    root = tmp_path / "root"
    write_manifest_root(root, '{"gtest": "BSD-3-Clause"}')
    code, lines = run_cli(root, capsys)
    assert code == 0
    assert lines == []


def test_default_command_still_checks_dependency_rules(
    capsys: pytest.CaptureFixture[str],
) -> None:
    code = main(["--root", str(REPO_ROOT)])
    captured = capsys.readouterr()
    assert code == 0
    assert captured.out == ""

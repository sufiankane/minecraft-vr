from __future__ import annotations

import json
from pathlib import Path
from typing import Any

import pytest

from depcheck.__main__ import main
from depcheck.nuget import NugetError, parse_report, scan_report


def _report() -> Any:
    return {
        "version": 1,
        "parameters": "--vulnerable --include-transitive",
        "projects": [
            {
                "path": "C:/repo/dotnet/src/Voxel/Cubeglass.Voxel.csproj",
                "frameworks": [
                    {
                        "framework": "net10.0",
                        "topLevelPackages": [
                            {
                                "id": "Vulnerable.Package",
                                "requestedVersion": "1.0.0",
                                "resolvedVersion": "1.0.0",
                                "vulnerabilities": [
                                    {
                                        "severity": "High",
                                        "advisoryurl": "https://github.com/advisories/GHSA-0000-0000-0000",
                                    }
                                ],
                            },
                            {"id": "Safe.Package", "resolvedVersion": "2.0.0"},
                        ],
                        "transitivePackages": [
                            {
                                "id": "Deep.Package",
                                "resolvedVersion": "3.0.0",
                                "vulnerabilities": [
                                    {
                                        "severity": "Moderate",
                                        "advisoryurl": "https://github.com/advisories/GHSA-1111-1111-1111",
                                    }
                                ],
                            }
                        ],
                    }
                ],
            }
        ],
    }


def test_parse_report_finds_top_level_and_transitive_vulnerabilities() -> None:
    findings = parse_report(json.dumps(_report()))
    assert len(findings) == 2
    assert "Vulnerable.Package 1.0.0" in findings[0]
    assert "High" in findings[0]
    assert "GHSA-0000-0000-0000" in findings[0]
    assert "Deep.Package 3.0.0" in findings[1]
    assert "Moderate" in findings[1]


def test_parse_report_without_vulnerabilities_is_empty() -> None:
    report = _report()
    framework = report["projects"][0]["frameworks"][0]
    framework["topLevelPackages"] = [{"id": "Safe.Package", "resolvedVersion": "2.0.0"}]
    framework["transitivePackages"] = []
    assert parse_report(json.dumps(report)) == []


def test_parse_report_without_projects_is_an_error() -> None:
    with pytest.raises(NugetError, match="projects"):
        parse_report('{"version": 1}')


def test_project_without_frameworks_is_not_an_error() -> None:
    # `dotnet list package --format json` omits `frameworks` for a project with
    # no vulnerable packages.
    assert parse_report('{"projects": [{"path": "src/Safe.csproj"}]}') == []


def test_scan_report_accepts_a_utf16_bom_report(tmp_path: Path) -> None:
    # PowerShell `>` redirection on Windows writes UTF-16 with a BOM.
    path = tmp_path / "report.json"
    path.write_bytes(json.dumps(_report()).encode("utf-16"))
    findings = scan_report(path)
    assert len(findings) == 2


def test_parse_report_with_human_text_is_an_error() -> None:
    with pytest.raises(NugetError, match="not valid JSON"):
        parse_report("The following sources were used:\n  https://api.nuget.org/v3/index.json\n")


def test_parse_report_missing_advisory_is_an_error() -> None:
    report = _report()
    vulnerability = report["projects"][0]["frameworks"][0]["topLevelPackages"][0][
        "vulnerabilities"
    ][0]
    del vulnerability["advisoryurl"]
    with pytest.raises(NugetError, match="advisoryurl"):
        parse_report(json.dumps(report))


def test_scan_report_missing_file_is_an_error(tmp_path: Path) -> None:
    with pytest.raises(NugetError, match="cannot read"):
        scan_report(tmp_path / "missing.json")


def test_cli_fails_on_findings(tmp_path: Path, capsys: pytest.CaptureFixture[str]) -> None:
    path = tmp_path / "report.json"
    path.write_text(json.dumps(_report()), encoding="utf-8")
    code = main(["nuget", "--report", str(path)])
    captured = capsys.readouterr().out
    assert code == 1
    assert "Vulnerable.Package" in captured


def test_cli_passes_without_findings(tmp_path: Path, capsys: pytest.CaptureFixture[str]) -> None:
    path = tmp_path / "report.json"
    path.write_text('{"projects": []}', encoding="utf-8")
    code = main(["nuget", "--report", str(path)])
    captured = capsys.readouterr().out
    assert code == 0
    assert "no vulnerable" in captured

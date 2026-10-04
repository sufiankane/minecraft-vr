from __future__ import annotations

import shutil
import subprocess
from pathlib import Path

import pytest

REPO_ROOT = Path(__file__).parents[2]
SCRIPT = REPO_ROOT / "scripts" / "check-unity-results.ps1"
POWERSHELL = shutil.which("pwsh") or shutil.which("powershell")

pytestmark = pytest.mark.skipif(POWERSHELL is None, reason="PowerShell is not available")

_EXECUTION_POLICY_ARGS = (
    ["-ExecutionPolicy", "Bypass"]
    if POWERSHELL is not None and Path(POWERSHELL).stem.lower() == "powershell"
    else []
)


def _results(
    *,
    total: int = 3,
    failed: int = 0,
    skipped: int = 0,
    assemblies: tuple[str, ...] = ("Cubeglass.Unity.Bridge.Tests.dll",),
) -> str:
    suites = "\n".join(f'  <test-suite type="Assembly" name="{name}" />' for name in assemblies)
    return (
        '<?xml version="1.0" encoding="utf-8"?>\n'
        f'<test-run total="{total}" passed="{total - failed - skipped}" failed="{failed}" skipped="{skipped}">\n'
        f"{suites}\n"
        "</test-run>\n"
    )


def _run(
    results_path: Path,
    *,
    mode: str = "EditMode",
    assemblies: str = "Cubeglass.Unity.Bridge.Tests.dll",
    test_exit_code: int = 0,
) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        [
            POWERSHELL,
            "-NoProfile",
            "-NonInteractive",
            *_EXECUTION_POLICY_ARGS,
            "-File",
            str(SCRIPT),
            "-Mode",
            mode,
            "-ResultsPath",
            str(results_path),
            "-RequiredAssemblies",
            assemblies,
            "-TestExitCode",
            str(test_exit_code),
        ],
        capture_output=True,
        text=True,
        check=False,
    )


def test_clean_results_pass(tmp_path: Path) -> None:
    results = tmp_path / "results.xml"
    results.write_text(_results(), encoding="utf-8")

    completed = _run(results)

    assert completed.returncode == 0, completed.stdout + completed.stderr
    assert "EditMode tests passed: 3 test(s)." in completed.stdout


def test_zero_tests_fail(tmp_path: Path) -> None:
    results = tmp_path / "results.xml"
    results.write_text(_results(total=0), encoding="utf-8")

    completed = _run(results)

    assert completed.returncode == 1
    assert "total=0" in completed.stdout


def test_failed_tests_fail(tmp_path: Path) -> None:
    results = tmp_path / "results.xml"
    results.write_text(_results(total=3, failed=1), encoding="utf-8")

    completed = _run(results)

    assert completed.returncode == 1
    assert "failed=1" in completed.stdout


def test_skipped_tests_fail(tmp_path: Path) -> None:
    results = tmp_path / "results.xml"
    results.write_text(_results(total=3, skipped=1), encoding="utf-8")

    completed = _run(results)

    assert completed.returncode == 1
    assert "skipped=1" in completed.stdout


def test_missing_required_assembly_fails(tmp_path: Path) -> None:
    results = tmp_path / "results.xml"
    results.write_text(_results(assemblies=("Some.Other.Tests.dll",)), encoding="utf-8")

    completed = _run(results, assemblies="Cubeglass.Unity.Bridge.Tests.dll,Some.Other.Tests.dll")

    assert completed.returncode == 1
    assert "Cubeglass.Unity.Bridge.Tests.dll" in completed.stdout


def test_missing_results_file_fails(tmp_path: Path) -> None:
    completed = _run(tmp_path / "absent.xml")

    assert completed.returncode == 1
    assert "no NUnit results" in completed.stdout


def test_nonzero_unity_exit_code_fails_even_with_clean_xml(tmp_path: Path) -> None:
    results = tmp_path / "results.xml"
    results.write_text(_results(), encoding="utf-8")

    completed = _run(results, test_exit_code=1)

    assert completed.returncode == 1
    assert "exit=1" in completed.stdout


def test_no_test_run_element_fails(tmp_path: Path) -> None:
    results = tmp_path / "results.xml"
    results.write_text('<?xml version="1.0" encoding="utf-8"?>\n<other />\n', encoding="utf-8")

    completed = _run(results)

    assert completed.returncode == 1
    assert "no /test-run element" in completed.stdout

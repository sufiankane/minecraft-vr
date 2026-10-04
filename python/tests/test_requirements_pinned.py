from __future__ import annotations

import re
from pathlib import Path

import pytest

REQUIREMENTS = Path(__file__).parents[1]
CI_FILE = REQUIREMENTS / "requirements-ci.txt"
DEV_FILE = REQUIREMENTS / "requirements-dev.txt"
AUDIT_FILE = REQUIREMENTS / "requirements-audit.txt"
ALL_FILES = [CI_FILE, DEV_FILE, AUDIT_FILE]
FILE_IDS = ["ci", "dev", "audit"]

_PIN_RE = re.compile(r"^[A-Za-z0-9_.\-\[\]]+==[^\s;]+$")


def _logical_lines(path: Path) -> list[str]:
    entries: list[str] = []
    current = ""
    for raw in path.read_text(encoding="utf-8").splitlines():
        stripped = raw.strip()
        if not stripped or stripped.startswith("#"):
            continue
        stripped = stripped.removesuffix("\\").rstrip()
        current += stripped
        if raw.rstrip().endswith("\\"):
            continue
        entries.append(current)
        current = ""
    if current:
        entries.append(current)
    return entries


def _pins(path: Path) -> list[str]:
    return [entry.split("--hash", 1)[0].strip() for entry in _logical_lines(path) if not entry.startswith("-")]


@pytest.mark.parametrize("path", ALL_FILES, ids=FILE_IDS)
def test_requirements_require_hashes(path: Path) -> None:
    entries = _logical_lines(path)
    assert "--require-hashes" in entries


@pytest.mark.parametrize("path", ALL_FILES, ids=FILE_IDS)
def test_every_pin_carries_sha256_hashes(path: Path) -> None:
    for entry in _logical_lines(path):
        if entry.startswith("-"):
            continue
        pin = entry.split("--hash", 1)[0].strip()
        assert _PIN_RE.match(pin), f"{path.name}: unpinned or ranged requirement: {pin}"
        assert "--hash=sha256:" in entry, f"{path.name}: pin without a hash: {pin}"


@pytest.mark.parametrize("path", ALL_FILES, ids=FILE_IDS)
def test_no_index_override_can_swap_artefacts(path: Path) -> None:
    text = path.read_text(encoding="utf-8")
    assert "--index-url" not in text
    assert "--extra-index-url" not in text
    assert "--trusted-host" not in text


def test_audit_file_pins_the_supply_chain_scanner() -> None:
    assert any(pin.startswith("pip-audit==2.10.1") for pin in _pins(AUDIT_FILE))


def test_ci_file_pins_the_documented_top_level_dependencies() -> None:
    pins = _pins(CI_FILE)
    assert any(pin.startswith("gcovr==8.6") for pin in pins)
    assert any(pin.startswith("clang-format==23.1.2") for pin in pins)


def test_dev_file_pins_the_documented_top_level_dependencies() -> None:
    pins = _pins(DEV_FILE)
    for expected in ("pytest==9.1.1", "pytest-cov==7.1.0", "mypy==2.3.1", "ruff==0.16.9"):
        assert any(pin.startswith(expected) for pin in pins), expected


def test_dev_file_pins_every_transitive_dependency() -> None:
    names = {pin.split("==", 1)[0] for pin in _pins(DEV_FILE)}
    assert {"coverage[toml]", "pluggy", "iniconfig", "packaging", "mypy-extensions"} <= names

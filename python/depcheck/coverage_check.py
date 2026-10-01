"""Cobertura XML line-coverage floors for the Cubeglass gates.

The coverage gate parses a Cobertura XML report produced by any toolchain used in
CI (``gcovr`` for C++, ``coverlet`` for .NET, ``coverage.py`` for Python) and
verifies that a named module meets a minimum line-coverage percentage.

A module is matched case-insensitively against package names and class
``name``/``filename`` attributes. That covers every producer: ``gcovr`` and
``coverage.py`` place the module directory in the source path, while ``coverlet``
names the package after the assembly (``Cubeglass.CoreMath``) and reports no
coverable lines for a type that emits no IL.

Only production sources count: a file path matches only when the module is the
leading component after the source root, and any path with a ``test``/``tests``
segment before the match is ignored, so a test suite can never inflate (or
deflate) the module's floor.
"""

from __future__ import annotations

import re
import xml.etree.ElementTree as ET
from dataclasses import dataclass
from pathlib import Path

_SOURCE_ROOT_SEGMENTS: frozenset[str] = frozenset({"cpp", "dotnet", "src", "python"})
_TEST_SEGMENTS: frozenset[str] = frozenset({"test", "tests"})


class CoverageError(Exception):
    """Raised when a coverage report cannot be read or parsed."""


@dataclass(frozen=True)
class CoverageResult:
    """Outcome of a single module-versus-floor coverage check."""

    report: Path
    module: str
    floor: float
    matched_units: int
    covered_lines: int
    total_lines: int

    @property
    def rate(self) -> float:
        """Observed line-coverage percentage for the matched module."""

        if self.matched_units == 0:
            return 0.0
        if self.total_lines == 0:
            return 100.0
        return 100.0 * self.covered_lines / self.total_lines

    @property
    def meets_floor(self) -> bool:
        """Whether the module matched and reached the configured floor."""

        return self.matched_units > 0 and self.rate + 1e-9 >= self.floor

    def summary(self) -> str:
        """Human-readable one-line result naming module, floor and observed rate."""

        verdict = "PASS" if self.meets_floor else "FAIL"
        if self.matched_units == 0:
            return (
                f"coverage {verdict}: module '{self.module}' matched nothing in "
                f"{self.report}; observed 0.00%; floor {self.floor:g}%"
            )
        if self.total_lines == 0:
            return (
                f"WARNING: module '{self.module}' matched {self.matched_units} units "
                f"but has 0 coverable lines; floor not enforced"
            )
        return (
            f"coverage {verdict}: module '{self.module}' observed {self.rate:.2f}% "
            f"({self.covered_lines}/{self.total_lines} lines); floor {self.floor:g}%"
        )


def _normalise(value: str) -> str:
    return value.replace("\\", "/").lower()


def _tokens(value: str) -> list[str]:
    return [part for part in re.split(r"[./]", value) if part]


def _has_test_prefix(parts: list[str], index: int) -> bool:
    """Whether a ``test``/``tests`` segment appears before ``index``."""

    return any(part in _TEST_SEGMENTS for part in parts[:index])


def _name_matches(module_tokens: list[str], candidate_tokens: list[str]) -> bool:
    """Boundary match for a dotted package/type name, ignoring test prefixes."""

    width = len(module_tokens)
    for index in range(len(candidate_tokens) - width + 1):
        if candidate_tokens[index : index + width] != module_tokens:
            continue
        if not _has_test_prefix(candidate_tokens, index):
            return True
    return False


def _path_matches(module_tokens: list[str], text: str) -> bool:
    """Match a path only when the module is the leading component after the
    source root (index 0, or immediately after a known source-root segment) and
    no ``test``/``tests`` segment precedes it. That keeps test sources such as
    ``tests/core-math/version_test.cpp`` out of a production module's floor.
    """

    components = [part for part in text.split("/") if part]
    width = len(module_tokens)
    for index in range(len(components) - width + 1):
        if components[index : index + width] != module_tokens:
            continue
        if _has_test_prefix(components, index):
            continue
        if index == 0 or components[index - 1] in _SOURCE_ROOT_SEGMENTS:
            return True
    return False


def _matches(module: str, candidate: str) -> bool:
    """Match a module on path-component or dotted-name boundaries.

    Substring matching is deliberately avoided: it would let the module
    ``depcheck`` match ``tests/test_depcheck.py`` and inflate the rate with test
    code. Path candidates additionally require the module to be the leading
    component after the source root, and name candidates reject a ``test``/
    ``tests`` prefix.
    """

    module_tokens = _tokens(_normalise(module.strip()))
    if not module_tokens:
        return False
    text = _normalise(candidate)
    if "/" in text:
        return _path_matches(module_tokens, text)
    return _name_matches(module_tokens, _tokens(text))


def _hits(line: ET.Element) -> int:
    try:
        return int(float(line.get("hits", "0")))
    except ValueError:
        return 0


def _parse(report: Path) -> ET.Element:
    if not report.is_file():
        raise CoverageError(f"coverage report not found: {report}")
    try:
        return ET.parse(report).getroot()
    except ET.ParseError as error:
        raise CoverageError(f"coverage report is not valid XML: {report}: {error}") from error
    except OSError as error:
        raise CoverageError(f"coverage report could not be read: {report}: {error}") from error


def check_coverage(report: Path, module: str, floor: float) -> CoverageResult:
    """Evaluate ``module`` line coverage in ``report`` against ``floor`` percent."""

    root = _parse(report)
    matched_units = 0
    covered_lines = 0
    total_lines = 0

    packages = root.find("packages")
    if packages is not None:
        for package in packages.findall("package"):
            if _matches(module, package.get("name", "")):
                matched_units += 1
            classes = package.find("classes")
            if classes is None:
                continue
            for cls in classes.findall("class"):
                if not (
                    _matches(module, cls.get("filename", "")) or _matches(module, cls.get("name", ""))
                ):
                    continue
                matched_units += 1
                lines = cls.find("lines")
                if lines is None:
                    continue
                for line in lines.findall("line"):
                    total_lines += 1
                    if _hits(line) > 0:
                        covered_lines += 1

    return CoverageResult(
        report=report,
        module=module,
        floor=floor,
        matched_units=matched_units,
        covered_lines=covered_lines,
        total_lines=total_lines,
    )

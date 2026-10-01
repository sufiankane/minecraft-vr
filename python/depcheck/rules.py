"""Dependency-rule checking for Cubeglass.

The checker reads ``contracts/layers.json`` under a repository root and enforces
the declared inward-only dependency rules for C++ (``#include`` directives) and
C# (``using`` directives and ``<ProjectReference>`` entries).

Only the standard library is used, and all parsing is line-based and
deterministic. Module directories named in ``layers.json`` but absent from the
tree are skipped rather than treated as errors, so forward-looking entries such
as ``handcore`` are safe before the module exists.
"""

from __future__ import annotations

import json
import re
from dataclasses import dataclass
from pathlib import Path
from typing import cast

RULE_FORBID_INCLUDES = "forbidIncludes"
RULE_FORBID_NAMESPACES = "forbidNamespaces"
RULE_ALLOWED_PROJECT_REFERENCES = "allowedProjectReferences"

_CPP_SUFFIXES: frozenset[str] = frozenset({".cpp", ".cc", ".cxx", ".hpp", ".hh", ".hxx", ".h"})

_IGNORED_DIRECTORIES: frozenset[str] = frozenset({"obj", "bin"})

_INCLUDE_RE = re.compile(r'^\s*#\s*include\s*[<"]([^>"]+)[>"]')
_USING_RE = re.compile(
    r"^\s*(?:global\s+)?using\s+(?:static\s+)?(?:[A-Za-z_]\w*\s*=\s*)?"
    r"(?:global\s*::\s*)?([A-Za-z_][\w.]*)\s*;"
)
_PROJECT_REFERENCE_RE = re.compile(r"<ProjectReference\b[^>]*?\bInclude\s*=\s*\"([^\"]+)\"")


@dataclass(frozen=True)
class Violation:
    """A single dependency-rule violation, reported as ``path:line rule``."""

    path: str
    line: int
    rule: str

    def __str__(self) -> str:
        return f"{self.path}:{self.line} {self.rule}"


@dataclass(frozen=True)
class _CppModule:
    forbid_includes: tuple[str, ...]


@dataclass(frozen=True)
class _DotnetModule:
    forbid_namespaces: tuple[str, ...]
    allowed_project_references: tuple[str, ...]


@dataclass(frozen=True)
class _Rules:
    cpp: dict[str, _CppModule]
    dotnet: dict[str, _DotnetModule]


def _load_object(path: Path) -> dict[str, object]:
    with path.open(encoding="utf-8") as handle:
        data: object = json.load(handle)
    if not isinstance(data, dict):
        raise TypeError(f"{path}: expected a JSON object at the top level")
    return {str(key): value for key, value in cast(dict[object, object], data).items()}


def _as_object(value: object) -> dict[str, object]:
    if not isinstance(value, dict):
        return {}
    return {str(key): item for key, item in cast(dict[object, object], value).items()}


def _as_string_tuple(value: object) -> tuple[str, ...]:
    if not isinstance(value, list):
        return ()
    return tuple(str(item) for item in cast(list[object], value))


def load_rules(root: Path) -> _Rules:
    """Load and normalise the rule set from ``<root>/contracts/layers.json``."""

    raw = _load_object(root / "contracts" / "layers.json")

    cpp: dict[str, _CppModule] = {}
    for module, value in _as_object(raw.get("cpp")).items():
        cpp[module] = _CppModule(forbid_includes=_as_string_tuple(_as_object(value).get("forbidIncludes")))

    dotnet: dict[str, _DotnetModule] = {}
    for project, value in _as_object(raw.get("dotnet")).items():
        project_rules = _as_object(value)
        dotnet[project] = _DotnetModule(
            forbid_namespaces=_as_string_tuple(project_rules.get("forbidNamespaces")),
            allowed_project_references=_as_string_tuple(project_rules.get("allowedProjectReferences")),
        )

    return _Rules(cpp=cpp, dotnet=dotnet)


def _read_lines(path: Path) -> list[str]:
    return path.read_text(encoding="utf-8", errors="replace").splitlines()


def _relative(root: Path, path: Path) -> str:
    return path.relative_to(root).as_posix()


def _is_ignored(path: Path, base: Path) -> bool:
    return any(part in _IGNORED_DIRECTORIES for part in path.relative_to(base).parts)


def _check_cpp_file(root: Path, path: Path, forbid_includes: tuple[str, ...]) -> list[Violation]:
    violations: list[Violation] = []
    relative = _relative(root, path)
    lowered = tuple(token.lower() for token in forbid_includes)
    for line_number, line in enumerate(_read_lines(path), start=1):
        match = _INCLUDE_RE.match(line)
        if match is None:
            continue
        target = match.group(1).lower()
        if any(token in target for token in lowered):
            violations.append(Violation(relative, line_number, RULE_FORBID_INCLUDES))
    return violations


def _namespace_is_forbidden(namespace: str, forbidden: tuple[str, ...]) -> bool:
    return any(namespace == entry or namespace.startswith(f"{entry}.") for entry in forbidden)


def _check_cs_file(root: Path, path: Path, forbid_namespaces: tuple[str, ...]) -> list[Violation]:
    violations: list[Violation] = []
    relative = _relative(root, path)
    for line_number, line in enumerate(_read_lines(path), start=1):
        match = _USING_RE.match(line)
        if match is None:
            continue
        if _namespace_is_forbidden(match.group(1), forbid_namespaces):
            violations.append(Violation(relative, line_number, RULE_FORBID_NAMESPACES))
    return violations


def _check_csproj_file(root: Path, path: Path, allowed: tuple[str, ...]) -> list[Violation]:
    violations: list[Violation] = []
    relative = _relative(root, path)
    for line_number, line in enumerate(_read_lines(path), start=1):
        for match in _PROJECT_REFERENCE_RE.finditer(line):
            reference = Path(match.group(1).replace("\\", "/")).stem
            if reference not in allowed:
                violations.append(Violation(relative, line_number, RULE_ALLOWED_PROJECT_REFERENCES))
    return violations


def _check_cpp(root: Path, rules: dict[str, _CppModule]) -> list[Violation]:
    base = root / "cpp"
    violations: list[Violation] = []
    for module in sorted(rules):
        module_dir = base / module
        if not module_dir.is_dir():
            continue
        for path in sorted(module_dir.rglob("*")):
            if path.is_file() and path.suffix.lower() in _CPP_SUFFIXES and not _is_ignored(path, module_dir):
                violations.extend(_check_cpp_file(root, path, rules[module].forbid_includes))
    return violations


def _check_dotnet(root: Path, rules: dict[str, _DotnetModule]) -> list[Violation]:
    base = root / "dotnet" / "src"
    violations: list[Violation] = []
    for project in sorted(rules):
        directory = project.rsplit(".", 1)[-1]
        project_dir = base / directory
        if not project_dir.is_dir():
            continue
        module = rules[project]
        for path in sorted(project_dir.rglob("*")):
            if not path.is_file() or _is_ignored(path, project_dir):
                continue
            if path.suffix == ".cs":
                violations.extend(_check_cs_file(root, path, module.forbid_namespaces))
            elif path.suffix == ".csproj":
                violations.extend(_check_csproj_file(root, path, module.allowed_project_references))
    return violations


def check_root(root: Path) -> list[Violation]:
    """Return every violation under ``root``, ordered by path, line and rule."""

    resolved = root.resolve()
    rules = load_rules(resolved)
    violations = _check_cpp(resolved, rules.cpp) + _check_dotnet(resolved, rules.dotnet)
    return sorted(violations, key=lambda violation: (violation.path, violation.line, violation.rule))

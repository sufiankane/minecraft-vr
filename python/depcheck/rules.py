"""Dependency-rule checking for Cubeglass.

The checker reads ``contracts/layers.json`` under a repository root and enforces
the declared inward-only dependency rules for C++ (``#include`` directives) and
C# (``using`` directives and ``<ProjectReference>`` entries).

Only the standard library is used, and all parsing is line-based and
deterministic. Comments are stripped with the same normaliser the contract gate
uses before directives are matched, so a comment between ``#include`` and the
header (``#include /* gap */ <windows.h>``) cannot hide an include, and a
commented-out include or using is never reported. Every ``#include`` /
``#include_next`` / ``#import`` directive that does not name a header literal is
reported as ``macroInclude``: the header is not resolvable, so the module's
include rules cannot be enforced on it and the gate fails closed.

The manifest itself is checked for completeness: every directory under ``cpp``
that contains a ``CMakeLists.txt`` (recursively, excluding any path component
named ``tests`` or ``tools`` and any ``build*`` directory) and every
``*.csproj`` under ``dotnet/src`` (recursively, excluding ``obj``/``bin``) must
have an entry in ``layers.json``, and every configured entry must point at a
module that exists. Nested modules keep their repository-relative path as the
module name. A forward-looking entry for a module that has not landed yet is
listed under the top-level ``deferred`` object (``{"deferred": {"cpp": [...],
"dotnet": [...]}}``), which also keeps the tree green before the module exists.

A .NET project may declare ``allowNamespaces`` alongside
``forbidNamespaces``: a namespace that matches an allowed entry (exact, or
``entry + "."`` prefix, the same matching rule as the forbid list) is not a
violation even when a forbid entry also matches. The allow list is the only way
to carve an exception out of a forbidden namespace, and it is deliberately
per-project.
"""

from __future__ import annotations

import json
import re
from dataclasses import dataclass
from pathlib import Path
from typing import cast

from depcheck.contracts import strip_comments

RULE_FORBID_INCLUDES = "forbidIncludes"
RULE_FORBID_NAMESPACES = "forbidNamespaces"
RULE_ALLOWED_PROJECT_REFERENCES = "allowedProjectReferences"
RULE_LAYERS_ENTRY_MISSING = "layersEntryMissing"
RULE_LAYERS_ENTRY_STALE = "layersEntryStale"
RULE_MACRO_INCLUDE = "macroInclude"

_CPP_SUFFIXES: frozenset[str] = frozenset({".cpp", ".cc", ".cxx", ".hpp", ".hh", ".hxx", ".h"})

_IGNORED_DIRECTORIES: frozenset[str] = frozenset({"obj", "bin"})

_CPP_NON_MODULE_DIRECTORIES: frozenset[str] = frozenset({"tests", "tools"})

_INCLUDE_DIRECTIVE_RE = re.compile(r"^\s*#\s*(?:include_next|include|import)\b(.*)$")
_HEADER_LITERAL_RE = re.compile(r'^\s*[<"]([^>"]+)[>"]')
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
    allow_namespaces: tuple[str, ...]
    allowed_project_references: tuple[str, ...]


@dataclass(frozen=True)
class _Rules:
    cpp: dict[str, _CppModule]
    dotnet: dict[str, _DotnetModule]
    deferred_cpp: frozenset[str]
    deferred_dotnet: frozenset[str]


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
            allow_namespaces=_as_string_tuple(project_rules.get("allowNamespaces")),
            allowed_project_references=_as_string_tuple(project_rules.get("allowedProjectReferences")),
        )

    deferred = _as_object(raw.get("deferred"))
    return _Rules(
        cpp=cpp,
        dotnet=dotnet,
        deferred_cpp=frozenset(_as_string_tuple(deferred.get("cpp"))),
        deferred_dotnet=frozenset(_as_string_tuple(deferred.get("dotnet"))),
    )


def _read_lines(path: Path) -> list[str]:
    """Return the file's lines with comments removed, line numbers preserved."""

    return strip_comments(path.read_text(encoding="utf-8", errors="replace")).splitlines()


def _relative(root: Path, path: Path) -> str:
    return path.relative_to(root).as_posix()


def _is_ignored(path: Path, base: Path) -> bool:
    return any(part in _IGNORED_DIRECTORIES for part in path.relative_to(base).parts)


def _check_cpp_file(root: Path, path: Path, forbid_includes: tuple[str, ...]) -> list[Violation]:
    violations: list[Violation] = []
    relative = _relative(root, path)
    lowered = tuple(token.lower() for token in forbid_includes)
    for line_number, line in enumerate(_read_lines(path), start=1):
        directive = _INCLUDE_DIRECTIVE_RE.match(line)
        if directive is None:
            continue
        header = _HEADER_LITERAL_RE.match(directive.group(1))
        if header is None:
            violations.append(Violation(relative, line_number, RULE_MACRO_INCLUDE))
            continue
        target = header.group(1).lower()
        if any(token in target for token in lowered):
            violations.append(Violation(relative, line_number, RULE_FORBID_INCLUDES))
    return violations


def _namespace_matches(namespace: str, entries: tuple[str, ...]) -> bool:
    return any(namespace == entry or namespace.startswith(f"{entry}.") for entry in entries)


def _check_cs_file(
    root: Path,
    path: Path,
    forbid_namespaces: tuple[str, ...],
    allow_namespaces: tuple[str, ...],
) -> list[Violation]:
    violations: list[Violation] = []
    relative = _relative(root, path)
    for line_number, line in enumerate(_read_lines(path), start=1):
        match = _USING_RE.match(line)
        if match is None:
            continue
        namespace = match.group(1)
        if _namespace_matches(namespace, forbid_namespaces) and not _namespace_matches(namespace, allow_namespaces):
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
                violations.extend(
                    _check_cs_file(root, path, module.forbid_namespaces, module.allow_namespaces)
                )
            elif path.suffix == ".csproj":
                violations.extend(_check_csproj_file(root, path, module.allowed_project_references))
    return violations


def _actual_cpp_modules(root: Path) -> set[str]:
    """Return the repository-relative directories that act as C++ modules.

    Discovery is recursive: any directory holding a ``CMakeLists.txt`` counts,
    at any depth, unless a path component is ``tests``/``tools`` or begins with
    ``build``. Nested modules keep their relative path (``group/module``).
    """

    base = root / "cpp"
    if not base.is_dir():
        return set()
    modules: set[str] = set()
    for cmake in base.rglob("CMakeLists.txt"):
        if not cmake.is_file():
            continue
        relative = cmake.parent.relative_to(base)
        parts = relative.parts
        if not parts:
            continue
        if any(part in _CPP_NON_MODULE_DIRECTORIES for part in parts):
            continue
        if any(part.startswith("build") for part in parts):
            continue
        modules.add(relative.as_posix())
    return modules


def _actual_dotnet_projects(root: Path) -> dict[str, Path]:
    base = root / "dotnet" / "src"
    projects: dict[str, Path] = {}
    if not base.is_dir():
        return projects
    for csproj in sorted(base.rglob("*.csproj")):
        if not csproj.is_file() or _is_ignored(csproj, base):
            continue
        projects[csproj.stem] = csproj
    return projects


def _check_manifest_completeness(root: Path, rules: _Rules) -> list[Violation]:
    """Fail on modules missing from ``layers.json`` and on stale entries.

    Completeness violations have no source line, so their ``Violation.line`` is
    0. ``deferred`` modules may be absent from the tree without being stale.
    """

    violations: list[Violation] = []

    actual_cpp = _actual_cpp_modules(root)
    configured_cpp = set(rules.cpp)
    for module in sorted(actual_cpp - configured_cpp - rules.deferred_cpp):
        violations.append(Violation(f"cpp/{module}", 0, RULE_LAYERS_ENTRY_MISSING))
    for module in sorted(configured_cpp - actual_cpp - rules.deferred_cpp):
        violations.append(Violation(f"cpp/{module}", 0, RULE_LAYERS_ENTRY_STALE))

    actual_dotnet = _actual_dotnet_projects(root)
    configured_dotnet = set(rules.dotnet)
    dotnet_src = root / "dotnet" / "src"
    for project in sorted(set(actual_dotnet) - configured_dotnet - rules.deferred_dotnet):
        directory = actual_dotnet[project].parent.relative_to(dotnet_src).as_posix()
        violations.append(Violation(f"dotnet/src/{directory}", 0, RULE_LAYERS_ENTRY_MISSING))
    for project in sorted(configured_dotnet - set(actual_dotnet) - rules.deferred_dotnet):
        directory = project.rsplit(".", 1)[-1]
        violations.append(Violation(f"dotnet/src/{directory}", 0, RULE_LAYERS_ENTRY_STALE))

    return violations


def check_root(root: Path) -> list[Violation]:
    """Return every violation under ``root``, ordered by path, line and rule."""

    resolved = root.resolve()
    rules = load_rules(resolved)
    violations = (
        _check_cpp(resolved, rules.cpp)
        + _check_dotnet(resolved, rules.dotnet)
        + _check_manifest_completeness(resolved, rules)
    )
    return sorted(violations, key=lambda violation: (violation.path, violation.line, violation.rule))

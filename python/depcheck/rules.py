"""Dependency-rule checking for Cubeglass.

The checker reads ``contracts/layers.json`` under a repository root and enforces
the declared inward-only dependency rules for C++ (``#include`` directives) and
C# (``using`` directives, fully-qualified code tokens and
``<ProjectReference>`` entries).

Only the standard library is used, and all parsing is line-based and
deterministic. Comments are stripped with the same normaliser the contract gate
uses before directives are matched (backslash-newline continuations are spliced
first, and the scanner is string/char-literal aware for C++ and C#), so a
comment between ``#include`` and the header (``#include /* gap */ <windows.h>``)
cannot hide an include, a comment marker inside a string cannot swallow a
region, and a commented-out include or using is never reported. Every
``#include`` / ``#include_next`` / ``#import`` directive that does not name a
header literal is reported as ``macroInclude``: the header is not resolvable, so
the module's include rules cannot be enforced on it and the gate fails closed.

C# files are also scanned for fully-qualified forbidden namespaces in code
(``System.IO.File`` with no ``using``): comments are removed and literal
contents are blanked first, then every dotted identifier is tested with the same
prefix rule as the using list, so ``allowNamespaces`` entries such as
``System.Threading.Tasks`` keep their subtree allowed.

The manifest itself is checked for completeness: every directory under ``cpp``
that contains a ``CMakeLists.txt`` (recursively, excluding any path component
named ``tests`` or ``tools`` and any ``build*`` directory) and every
``*.csproj`` under ``dotnet/src`` (recursively, excluding ``obj``/``bin``) must
have an entry in ``layers.json``, and every configured entry must point at a
module that exists. Nested modules keep their repository-relative path as the
module name; a nested .NET project is keyed by its relative directory (not its
stem) so two same-stem projects cannot shadow each other. A .NET entry may be
keyed by project name (conventionally, directory ``Name`` for
``Cubeglass.Name``) or by the relative directory path. A forward-looking entry
for a module that has not landed yet is listed under the top-level ``deferred``
object (``{"deferred": {"cpp": [...], "dotnet": [...]}}``), which also keeps the
tree green before the module exists.

A .NET project may declare ``allowNamespaces`` alongside
``forbidNamespaces``: a namespace that matches an allowed entry is not a
violation even when a forbid entry also matches. Two entry forms exist:

- a bare entry (``System.Threading.Tasks``) keeps the subtree allowed: it
  matches the entry itself and every ``entry + "."`` child, the same matching
  rule as the forbid list;
- an ``exact:`` entry (``exact:System.Threading.CancellationToken``) matches
  only that type or namespace, so a sibling such as
  ``System.Threading.CancellationTokenSource`` stays forbidden.

The allow list is the only way to carve an exception out of a forbidden
namespace, and it is deliberately per-project.
"""

from __future__ import annotations

import json
import re
from dataclasses import dataclass
from pathlib import Path
from typing import cast

from depcheck.contracts import mask_string_literals, strip_comments

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
# A dotted identifier in code, matched after comments are removed and literal
# contents are blanked, so `System.IO.File` counts but `"System.IO.File"` does not.
_QUALIFIED_NAME_RE = re.compile(r"(?<![\w.])([A-Za-z_]\w*(?:\.[A-Za-z_]\w*)+)")

# ``exact:`` allow entries match only the named type/namespace; bare entries keep
# the substring prefix rule (TD-050).
_EXACT_ALLOW_PREFIX = "exact:"


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
    """Match a namespace/type against a rule entry list.

    Bare entries keep prefix semantics (the entry itself or a ``entry.`` child);
    ``exact:`` entries accept only the exact name, which is how a single type is
    admitted from a forbidden parent namespace without admitting its siblings.
    """

    for entry in entries:
        if entry.startswith(_EXACT_ALLOW_PREFIX):
            if namespace == entry[len(_EXACT_ALLOW_PREFIX) :]:
                return True
        elif namespace == entry or namespace.startswith(f"{entry}."):
            return True
    return False


def _check_cs_file(
    root: Path,
    path: Path,
    forbid_namespaces: tuple[str, ...],
    allow_namespaces: tuple[str, ...],
) -> list[Violation]:
    violations: list[Violation] = []
    relative = _relative(root, path)
    masked = mask_string_literals(path.read_text(encoding="utf-8", errors="replace"))
    for line_number, line in enumerate(masked.splitlines(), start=1):
        directive = _USING_RE.match(line)
        if directive is not None:
            namespace = directive.group(1)
            if _namespace_matches(namespace, forbid_namespaces) and not _namespace_matches(
                namespace, allow_namespaces
            ):
                violations.append(Violation(relative, line_number, RULE_FORBID_NAMESPACES))
            continue
        for match in _QUALIFIED_NAME_RE.finditer(line):
            namespace = match.group(1)
            if _namespace_matches(namespace, forbid_namespaces) and not _namespace_matches(
                namespace, allow_namespaces
            ):
                violations.append(Violation(relative, line_number, RULE_FORBID_NAMESPACES))
                break
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


def _match_dotnet_rule(
    relative_dir: str,
    project_stem: str,
    rules: dict[str, _DotnetModule],
) -> tuple[str, _DotnetModule] | None:
    """Return the configured rule for a discovered C# project, if any.

    A rule key is either the relative directory itself (path form, for nested
    projects) or the project name, which conventionally lives in the directory
    named by its last dotted segment (``Cubeglass.Name`` -> ``Name``). The
    directory is part of the match, so a nested project cannot satisfy a
    top-level rule by sharing its stem.
    """

    if relative_dir in rules:
        return relative_dir, rules[relative_dir]
    for key in sorted(rules):
        if project_stem == key and relative_dir == key.rsplit(".", 1)[-1]:
            return key, rules[key]
    return None


def _check_dotnet(
    root: Path,
    projects: dict[str, Path],
    rules: dict[str, _DotnetModule],
) -> list[Violation]:
    violations: list[Violation] = []
    base = root / "dotnet" / "src"
    for relative_dir, csproj in sorted(projects.items()):
        matched = _match_dotnet_rule(relative_dir, csproj.stem, rules)
        if matched is None:
            continue
        module = matched[1]
        project_dir = base / relative_dir
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
    """Return discovered C# projects keyed by their directory under ``dotnet/src``.

    The key is the repository-relative directory, not the project stem: two
    same-stem projects in different directories are both represented, so one
    cannot shadow the other in the catalogue, in rule matching or in the
    completeness check.
    """

    base = root / "dotnet" / "src"
    projects: dict[str, Path] = {}
    if not base.is_dir():
        return projects
    for csproj in sorted(base.rglob("*.csproj")):
        if not csproj.is_file() or _is_ignored(csproj, base):
            continue
        projects[csproj.parent.relative_to(base).as_posix()] = csproj
    return projects


def _check_manifest_completeness(
    root: Path,
    rules: _Rules,
    actual_cpp: set[str],
    actual_dotnet: dict[str, Path],
) -> list[Violation]:
    """Fail on modules missing from ``layers.json`` and on stale entries.

    Completeness violations have no source line, so their ``Violation.line`` is
    0. ``deferred`` modules may be absent from the tree without being stale. A
    .NET project matches a configured entry only when both its stem and its
    relative directory agree with the entry (or the entry is the directory
    itself), so a nested project with a configured stem is still reported.
    """

    violations: list[Violation] = []

    configured_cpp = set(rules.cpp)
    for module in sorted(actual_cpp - configured_cpp - rules.deferred_cpp):
        violations.append(Violation(f"cpp/{module}", 0, RULE_LAYERS_ENTRY_MISSING))
    for module in sorted(configured_cpp - actual_cpp - rules.deferred_cpp):
        violations.append(Violation(f"cpp/{module}", 0, RULE_LAYERS_ENTRY_STALE))

    configured_dotnet = set(rules.dotnet)
    matched_dotnet: set[str] = set()
    for relative_dir, csproj in sorted(actual_dotnet.items()):
        matched = _match_dotnet_rule(relative_dir, csproj.stem, rules.dotnet)
        if matched is not None:
            matched_dotnet.add(matched[0])
            continue
        if csproj.stem in rules.deferred_dotnet:
            continue
        violations.append(Violation(f"dotnet/src/{relative_dir}", 0, RULE_LAYERS_ENTRY_MISSING))
    for project in sorted(configured_dotnet - matched_dotnet - rules.deferred_dotnet):
        directory = project.rsplit(".", 1)[-1]
        violations.append(Violation(f"dotnet/src/{directory}", 0, RULE_LAYERS_ENTRY_STALE))

    return violations


def check_root(root: Path) -> list[Violation]:
    """Return every violation under ``root``, ordered by path, line and rule."""

    resolved = root.resolve()
    rules = load_rules(resolved)
    actual_cpp = _actual_cpp_modules(resolved)
    actual_dotnet = _actual_dotnet_projects(resolved)
    violations = (
        _check_cpp(resolved, rules.cpp)
        + _check_dotnet(resolved, actual_dotnet, rules.dotnet)
        + _check_manifest_completeness(resolved, rules, actual_cpp, actual_dotnet)
    )
    return sorted(violations, key=lambda violation: (violation.path, violation.line, violation.rule))

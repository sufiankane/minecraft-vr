"""Licence allowlist checking for declared Cubeglass dependencies.

Every package declared by the C++ manifest (``cpp/vcpkg.json``) and the central
NuGet version file (``dotnet/Directory.Packages.props``) must have an entry in
``contracts/licence-allowlist.json``. A declared package without an entry is
reported so the licence gate fails until an owner records an allowed licence id.
A missing manifest is an explicit error rather than an empty declaration list,
so renaming or deleting a manifest cannot make the gate pass vacuously.

Only the standard library is used and parsing stays deterministic: the vcpkg
manifest is read as JSON and the NuGet props file is scanned for
``<PackageVersion Include="..." />`` ids.
"""

from __future__ import annotations

import json
import re
from pathlib import Path
from typing import cast

VCPKG_MANIFEST = ("cpp", "vcpkg.json")
DOTNET_PACKAGES = ("dotnet", "Directory.Packages.props")
ALLOWLIST = ("contracts", "licence-allowlist.json")

_PACKAGE_VERSION_RE = re.compile(r'<PackageVersion\b[^>]*?\bInclude\s*=\s*"([^"]+)"')


class LicenceError(Exception):
    """Raised when a declared-dependency manifest is missing.

    A missing manifest would otherwise yield an empty declared set and silently
    pass the gate, so it is surfaced as an explicit error instead.
    """


def _required_manifest(root: Path, parts: tuple[str, ...]) -> Path:
    path = root.joinpath(*parts)
    if not path.is_file():
        raise LicenceError(f"missing dependency manifest: {path}")
    return path


def _load_json_object(path: Path) -> dict[str, object]:
    with path.open(encoding="utf-8") as handle:
        data: object = json.load(handle)
    if not isinstance(data, dict):
        raise TypeError(f"{path}: expected a JSON object at the top level")
    return {str(key): value for key, value in cast(dict[object, object], data).items()}


def _declared_vcpkg_packages(path: Path) -> list[str]:
    dependencies = _load_json_object(path).get("dependencies")
    if not isinstance(dependencies, list):
        return []
    packages: list[str] = []
    for entry in cast(list[object], dependencies):
        if isinstance(entry, str):
            packages.append(entry)
        elif isinstance(entry, dict):
            name = cast(dict[object, object], entry).get("name")
            if isinstance(name, str):
                packages.append(name)
    return packages


def _declared_dotnet_packages(path: Path) -> list[str]:
    text = path.read_text(encoding="utf-8", errors="replace")
    return [match.group(1) for match in _PACKAGE_VERSION_RE.finditer(text)]


def _allowlisted_packages(root: Path) -> set[str]:
    path = root.joinpath(*ALLOWLIST)
    if not path.is_file():
        return set()
    return set(_load_json_object(path))


def check_licences(root: Path) -> list[str]:
    """Return every declared package missing from the licence allowlist.

    Raises :class:`LicenceError` when either manifest is absent, so a renamed
    or deleted manifest fails the gate instead of passing vacuously.
    """

    resolved = root.resolve()
    vcpkg_manifest = _required_manifest(resolved, VCPKG_MANIFEST)
    dotnet_manifest = _required_manifest(resolved, DOTNET_PACKAGES)
    allowlist = _allowlisted_packages(resolved)
    declared = _declared_vcpkg_packages(vcpkg_manifest) + _declared_dotnet_packages(dotnet_manifest)
    return sorted({package for package in declared if package not in allowlist})

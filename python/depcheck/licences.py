"""Licence allowlist checking for declared Cubeglass dependencies.

Every package declared by the C++ manifest (``cpp/vcpkg.json``) and the central
NuGet version file (``dotnet/Directory.Packages.props``) must have an entry in
``contracts/licence-allowlist.json``. A declared package without an entry is
reported so the licence gate fails until an owner records an allowed licence id.

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


def _load_json_object(path: Path) -> dict[str, object]:
    with path.open(encoding="utf-8") as handle:
        data: object = json.load(handle)
    if not isinstance(data, dict):
        raise TypeError(f"{path}: expected a JSON object at the top level")
    return {str(key): value for key, value in cast(dict[object, object], data).items()}


def _declared_vcpkg_packages(root: Path) -> list[str]:
    path = root.joinpath(*VCPKG_MANIFEST)
    if not path.is_file():
        return []
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


def _declared_dotnet_packages(root: Path) -> list[str]:
    path = root.joinpath(*DOTNET_PACKAGES)
    if not path.is_file():
        return []
    text = path.read_text(encoding="utf-8", errors="replace")
    return [match.group(1) for match in _PACKAGE_VERSION_RE.finditer(text)]


def _allowlisted_packages(root: Path) -> set[str]:
    path = root.joinpath(*ALLOWLIST)
    if not path.is_file():
        return set()
    return set(_load_json_object(path))


def check_licences(root: Path) -> list[str]:
    """Return every declared package missing from the licence allowlist."""

    resolved = root.resolve()
    allowlist = _allowlisted_packages(resolved)
    declared = _declared_vcpkg_packages(resolved) + _declared_dotnet_packages(resolved)
    return sorted({package for package in declared if package not in allowlist})

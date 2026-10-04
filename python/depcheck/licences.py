"""Licence allowlist checking for declared Cubeglass dependencies.

Every package declared by the C++ manifest (``cpp/vcpkg.json``) and the central
NuGet version file (``dotnet/Directory.Packages.props``) must have an entry in
``contracts/licence-allowlist.json``. A declared package without an entry is
reported so the licence gate fails until an owner records an allowed licence id.
A missing manifest is an explicit error rather than an empty declaration list,
so renaming or deleting a manifest cannot make the gate pass vacuously.

Every allowlisted licence id is also validated against an embedded list of
common SPDX identifiers, so a typo or an invented id ("MIT License", "BSD")
fails instead of sitting unused in the JSON.

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

_ALLOWLIST_REL = "/".join(ALLOWLIST)

# Common OSI/SPDX identifiers. This is deliberately a curated list rather than
# the full SPDX registry: it covers every licence Cubeglass may plausibly use
# and keeps the checker dependency-free. Extend it (and the message below) when
# a legitimate new dependency needs an id that is missing.
KNOWN_SPDX_IDS: frozenset[str] = frozenset(
    {
        "0BSD",
        "AFL-2.1",
        "AFL-3.0",
        "AGPL-3.0-only",
        "AGPL-3.0-or-later",
        "Apache-1.0",
        "Apache-1.1",
        "Apache-2.0",
        "Artistic-1.0",
        "Artistic-2.0",
        "Beerware",
        "BlueOak-1.0.0",
        "BSD-1-Clause",
        "BSD-2-Clause",
        "BSD-2-Clause-Patent",
        "BSD-3-Clause",
        "BSD-3-Clause-Clear",
        "BSD-3-Clause-LBNL",
        "BSD-4-Clause",
        "BSL-1.0",
        "bzip2-1.0.6",
        "CC0-1.0",
        "CC-BY-3.0",
        "CC-BY-4.0",
        "CC-BY-SA-4.0",
        "CDDL-1.0",
        "CDDL-1.1",
        "curl",
        "EPL-1.0",
        "EPL-2.0",
        "EUPL-1.2",
        "FSFAP",
        "GPL-1.0-only",
        "GPL-1.0-or-later",
        "GPL-2.0-only",
        "GPL-2.0-or-later",
        "GPL-3.0-only",
        "GPL-3.0-or-later",
        "HPND",
        "ISC",
        "JSON",
        "LGPL-2.0-only",
        "LGPL-2.0-or-later",
        "LGPL-2.1-only",
        "LGPL-2.1-or-later",
        "LGPL-3.0-only",
        "LGPL-3.0-or-later",
        "Libpng",
        "libpng-2.0",
        "MIT",
        "MIT-0",
        "MPL-1.0",
        "MPL-1.1",
        "MPL-2.0",
        "MS-PL",
        "MS-RL",
        "NASA-1.3",
        "NCSA",
        "NTP",
        "OFL-1.1",
        "OpenLDAP-2.8",
        "OpenSSL",
        "PHP-3.01",
        "PostgreSQL",
        "PSF-2.0",
        "Python-2.0",
        "Ruby",
        "Unicode-3.0",
        "Unicode-DFS-2016",
        "Unlicense",
        "UPL-1.0",
        "Vim",
        "W3C",
        "Watcom-1.0",
        "WTFPL",
        "X11",
        "Zlib",
        "ZPL-2.1",
    }
)

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


def _invalid_allowlist_ids(root: Path) -> list[str]:
    path = root.joinpath(*ALLOWLIST)
    if not path.is_file():
        return []
    violations: list[str] = []
    for package, licence in _load_json_object(path).items():
        if not isinstance(licence, str) or licence not in KNOWN_SPDX_IDS:
            violations.append(
                f'{_ALLOWLIST_REL}: package "{package}" has unknown SPDX licence id '
                f"{licence!r}; use a known SPDX identifier"
            )
    return violations


def check_licences(root: Path) -> list[str]:
    """Return every licence-gate failure under ``root``.

    Failures are declared packages missing from the allowlist (plain package
    names) and allowlist entries whose licence id is not a known SPDX
    identifier (a message naming the package and the id).

    Raises :class:`LicenceError` when either manifest is absent, so a renamed
    or deleted manifest fails the gate instead of passing vacuously.
    """

    resolved = root.resolve()
    vcpkg_manifest = _required_manifest(resolved, VCPKG_MANIFEST)
    dotnet_manifest = _required_manifest(resolved, DOTNET_PACKAGES)
    allowlist = _allowlisted_packages(resolved)
    declared = _declared_vcpkg_packages(vcpkg_manifest) + _declared_dotnet_packages(dotnet_manifest)
    missing = sorted({package for package in declared if package not in allowlist})
    return missing + _invalid_allowlist_ids(resolved)

"""Parse ``dotnet list package --vulnerable --format json`` output.

The nightly supply-chain job used to fail on the marker text ``has the
following vulnerable packages``; that text is human output and can change with
the SDK. This module reads the machine-readable report instead and returns one
message per vulnerable package. A report that does not have the expected JSON
shape raises :class:`NugetError` so an SDK change fails the scan loudly instead
of passing it.
"""

from __future__ import annotations

import json
from pathlib import Path
from typing import cast


class NugetError(Exception):
    """Raised when the NuGet vulnerability report cannot be read or parsed."""


def _as_object(value: object, context: str) -> dict[str, object]:
    if not isinstance(value, dict):
        raise NugetError(f"{context}: expected a JSON object")
    return cast(dict[str, object], value)


def _as_list(value: object, context: str) -> list[object]:
    if not isinstance(value, list):
        raise NugetError(f"{context}: expected a JSON array")
    return cast(list[object], value)


def _as_string(value: object, context: str) -> str:
    if not isinstance(value, str):
        raise NugetError(f"{context}: expected a JSON string")
    return value


def _package_vulnerabilities(
    package: object, project: str, framework: str, context: str
) -> list[str]:
    entry = _as_object(package, context)
    package_id = _as_string(entry.get("id"), f"{context}.id")
    resolved = entry.get("resolvedVersion")
    if not isinstance(resolved, str):
        resolved = "unknown"
    vulnerabilities = _as_list(entry.get("vulnerabilities", []), f"{context}.vulnerabilities")
    messages: list[str] = []
    for index, vulnerability in enumerate(vulnerabilities):
        details = _as_object(vulnerability, f"{context}.vulnerabilities[{index}]")
        severity = _as_string(
            details.get("severity"), f"{context}.vulnerabilities[{index}].severity"
        )
        advisory = _as_string(
            details.get("advisoryurl"), f"{context}.vulnerabilities[{index}].advisoryurl"
        )
        messages.append(
            f"{project} ({framework}): {package_id} {resolved} has a {severity} vulnerability: {advisory}"
        )
    return messages


def parse_report(text: str) -> list[str]:
    """Return one message per vulnerable package in a `dotnet list` JSON report."""

    try:
        document: object = json.loads(text)
    except json.JSONDecodeError as error:
        raise NugetError(
            f"report is not valid JSON ({error}); run `dotnet list package --vulnerable "
            f"--include-transitive --format json` and capture stdout only"
        ) from error
    root = _as_object(document, "report")
    projects = _as_list(root.get("projects"), "report.projects")
    findings: list[str] = []
    for project_index, project_value in enumerate(projects):
        context = f"report.projects[{project_index}]"
        project = _as_object(project_value, context)
        project_path = _as_string(project.get("path"), f"{context}.path")
        frameworks_value = project.get("frameworks", [])
        if frameworks_value is None:
            continue
        frameworks = _as_list(frameworks_value, f"{context}.frameworks")
        for framework_index, framework_value in enumerate(frameworks):
            framework_context = f"{context}.frameworks[{framework_index}]"
            framework = _as_object(framework_value, framework_context)
            framework_name = _as_string(
                framework.get("framework"), f"{framework_context}.framework"
            )
            for collection in ("topLevelPackages", "transitivePackages"):
                packages = framework.get(collection, [])
                if packages is None:
                    continue
                for package_index, package in enumerate(
                    _as_list(packages, f"{framework_context}.{collection}")
                ):
                    findings.extend(
                        _package_vulnerabilities(
                            package,
                            project_path,
                            framework_name,
                            f"{framework_context}.{collection}[{package_index}]",
                        )
                    )
    return findings


def scan_report(path: Path) -> list[str]:
    """Read and parse a report file, raising :class:`NugetError` on I/O failure.

    PowerShell's ``>`` redirection writes UTF-16 with a BOM on Windows, so the
    decoder accepts the two encodings a captured report can have.
    """

    try:
        data = path.read_bytes()
    except OSError as error:
        raise NugetError(f"cannot read NuGet report {path}: {error}") from error
    if data.startswith((b"\xff\xfe", b"\xfe\xff")):
        return parse_report(data.decode("utf-16"))
    return parse_report(data.decode("utf-8-sig"))

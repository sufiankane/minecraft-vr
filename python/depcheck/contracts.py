"""Contract-compatibility gate for the frozen Cubeglass ABI surface.

The gate freezes the C ABI (``contracts/cg_types.h``,
``contracts/cg_unity_bridge.h``) and the shared C++ contract vocabulary
(``contracts/cpp/ports.hpp``, ``contracts/cpp/result.hpp``) in
``contracts/abi-baseline.json``. The fingerprint is computed over the contract
text with comments and whitespace stripped, so formatting-only edits pass while
any real change to a struct, enum, function declaration or macro changes it.

A changed fingerprint fails unless ``CG_ABI_VERSION`` (declared in
``contracts/cg_types.h``) was bumped and the baseline regenerated with
``python -m depcheck contracts --update``. ``--update`` refuses to write while
the version is unchanged, so a contract edit cannot silently rebase the
baseline without the required version bump. When no baseline exists yet,
``--update`` bootstraps it (there is no recorded version to compare against).

The baseline is a generated record, not a source of truth: a baseline-only edit
must never pass the gate. The gate therefore also pins the expected source
versions in :data:`KNOWN_ABI_VERSIONS` (code, not data) and fails when the
version extracted from the source disagrees with the anchor. A real contract
change needs two reviewed edits — bump the source version *and* update the
anchor — before ``--update`` regenerates the baseline. The baseline is also
required to be internally consistent: its stored ``fingerprint`` is recomputed
from its own ``files`` map, so hand-editing the fingerprint or the file hashes
alone fails. The anchor covers ``contracts/cg_types.h`` (``CG_ABI_VERSION``)
and the ``schema`` of ``contracts/golden/transforms.json``;
``contracts/cg_unity_bridge.h`` carries no version of its own (it includes
``cg_types.h`` and is covered by the same ABI version).

Only the standard library is used and the fingerprint is deterministic:

- every contract file is read as UTF-8 and normalised by removing ``//`` and
  ``/* ... */`` comments and all whitespace;
- each file's SHA-256 is recorded, and the baseline fingerprint is the SHA-256
  over the sorted ``relative-path:file-hash`` lines.
"""

from __future__ import annotations

import hashlib
import json
import re
from dataclasses import dataclass
from pathlib import Path
from typing import cast

CONTRACT_FILES: tuple[tuple[str, ...], ...] = (
    ("contracts", "cg_types.h"),
    ("contracts", "cg_unity_bridge.h"),
    ("contracts", "cpp", "ports.hpp"),
    ("contracts", "cpp", "result.hpp"),
)
BASELINE_PARTS: tuple[str, ...] = ("contracts", "abi-baseline.json")
VERSION_MACRO = "CG_ABI_VERSION"

# Known-good source versions, maintained in code on purpose: the anchor is the
# independent record that a baseline-only edit cannot forge. Keys are
# repository-relative paths; C/C++ headers are read for ``VERSION_MACRO`` and
# JSON fixtures for their integer ``schema``.
KNOWN_ABI_VERSIONS: dict[str, int] = {
    "contracts/cg_types.h": 2,
    "contracts/golden/transforms.json": 1,
}

_BLOCK_COMMENT_RE = re.compile(r"/\*.*?\*/", re.DOTALL)
_LINE_COMMENT_RE = re.compile(r"//[^\n\r]*")
_WHITESPACE_RE = re.compile(r"\s+")
# Anchored to the start of a line and applied to comment-stripped text, so a
# commented-out `#define CG_ABI_VERSION 99` can never masquerade as a bump.
_VERSION_RE = re.compile(rf"(?m)^[ \t]*#[ \t]*define[ \t]+{VERSION_MACRO}[ \t]+(\d+)")


class ContractError(Exception):
    """Raised when a contract file or the ABI baseline cannot be used.

    A missing contract file, a missing ``CG_ABI_VERSION`` declare or a malformed
    baseline would otherwise let the gate pass or fail for the wrong reason, so
    each is surfaced as an explicit error.
    """


@dataclass(frozen=True)
class ContractState:
    """The ABI version and normalised fingerprint of the contract surface."""

    version: int
    fingerprint: str
    files: dict[str, str]


def _relative(parts: tuple[str, ...]) -> str:
    return "/".join(parts)


def strip_comments(text: str) -> str:
    """Remove comments while keeping line breaks, so line anchors stay valid."""

    def _drop_block(match: re.Match[str]) -> str:
        return "\n" * match.group(0).count("\n")

    without_block_comments = _BLOCK_COMMENT_RE.sub(_drop_block, text)
    return _LINE_COMMENT_RE.sub("", without_block_comments)


def normalise(text: str) -> str:
    """Strip comments and whitespace so formatting-only edits are invisible."""

    return _WHITESPACE_RE.sub("", strip_comments(text))


def _read_contracts(root: Path) -> dict[str, str]:
    texts: dict[str, str] = {}
    for parts in CONTRACT_FILES:
        relative = _relative(parts)
        path = root.joinpath(*parts)
        if not path.is_file():
            raise ContractError(f"missing contract file: {relative}")
        texts[relative] = path.read_text(encoding="utf-8")
    return texts


def extract_abi_version(texts: dict[str, str]) -> int:
    """Return ``CG_ABI_VERSION`` from the contract headers.

    The macro lives in ``contracts/cg_types.h``; scanning every contract file
    keeps the check working if the declaration ever moves. Comment-stripped text
    is scanned so a commented-out define cannot fake a version bump.
    """

    for relative in sorted(texts):
        match = _VERSION_RE.search(strip_comments(texts[relative]))
        if match is not None:
            return int(match.group(1))
    raise ContractError(f"no {VERSION_MACRO} define found in the contract headers")


def _fingerprint(files: dict[str, str]) -> str:
    """SHA-256 over the sorted ``relative-path:file-hash`` lines."""

    return hashlib.sha256(
        "\n".join(f"{relative}:{files[relative]}" for relative in sorted(files)).encode("utf-8")
    ).hexdigest()


def compute_state(root: Path) -> ContractState:
    """Compute the normalised ABI version and fingerprint under ``root``."""

    texts = _read_contracts(root)
    files = {relative: hashlib.sha256(normalise(text).encode("utf-8")).hexdigest() for relative, text in texts.items()}
    return ContractState(version=extract_abi_version(texts), fingerprint=_fingerprint(files), files=files)


def _read_anchor_version(root: Path, texts: dict[str, str], relative: str) -> int:
    """Extract the version the anchor tracks for ``relative``.

    Contract headers are read for ``VERSION_MACRO``; other anchored files are
    JSON documents with an integer ``schema``.
    """

    if relative in texts:
        match = _VERSION_RE.search(strip_comments(texts[relative]))
        if match is None:
            raise ContractError(f"{relative}: no {VERSION_MACRO} define found")
        return int(match.group(1))
    path = root.joinpath(*relative.split("/"))
    if not path.is_file():
        raise ContractError(f"missing contract file: {relative}")
    try:
        data: object = json.loads(path.read_text(encoding="utf-8"))
    except json.JSONDecodeError as error:
        raise ContractError(f"{relative}: invalid JSON ({error})") from error
    if not isinstance(data, dict):
        raise ContractError(f"{relative}: expected a JSON object at the top level")
    schema = cast(dict[object, object], data).get("schema")
    if not isinstance(schema, int) or isinstance(schema, bool):
        raise ContractError(f"{relative}: expected an integer 'schema'")
    return schema


def _check_anchors(root: Path, texts: dict[str, str]) -> list[str]:
    """Return the failures where a source version disagrees with the code anchor."""

    update = "`python -m depcheck contracts --update`"
    failures: list[str] = []
    for relative, expected in KNOWN_ABI_VERSIONS.items():
        actual = _read_anchor_version(root, texts, relative)
        if actual != expected:
            failures.append(
                f"contracts: {relative} declares version {actual}, but the code anchor "
                f"KNOWN_ABI_VERSIONS records {expected}; a real contract change needs both a source "
                f"version bump and an anchor update (two reviewed edits), then {update}"
            )
    return failures


def load_baseline(root: Path) -> ContractState | None:
    """Load ``contracts/abi-baseline.json``; ``None`` when it does not exist."""

    path = root.joinpath(*BASELINE_PARTS)
    if not path.is_file():
        return None
    try:
        data: object = json.loads(path.read_text(encoding="utf-8"))
    except json.JSONDecodeError as error:
        raise ContractError(f"{_relative(BASELINE_PARTS)}: invalid JSON ({error})") from error
    if not isinstance(data, dict):
        raise ContractError(f"{_relative(BASELINE_PARTS)}: expected a JSON object at the top level")
    baseline = cast(dict[object, object], data)
    version = baseline.get("abiVersion")
    fingerprint = baseline.get("fingerprint")
    files = baseline.get("files")
    if not isinstance(version, int) or not isinstance(fingerprint, str) or not isinstance(files, dict):
        raise ContractError(
            f"{_relative(BASELINE_PARTS)}: expected 'abiVersion' (int), 'fingerprint' (str) and 'files' (object)"
        )
    return ContractState(
        version=version,
        fingerprint=fingerprint,
        files={str(key): str(value) for key, value in cast(dict[object, object], files).items()},
    )


def _write_baseline(root: Path, state: ContractState) -> None:
    path = root.joinpath(*BASELINE_PARTS)
    payload = {"abiVersion": state.version, "fingerprint": state.fingerprint, "files": state.files}
    path.write_text(json.dumps(payload, indent=2, sort_keys=True) + "\n", encoding="utf-8")


def changed_files(state: ContractState, baseline: ContractState) -> list[str]:
    """Return the contract files whose normalised content differs."""

    return sorted(
        relative for relative in state.files if baseline.files.get(relative) != state.files.get(relative)
    )


def check_contracts(root: Path) -> list[str]:
    """Return the contract-compatibility failures under ``root``.

    The gate trusts neither the baseline's stored version nor its stored
    fingerprint: the source version must match the code anchor
    (:data:`KNOWN_ABI_VERSIONS`), the baseline version must match the source,
    the baseline fingerprint must recompute from the baseline's own ``files``
    map, and the source fingerprint must match the baseline. Only then does an
    unchanged fingerprint pass; a changed fingerprint fails either because the
    version was not bumped or because the bumped baseline was not regenerated,
    and both messages name the changed files.
    """

    resolved = root.resolve()
    texts = _read_contracts(resolved)
    state = compute_state(resolved)
    baseline = load_baseline(resolved)
    if baseline is None:
        return [f"{_relative(BASELINE_PARTS)}: missing ABI baseline; run `python -m depcheck contracts --update`"]
    anchor_failures = _check_anchors(resolved, texts)
    if anchor_failures:
        return anchor_failures
    if baseline.fingerprint != _fingerprint(baseline.files):
        return [
            (
                f"{_relative(BASELINE_PARTS)}: fingerprint {baseline.fingerprint} does not match its own "
                f"files map ({_fingerprint(baseline.files)}); the baseline file was edited by hand and is "
                f"not a trustworthy record; restore it or run `python -m depcheck contracts --update` after "
                f"a bump"
            )
        ]
    if state.fingerprint == baseline.fingerprint:
        if state.version != baseline.version:
            return [
                (
                    f"{_relative(BASELINE_PARTS)}: abiVersion {baseline.version} disagrees with "
                    f"{VERSION_MACRO} {state.version}; the recorded baseline version is not the source "
                    f"version; restore the baseline or run `python -m depcheck contracts --update` after "
                    f"a bump"
                )
            ]
        return []
    detail = ", ".join(changed_files(state, baseline))
    update = "`python -m depcheck contracts --update`"
    if state.version == baseline.version:
        return [
            (
                f"contracts: ABI surface changed ({detail}) but {VERSION_MACRO} is still "
                f"{state.version}; bump {VERSION_MACRO} and run {update}"
            )
        ]
    return [
        (
            f"contracts: {VERSION_MACRO} moved {baseline.version} -> {state.version} but "
            f"{_relative(BASELINE_PARTS)} was not regenerated ({detail}); run {update}"
        )
    ]


def update_baseline(root: Path) -> ContractState:
    """Regenerate the baseline, refusing while the ABI version is unchanged.

    Returns the state that is now recorded. Raises :class:`ContractError` when
    the source version disagrees with the code anchor or when the surface
    changed but ``CG_ABI_VERSION`` did not, so ``--update`` cannot be used to
    accept a contract edit without a bump and an anchor update.
    """

    resolved = root.resolve()
    texts = _read_contracts(resolved)
    anchor_failures = _check_anchors(resolved, texts)
    if anchor_failures:
        raise ContractError(anchor_failures[0])
    state = compute_state(resolved)
    baseline = load_baseline(resolved)
    if baseline is not None and state.fingerprint != baseline.fingerprint and state.version == baseline.version:
        raise ContractError(
            f"refusing to update {_relative(BASELINE_PARTS)}: the ABI surface changed "
            f"({', '.join(changed_files(state, baseline))}) but {VERSION_MACRO} is still "
            f"{state.version}; bump {VERSION_MACRO} first"
        )
    _write_baseline(resolved, state)
    return state

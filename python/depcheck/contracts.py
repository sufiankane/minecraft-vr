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

_BLOCK_COMMENT_RE = re.compile(r"/\*.*?\*/", re.DOTALL)
_LINE_COMMENT_RE = re.compile(r"//[^\n\r]*")
_WHITESPACE_RE = re.compile(r"\s+")
_VERSION_RE = re.compile(rf"#\s*define\s+{VERSION_MACRO}\s+(\d+)")


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


def normalise(text: str) -> str:
    """Strip comments and whitespace so formatting-only edits are invisible."""

    without_block_comments = _BLOCK_COMMENT_RE.sub(" ", text)
    without_comments = _LINE_COMMENT_RE.sub(" ", without_block_comments)
    return _WHITESPACE_RE.sub("", without_comments)


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
    keeps the check working if the declaration ever moves.
    """

    for relative in sorted(texts):
        match = _VERSION_RE.search(texts[relative])
        if match is not None:
            return int(match.group(1))
    raise ContractError(f"no {VERSION_MACRO} define found in the contract headers")


def compute_state(root: Path) -> ContractState:
    """Compute the normalised ABI version and fingerprint under ``root``."""

    texts = _read_contracts(root)
    files = {relative: hashlib.sha256(normalise(text).encode("utf-8")).hexdigest() for relative, text in texts.items()}
    fingerprint = hashlib.sha256(
        "\n".join(f"{relative}:{files[relative]}" for relative in sorted(files)).encode("utf-8")
    ).hexdigest()
    return ContractState(version=extract_abi_version(texts), fingerprint=fingerprint, files=files)


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

    An unchanged fingerprint passes. A changed fingerprint fails either because
    the version was not bumped or because the bumped baseline was not
    regenerated; both messages name the changed files.
    """

    resolved = root.resolve()
    state = compute_state(resolved)
    baseline = load_baseline(resolved)
    if baseline is None:
        return [f"{_relative(BASELINE_PARTS)}: missing ABI baseline; run `python -m depcheck contracts --update`"]
    if state.fingerprint == baseline.fingerprint:
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
    the surface changed but ``CG_ABI_VERSION`` did not, so ``--update`` cannot
    be used to accept a contract edit without a bump.
    """

    resolved = root.resolve()
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

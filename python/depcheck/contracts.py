"""Contract-compatibility gate for the frozen Cubeglass ABI surface.

The gate freezes the C ABI (``contracts/cg_types.h``,
``contracts/cg_unity_bridge.h``), the shared C++ contract vocabulary
(``contracts/cpp/ports.hpp``, ``contracts/cpp/result.hpp``) and the golden
transform fixture (``contracts/golden/transforms.json``) in
``contracts/abi-baseline.json``. The fingerprint is computed over the contract
text with comments and whitespace stripped, so formatting-only edits pass while
any real change to a struct, enum, function declaration, macro or fixture case
changes it.

Truth is anchored in *code*, not in the generated baseline. Two constants in
this module are the independent record a baseline-only edit cannot forge:

- :data:`KNOWN_ABI_VERSIONS` pins the source version each artefact must declare
  (``CG_ABI_VERSION`` in ``contracts/cg_types.h``; the ``schema`` field of
  ``contracts/golden/transforms.json``);
- :data:`KNOWN_FINGERPRINTS` pins the normalised-declaration SHA-256 of every
  contract file.

``check_contracts`` recomputes the live fingerprints and fails when any of them
differs from :data:`KNOWN_FINGERPRINTS`, before it even looks at the baseline.
Intended drift therefore requires a review-visible edit of the code constant
(plus a regenerated baseline); no baseline-only edit can admit it. The
version-bump discipline is additionally checked by the source-vs-
:data:`KNOWN_ABI_VERSIONS` anchor, but because both constants live in this file,
an edit of ``KNOWN_FINGERPRINTS`` that leaves the version and the anchor at
their old values would pass the gate: the bump tied to a fingerprint change
remains a review-enforced convention, not a machine-enforced one.

``--update`` refuses while the live fingerprint differs from the code constant,
printing exactly which constant to edit; it may otherwise refresh the baseline
at any time (that is how a stale or tampered baseline is repaired).

Only the standard library is used and the fingerprint is deterministic:

- a backslash-newline continuation is spliced first (the C translation phase
  before comment removal), preserving line numbers;
- ``//`` and ``/* ... */`` comments are removed with a string/char-literal
  aware scanner for C++ and C# (including C# verbatim, interpolated and raw
  strings), so ``"/*"`` in a string cannot swallow a region;
- every contract file is read as UTF-8 and normalised by removing comments and
  all whitespace;
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
    ("contracts", "golden", "transforms.json"),
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

# Known-good normalised-declaration SHA-256 per contract file, maintained in
# code on purpose. Any live file whose hash differs fails the gate regardless of
# what the baseline says; update the constant (and bump the ABI version) as a
# reviewed change, then regenerate the baseline with
# ``python -m depcheck contracts --update``.
KNOWN_FINGERPRINTS: dict[str, str] = {
    "contracts/cg_types.h": "e0d76b949966224211693b414a8d8be916461ca0493a941aa1a297037d0fbaf6",
    "contracts/cg_unity_bridge.h": "467727ee3306be1467d9b3408d7fffcbbca0f3036c2d0133a0e04cfb11ef089e",
    "contracts/cpp/ports.hpp": "4e07756dce5528fc2a21f7f67364a084ca6f4666e2b2758373656cf88925f184",
    "contracts/cpp/result.hpp": "2dce1d4b7ee35a7f4df6a2bc518320975a5ccd7390f0b0c1529fa4394e89dd64",
    "contracts/golden/transforms.json": "db36e68d8ac08af5cc8b18d70bb1c171229b6438e932fc68b7f47b43808854f0",
}

_WHITESPACE_RE = re.compile(r"\s+")
# Anchored to the start of a line and applied to comment-stripped text, so a
# commented-out `#define CG_ABI_VERSION 99` can never masquerade as a bump.
_VERSION_RE = re.compile(rf"(?m)^[ \t]*#[ \t]*define[ \t]+{VERSION_MACRO}[ \t]+(\d+)")

_CPP_RAW_PREFIXES: tuple[str, ...] = ("u8R", "uR", "UR", "LR", "R")


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


def _splice_lines(text: str) -> str:
    """Splice backslash-newline continuations, preserving the line count.

    This is the C translation phase that precedes comment removal, so a
    directive split as ``#in\\`` + newline + ``clude <x>`` is one logical line
    before any other parsing. Every consumed physical line is left as a blank
    line so line numbers in later diagnostics do not shift.
    """

    lines = text.split("\n")
    spliced_lines: list[str] = []
    index = 0
    while index < len(lines):
        current = lines[index]
        spliced = 0
        while True:
            current = current.removesuffix("\r")
            if current.endswith("\\") and index + 1 < len(lines):
                current = current[:-1] + lines[index + 1]
                index += 1
                spliced += 1
            else:
                break
        spliced_lines.append(current)
        spliced_lines.extend([""] * spliced)
        index += 1
    return "\n".join(spliced_lines)


def _quoted_end(text: str, start: int, quote: str, *, verbatim: bool) -> int:
    """Return the index just past a quoted literal starting at ``start``.

    ``verbatim`` covers C# verbatim strings (``@"..."``) where ``""`` is an
    escaped quote and backslashes are literal. Non-verbatim literals stop at an
    unescaped newline, so an unterminated quote cannot swallow the rest of the
    file.
    """

    index = start + 1
    length = len(text)
    while index < length:
        character = text[index]
        if verbatim:
            if character == quote:
                if index + 1 < length and text[index + 1] == quote:
                    index += 2
                    continue
                return index + 1
            index += 1
        else:
            if character == "\\":
                index += 2
                continue
            if character == quote:
                return index + 1
            if character == "\n":
                return index
            index += 1
    return length


def _raw_end(text: str, quote_index: int, quote_count: int) -> int:
    """Return the index just past a C# raw string (``\"\"\"...\"\"\"``)."""

    closing = '"' * quote_count
    close = text.find(closing, quote_index + quote_count)
    return len(text) if close == -1 else close + quote_count


def _csharp_literal(text: str, index: int) -> tuple[int, int, bool] | None:
    """Return ``(quote_index, quote_count, verbatim)`` for a C# literal prefix."""

    cursor = index
    while cursor < len(text) and text[cursor] in "$@":
        cursor += 1
    quote_count = 0
    while cursor + quote_count < len(text) and text[cursor + quote_count] == '"':
        quote_count += 1
    prefix = text[index:cursor]
    if quote_count >= 3:
        return cursor, quote_count, True
    if quote_count == 1 and prefix in ("@", "$", "@$", "$@"):
        return cursor, 1, "@" in prefix
    return None


def _cpp_raw_end(text: str, index: int) -> int | None:
    """Return the end of a C++ raw string literal starting at ``index``."""

    if index > 0 and (text[index - 1].isalnum() or text[index - 1] == "_"):
        return None
    for prefix in _CPP_RAW_PREFIXES:
        if not text.startswith(prefix + '"', index):
            continue
        quote_index = index + len(prefix)
        paren = text.find("(", quote_index + 1, quote_index + 18)
        if paren == -1:
            return None
        delimiter = text[quote_index + 1 : paren]
        if any(character in delimiter for character in ('"', "\\", " ", "\t", "\n", "\r")):
            return None
        closing = ")" + delimiter + '"'
        close = text.find(closing, paren + 1)
        return len(text) if close == -1 else close + len(closing)
    return None


def _copy_literal(text: str, start: int, end: int, out: list[str], *, mask: bool) -> None:
    """Copy or blank a literal span, keeping its newlines for line numbers."""

    segment = text[start:end]
    if mask:
        out.append("".join("\n" if character == "\n" else " " for character in segment))
    else:
        out.append(segment)


def _scan(text: str, *, mask_literals: bool) -> str:
    """Remove comments outside string/char literals, optionally masking them."""

    out: list[str] = []
    index = 0
    length = len(text)
    while index < length:
        character = text[index]
        if character == "/" and index + 1 < length:
            following = text[index + 1]
            if following == "/":
                newline = text.find("\n", index)
                if newline == -1:
                    break
                index = newline
                continue
            if following == "*":
                closing = text.find("*/", index + 2)
                if closing == -1:
                    out.append("\n" * text.count("\n", index))
                    break
                commented = text[index : closing + 2]
                out.append("\n" * commented.count("\n"))
                index = closing + 2
                continue
        if character in "$@":
            literal = _csharp_literal(text, index)
            if literal is not None:
                quote_index, quote_count, verbatim = literal
                end = (
                    _raw_end(text, quote_index, quote_count)
                    if quote_count >= 3
                    else _quoted_end(text, quote_index, '"', verbatim=verbatim)
                )
                _copy_literal(text, index, end, out, mask=mask_literals)
                index = end
                continue
        if character == '"':
            quote_count = 0
            while index + quote_count < length and text[index + quote_count] == '"':
                quote_count += 1
            if quote_count >= 3:
                end = _raw_end(text, index, quote_count)
            else:
                end = _quoted_end(text, index, '"', verbatim=False)
            _copy_literal(text, index, end, out, mask=mask_literals)
            index = end
            continue
        if character == "'" and not (index > 0 and (text[index - 1].isalnum() or text[index - 1] == "_")):
            end = _quoted_end(text, index, "'", verbatim=False)
            _copy_literal(text, index, end, out, mask=mask_literals)
            index = end
            continue
        raw_end = _cpp_raw_end(text, index)
        if raw_end is not None:
            _copy_literal(text, index, raw_end, out, mask=mask_literals)
            index = raw_end
            continue
        # C++ phase-1 digraphs, translated in code only: a literal that spells
        # `%:include` stays literal text and must not become a directive.
        if character == "%" and text.startswith("%:%:", index):
            out.append("##")
            index += 4
            continue
        if character == "%" and text.startswith("%:", index):
            out.append("#")
            index += 2
            continue
        out.append(character)
        index += 1
    return "".join(out)


def strip_comments(text: str) -> str:
    """Remove comments outside literals, keeping line breaks and line numbers."""

    return _scan(_splice_lines(text), mask_literals=False)


def mask_string_literals(text: str) -> str:
    """Remove comments and blank out literal contents, keeping line numbers.

    Used for token scans (for example C# fully-qualified namespace matching)
    where text inside a string must never count as code.
    """

    return _scan(_splice_lines(text), mask_literals=True)


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


def _read_anchor_version(texts: dict[str, str], relative: str) -> int:
    """Extract the version the anchor tracks for ``relative``.

    C/C++ contract files are read for ``VERSION_MACRO``; JSON files (the golden
    fixture) for their integer ``schema``.
    """

    text = texts.get(relative)
    if text is None:
        raise ContractError(f"missing contract file: {relative}")
    if relative.endswith(".json"):
        try:
            data: object = json.loads(text)
        except json.JSONDecodeError as error:
            raise ContractError(f"{relative}: invalid JSON ({error})") from error
        if not isinstance(data, dict):
            raise ContractError(f"{relative}: expected a JSON object at the top level")
        schema = cast(dict[object, object], data).get("schema")
        if not isinstance(schema, int) or isinstance(schema, bool):
            raise ContractError(f"{relative}: expected an integer 'schema'")
        return schema
    match = _VERSION_RE.search(strip_comments(text))
    if match is None:
        raise ContractError(f"{relative}: no {VERSION_MACRO} define found")
    return int(match.group(1))


def _check_anchors(texts: dict[str, str]) -> list[str]:
    """Return the failures where a source version disagrees with the code anchor."""

    update = "`python -m depcheck contracts --update`"
    failures: list[str] = []
    for relative, expected in KNOWN_ABI_VERSIONS.items():
        actual = _read_anchor_version(texts, relative)
        if actual != expected:
            failures.append(
                f"contracts: {relative} declares version {actual}, but the code anchor "
                f"KNOWN_ABI_VERSIONS records {expected}; a real contract change needs the source "
                f"version bump, both code anchors updated and then {update}"
            )
    return failures


def _fingerprint_failures(state: ContractState) -> list[str]:
    """Return the live files whose hash disagrees with the code anchor."""

    failures: list[str] = []
    for relative in sorted(set(KNOWN_FINGERPRINTS) | set(state.files)):
        actual = state.files.get(relative)
        expected = KNOWN_FINGERPRINTS.get(relative)
        if actual == expected:
            continue
        failures.append(
            f"contracts: {relative} hashes to {actual} but the code anchor KNOWN_FINGERPRINTS records "
            f"{expected}; if the change is intended, bump the source ABI version, update KNOWN_ABI_VERSIONS "
            f"and KNOWN_FINGERPRINTS in python/depcheck/contracts.py, then run "
            f"`python -m depcheck contracts --update`"
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

    The live normalised fingerprints are compared against the hardcoded
    :data:`KNOWN_FINGERPRINTS` first, so a source edit fails even when the
    baseline was consistently rewritten to match it. Source versions must agree
    with :data:`KNOWN_ABI_VERSIONS`, and the baseline (bookkeeping only) must
    record the version and hashes of the live surface; any baseline-only edit
    therefore fails without ever being able to admit drift.
    """

    resolved = root.resolve()
    texts = _read_contracts(resolved)
    state = compute_state(resolved)
    fingerprint_failures = _fingerprint_failures(state)
    if fingerprint_failures:
        return fingerprint_failures
    anchor_failures = _check_anchors(texts)
    if anchor_failures:
        return anchor_failures
    baseline = load_baseline(resolved)
    if baseline is None:
        return [f"{_relative(BASELINE_PARTS)}: missing ABI baseline; run `python -m depcheck contracts --update`"]
    if baseline.version != state.version:
        return [
            (
                f"{_relative(BASELINE_PARTS)}: abiVersion {baseline.version} disagrees with "
                f"{VERSION_MACRO} {state.version}; the baseline does not record the live surface "
                f"(bookkeeping only); run `python -m depcheck contracts --update`"
            )
        ]
    if baseline.files != state.files or baseline.fingerprint != state.fingerprint:
        detail = ", ".join(changed_files(state, baseline))
        return [
            (
                f"{_relative(BASELINE_PARTS)}: the recorded contract surface does not match the live "
                f"contract surface ({detail}); the baseline is bookkeeping and cannot vouch for the source; "
                f"run `python -m depcheck contracts --update`"
            )
        ]
    return []


def update_baseline(root: Path) -> ContractState:
    """Regenerate the baseline once the live surface matches the code anchors.

    Returns the state that is now recorded. Raises :class:`ContractError` while
    a live fingerprint differs from :data:`KNOWN_FINGERPRINTS` or a source
    version differs from :data:`KNOWN_ABI_VERSIONS`, printing exactly which
    code constant to edit first, so ``--update`` cannot be used to accept a
    contract edit without updating the anchors in code.
    """

    resolved = root.resolve()
    texts = _read_contracts(resolved)
    state = compute_state(resolved)
    fingerprint_failures = _fingerprint_failures(state)
    if fingerprint_failures:
        raise ContractError(f"refusing to update {_relative(BASELINE_PARTS)}: {fingerprint_failures[0]}")
    anchor_failures = _check_anchors(texts)
    if anchor_failures:
        raise ContractError(f"refusing to update {_relative(BASELINE_PARTS)}: {anchor_failures[0]}")
    _write_baseline(resolved, state)
    return state

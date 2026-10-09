"""Reads `.cgrec` sessions (dossier 5.8) for calibration: manifest, CSV rows and
the two camera streams as numpy images.

The Luma Ultra delivers a single stereo pair, so the recorder zero-fills the
absent `l1`/`r1` streams; the reader exposes `left`/`right` (the `l0`/`r0`
pair) and reports which streams actually carry data.
"""

from __future__ import annotations

import json
from dataclasses import dataclass
from pathlib import Path

import numpy as np
import numpy.typing as npt

UInt8Image = npt.NDArray[np.uint8]

MANIFEST_NAME = "manifest.json"
CSV_NAME = "stereo.csv"
FRAMES_DIR = "frames"
PGM_SUFFIXES = ("_l0", "_r0", "_l1", "_r1")


class SessionError(RuntimeError):
    """A `.cgrec` session cannot be read; the message names the reason."""


@dataclass(frozen=True)
class SessionManifest:
    schema: int
    width: int
    height: int
    stride: int
    frame_count: int
    storage: str  # "pgm" | "bin"
    device_serial: str | None


@dataclass(frozen=True)
class SessionFrame:
    """One frame: the left/right images and the recorded sequence number."""

    seq: int
    sdk_time_s: float
    left: UInt8Image
    right: UInt8Image


def read_manifest(session_dir: Path) -> SessionManifest:
    path = session_dir / MANIFEST_NAME
    if not path.is_file():
        raise SessionError(f"manifest missing: {path}")
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
    except json.JSONDecodeError as error:
        raise SessionError(f"manifest is not valid JSON: {error}") from error
    try:
        storage = str(data["storage"])
        if storage not in ("pgm", "bin"):
            raise SessionError(f"manifest storage must be 'pgm' or 'bin', got {storage!r}")
        return SessionManifest(
            schema=int(data["schema"]),
            width=int(data["width"]),
            height=int(data["height"]),
            stride=int(data["stride"]),
            frame_count=int(data["frame_count"]),
            storage=storage,
            device_serial=str(data["device_serial"]) if data.get("device_serial") else None,
        )
    except KeyError as error:
        raise SessionError(f"manifest is missing the field {error}") from error


def read_rows(session_dir: Path) -> list[tuple[int, float]]:
    """The `(seq, sdk_time_s)` pairs from `stereo.csv`, in recorded order."""
    path = session_dir / CSV_NAME
    if not path.is_file():
        raise SessionError(f"stereo.csv missing: {path}")
    rows: list[tuple[int, float]] = []
    for line_number, line in enumerate(path.read_text(encoding="utf-8").splitlines(), start=1):
        if line_number == 1 or not line:
            continue
        fields = line.split(",")
        if len(fields) != 5:
            raise SessionError(f"stereo.csv line {line_number} has {len(fields)} fields, expected 5")
        rows.append((int(fields[0]), float(fields[2])))
    return rows


def _frame_stem(seq: int) -> str:
    return f"{seq:08d}"


def read_pgm(path: Path) -> UInt8Image:
    """Reads a binary P5 PGM (maxval 255) into a (H, W) uint8 array."""
    data = path.read_bytes()
    tokens: list[bytes] = []
    index = 0
    while len(tokens) < 4 and index < len(data):
        while index < len(data) and data[index] in b" \t\r\n":
            index += 1
        if index < len(data) and data[index : index + 1] == b"#":
            while index < len(data) and data[index : index + 1] != b"\n":
                index += 1
            continue
        start = index
        while index < len(data) and data[index] not in b" \t\r\n":
            index += 1
        tokens.append(data[start:index])
    if len(tokens) != 4 or tokens[0] != b"P5":
        raise SessionError(f"{path.name}: not a binary P5 PGM")
    width, height = int(tokens[1]), int(tokens[2])
    if tokens[3] != b"255":
        raise SessionError(f"{path.name}: maxval must be 255")
    # The token scanner consumed exactly one delimiter after the maxval.
    payload = data[index + 1 :]
    expected = width * height
    if len(payload) < expected:
        raise SessionError(f"{path.name}: payload {len(payload)} bytes, expected {expected}")
    return np.frombuffer(payload[:expected], dtype=np.uint8).reshape(height, width).copy()


def read_frame(session_dir: Path, manifest: SessionManifest, seq: int) -> SessionFrame:
    """Reads one frame's left/right images."""
    stem = _frame_stem(seq)
    if manifest.storage == "pgm":
        left = read_pgm(session_dir / FRAMES_DIR / f"{stem}_l0.pgm")
        right = read_pgm(session_dir / FRAMES_DIR / f"{stem}_r0.pgm")
    else:
        path = session_dir / FRAMES_DIR / f"{stem}.bin"
        if not path.is_file():
            raise SessionError(f"frame missing: {path}")
        data = path.read_bytes()
        image_bytes = manifest.stride * manifest.height
        if len(data) < image_bytes * 2:
            raise SessionError(f"{path.name}: {len(data)} bytes, expected {image_bytes * 4} (four streams)")
        left = np.frombuffer(data[:image_bytes], dtype=np.uint8).reshape(manifest.height, manifest.stride).copy()
        right = np.frombuffer(
            data[image_bytes : image_bytes * 2], dtype=np.uint8
        ).reshape(manifest.height, manifest.stride).copy()
    cropped_left = _validate_geometry(left, manifest, "left")
    cropped_right = _validate_geometry(right, manifest, "right")
    time_s = dict(read_rows(session_dir)).get(seq, 0.0)
    return SessionFrame(seq=seq, sdk_time_s=time_s, left=cropped_left, right=cropped_right)


def _validate_geometry(image: UInt8Image, manifest: SessionManifest, name: str) -> UInt8Image:
    if image.shape[0] != manifest.height or image.shape[1] < manifest.width:
        raise SessionError(
            f"{name} image is {image.shape[0]}x{image.shape[1]}, expected {manifest.height}x{manifest.width}+"
        )
    return image[:, : manifest.width]


def frame_sequences(session_dir: Path) -> list[int]:
    """The frame sequence numbers the CSV records, in order."""
    return [seq for seq, _ in read_rows(session_dir)]

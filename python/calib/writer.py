"""Writes `calibration.json` (dossier 5.7), validated against the contract
schema before it lands on disk."""

from __future__ import annotations

import json
from collections.abc import Iterable
from datetime import UTC, datetime
from pathlib import Path
from typing import Any

from jsonschema import Draft202012Validator
from jsonschema.exceptions import ValidationError

from calib.pipeline import CalibrationRun
from calib.session import SessionError
from calib.types import Intrinsics

#: The committed JSON Schema (dossier 5.7: "JSON Schema in `contracts/`").
SCHEMA_PATH = Path(__file__).resolve().parents[2] / "contracts" / "calibration.schema.json"

#: Default hand-profile tolerance (5.7's `tolerance`).
DEFAULT_BONE_TOLERANCE = 0.15


def utc_now_iso() -> str:
    """`created_utc` in the ISO-8601 form the schema expects."""
    return datetime.now(UTC).strftime("%Y-%m-%dT%H:%M:%SZ")


def _camera_document(intrinsics: Intrinsics) -> dict[str, Any]:
    return {
        "model": intrinsics.model,
        "fx": intrinsics.fx,
        "fy": intrinsics.fy,
        "cx": intrinsics.cx,
        "cy": intrinsics.cy,
        "dist": [float(value) for value in intrinsics.dist],
    }


def calibration_document(
    run: CalibrationRun,
    *,
    created_utc: str,
    device_serial: str | None = None,
    head_from_left_camera: tuple[Iterable[float], Iterable[float]] | None = None,
    bone_lengths_m: Iterable[float] = (),
    bone_tolerance: float = DEFAULT_BONE_TOLERANCE,
) -> dict[str, Any]:
    """Builds the 5.7 document; head alignment and bone lengths default to the
    not-yet-measured values (identity pose, empty profile)."""
    rig = run.solution.rig
    if head_from_left_camera is None:
        head_p: list[float] = [0.0, 0.0, 0.0]
        head_q: list[float] = [1.0, 0.0, 0.0, 0.0]
    else:
        head_p = [float(value) for value in head_from_left_camera[0]]
        head_q = [float(value) for value in head_from_left_camera[1]]
    return {
        "schema": 1,
        "device_serial": device_serial if device_serial is not None else (run.device_serial or ""),
        "created_utc": created_utc,
        "rig": {
            "image_size": [rig.image_size[0], rig.image_size[1]],
            "left": _camera_document(rig.left),
            "right": _camera_document(rig.right),
            "right_from_left": {"p": list(rig.right_from_left_p), "q": list(rig.right_from_left_q)},
        },
        "head_from_left_camera": {"p": head_p, "q": head_q},
        "hand_profile": {
            "bone_lengths_m": [float(value) for value in bone_lengths_m],
            "tolerance": float(bone_tolerance),
        },
        "quality": {"reproj_rms_px": float(rig.reproj_rms_px), "n_views": int(rig.n_views)},
    }


def validate_document(document: dict[str, Any], schema_path: Path = SCHEMA_PATH) -> None:
    """Validates against the committed schema; a failure names the problem."""
    schema = json.loads(schema_path.read_text(encoding="utf-8"))
    try:
        Draft202012Validator(schema).validate(document)
    except ValidationError as error:
        location = "/".join(str(part) for part in error.absolute_path) or "(root)"
        raise SessionError(f"calibration document is invalid at {location}: {error.message}") from error


def write_calibration(
    run: CalibrationRun,
    path: Path,
    *,
    created_utc: str,
    device_serial: str | None = None,
    head_from_left_camera: tuple[Iterable[float], Iterable[float]] | None = None,
    bone_lengths_m: Iterable[float] = (),
    bone_tolerance: float = DEFAULT_BONE_TOLERANCE,
) -> dict[str, Any]:
    """Builds, validates and writes the document; returns it."""
    document = calibration_document(
        run,
        created_utc=created_utc,
        device_serial=device_serial,
        head_from_left_camera=head_from_left_camera,
        bone_lengths_m=bone_lengths_m,
        bone_tolerance=bone_tolerance,
    )
    validate_document(document)
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(document, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    return document

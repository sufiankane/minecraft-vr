"""S9 Task 2: the `calibration.json` writer, the schema gate and the CLI."""

from __future__ import annotations

import json
import re
from collections.abc import Callable
from pathlib import Path
from typing import Any

import pytest
from calib_fixtures import render_session

from calib.__main__ import main
from calib.pipeline import CalibrationRun, View, select_model
from calib.session import SessionError
from calib.synth import default_pinhole_rig, synthetic_views
from calib.types import BoardSpec
from calib.writer import SCHEMA_PATH, calibration_document, utc_now_iso, validate_document, write_calibration

BOARD = BoardSpec(inner_cols=9, inner_rows=6, square_m=0.025)


def _run() -> CalibrationRun:
    rig = default_pinhole_rig()
    views = synthetic_views(rig, BOARD, n_views=14, seed=31)
    collected = [
        View(object_points=obj, image_left=left, image_right=right)
        for obj, left, right in zip(views.object_points, views.image_left, views.image_right)
    ]
    solution, attempts = select_model(collected, rig.image_size)
    return CalibrationRun(
        session_dir=Path("synthetic"),
        board=BOARD,
        views_used=14,
        attempts=attempts,
        solution=solution,
        device_serial="TEST-0001",
    )


def test_document_matches_the_5_7_shape() -> None:
    document = calibration_document(_run(), created_utc="2026-10-08T00:00:00Z")
    assert document["schema"] == 1
    assert document["device_serial"] == "TEST-0001"
    assert document["rig"]["image_size"] == [640, 480]
    assert document["rig"]["left"]["model"] == "pinhole"
    assert len(document["rig"]["left"]["dist"]) == 5
    assert len(document["rig"]["right_from_left"]["q"]) == 4
    assert document["head_from_left_camera"] == {"p": [0.0, 0.0, 0.0], "q": [1.0, 0.0, 0.0, 0.0]}
    assert document["hand_profile"] == {"bone_lengths_m": [], "tolerance": 0.15}
    assert document["quality"]["n_views"] == 14
    validate_document(document)


def test_writer_round_trips_and_validates(tmp_path: Path) -> None:
    target = tmp_path / "calibration.json"
    document = write_calibration(_run(), target, created_utc=utc_now_iso())
    reloaded = json.loads(target.read_text(encoding="utf-8"))
    assert reloaded == document
    assert SCHEMA_PATH.is_file(), "the contract schema is committed"


@pytest.mark.parametrize(
    "mutate",
    [
        lambda document: document.update({"schema": 2}),
        lambda document: document["rig"]["left"].update({"fx": 0.0}),
        lambda document: document["hand_profile"].update({"tolerance": 2.0}),
        lambda document: document.pop("quality"),
        lambda document: document.update({"extra": True}),
    ],
)
def test_schema_rejections_are_named(mutate: Callable[[dict[str, Any]], None]) -> None:
    document = calibration_document(_run(), created_utc="2026-10-08T00:00:00Z")
    mutate(document)
    with pytest.raises(SessionError, match="invalid"):
        validate_document(document)


def test_utc_now_has_the_iso_shape() -> None:
    assert re.fullmatch(r"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z", utc_now_iso())


def test_cli_solves_a_rendered_session_and_writes_the_file(tmp_path: Path) -> None:
    rig = default_pinhole_rig()
    session = tmp_path / "session"
    render_session(session, BOARD, rig, views=10, seed=32)
    target = tmp_path / "calibration.json"
    exit_code = main(
        [
            "--session",
            str(session),
            "--board",
            "9x6",
            "--square",
            "0.025",
            "--serial",
            "TEST-0002",
            "--out",
            str(target),
        ]
    )
    assert exit_code == 0
    document = json.loads(target.read_text(encoding="utf-8"))
    assert document["device_serial"] == "TEST-0002"
    assert document["rig"]["left"]["model"] == "pinhole"


def test_cli_reports_an_unreadable_session(tmp_path: Path) -> None:
    assert main(["--session", str(tmp_path / "missing")]) == 1

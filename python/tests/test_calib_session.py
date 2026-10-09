"""S9 Task 2: the `.cgrec` reader against hand-written sessions."""

from __future__ import annotations

import json
from pathlib import Path

import numpy as np
import pytest
from calib_fixtures import write_session

from calib.session import SessionError, frame_sequences, read_frame, read_manifest, read_rows


def _pattern(height: int = 6, width: int = 8) -> np.ndarray:
    rows = np.arange(height, dtype=np.uint8).reshape(-1, 1)
    columns = np.arange(width, dtype=np.uint8).reshape(1, -1)
    return (rows * 17 + columns * 3).astype(np.uint8)


def test_reads_a_pgm_session(tmp_path: Path) -> None:
    left, right = _pattern(), _pattern() + 100
    write_session(tmp_path, [(left, right)], width=8, height=6)
    manifest = read_manifest(tmp_path)
    assert (manifest.schema, manifest.width, manifest.height, manifest.stride) == (2, 8, 6, 8)
    assert manifest.storage == "pgm"
    assert manifest.frame_count == 1
    assert frame_sequences(tmp_path) == [1]
    rows = read_rows(tmp_path)
    assert len(rows) == 1
    assert rows[0][0] == 1
    assert rows[0][1] == pytest.approx(0.04)
    frame = read_frame(tmp_path, manifest, 1)
    assert np.array_equal(frame.left, left)
    assert np.array_equal(frame.right, right)
    assert frame.sdk_time_s == pytest.approx(0.04)


def test_reads_a_padded_bin_session(tmp_path: Path) -> None:
    left, right = _pattern(), _pattern() + 7
    write_session(tmp_path, [(left, right)], width=8, height=6, stride=12, storage="bin")
    manifest = read_manifest(tmp_path)
    assert (manifest.stride, manifest.storage) == (12, "bin")
    frame = read_frame(tmp_path, manifest, 1)
    assert frame.left.shape == (6, 8), "row padding is cropped away"
    assert np.array_equal(frame.left, left)
    assert np.array_equal(frame.right, right)


def test_missing_manifest_names_the_file(tmp_path: Path) -> None:
    with pytest.raises(SessionError, match="manifest missing"):
        read_manifest(tmp_path)


def test_invalid_json_and_unknown_storage_are_named(tmp_path: Path) -> None:
    (tmp_path / "manifest.json").write_text("{ not json", encoding="utf-8")
    with pytest.raises(SessionError, match="not valid JSON"):
        read_manifest(tmp_path)
    (tmp_path / "manifest.json").write_text(json.dumps({"schema": 2, "storage": "raw"}), encoding="utf-8")
    with pytest.raises(SessionError, match="storage"):
        read_manifest(tmp_path)


def test_missing_fields_and_short_frames_are_named(tmp_path: Path) -> None:
    (tmp_path / "manifest.json").write_text(json.dumps({"schema": 2}), encoding="utf-8")
    with pytest.raises(SessionError, match="missing the field"):
        read_manifest(tmp_path)

    left, right = _pattern(), _pattern()
    write_session(tmp_path, [(left, right)], width=8, height=6, storage="bin")
    manifest = read_manifest(tmp_path)
    (tmp_path / "frames" / "00000001.bin").write_bytes(b"\x00" * 16)
    with pytest.raises(SessionError, match="expected"):
        read_frame(tmp_path, manifest, 1)


def test_missing_csv_is_named(tmp_path: Path) -> None:
    with pytest.raises(SessionError, match="stereo.csv missing"):
        read_rows(tmp_path)

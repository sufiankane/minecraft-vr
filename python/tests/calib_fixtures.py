"""Shared S9 test helpers: write `.cgrec` sessions from arrays and render
checkerboard views that `findChessboardCornersSB` can actually detect."""

from __future__ import annotations

import json
from pathlib import Path

import cv2
import numpy as np
import numpy.typing as npt

from calib.projection import project, rotation_from_quaternion, transform_points
from calib.types import BoardSpec, StereoRig

UInt8Image = npt.NDArray[np.uint8]

CELL_PX = 40


def write_pgm(path: Path, image: UInt8Image) -> None:
    height, width = image.shape
    header = f"P5\n{width} {height}\n255\n".encode("ascii")
    path.write_bytes(header + image.tobytes())


def write_session(
    session_dir: Path,
    frames: list[tuple[UInt8Image, UInt8Image]],
    *,
    width: int,
    height: int,
    stride: int | None = None,
    storage: str = "pgm",
) -> None:
    """Writes a minimal `.cgrec` session (`l1`/`r1` zero-filled like the device)."""
    resolved_stride = width if stride is None else stride
    frames_dir = session_dir / "frames"
    frames_dir.mkdir(parents=True, exist_ok=True)
    manifest = {
        "schema": 2,
        "width": width,
        "height": height,
        "stride": resolved_stride,
        "frame_count": len(frames),
        "dropped": 0,
        "storage": storage,
        "dof": "3dof",
        "sdk_version": "test",
        "created_utc": "2026-10-08T00:00:00Z",
    }
    (session_dir / "manifest.json").write_text(json.dumps(manifest), encoding="utf-8")
    rows = ["seq,host_time_ns,sdk_time_s,width,height"]
    for index, (left, right) in enumerate(frames):
        seq = index + 1
        rows.append(f"{seq},{seq * 40_000_000},{seq * 0.04:.3f},{width},{height}")
        stem = f"{seq:08d}"
        zero = np.zeros((height, width), dtype=np.uint8)
        if storage == "pgm":
            for name, image in (("_l0", left), ("_r0", right), ("_l1", zero), ("_r1", zero)):
                write_pgm(frames_dir / f"{stem}{name}.pgm", image)
        else:
            pad = resolved_stride - width
            padded = [np.hstack([image, np.zeros((height, pad), dtype=np.uint8)]) for image in (left, right, zero, zero)]
            (frames_dir / f"{stem}.bin").write_bytes(b"".join(image.tobytes() for image in padded))
    (session_dir / "stereo.csv").write_text("\n".join(rows) + "\n", encoding="utf-8")


def random_board_pose(rng: np.random.Generator, z_m: float) -> tuple[npt.NDArray[np.float64], npt.NDArray[np.float64]]:
    """A view tilted by up to ~15 degrees, kept well inside the frame so the
    rendered corners never leave the image."""
    angle = np.deg2rad(15.0)
    rvec = rng.uniform(-angle, angle, 3)
    tvec = np.array([rng.uniform(-0.03, 0.03), rng.uniform(-0.025, 0.025), z_m], dtype=np.float64)
    return rvec, tvec


def render_checkerboard(
    board: BoardSpec, rig: StereoRig, rvec: npt.NDArray[np.float64], tvec: npt.NDArray[np.float64]
) -> tuple[UInt8Image, UInt8Image]:
    """Warps a checkerboard texture onto the projected board plane for both cameras.

    The texture's inner lattice quad maps to the projected inner corners, so
    the rendered pattern matches the geometry the solver later recovers.
    """
    margin = 2 * CELL_PX
    texture_width = (board.inner_cols + 1) * CELL_PX + 2 * margin
    texture_height = (board.inner_rows + 1) * CELL_PX + 2 * margin
    texture = np.full((texture_height, texture_width), 255, dtype=np.uint8)
    for row in range(board.inner_rows + 1):
        for column in range(board.inner_cols + 1):
            if (row + column) % 2 == 0:
                y0 = margin + row * CELL_PX
                x0 = margin + column * CELL_PX
                texture[y0 : y0 + CELL_PX, x0 : x0 + CELL_PX] = 0

    src = np.array(
        [
            [margin + CELL_PX, margin + CELL_PX],
            [margin + CELL_PX * board.inner_cols, margin + CELL_PX],
            [margin + CELL_PX * board.inner_cols, margin + CELL_PX * board.inner_rows],
            [margin + CELL_PX, margin + CELL_PX * board.inner_rows],
        ],
        dtype=np.float32,
    )

    board_points = board.object_points().astype(np.float64)
    view = transform_points(board_points, rvec, tvec)
    left_px = project(rig.left, view)
    right_rotation = rotation_from_quaternion(rig.right_from_left_q)
    right_px = project(rig.right, view @ right_rotation.T + np.asarray(rig.right_from_left_p, dtype=np.float64))

    columns, rows = board.inner_cols, board.inner_rows

    def quad(points: npt.NDArray[np.float64]) -> npt.NDArray[np.float32]:
        return np.array(
            [points[0], points[columns - 1], points[columns * rows - 1], points[(rows - 1) * columns]],
            dtype=np.float32,
        )

    def warp(points: npt.NDArray[np.float64]) -> UInt8Image:
        matrix = cv2.getPerspectiveTransform(src, quad(points))
        return np.asarray(cv2.warpPerspective(texture, matrix, rig.image_size, borderValue=255), dtype=np.uint8)

    return warp(left_px), warp(right_px)


def render_session(
    session_dir: Path, board: BoardSpec, rig: StereoRig, *, views: int, seed: int, storage: str = "pgm"
) -> None:
    """Renders `views` board views and writes them as a session."""
    rng = np.random.default_rng(seed)
    frames: list[tuple[UInt8Image, UInt8Image]] = []
    for _ in range(views):
        rvec, tvec = random_board_pose(rng, float(rng.uniform(0.4, 0.6)))
        frames.append(render_checkerboard(board, rig, rvec, tvec))
    write_session(session_dir, frames, width=rig.image_size[0], height=rig.image_size[1], storage=storage)

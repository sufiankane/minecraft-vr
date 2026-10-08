"""Calibration value types (dossier 5.7 shapes, camera-model agnostic)."""

from __future__ import annotations

import math
from dataclasses import dataclass

import numpy as np
import numpy.typing as npt

PINHOLE = "pinhole"
FISHEYE = "fisheye"


@dataclass(frozen=True)
class BoardSpec:
    """A chessboard: inner-corner grid and square size in metres."""

    inner_cols: int
    inner_rows: int
    square_m: float

    @property
    def corner_count(self) -> int:
        return self.inner_cols * self.inner_rows

    def object_points(self) -> npt.NDArray[np.float32]:
        """The board's corner grid in board coordinates (Z = 0), row-major."""
        grid = np.mgrid[0 : self.inner_cols, 0 : self.inner_rows].T.reshape(-1, 2)
        points = np.zeros((self.corner_count, 3), dtype=np.float32)
        points[:, :2] = grid.astype(np.float32) * np.float32(self.square_m)
        return points


@dataclass(frozen=True)
class Intrinsics:
    """One camera: model name, focal lengths, principal point, distortion."""

    model: str
    fx: float
    fy: float
    cx: float
    cy: float
    dist: tuple[float, ...]

    def matrix(self) -> npt.NDArray[np.float64]:
        return np.array(
            [[self.fx, 0.0, self.cx], [0.0, self.fy, self.cy], [0.0, 0.0, 1.0]], dtype=np.float64
        )

    def dist_array(self) -> npt.NDArray[np.float64]:
        return np.asarray(self.dist, dtype=np.float64)


@dataclass(frozen=True)
class StereoRig:
    """A calibrated stereo pair; the 5.7 `rig` object in memory."""

    image_size: tuple[int, int]
    left: Intrinsics
    right: Intrinsics
    right_from_left_p: tuple[float, float, float]
    right_from_left_q: tuple[float, float, float, float]
    reproj_rms_px: float
    n_views: int

    @property
    def baseline_m(self) -> float:
        return math.sqrt(sum(component * component for component in self.right_from_left_p))

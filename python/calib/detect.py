"""Chessboard detection for calibration frames (OpenCV)."""

from __future__ import annotations

import cv2
import numpy as np
import numpy.typing as npt

from calib.types import BoardSpec

FloatArray = npt.NDArray[np.float64]


def find_board(image: npt.NDArray[np.uint8], board: BoardSpec, *, equalize: bool = True) -> FloatArray | None:
    """Finds the board's inner corners; None when the pattern is not visible.

    `findChessboardCornersSB` is the primary detector (robust to noise and the
    mounted-camera vignette); the classic detector with sub-pixel refinement is
    the fallback for frames SB refuses.
    """
    if image.ndim != 2:
        raise ValueError("find_board expects a single-channel image")
    prepared = cv2.equalizeHist(image) if equalize else image
    pattern = (board.inner_cols, board.inner_rows)
    found, corners = cv2.findChessboardCornersSB(prepared, pattern, flags=cv2.CALIB_CB_EXHAUSTIVE)
    if found and corners is not None:
        return np.asarray(corners, dtype=np.float64).reshape(-1, 2)

    found, corners = cv2.findChessboardCorners(
        prepared, pattern, flags=cv2.CALIB_CB_ADAPTIVE_THRESH | cv2.CALIB_CB_NORMALIZE_IMAGE
    )
    if not found or corners is None:
        return None
    refined = cv2.cornerSubPix(
        prepared,
        np.asarray(corners, dtype=np.float32),
        (5, 5),
        (-1, -1),
        (cv2.TERM_CRITERIA_EPS + cv2.TERM_CRITERIA_MAX_ITER, 30, 0.01),
    )
    return np.asarray(refined, dtype=np.float64).reshape(-1, 2)

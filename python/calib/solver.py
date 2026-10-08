"""Stereo solvers: recover a pinhole rig from projected corner views."""

from __future__ import annotations

from dataclasses import dataclass

import cv2
import numpy as np
import numpy.typing as npt

from calib.projection import quaternion_from_rotation
from calib.types import PINHOLE, Intrinsics, StereoRig

FloatArray = npt.NDArray[np.float64]


@dataclass(frozen=True)
class PinholeSolution:
    """The solved rig plus the per-stage residuals for the quality report."""

    rig: StereoRig
    left_rms_px: float
    right_rms_px: float
    stereo_rms_px: float


def _as_view_list(views: list[FloatArray]) -> list[npt.NDArray[np.float32]]:
    # OpenCV 5.x demands float32 Point3f object points (float64 worked in 4.x).
    return [np.asarray(view, dtype=np.float32).reshape(-1, 1, 3) for view in views]


def _as_point_list(points: list[FloatArray]) -> list[npt.NDArray[np.float32]]:
    return [np.asarray(point, dtype=np.float32).reshape(-1, 1, 2) for point in points]


def calibrate_pinhole(
    object_points: list[FloatArray],
    image_points: list[FloatArray],
    image_size: tuple[int, int],
) -> tuple[Intrinsics, float]:
    """Per-camera pinhole intrinsics via `cv2.calibrateCamera`; returns (camera, RMS)."""
    rms, matrix, dist, _, _ = cv2.calibrateCamera(
        _as_view_list(object_points), _as_point_list(image_points), image_size, None, None
    )
    intrinsics = Intrinsics(
        PINHOLE,
        float(matrix[0][0]),
        float(matrix[1][1]),
        float(matrix[0][2]),
        float(matrix[1][2]),
        tuple(float(value) for value in np.asarray(dist).ravel()),
    )
    return intrinsics, float(rms)


def solve_pinhole_stereo(
    object_points: list[FloatArray],
    image_left: list[FloatArray],
    image_right: list[FloatArray],
    image_size: tuple[int, int],
) -> PinholeSolution:
    """Solves both cameras and their relative pose; intrinsics stay fixed in the stereo stage.

    The intrinsics are solved per camera first, then `cv2.stereoCalibrate` with
    `CALIB_FIX_INTRINSIC` recovers the relative pose, so the reported RMS is the
    stereo-consistent residual rather than a re-fit of the same data.
    """
    if len(object_points) != len(image_left) or len(object_points) != len(image_right):
        raise ValueError("every view needs left and right image points")
    if len(object_points) < 3:
        raise ValueError("a stereo solve needs at least three views")

    left, left_rms = calibrate_pinhole(object_points, image_left, image_size)
    right, right_rms = calibrate_pinhole(object_points, image_right, image_size)

    stereo_rms, _, _, _, _, rotation, translation, _, _ = cv2.stereoCalibrate(
        _as_view_list(object_points),
        _as_point_list(image_left),
        _as_point_list(image_right),
        left.matrix(),
        left.dist_array(),
        right.matrix(),
        right.dist_array(),
        image_size,
        flags=cv2.CALIB_FIX_INTRINSIC,
    )
    rotation_matrix = np.asarray(rotation, dtype=np.float64)
    translation_vector = np.asarray(translation, dtype=np.float64).reshape(3)
    rig = StereoRig(
        image_size=image_size,
        left=left,
        right=right,
        right_from_left_p=(float(translation_vector[0]), float(translation_vector[1]), float(translation_vector[2])),
        right_from_left_q=quaternion_from_rotation(rotation_matrix),
        reproj_rms_px=float(stereo_rms),
        n_views=len(object_points),
    )
    return PinholeSolution(rig=rig, left_rms_px=float(left_rms), right_rms_px=float(right_rms), stereo_rms_px=float(stereo_rms))

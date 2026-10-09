"""Stereo solvers: recover a stereo rig from projected corner views (pinhole or
fisheye lens model)."""

from __future__ import annotations

from dataclasses import dataclass

import cv2
import numpy as np
import numpy.typing as npt

from calib.projection import quaternion_from_rotation
from calib.types import FISHEYE, PINHOLE, Intrinsics, StereoRig

FloatArray = npt.NDArray[np.float64]

# The fisheye flags live under `cv2.fisheye` on 4.x and under the top-level
# `cv2` namespace on 5.x; resolve whichever this build exposes.
_CALIB_RECOMPUTE_EXTRINSIC: int = getattr(cv2, "CALIB_RECOMPUTE_EXTRINSIC", None) or cv2.fisheye.CALIB_RECOMPUTE_EXTRINSIC
_CALIB_FIX_INTRINSIC: int = getattr(cv2, "CALIB_FIX_INTRINSIC", None) or cv2.fisheye.CALIB_FIX_INTRINSIC


@dataclass(frozen=True)
class StereoSolution:
    """The solved rig, its model and the per-stage residuals for the report."""

    model: str
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
) -> StereoSolution:
    """Solves both cameras and their relative pose; intrinsics stay fixed in the stereo stage.

    The intrinsics are solved per camera first, then `cv2.stereoCalibrate` with
    `CALIB_FIX_INTRINSIC` recovers the relative pose, so the reported RMS is the
    stereo-consistent residual rather than a re-fit of the same data.
    """
    _validate_view_lists(object_points, image_left, image_right)

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
    rig = _rig_from_stereo(left, right, rotation, translation, image_size, float(stereo_rms), len(object_points))
    return StereoSolution(
        model=PINHOLE,
        rig=rig,
        left_rms_px=float(left_rms),
        right_rms_px=float(right_rms),
        stereo_rms_px=float(stereo_rms),
    )


def _validate_view_lists(
    object_points: list[FloatArray], image_left: list[FloatArray], image_right: list[FloatArray]
) -> None:
    if len(object_points) != len(image_left) or len(object_points) != len(image_right):
        raise ValueError("every view needs left and right image points")
    if len(object_points) < 3:
        raise ValueError("a stereo solve needs at least three views")


def _rig_from_stereo(
    left: Intrinsics,
    right: Intrinsics,
    rotation: npt.NDArray[np.generic],
    translation: npt.NDArray[np.generic],
    image_size: tuple[int, int],
    stereo_rms: float,
    n_views: int,
) -> StereoRig:
    rotation_matrix = np.asarray(rotation, dtype=np.float64)
    translation_vector = np.asarray(translation, dtype=np.float64).reshape(3)
    return StereoRig(
        image_size=image_size,
        left=left,
        right=right,
        right_from_left_p=(float(translation_vector[0]), float(translation_vector[1]), float(translation_vector[2])),
        right_from_left_q=quaternion_from_rotation(rotation_matrix),
        reproj_rms_px=stereo_rms,
        n_views=n_views,
    )


def _fisheye_objects(views: list[FloatArray]) -> list[FloatArray]:
    return [np.asarray(view, dtype=np.float64).reshape(-1, 1, 3) for view in views]


def _fisheye_points(points: list[FloatArray]) -> list[FloatArray]:
    return [np.asarray(point, dtype=np.float64).reshape(-1, 1, 2) for point in points]


def calibrate_fisheye(
    object_points: list[FloatArray],
    image_points: list[FloatArray],
    image_size: tuple[int, int],
) -> tuple[Intrinsics, float]:
    """Per-camera fisheye (equidistant) intrinsics via `cv2.fisheye.calibrate`."""
    matrix = np.eye(3, dtype=np.float64)
    dist = np.zeros((4, 1), dtype=np.float64)
    rms, calibrated_matrix, calibrated_dist, _, _ = cv2.fisheye.calibrate(
        _fisheye_objects(object_points),
        _fisheye_points(image_points),
        image_size,
        matrix,
        dist,
        flags=_CALIB_RECOMPUTE_EXTRINSIC,
    )
    calibrated = np.asarray(calibrated_matrix, dtype=np.float64)
    intrinsics = Intrinsics(
        FISHEYE,
        float(calibrated[0][0]),
        float(calibrated[1][1]),
        float(calibrated[0][2]),
        float(calibrated[1][2]),
        tuple(float(value) for value in np.asarray(calibrated_dist).ravel()),
    )
    return intrinsics, float(rms)


def solve_fisheye_stereo(
    object_points: list[FloatArray],
    image_left: list[FloatArray],
    image_right: list[FloatArray],
    image_size: tuple[int, int],
) -> StereoSolution:
    """The fisheye counterpart of `solve_pinhole_stereo` (same staging)."""
    _validate_view_lists(object_points, image_left, image_right)

    left, left_rms = calibrate_fisheye(object_points, image_left, image_size)
    right, right_rms = calibrate_fisheye(object_points, image_right, image_size)

    result = cv2.fisheye.stereoCalibrate(
        _fisheye_objects(object_points),
        _fisheye_points(image_left),
        _fisheye_points(image_right),
        left.matrix(),
        left.dist_array().reshape(-1, 1),
        right.matrix(),
        right.dist_array().reshape(-1, 1),
        image_size,
        flags=_CALIB_FIX_INTRINSIC,
    )
    stereo_rms = float(result[0])
    rotation = np.asarray(result[5], dtype=np.float64)
    translation = np.asarray(result[6], dtype=np.float64)
    rig = _rig_from_stereo(left, right, rotation, translation, image_size, stereo_rms, len(object_points))
    return StereoSolution(
        model=FISHEYE,
        rig=rig,
        left_rms_px=float(left_rms),
        right_rms_px=float(right_rms),
        stereo_rms_px=stereo_rms,
    )

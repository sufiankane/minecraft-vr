"""Projection and rigid-transform helpers (pinhole and fisheye models)."""

from __future__ import annotations

import math

import cv2
import numpy as np
import numpy.typing as npt

from calib.types import FISHEYE, PINHOLE, Intrinsics


def rotation_matrix(rvec: npt.NDArray[np.float64]) -> npt.NDArray[np.float64]:
    """Rodrigues vector to a 3x3 rotation matrix."""
    matrix = cv2.Rodrigues(np.asarray(rvec, dtype=np.float64).reshape(3))[0]
    return np.asarray(matrix, dtype=np.float64)


def transform_points(
    points: npt.NDArray[np.float64], rvec: npt.NDArray[np.float64], tvec: npt.NDArray[np.float64]
) -> npt.NDArray[np.float64]:
    """Applies `x' = R(rvec) x + t` to an (N, 3) point array."""
    rotation = rotation_matrix(rvec)
    translation = np.asarray(tvec, dtype=np.float64).reshape(3)
    return points @ rotation.T + translation


def rotation_from_quaternion(quaternion: tuple[float, float, float, float]) -> npt.NDArray[np.float64]:
    """Unit quaternion `(w, x, y, z)` to a 3x3 rotation matrix."""
    w, x, y, z = (float(component) for component in quaternion)
    norm = math.sqrt(w * w + x * x + y * y + z * z)
    if norm <= 0.0:
        raise ValueError("a quaternion must have a non-zero norm")
    w, x, y, z = w / norm, x / norm, y / norm, z / norm
    return np.array(
        [
            [1.0 - 2.0 * (y * y + z * z), 2.0 * (x * y - z * w), 2.0 * (x * z + y * w)],
            [2.0 * (x * y + z * w), 1.0 - 2.0 * (x * x + z * z), 2.0 * (y * z - x * w)],
            [2.0 * (x * z - y * w), 2.0 * (y * z + x * w), 1.0 - 2.0 * (x * x + y * y)],
        ],
        dtype=np.float64,
    )


def quaternion_from_rotation(rotation: npt.NDArray[np.float64]) -> tuple[float, float, float, float]:
    """Rotation matrix to a unit quaternion `(w, x, y, z)` (Shepperd's method)."""
    matrix = np.asarray(rotation, dtype=np.float64)
    trace = float(matrix[0, 0] + matrix[1, 1] + matrix[2, 2])
    if trace > 0.0:
        scale = math.sqrt(trace + 1.0) * 2.0
        w = 0.25 * scale
        x = (matrix[2, 1] - matrix[1, 2]) / scale
        y = (matrix[0, 2] - matrix[2, 0]) / scale
        z = (matrix[1, 0] - matrix[0, 1]) / scale
    elif matrix[0, 0] > matrix[1, 1] and matrix[0, 0] > matrix[2, 2]:
        scale = math.sqrt(1.0 + matrix[0, 0] - matrix[1, 1] - matrix[2, 2]) * 2.0
        w = (matrix[2, 1] - matrix[1, 2]) / scale
        x = 0.25 * scale
        y = (matrix[0, 1] + matrix[1, 0]) / scale
        z = (matrix[0, 2] + matrix[2, 0]) / scale
    elif matrix[1, 1] > matrix[2, 2]:
        scale = math.sqrt(1.0 + matrix[1, 1] - matrix[0, 0] - matrix[2, 2]) * 2.0
        w = (matrix[0, 2] - matrix[2, 0]) / scale
        x = (matrix[0, 1] + matrix[1, 0]) / scale
        y = 0.25 * scale
        z = (matrix[1, 2] + matrix[2, 1]) / scale
    else:
        scale = math.sqrt(1.0 + matrix[2, 2] - matrix[0, 0] - matrix[1, 1]) * 2.0
        w = (matrix[1, 0] - matrix[0, 1]) / scale
        x = (matrix[0, 2] + matrix[2, 0]) / scale
        y = (matrix[1, 2] + matrix[2, 1]) / scale
        z = 0.25 * scale
    norm = math.sqrt(w * w + x * x + y * y + z * z)
    return (w / norm, x / norm, y / norm, z / norm)


def project(intrinsics: Intrinsics, points_camera: npt.NDArray[np.float64]) -> npt.NDArray[np.float64]:
    """Projects (N, 3) camera-frame points with the camera's own model."""
    points = np.asarray(points_camera, dtype=np.float64).reshape(-1, 1, 3)
    if intrinsics.model == FISHEYE:
        projected, _ = cv2.fisheye.projectPoints(
            points, np.zeros(3), np.zeros(3), intrinsics.matrix(), intrinsics.dist_array().reshape(-1, 1)
        )
    elif intrinsics.model == PINHOLE:
        projected, _ = cv2.projectPoints(
            points, np.zeros(3), np.zeros(3), intrinsics.matrix(), intrinsics.dist_array()
        )
    else:
        raise ValueError(f"unsupported camera model: {intrinsics.model!r}")
    return np.asarray(projected, dtype=np.float64).reshape(-1, 2)

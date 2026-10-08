"""S9 Task 1 coverage: the fisheye projection branch, the quaternion's
Shepperd branches and the solvers' argument validation."""

from __future__ import annotations

import math

import numpy as np
import pytest

from calib.projection import (
    project,
    quaternion_from_rotation,
    rotation_from_quaternion,
    rotation_matrix,
)
from calib.solver import solve_pinhole_stereo
from calib.synth import default_pinhole_rig, synthetic_views
from calib.types import FISHEYE, PINHOLE, BoardSpec, Intrinsics

BOARD = BoardSpec(inner_cols=9, inner_rows=6, square_m=0.025)


def test_fisheye_projection_uses_the_fisheye_model() -> None:
    fisheye = Intrinsics(FISHEYE, 300.0, 300.0, 320.0, 240.0, (-0.02, 0.001, 0.0, 0.0))
    pinhole = Intrinsics(PINHOLE, 300.0, 300.0, 320.0, 240.0, (0.0, 0.0, 0.0, 0.0, 0.0))
    point = np.array([[0.05, 0.0, 0.5]])
    fisheye_px = project(fisheye, point)
    pinhole_px = project(pinhole, point)
    assert np.all(np.isfinite(fisheye_px))
    assert fisheye_px[0][0] > fisheye.cx
    assert abs(fisheye_px[0][1] - fisheye.cy) < 1.0
    # The distortion pulls the corner point inward relative to the pinhole ray.
    assert fisheye_px[0][0] < pinhole_px[0][0]


def test_projection_rejects_an_unknown_model() -> None:
    unknown = Intrinsics("cylindrical", 300.0, 300.0, 320.0, 240.0, ())
    with pytest.raises(ValueError):
        project(unknown, np.array([[0.0, 0.0, 0.5]]))


def test_quaternion_shepperd_branches_round_trip() -> None:
    # Half turns put the trace at -1 and force each of the three off-diagonal
    # branches.
    for rvec in (
        np.array([math.pi, 0.0, 0.0]),
        np.array([0.0, math.pi, 0.0]),
        np.array([0.0, 0.0, math.pi]),
    ):
        rotation = rotation_matrix(rvec)
        restored = rotation_from_quaternion(quaternion_from_rotation(rotation))
        assert np.allclose(restored, rotation, atol=1e-9)


def test_rotation_from_quaternion_rejects_a_zero_norm() -> None:
    with pytest.raises(ValueError):
        rotation_from_quaternion((0.0, 0.0, 0.0, 0.0))


def test_solver_rejects_mismatched_view_lists() -> None:
    rig = default_pinhole_rig()
    views = synthetic_views(rig, BOARD, n_views=4, seed=5)
    with pytest.raises(ValueError):
        solve_pinhole_stereo(views.object_points, views.image_left[:-1], views.image_right, rig.image_size)

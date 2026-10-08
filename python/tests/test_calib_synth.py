"""S9 Task 1: the solver recovers a known synthetic rig within the dossier's
ground-truth tolerances (focal 0.5 percent, baseline 0.5 mm, RMS below the
0.5 px threshold proposed for the stage ADR)."""

from __future__ import annotations

import math

import numpy as np
import pytest

from calib.projection import quaternion_from_rotation, rotation_from_quaternion, rotation_matrix
from calib.solver import solve_pinhole_stereo
from calib.synth import default_pinhole_rig, synthetic_views
from calib.types import BoardSpec

BOARD = BoardSpec(inner_cols=9, inner_rows=6, square_m=0.025)
FOCAL_TOLERANCE = 0.005  # 0.5 percent
BASELINE_TOLERANCE_M = 0.0005  # 0.5 mm
RMS_THRESHOLD_PX = 0.5


def _assert_recovers(seed: int, noise_px: float, lateral_tolerance_m: float) -> None:
    rig = default_pinhole_rig()
    views = synthetic_views(rig, BOARD, n_views=14, seed=seed, noise_px=noise_px)
    solution = solve_pinhole_stereo(views.object_points, views.image_left, views.image_right, rig.image_size)
    solved = solution.rig

    assert math.isclose(solved.left.fx, rig.left.fx, rel_tol=FOCAL_TOLERANCE)
    assert math.isclose(solved.left.fy, rig.left.fy, rel_tol=FOCAL_TOLERANCE)
    assert math.isclose(solved.right.fx, rig.right.fx, rel_tol=FOCAL_TOLERANCE)
    assert math.isclose(solved.right.fy, rig.right.fy, rel_tol=FOCAL_TOLERANCE)
    assert math.isclose(solved.baseline_m, rig.baseline_m, abs_tol=BASELINE_TOLERANCE_M)
    assert solved.reproj_rms_px < RMS_THRESHOLD_PX
    assert solved.n_views == 14

    # The relative pose's direction: the right camera is displaced along -X in
    # the left frame and the recovered rotation is (near) identity. The lateral
    # components carry the pose's noise floor, so their bound is a parameter
    # (the gate's own tolerances are focal and baseline, asserted above).
    assert solved.right_from_left_p[0] < 0.0
    assert abs(solved.right_from_left_p[1]) < lateral_tolerance_m
    assert abs(solved.right_from_left_p[2]) < lateral_tolerance_m
    assert solved.right_from_left_q[0] > 0.999


def test_exact_synthetic_views_recover_the_rig() -> None:
    _assert_recovers(seed=7, noise_px=0.0, lateral_tolerance_m=1e-4)


def test_slightly_noisy_views_recover_the_rig() -> None:
    _assert_recovers(seed=11, noise_px=0.05, lateral_tolerance_m=2e-3)


def test_fewer_than_three_views_are_rejected() -> None:
    rig = default_pinhole_rig()
    with pytest.raises(ValueError):
        synthetic_views(rig, BOARD, n_views=2, seed=3)
    views = synthetic_views(rig, BOARD, n_views=3, seed=3)
    # The solver refuses a short list even when it is built by hand.
    with pytest.raises(ValueError):
        solve_pinhole_stereo(views.object_points[:1], views.image_left[:1], views.image_right[:1], rig.image_size)


def test_quaternion_round_trip_is_stable() -> None:
    rvec = np.array([0.12, -0.34, 0.05])
    rotation = rotation_matrix(rvec)
    restored = rotation_from_quaternion(quaternion_from_rotation(rotation))
    assert np.allclose(restored, rotation, atol=1e-9)

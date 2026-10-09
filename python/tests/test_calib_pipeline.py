"""S9 Task 2: rendered sessions detect, solve and select the lens model."""

from __future__ import annotations

import math
from pathlib import Path

import numpy as np
import pytest
from calib_fixtures import render_session, write_session

from calib.pipeline import MIN_VIEWS, collect_views, run_calibration, select_model
from calib.session import SessionError
from calib.solver import StereoSolution, solve_fisheye_stereo
from calib.synth import default_fisheye_rig, default_pinhole_rig, synthetic_views
from calib.types import FISHEYE, PINHOLE, BoardSpec, Intrinsics, StereoRig

BOARD = BoardSpec(inner_cols=9, inner_rows=6, square_m=0.025)
FOCAL_TOLERANCE = 0.005
BASELINE_TOLERANCE_M = 0.0005


def test_rendered_session_recovers_the_rig(tmp_path: Path) -> None:
    rig = default_pinhole_rig()
    render_session(tmp_path / "session", BOARD, rig, views=14, seed=21)
    run = run_calibration(tmp_path / "session", BOARD)

    assert run.views_used == 14
    assert run.chosen_model == PINHOLE
    solved = run.solution.rig
    assert math.isclose(solved.left.fx, rig.left.fx, rel_tol=FOCAL_TOLERANCE)
    assert math.isclose(solved.right.fy, rig.right.fy, rel_tol=FOCAL_TOLERANCE)
    assert math.isclose(solved.baseline_m, rig.baseline_m, abs_tol=BASELINE_TOLERANCE_M)
    assert solved.reproj_rms_px < 0.5
    assert len(run.attempts) == 2, "both lens models are attempted and reported"


def test_rendered_bin_session_is_read_through_the_padding(tmp_path: Path) -> None:
    rig = default_pinhole_rig()
    render_session(tmp_path / "session", BOARD, rig, views=10, seed=22, storage="bin")
    views = collect_views(tmp_path / "session", BOARD)
    assert len(views) == 10
    run = run_calibration(tmp_path / "session", BOARD)
    assert math.isclose(run.solution.rig.baseline_m, rig.baseline_m, abs_tol=BASELINE_TOLERANCE_M)


def test_select_model_prefers_pinhole_on_pinhole_points() -> None:
    rig = default_pinhole_rig()
    views = synthetic_views(rig, BOARD, n_views=14, seed=23)
    from calib.pipeline import View

    collected = [
        View(object_points=obj, image_left=left, image_right=right)
        for obj, left, right in zip(views.object_points, views.image_left, views.image_right)
    ]
    solution, attempts = select_model(collected, rig.image_size)
    assert solution.model == PINHOLE, "pinhole wins whenever it already meets the RMS threshold"
    pinhole_attempt = next(attempt for attempt in attempts if attempt.model == PINHOLE)
    assert pinhole_attempt.stereo_rms_px is not None and pinhole_attempt.stereo_rms_px < 0.5


def _solution(model: str, rms: float) -> StereoSolution:
    rig = StereoRig(
        image_size=(640, 480),
        left=Intrinsics(model, 300.0, 300.0, 320.0, 240.0, (0.0, 0.0, 0.0, 0.0)),
        right=Intrinsics(model, 300.0, 300.0, 320.0, 240.0, (0.0, 0.0, 0.0, 0.0)),
        right_from_left_p=(-0.0635, 0.0, 0.0),
        right_from_left_q=(1.0, 0.0, 0.0, 0.0),
        reproj_rms_px=rms,
        n_views=14,
    )
    return StereoSolution(model=model, rig=rig, left_rms_px=rms, right_rms_px=rms, stereo_rms_px=rms)


def test_choose_solution_rule() -> None:
    from calib.pipeline import choose_solution

    # Pinhole within the threshold wins even when fisheye fits better (the
    # simpler model is preferred once it is good enough).
    assert choose_solution([_solution(PINHOLE, 0.4), _solution(FISHEYE, 0.1)]).model == PINHOLE
    # A failing pinhole is replaced by a better fisheye fit.
    assert choose_solution([_solution(PINHOLE, 0.8), _solution(FISHEYE, 0.3)]).model == FISHEYE
    # A failing pinhole still beats a worse fisheye fit.
    assert choose_solution([_solution(PINHOLE, 0.8), _solution(FISHEYE, 1.2)]).model == PINHOLE
    # Whatever converged is used when only one model does.
    assert choose_solution([_solution(FISHEYE, 1.2)]).model == FISHEYE
    with pytest.raises(SessionError, match="neither lens model"):
        choose_solution([])


def test_fisheye_solver_recovers_fisheye_ground_truth() -> None:
    rig = default_fisheye_rig()
    views = synthetic_views(rig, BOARD, n_views=20, seed=24, distance_range_m=(0.6, 1.0))
    solution = solve_fisheye_stereo(views.object_points, views.image_left, views.image_right, rig.image_size)
    assert solution.model == FISHEYE
    assert math.isclose(solution.rig.left.fx, rig.left.fx, rel_tol=FOCAL_TOLERANCE)
    assert math.isclose(solution.rig.baseline_m, rig.baseline_m, abs_tol=BASELINE_TOLERANCE_M)
    assert solution.stereo_rms_px < 0.5


def test_too_few_views_refuse_to_solve(tmp_path: Path) -> None:
    rig = default_pinhole_rig()
    render_session(tmp_path / "session", BOARD, rig, views=MIN_VIEWS - 2, seed=25)
    with pytest.raises(SessionError, match="at least"):
        run_calibration(tmp_path / "session", BOARD)


def test_session_without_views_refuses_to_solve(tmp_path: Path) -> None:
    rig = default_pinhole_rig()
    render_session(tmp_path / "session", BOARD, rig, views=1, seed=26)
    with pytest.raises(SessionError, match="at least"):
        run_calibration(tmp_path / "session", BOARD)


def test_session_with_no_board_reports_zero_views(tmp_path: Path) -> None:
    blank = np.zeros((480, 640), dtype=np.uint8)
    write_session(tmp_path, [(blank, blank)], width=640, height=480)
    assert collect_views(tmp_path, BOARD) == []
    with pytest.raises(SessionError, match="only 0 board views detected"):
        run_calibration(tmp_path, BOARD)

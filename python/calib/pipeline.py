"""The calibration pipeline: collect board views from a `.cgrec` session,
solve both lens models and apply the model-selection rule (pinhole wins when
it already meets the residual threshold; otherwise the lower stereo RMS)."""

from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path

import cv2
import numpy as np
import numpy.typing as npt

from calib.detect import find_board
from calib.session import SessionError, frame_sequences, read_frame, read_manifest
from calib.solver import StereoSolution, solve_fisheye_stereo, solve_pinhole_stereo
from calib.types import FISHEYE, PINHOLE, BoardSpec

FloatArray = npt.NDArray[np.float64]

#: Fewer detected views than this refuse to solve; the procedure targets 20-30.
MIN_VIEWS = 8

#: Pinhole is the simpler model and wins whenever it already meets this
#: residual; fisheye is only chosen when pinhole fails the threshold and the
#: fisheye fit is better (the S9 ADR proposes 0.5 px).
MODEL_RMS_THRESHOLD_PX = 0.5


@dataclass(frozen=True)
class View:
    """One detected board view: board-local corners and both cameras' pixels."""

    object_points: FloatArray
    image_left: FloatArray
    image_right: FloatArray


@dataclass(frozen=True)
class ModelAttempt:
    """One lens model's outcome for the quality report."""

    model: str
    stereo_rms_px: float | None
    n_views: int
    error: str | None = None


@dataclass(frozen=True)
class CalibrationRun:
    """Everything the writer and the report need."""

    session_dir: Path
    board: BoardSpec
    views_used: int
    attempts: tuple[ModelAttempt, ...]
    solution: StereoSolution
    device_serial: str | None

    @property
    def chosen_model(self) -> str:
        return self.solution.model


def collect_views(
    session_dir: Path, board: BoardSpec, *, max_views: int = 0, equalize: bool = True
) -> list[View]:
    """Detects the board in both streams of every frame; frames where either
    camera misses the pattern are skipped."""
    manifest = read_manifest(session_dir)
    views: list[View] = []
    object_points = board.object_points().astype(np.float64)
    for seq in frame_sequences(session_dir):
        frame = read_frame(session_dir, manifest, seq)
        left_px = find_board(frame.left, board, equalize=equalize)
        right_px = find_board(frame.right, board, equalize=equalize)
        if left_px is None or right_px is None:
            continue
        views.append(View(object_points=object_points.copy(), image_left=left_px, image_right=right_px))
        if max_views > 0 and len(views) >= max_views:
            break
    return views


def choose_solution(solutions: list[StereoSolution]) -> StereoSolution:
    """The model-selection rule: pinhole wins whenever its residual already
    meets `MODEL_RMS_THRESHOLD_PX` (the simpler model); otherwise the lower
    residual among the converged models wins."""
    if not solutions:
        raise SessionError("neither lens model converged; see the attempts for the errors")
    pinhole = next((solution for solution in solutions if solution.model == PINHOLE), None)
    if pinhole is not None and pinhole.stereo_rms_px <= MODEL_RMS_THRESHOLD_PX:
        return pinhole
    return min(solutions, key=lambda solution: solution.stereo_rms_px)


def select_model(views: list[View], image_size: tuple[int, int]) -> tuple[StereoSolution, tuple[ModelAttempt, ...]]:
    """Solves pinhole and fisheye and applies `choose_solution`."""
    if not views:
        raise SessionError("no board views to solve")
    object_points = [view.object_points for view in views]
    image_left = [view.image_left for view in views]
    image_right = [view.image_right for view in views]

    solutions: list[StereoSolution] = []
    attempts: list[ModelAttempt] = []
    for model, solver in ((PINHOLE, solve_pinhole_stereo), (FISHEYE, solve_fisheye_stereo)):
        try:
            solution = solver(object_points, image_left, image_right, image_size)
        except (cv2.error, ValueError) as error:
            attempts.append(ModelAttempt(model=model, stereo_rms_px=None, n_views=0, error=str(error).splitlines()[0]))
            continue
        solutions.append(solution)
        attempts.append(ModelAttempt(model=model, stereo_rms_px=solution.stereo_rms_px, n_views=solution.rig.n_views))
    return choose_solution(solutions), tuple(attempts)


def run_calibration(session_dir: Path, board: BoardSpec, *, max_views: int = 0) -> CalibrationRun:
    """Collects, solves and selects; raises `SessionError` when the session is
    unreadable or has too few usable views."""
    manifest = read_manifest(session_dir)
    views = collect_views(session_dir, board, max_views=max_views)
    if len(views) < MIN_VIEWS:
        raise SessionError(f"only {len(views)} board views detected; at least {MIN_VIEWS} are required")
    solution, attempts = select_model(views, (manifest.width, manifest.height))
    return CalibrationRun(
        session_dir=session_dir,
        board=board,
        views_used=len(views),
        attempts=attempts,
        solution=solution,
        device_serial=manifest.device_serial,
    )


def format_report(run: CalibrationRun) -> str:
    """The human-readable quality report printed by the CLI."""
    lines = [
        f"session: {run.session_dir}",
        f"board: {run.board.inner_cols}x{run.board.inner_rows} squares of {run.board.square_m} m",
        f"views used: {run.views_used}",
    ]
    for attempt in run.attempts:
        if attempt.stereo_rms_px is None:
            lines.append(f"  {attempt.model}: failed ({attempt.error})")
        else:
            lines.append(f"  {attempt.model}: rms {attempt.stereo_rms_px:.4f} px over {attempt.n_views} views")
    rig = run.solution.rig
    lines.extend(
        [
            f"chosen model: {run.chosen_model}",
            f"left fx/fy: {rig.left.fx:.2f} / {rig.left.fy:.2f}",
            f"right fx/fy: {rig.right.fx:.2f} / {rig.right.fy:.2f}",
            f"baseline: {rig.baseline_m * 1000.0:.3f} mm",
            f"stereo rms: {rig.reproj_rms_px:.4f} px",
        ]
    )
    return "\n".join(lines)

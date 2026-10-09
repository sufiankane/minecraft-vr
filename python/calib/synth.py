"""Synthetic ground-truth stereo data: a known rig, board poses, noiseless or noisy views."""

from __future__ import annotations

from dataclasses import dataclass

import numpy as np
import numpy.typing as npt

from calib.projection import project, rotation_from_quaternion, transform_points
from calib.types import FISHEYE, PINHOLE, BoardSpec, Intrinsics, StereoRig

FloatArray = npt.NDArray[np.float64]


def default_pinhole_rig(
    *, fx: float = 600.0, baseline_m: float = 0.0635, image_size: tuple[int, int] = (640, 480)
) -> StereoRig:
    """A realistic Luma-Ultra-like pair: square pixels, centred principal point."""
    width, height = image_size
    intrinsics = Intrinsics(PINHOLE, fx, fx, width / 2.0, height / 2.0, (0.0, 0.0, 0.0, 0.0, 0.0))
    # The right camera sits `baseline` to the +X side of the left camera and
    # looks the same way (identity rotation); its frame translates by -x.
    return StereoRig(
        image_size=image_size,
        left=intrinsics,
        right=intrinsics,
        right_from_left_p=(-baseline_m, 0.0, 0.0),
        right_from_left_q=(1.0, 0.0, 0.0, 0.0),
        reproj_rms_px=0.0,
        n_views=0,
    )


def default_fisheye_rig(
    *,
    fx: float = 300.0,
    baseline_m: float = 0.0635,
    image_size: tuple[int, int] = (640, 480),
    dist: tuple[float, float, float, float] = (-0.02, 0.001, 0.0, 0.0),
) -> StereoRig:
    """A fisheye (equidistant) pair; the default distortion is mild, so tests
    that need a pinhole-rejecting lens pass a stronger `dist`."""
    width, height = image_size
    intrinsics = Intrinsics(FISHEYE, fx, fx, width / 2.0, height / 2.0, dist)
    return StereoRig(
        image_size=image_size,
        left=intrinsics,
        right=intrinsics,
        right_from_left_p=(-baseline_m, 0.0, 0.0),
        right_from_left_q=(1.0, 0.0, 0.0, 0.0),
        reproj_rms_px=0.0,
        n_views=0,
    )


@dataclass(frozen=True)
class SynthViews:
    """Object points and the two cameras' projected pixels, per view."""

    object_points: list[FloatArray]
    image_left: list[FloatArray]
    image_right: list[FloatArray]
    rig: StereoRig


def _random_board_pose(rng: np.random.Generator, z_m: float) -> tuple[FloatArray, FloatArray]:
    """A view tilted by up to ~25 degrees, centred within the frame."""
    angle = np.deg2rad(22.0)
    rvec = rng.uniform(-angle, angle, 3)
    tvec = np.array(
        [rng.uniform(-0.08, 0.08), rng.uniform(-0.06, 0.06), z_m],
        dtype=np.float64,
    )
    return rvec, tvec


def synthetic_views(
    rig: StereoRig,
    board: BoardSpec,
    *,
    n_views: int,
    seed: int,
    noise_px: float = 0.0,
    distance_range_m: tuple[float, float] = (0.35, 0.65),
) -> SynthViews:
    """Generates `n_views` board views seen by both cameras of `rig`.

    The left camera's frame is the world frame; the right camera's projection
    composes the rig's `right_from_left` transform, so the returned pixels are
    consistent with the 5.7 convention the solver reports.
    """
    if n_views < 3:
        raise ValueError("a stereo solve needs at least three views")
    rng = np.random.default_rng(seed)
    board_points = board.object_points().astype(np.float64)

    right_rotation = rotation_from_quaternion(rig.right_from_left_q)
    right_translation = np.asarray(rig.right_from_left_p, dtype=np.float64)

    object_points: list[FloatArray] = []
    image_left: list[FloatArray] = []
    image_right: list[FloatArray] = []
    for _ in range(n_views):
        distance = float(rng.uniform(distance_range_m[0], distance_range_m[1]))
        rvec, tvec = _random_board_pose(rng, distance)
        view_points = transform_points(board_points, rvec, tvec)
        left_px = project(rig.left, view_points)
        right_view = view_points @ right_rotation.T + right_translation
        right_px = project(rig.right, right_view)
        if noise_px > 0.0:
            left_px = left_px + rng.normal(0.0, noise_px, left_px.shape)
            right_px = right_px + rng.normal(0.0, noise_px, right_px.shape)
        object_points.append(board_points.copy())
        image_left.append(left_px)
        image_right.append(right_px)
    return SynthViews(object_points=object_points, image_left=image_left, image_right=image_right, rig=rig)

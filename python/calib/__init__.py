"""Cubeglass offline calibration tooling (dossier S9): synthetic ground truth,
stereo solve and, once the pipeline lands, `.cgrec` captures and the
`calibration.json` writer."""

from calib.projection import (
    project,
    quaternion_from_rotation,
    rotation_from_quaternion,
    rotation_matrix,
    transform_points,
)
from calib.solver import PinholeSolution, calibrate_pinhole, solve_pinhole_stereo
from calib.synth import SynthViews, default_pinhole_rig, synthetic_views
from calib.types import FISHEYE, PINHOLE, BoardSpec, Intrinsics, StereoRig

#: Major version of the `calibration.json` schema (dossier 5.7). The loader
#: rejects unknown major versions; a breaking change bumps this constant.
SCHEMA_VERSION = 1

__all__ = [
    "FISHEYE",
    "PINHOLE",
    "SCHEMA_VERSION",
    "BoardSpec",
    "Intrinsics",
    "PinholeSolution",
    "StereoRig",
    "SynthViews",
    "calibrate_pinhole",
    "default_pinhole_rig",
    "project",
    "quaternion_from_rotation",
    "rotation_from_quaternion",
    "rotation_matrix",
    "solve_pinhole_stereo",
    "synthetic_views",
    "transform_points",
]

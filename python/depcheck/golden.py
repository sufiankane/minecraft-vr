"""Independent reference implementation for the golden transform fixture.

The C++ golden gate (``cpp/tests/core-math/golden_test.cpp``) checks the engine
against ``contracts/golden/transforms.json``; both sides can drift together
because the fixture values were themselves captured from the engine (security
review M-12). This module derives the expected values independently, in plain
Python, from the published conventions (ADR-0004: right-handed, y-up, metres,
seconds; Hamilton quaternions, ``[w, x, y, z]``) and compares the result with the
fixture within the fixture's own tolerance. It is a cross-check of the *values*,
not a replacement for the C++ gate.

Only the standard library is used. ``sdk_pose_to_internal`` casts through
IEEE-754 single precision because the SDK pose is a ``float[7]`` at the ABI
boundary, and the fixture's 1e-6 tolerance then holds.
"""

from __future__ import annotations

import json
import math
import struct
from collections.abc import Callable, Mapping, Sequence
from pathlib import Path
from typing import cast

GOLDEN_FIXTURE_PARTS: tuple[str, ...] = ("contracts", "golden", "transforms.json")


class GoldenError(Exception):
    """Raised when the golden fixture cannot be read or a case is unknown."""


Vec3 = tuple[float, float, float]
Quat = tuple[float, float, float, float]
Pose = tuple[Vec3, Quat]


def _float32(value: float) -> float:
    return float(struct.unpack("f", struct.pack("f", value))[0])


def _as_float_list(value: object, length: int, case: str) -> list[float]:
    if not isinstance(value, list) or len(value) != length:
        raise GoldenError(f"{case}: expected a {length}-component array")
    result: list[float] = []
    for item in cast(list[object], value):
        if not isinstance(item, (int, float)) or isinstance(item, bool):
            raise GoldenError(f"{case}: non-numeric component {item!r}")
        result.append(float(item))
    return result


def _vec3(value: object, case: str) -> Vec3:
    x, y, z = _as_float_list(value, 3, case)
    return (x, y, z)


def _quat(value: object, case: str) -> Quat:
    w, x, y, z = _as_float_list(value, 4, case)
    return (w, x, y, z)


def _pose(value: object, case: str) -> Pose:
    if not isinstance(value, dict):
        raise GoldenError(f"{case}: expected a pose object")
    data = cast(dict[object, object], value)
    return (_vec3(data.get("p"), case), _quat(data.get("q"), case))


def _add(a: Vec3, b: Vec3) -> Vec3:
    return (a[0] + b[0], a[1] + b[1], a[2] + b[2])


def _scale(a: Vec3, factor: float) -> Vec3:
    return (a[0] * factor, a[1] * factor, a[2] * factor)


def _dot(a: Vec3, b: Vec3) -> float:
    return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]


def _cross(a: Vec3, b: Vec3) -> Vec3:
    return (
        a[1] * b[2] - a[2] * b[1],
        a[2] * b[0] - a[0] * b[2],
        a[0] * b[1] - a[1] * b[0],
    )


def _length(a: Vec3) -> float:
    return math.sqrt(_dot(a, a))


def _normalized_vec3(a: Vec3) -> Vec3:
    norm = _length(a)
    if norm == 0.0:
        return a
    return _scale(a, 1.0 / norm)


def _quat_multiply(a: Quat, b: Quat) -> Quat:
    aw, ax, ay, az = a
    bw, bx, by, bz = b
    return (
        aw * bw - ax * bx - ay * by - az * bz,
        aw * bx + ax * bw + ay * bz - az * by,
        aw * by - ax * bz + ay * bw + az * bx,
        aw * bz + ax * by - ay * bx + az * bw,
    )


def _quat_normalize(q: Quat) -> Quat:
    norm = math.sqrt(sum(component * component for component in q))
    if norm == 0.0:
        raise GoldenError("quaternion with zero norm")
    return cast(Quat, tuple(component / norm for component in q))


def _quat_conjugate(q: Quat) -> Quat:
    w, x, y, z = q
    return (w, -x, -y, -z)


def _quat_rotate(q: Quat, v: Vec3) -> Vec3:
    rotated = _quat_multiply(_quat_multiply(q, (0.0, *v)), _quat_conjugate(q))
    return (rotated[1], rotated[2], rotated[3])


def _quat_slerp(a: Quat, b: Quat, t: float) -> Quat:
    dot = sum(left * right for left, right in zip(a, b, strict=True))
    if dot < 0.0:
        b = cast(Quat, tuple(-component for component in b))
        dot = -dot
    dot = min(1.0, max(-1.0, dot))
    theta = math.acos(dot)
    if theta < 1e-12:
        return _quat_normalize(cast(Quat, tuple((1.0 - t) * l + t * r for l, r in zip(a, b, strict=True))))
    sin_theta = math.sin(theta)
    left_weight = math.sin((1.0 - t) * theta) / sin_theta
    right_weight = math.sin(t * theta) / sin_theta
    return _quat_normalize(
        cast(Quat, tuple(left_weight * l + right_weight * r for l, r in zip(a, b, strict=True)))
    )


def _pose_compose(a: Pose, b: Pose) -> Pose:
    return (_add(a[0], _quat_rotate(a[1], b[0])), _quat_normalize(_quat_multiply(a[1], b[1])))


def _pose_inverse(a: Pose) -> Pose:
    inverse = _quat_conjugate(a[1])
    return (_scale(_quat_rotate(inverse, a[0]), -1.0), inverse)


def _pose_transform_point(a: Pose, v: Vec3) -> Vec3:
    return _add(a[0], _quat_rotate(a[1], v))


def _median(values: Sequence[float]) -> float:
    ordered = sorted(values)
    middle = len(ordered) // 2
    if len(ordered) % 2 == 1:
        return ordered[middle]
    return (ordered[middle - 1] + ordered[middle]) / 2.0


def _clock_map(samples: Sequence[Sequence[float]], query_sdk: float) -> int:
    offsets = [host_ns - sdk_seconds * 1.0e9 for sdk_seconds, host_ns in samples]
    return round(query_sdk * 1.0e9 + _median(offsets))


def _sdk_pose_to_internal(sdk: Sequence[float]) -> Pose:
    values = [_float32(component) for component in sdk]
    return ((values[0], values[1], values[2]), _quat_normalize((values[3], values[4], values[5], values[6])))


def _unity_position(p: Vec3) -> Vec3:
    return (p[0], p[1], -p[2])


def _unity_rotation(q: Quat) -> Quat:
    w, x, y, z = q
    return (w, x, -y, -z)


def _unity_pose(a: Pose) -> Pose:
    return (_unity_position(a[0]), _unity_rotation(a[1]))


def _compare_scalar(case: str, expected: float, actual: float, tolerance: float) -> list[str]:
    if abs(expected - actual) > tolerance:
        return [f"golden case {case!r}: fixture records {expected!r} but the independent result is {actual!r}"]
    return []


def _compare_vec3(case: str, expected: Vec3, actual: Vec3, tolerance: float) -> list[str]:
    return [
        f"golden case {case!r}: fixture records {list(expected)!r} but the independent result is {list(actual)!r}"
        for index in range(3)
        if abs(expected[index] - actual[index]) > tolerance
    ]


def _compare_quat(case: str, expected: Quat, actual: Quat, tolerance: float) -> list[str]:
    return [
        f"golden case {case!r}: fixture records {list(expected)!r} but the independent result is {list(actual)!r}"
        for index in range(4)
        if abs(expected[index] - actual[index]) > tolerance
    ]


def _compare_pose(case: str, expected: Pose, actual: Pose, tolerance: float) -> list[str]:
    return _compare_vec3(case, expected[0], actual[0], tolerance) + _compare_quat(
        case, expected[1], actual[1], tolerance
    )


CaseCheck = Callable[[str, dict[str, object], object, float], list[str]]


def _check_vec3_dot(case: str, data: dict[str, object], expected: object, tolerance: float) -> list[str]:
    return _compare_scalar(case, float(cast(float, expected)), _dot(_vec3(data["a"], case), _vec3(data["b"], case)), tolerance)


def _check_vec3_cross(case: str, data: dict[str, object], expected: object, tolerance: float) -> list[str]:
    return _compare_vec3(
        case, _vec3(expected, case), _cross(_vec3(data["a"], case), _vec3(data["b"], case)), tolerance
    )


def _check_vec3_length(case: str, data: dict[str, object], expected: object, tolerance: float) -> list[str]:
    return _compare_scalar(case, float(cast(float, expected)), _length(_vec3(data["a"], case)), tolerance)


def _check_vec3_normalize(case: str, data: dict[str, object], expected: object, tolerance: float) -> list[str]:
    return _compare_vec3(case, _vec3(expected, case), _normalized_vec3(_vec3(data["a"], case)), tolerance)


def _check_quat_normalize(case: str, data: dict[str, object], expected: object, tolerance: float) -> list[str]:
    return _compare_quat(case, _quat(expected, case), _quat_normalize(_quat(data["q"], case)), tolerance)


def _check_quat_rotate(case: str, data: dict[str, object], expected: object, tolerance: float) -> list[str]:
    return _compare_vec3(
        case, _vec3(expected, case), _quat_rotate(_quat(data["q"], case), _vec3(data["v"], case)), tolerance
    )


def _check_quat_multiply(case: str, data: dict[str, object], expected: object, tolerance: float) -> list[str]:
    return _compare_quat(
        case, _quat(expected, case), _quat_multiply(_quat(data["a"], case), _quat(data["b"], case)), tolerance
    )


def _check_quat_inverse(case: str, data: dict[str, object], expected: object, tolerance: float) -> list[str]:
    return _compare_quat(case, _quat(expected, case), _quat_conjugate(_quat(data["q"], case)), tolerance)


def _check_quat_slerp(case: str, data: dict[str, object], expected: object, tolerance: float) -> list[str]:
    t = float(cast(float, data["t"]))
    return _compare_quat(
        case, _quat(expected, case), _quat_slerp(_quat(data["a"], case), _quat(data["b"], case), t), tolerance
    )


def _check_pose_compose(case: str, data: dict[str, object], expected: object, tolerance: float) -> list[str]:
    return _compare_pose(
        case, _pose(expected, case), _pose_compose(_pose(data["a"], case), _pose(data["b"], case)), tolerance
    )


def _check_pose_inverse(case: str, data: dict[str, object], expected: object, tolerance: float) -> list[str]:
    return _compare_pose(case, _pose(expected, case), _pose_inverse(_pose(data["a"], case)), tolerance)


def _check_pose_transform_point(case: str, data: dict[str, object], expected: object, tolerance: float) -> list[str]:
    return _compare_vec3(case, _vec3(expected, case), _pose_transform_point(_pose(data["a"], case), _vec3(data["v"], case)), tolerance)


def _check_sdk_pose_to_internal(case: str, data: dict[str, object], expected: object, tolerance: float) -> list[str]:
    sdk = _as_float_list(data["sdk"], 7, case)
    return _compare_pose(case, _pose(expected, case), _sdk_pose_to_internal(sdk), tolerance)


def _check_unity_position(case: str, data: dict[str, object], expected: object, tolerance: float) -> list[str]:
    return _compare_vec3(case, _vec3(expected, case), _unity_position(_vec3(data["p"], case)), tolerance)


def _check_unity_rotation(case: str, data: dict[str, object], expected: object, tolerance: float) -> list[str]:
    return _compare_quat(case, _quat(expected, case), _unity_rotation(_quat(data["q"], case)), tolerance)


def _check_unity_pose(case: str, data: dict[str, object], expected: object, tolerance: float) -> list[str]:
    return _compare_pose(case, _pose(expected, case), _unity_pose(_pose(data["a"], case)), tolerance)


def _check_clock_map(case: str, data: dict[str, object], expected: object, tolerance: float) -> list[str]:
    samples = data["samples"]
    if not isinstance(samples, list):
        raise GoldenError(f"{case}: clock_map samples must be an array")
    rows = [_as_float_list(row, 2, case) for row in cast(list[object], samples)]
    actual = _clock_map(rows, float(cast(float, data["query_sdk"])))
    if actual != expected:
        return [f"golden case {case!r}: fixture records {expected!r} but the independent result is {actual!r}"]
    return []


_CHECKERS: dict[str, CaseCheck] = {
    "vec3_dot": _check_vec3_dot,
    "vec3_cross": _check_vec3_cross,
    "vec3_length": _check_vec3_length,
    "vec3_normalize": _check_vec3_normalize,
    "quat_normalize": _check_quat_normalize,
    "quat_rotate": _check_quat_rotate,
    "quat_multiply": _check_quat_multiply,
    "quat_inverse": _check_quat_inverse,
    "quat_slerp": _check_quat_slerp,
    "pose_compose": _check_pose_compose,
    "pose_inverse": _check_pose_inverse,
    "pose_transform_point": _check_pose_transform_point,
    "sdk_pose_to_internal": _check_sdk_pose_to_internal,
    "unity_position": _check_unity_position,
    "unity_rotation": _check_unity_rotation,
    "unity_pose": _check_unity_pose,
    "clock_map": _check_clock_map,
}

GOLDEN_OPS: frozenset[str] = frozenset(_CHECKERS)


def verify_golden(fixture: Path) -> list[str]:
    """Return one message per golden case whose expected value is wrong.

    Raises :class:`GoldenError` when the fixture cannot be read, has the wrong
    shape, or names an op this reference implementation does not know (an unknown
    op must fail loudly rather than be skipped).
    """

    try:
        document: object = json.loads(fixture.read_text(encoding="utf-8"))
    except FileNotFoundError as error:
        raise GoldenError(f"golden fixture not found: {fixture}") from error
    except json.JSONDecodeError as error:
        raise GoldenError(f"golden fixture is not valid JSON: {fixture}: {error}") from error
    if not isinstance(document, dict):
        raise GoldenError(f"{fixture}: expected a JSON object at the top level")
    data = cast(dict[str, object], document)
    tolerance_value = data.get("tolerance")
    if not isinstance(tolerance_value, (int, float)) or isinstance(tolerance_value, bool):
        raise GoldenError(f"{fixture}: expected a numeric 'tolerance'")
    tolerance = float(tolerance_value)
    cases = data.get("cases")
    if not isinstance(cases, list):
        raise GoldenError(f"{fixture}: expected a 'cases' array")

    failures: list[str] = []
    seen_ops: set[str] = set()
    for raw in cast(list[object], cases):
        if not isinstance(raw, dict):
            raise GoldenError(f"{fixture}: each case must be an object")
        entry = cast(dict[str, object], raw)
        name = entry.get("name")
        op = entry.get("op")
        if not isinstance(name, str) or not isinstance(op, str):
            raise GoldenError(f"{fixture}: each case needs string 'name' and 'op'")
        checker = _CHECKERS.get(op)
        if checker is None:
            raise GoldenError(f"golden case {name!r}: unknown op {op!r}")
        seen_ops.add(op)
        input_value = entry.get("input")
        if not isinstance(input_value, dict):
            raise GoldenError(f"golden case {name!r}: expected an 'input' object")
        failures.extend(checker(name, cast(dict[str, object], input_value), entry.get("expected"), tolerance))
    unused = sorted(GOLDEN_OPS - seen_ops)
    if unused:
        failures.append(
            f"golden reference implements op(s) {', '.join(unused)} that no fixture case exercises; "
            f"add a case or remove the branch"
        )
    return failures


def load_reference_ops() -> Mapping[str, CaseCheck]:
    """Expose the op dispatch table (read-only) for tooling and tests."""

    return _CHECKERS

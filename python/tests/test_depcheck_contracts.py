from __future__ import annotations

import json
import subprocess
import sys
from pathlib import Path

import pytest

import depcheck.contracts as contracts_module
from depcheck.__main__ import main
from depcheck.contracts import extract_abi_version

REPO_ROOT = Path(__file__).parents[2]

_TYPES = """\
#ifndef CG_TYPES_H
#define CG_TYPES_H
#include <stdint.h>
#define CG_ABI_VERSION {version}
typedef struct {{ float x, y, z; }} cg_vec3;
typedef enum {{ CG_OK = 0, CG_ERR_DEVICE = 3 }} cg_status;
typedef struct {{
  cg_vec3 pose;
  uint32_t sequence;{field}
}} cg_head_sample;
#endif
"""

_BRIDGE = """\
#ifndef CG_UNITY_BRIDGE_H
#define CG_UNITY_BRIDGE_H
#include "cg_types.h"
cg_status cg_bridge_open(void** out_handle);
cg_status cg_bridge_read_head(void* h, cg_head_sample* out);
void cg_bridge_close(void* h);{function}
#endif
"""

_PORTS = """\
#pragma once
#include <cstdint>
namespace cg {{
struct HeadSample {{ std::int64_t time_ns; uint32_t seq; }};
class IHeadPoseSource {{ public: virtual ~IHeadPoseSource() = default; }};
}}
"""

_RESULT = """\
#pragma once
namespace cg {{ enum class StatusCode {{ Ok, Internal }}; }}
"""


def write_contracts(
    root: Path,
    *,
    version: int = 2,
    field: str = "",
    function: str = "",
) -> None:
    contracts = root / "contracts"
    (contracts / "cpp").mkdir(parents=True, exist_ok=True)
    (contracts / "golden").mkdir(parents=True, exist_ok=True)
    (contracts / "cg_types.h").write_text(
        _TYPES.format(version=version, field=field), encoding="utf-8"
    )
    (contracts / "cg_unity_bridge.h").write_text(_BRIDGE.format(function=function), encoding="utf-8")
    (contracts / "cpp" / "ports.hpp").write_text(_PORTS, encoding="utf-8")
    (contracts / "cpp" / "result.hpp").write_text(_RESULT, encoding="utf-8")
    (contracts / "golden" / "transforms.json").write_text('{\n  "schema": 1\n}\n', encoding="utf-8")


def initialise(root: Path, capsys: pytest.CaptureFixture[str]) -> None:
    write_contracts(root)
    assert main(["contracts", "--root", str(root), "--update"]) == 0
    capsys.readouterr()


def run_check(root: Path, capsys: pytest.CaptureFixture[str]) -> tuple[int, list[str]]:
    code = main(["contracts", "--root", str(root)])
    captured = capsys.readouterr()
    lines = [line for line in captured.out.splitlines() if line]
    return code, lines


def test_unchanged_contracts_pass(tmp_path: Path, capsys: pytest.CaptureFixture[str]) -> None:
    initialise(tmp_path, capsys)
    code, lines = run_check(tmp_path, capsys)
    assert code == 0
    assert lines == []


def test_comment_and_whitespace_only_change_passes(
    tmp_path: Path, capsys: pytest.CaptureFixture[str]
) -> None:
    initialise(tmp_path, capsys)
    types = tmp_path / "contracts" / "cg_types.h"
    types.write_text(
        types.read_text(encoding="utf-8").replace(
            "typedef struct { float x, y, z; } cg_vec3;",
            "typedef struct { float x, y, z; } cg_vec3; /* reformatted */\n\n",
        ),
        encoding="utf-8",
    )
    code, lines = run_check(tmp_path, capsys)
    assert code == 0
    assert lines == []


def test_struct_field_change_without_bump_fails(
    tmp_path: Path, capsys: pytest.CaptureFixture[str]
) -> None:
    initialise(tmp_path, capsys)
    write_contracts(tmp_path, field="\n  uint32_t flags;")
    code, lines = run_check(tmp_path, capsys)
    assert code == 1
    assert len(lines) == 1
    assert "contracts/cg_types.h" in lines[0]
    assert "CG_ABI_VERSION is still 2" in lines[0]
    assert "bump CG_ABI_VERSION" in lines[0]


def test_header_addition_without_bump_fails(
    tmp_path: Path, capsys: pytest.CaptureFixture[str]
) -> None:
    initialise(tmp_path, capsys)
    write_contracts(tmp_path, function="\ncg_status cg_bridge_recenter(void* h);")
    code, lines = run_check(tmp_path, capsys)
    assert code == 1
    assert len(lines) == 1
    assert "contracts/cg_unity_bridge.h" in lines[0]
    assert "CG_ABI_VERSION is still 2" in lines[0]


def _anchor_with_version(version: int, monkeypatch: pytest.MonkeyPatch) -> None:
    anchors = dict(contracts_module.KNOWN_ABI_VERSIONS)
    anchors["contracts/cg_types.h"] = version
    monkeypatch.setattr(contracts_module, "KNOWN_ABI_VERSIONS", anchors)


def test_bump_without_update_fails_then_update_passes(
    tmp_path: Path, capsys: pytest.CaptureFixture[str], monkeypatch: pytest.MonkeyPatch
) -> None:
    initialise(tmp_path, capsys)
    write_contracts(tmp_path, version=3, field="\n  uint32_t flags;")
    _anchor_with_version(3, monkeypatch)

    code, lines = run_check(tmp_path, capsys)
    assert code == 1
    assert "CG_ABI_VERSION moved 2 -> 3" in lines[0]
    assert "was not regenerated" in lines[0]

    assert main(["contracts", "--root", str(tmp_path), "--update"]) == 0
    capsys.readouterr()
    code, lines = run_check(tmp_path, capsys)
    assert code == 0
    assert lines == []

    baseline = json.loads((tmp_path / "contracts" / "abi-baseline.json").read_text(encoding="utf-8"))
    assert baseline["abiVersion"] == 3


def test_source_bump_without_anchor_update_fails(
    tmp_path: Path, capsys: pytest.CaptureFixture[str]
) -> None:
    initialise(tmp_path, capsys)
    write_contracts(tmp_path, version=3, field="\n  uint32_t flags;")

    code, lines = run_check(tmp_path, capsys)
    assert code == 1
    assert "KNOWN_ABI_VERSIONS" in lines[0]
    assert "contracts/cg_types.h declares version 3" in lines[0]

    code = main(["contracts", "--root", str(tmp_path), "--update"])
    captured = capsys.readouterr()
    assert code == 1
    assert "KNOWN_ABI_VERSIONS" in captured.out


def test_baseline_version_edited_alone_fails(
    tmp_path: Path, capsys: pytest.CaptureFixture[str]
) -> None:
    initialise(tmp_path, capsys)
    baseline_path = tmp_path / "contracts" / "abi-baseline.json"
    data = json.loads(baseline_path.read_text(encoding="utf-8"))
    data["abiVersion"] = 3
    baseline_path.write_text(json.dumps(data, indent=2, sort_keys=True) + "\n", encoding="utf-8")

    code, lines = run_check(tmp_path, capsys)
    assert code == 1
    assert len(lines) == 1
    assert "abiVersion 3" in lines[0]
    assert "CG_ABI_VERSION 2" in lines[0]


def test_baseline_fingerprint_edited_alone_fails(
    tmp_path: Path, capsys: pytest.CaptureFixture[str]
) -> None:
    initialise(tmp_path, capsys)
    baseline_path = tmp_path / "contracts" / "abi-baseline.json"
    data = json.loads(baseline_path.read_text(encoding="utf-8"))
    data["fingerprint"] = "0" * 64
    baseline_path.write_text(json.dumps(data, indent=2, sort_keys=True) + "\n", encoding="utf-8")

    code, lines = run_check(tmp_path, capsys)
    assert code == 1
    assert len(lines) == 1
    assert "does not match its own files map" in lines[0]


def test_fixture_schema_edited_without_anchor_update_fails(
    tmp_path: Path, capsys: pytest.CaptureFixture[str]
) -> None:
    initialise(tmp_path, capsys)
    fixture = tmp_path / "contracts" / "golden" / "transforms.json"
    fixture.write_text('{\n  "schema": 2\n}\n', encoding="utf-8")

    code, lines = run_check(tmp_path, capsys)
    assert code == 1
    assert "transforms.json declares version 2" in lines[0]
    assert "KNOWN_ABI_VERSIONS" in lines[0]


def test_update_refuses_when_version_did_not_change(
    tmp_path: Path, capsys: pytest.CaptureFixture[str]
) -> None:
    initialise(tmp_path, capsys)
    baseline_path = tmp_path / "contracts" / "abi-baseline.json"
    before = baseline_path.read_text(encoding="utf-8")
    write_contracts(tmp_path, field="\n  uint32_t flags;")

    code = main(["contracts", "--root", str(tmp_path), "--update"])
    captured = capsys.readouterr()
    assert code == 1
    assert "refusing to update" in captured.out
    assert baseline_path.read_text(encoding="utf-8") == before


def test_version_ignores_commented_defines() -> None:
    texts = {
        "contracts/cg_types.h": (
            "// #define CG_ABI_VERSION 99\n"
            "/* #define CG_ABI_VERSION 98 */\n"
            " #define CG_ABI_VERSION 7 // trailing comment\n"
        )
    }
    assert extract_abi_version(texts) == 7


def test_commented_out_define_cannot_fake_a_bump(
    tmp_path: Path, capsys: pytest.CaptureFixture[str]
) -> None:
    initialise(tmp_path, capsys)
    types = tmp_path / "contracts" / "cg_types.h"
    types.write_text(
        types.read_text(encoding="utf-8")
        .replace("uint32_t sequence;", "uint32_t sequence;\n  uint32_t flags;")
        .replace(
            "#define CG_ABI_VERSION 2",
            "// #define CG_ABI_VERSION 99\n#define CG_ABI_VERSION 2 /* 99 is commented out */",
        ),
        encoding="utf-8",
    )

    code = main(["contracts", "--root", str(tmp_path), "--update"])
    captured = capsys.readouterr()
    assert code == 1
    assert "refusing to update" in captured.out
    assert "CG_ABI_VERSION is still 2" in captured.out

    code, lines = run_check(tmp_path, capsys)
    assert code == 1
    assert "CG_ABI_VERSION is still 2" in lines[0]


def test_missing_baseline_fails(tmp_path: Path, capsys: pytest.CaptureFixture[str]) -> None:
    write_contracts(tmp_path)
    code, lines = run_check(tmp_path, capsys)
    assert code == 1
    assert "missing ABI baseline" in lines[0]


def test_missing_contract_file_is_an_error(tmp_path: Path, capsys: pytest.CaptureFixture[str]) -> None:
    initialise(tmp_path, capsys)
    (tmp_path / "contracts" / "cpp" / "result.hpp").unlink()
    code, lines = run_check(tmp_path, capsys)
    assert code == 1
    assert "missing contract file: contracts/cpp/result.hpp" in lines[0]


def test_repository_contracts_match_the_committed_baseline() -> None:
    result = subprocess.run(
        [sys.executable, "-m", "depcheck", "contracts", "--root", "."],
        cwd=REPO_ROOT,
        capture_output=True,
        text=True,
        check=False,
    )
    assert result.returncode == 0
    assert result.stdout.strip() == ""

from __future__ import annotations

import json
import subprocess
import sys
from pathlib import Path

import pytest

import depcheck.contracts as contracts_module
from depcheck.__main__ import main
from depcheck.contracts import extract_abi_version, mask_string_literals, normalise, strip_comments

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
    version: int = contracts_module.KNOWN_ABI_VERSIONS["contracts/cg_types.h"],
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


def _refresh_code_constants(
    root: Path,
    monkeypatch: pytest.MonkeyPatch,
    *,
    version: int | None = None,
) -> None:
    """Point the code anchors at the test tree, like a reviewed constant edit."""

    state = contracts_module.compute_state(root)
    monkeypatch.setattr(contracts_module, "KNOWN_FINGERPRINTS", dict(state.files))
    if version is not None:
        anchors = dict(contracts_module.KNOWN_ABI_VERSIONS)
        anchors["contracts/cg_types.h"] = version
        monkeypatch.setattr(contracts_module, "KNOWN_ABI_VERSIONS", anchors)


def initialise(root: Path, capsys: pytest.CaptureFixture[str], monkeypatch: pytest.MonkeyPatch) -> None:
    write_contracts(root)
    _refresh_code_constants(root, monkeypatch)
    assert main(["contracts", "--root", str(root), "--update"]) == 0
    capsys.readouterr()


def run_check(root: Path, capsys: pytest.CaptureFixture[str]) -> tuple[int, list[str]]:
    code = main(["contracts", "--root", str(root)])
    captured = capsys.readouterr()
    lines = [line for line in captured.out.splitlines() if line]
    return code, lines


def run_update(root: Path, capsys: pytest.CaptureFixture[str]) -> tuple[int, str]:
    code = main(["contracts", "--root", str(root), "--update"])
    captured = capsys.readouterr()
    return code, captured.out


def test_unchanged_contracts_pass(
    tmp_path: Path, capsys: pytest.CaptureFixture[str], monkeypatch: pytest.MonkeyPatch
) -> None:
    initialise(tmp_path, capsys, monkeypatch)
    code, lines = run_check(tmp_path, capsys)
    assert code == 0
    assert lines == []


def test_comment_and_whitespace_only_change_passes(
    tmp_path: Path, capsys: pytest.CaptureFixture[str], monkeypatch: pytest.MonkeyPatch
) -> None:
    initialise(tmp_path, capsys, monkeypatch)
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


def test_live_layout_drift_fails_against_the_code_constant(
    tmp_path: Path, capsys: pytest.CaptureFixture[str], monkeypatch: pytest.MonkeyPatch
) -> None:
    initialise(tmp_path, capsys, monkeypatch)
    write_contracts(tmp_path, field="\n  uint32_t flags;")

    code, lines = run_check(tmp_path, capsys)
    assert code == 1
    assert len(lines) == 1
    assert "contracts/cg_types.h" in lines[0]
    assert "KNOWN_FINGERPRINTS" in lines[0]

    code, output = run_update(tmp_path, capsys)
    assert code == 1
    assert "refusing to update" in output
    assert "KNOWN_FINGERPRINTS" in output
    assert "python/depcheck/contracts.py" in output


def test_header_addition_without_bump_fails(
    tmp_path: Path, capsys: pytest.CaptureFixture[str], monkeypatch: pytest.MonkeyPatch
) -> None:
    initialise(tmp_path, capsys, monkeypatch)
    write_contracts(tmp_path, function="\ncg_status cg_bridge_recenter(void* h);")

    code, lines = run_check(tmp_path, capsys)
    assert code == 1
    assert len(lines) == 1
    assert "contracts/cg_unity_bridge.h" in lines[0]
    assert "KNOWN_FINGERPRINTS" in lines[0]


def test_bump_without_update_fails_then_update_passes(
    tmp_path: Path, capsys: pytest.CaptureFixture[str], monkeypatch: pytest.MonkeyPatch
) -> None:
    initialise(tmp_path, capsys, monkeypatch)
    anchor = contracts_module.KNOWN_ABI_VERSIONS["contracts/cg_types.h"]
    bumped = anchor + 1
    write_contracts(tmp_path, version=bumped, field="\n  uint32_t flags;")
    _refresh_code_constants(tmp_path, monkeypatch, version=bumped)

    code, lines = run_check(tmp_path, capsys)
    assert code == 1
    assert f"abiVersion {anchor} disagrees with CG_ABI_VERSION {bumped}" in lines[0]

    assert main(["contracts", "--root", str(tmp_path), "--update"]) == 0
    capsys.readouterr()
    code, lines = run_check(tmp_path, capsys)
    assert code == 0
    assert lines == []

    baseline = json.loads((tmp_path / "contracts" / "abi-baseline.json").read_text(encoding="utf-8"))
    assert baseline["abiVersion"] == bumped
    assert set(baseline["files"]) == set(contracts_module.KNOWN_FINGERPRINTS)


def test_source_bump_without_anchor_update_fails(
    tmp_path: Path, capsys: pytest.CaptureFixture[str], monkeypatch: pytest.MonkeyPatch
) -> None:
    initialise(tmp_path, capsys, monkeypatch)
    anchor = contracts_module.KNOWN_ABI_VERSIONS["contracts/cg_types.h"]
    bumped = anchor + 1
    write_contracts(tmp_path, version=bumped, field="\n  uint32_t flags;")
    _refresh_code_constants(tmp_path, monkeypatch)

    code, lines = run_check(tmp_path, capsys)
    assert code == 1
    assert "KNOWN_ABI_VERSIONS" in lines[0]
    assert f"contracts/cg_types.h declares version {bumped}" in lines[0]

    code, output = run_update(tmp_path, capsys)
    assert code == 1
    assert "KNOWN_ABI_VERSIONS" in output


def test_baseline_version_edited_alone_fails(
    tmp_path: Path, capsys: pytest.CaptureFixture[str], monkeypatch: pytest.MonkeyPatch
) -> None:
    initialise(tmp_path, capsys, monkeypatch)
    baseline_path = tmp_path / "contracts" / "abi-baseline.json"
    data = json.loads(baseline_path.read_text(encoding="utf-8"))
    anchor = contracts_module.KNOWN_ABI_VERSIONS["contracts/cg_types.h"]
    data["abiVersion"] = anchor + 1
    baseline_path.write_text(json.dumps(data, indent=2, sort_keys=True) + "\n", encoding="utf-8")

    code, lines = run_check(tmp_path, capsys)
    assert code == 1
    assert len(lines) == 1
    assert f"abiVersion {anchor + 1}" in lines[0]
    assert f"CG_ABI_VERSION {anchor}" in lines[0]


def test_baseline_fingerprint_edited_alone_fails(
    tmp_path: Path, capsys: pytest.CaptureFixture[str], monkeypatch: pytest.MonkeyPatch
) -> None:
    initialise(tmp_path, capsys, monkeypatch)
    baseline_path = tmp_path / "contracts" / "abi-baseline.json"
    data = json.loads(baseline_path.read_text(encoding="utf-8"))
    data["fingerprint"] = "0" * 64
    baseline_path.write_text(json.dumps(data, indent=2, sort_keys=True) + "\n", encoding="utf-8")

    code, lines = run_check(tmp_path, capsys)
    assert code == 1
    assert len(lines) == 1
    assert "does not match the live contract surface" in lines[0]


def test_consistently_rewritten_baseline_cannot_admit_drift(
    tmp_path: Path, capsys: pytest.CaptureFixture[str], monkeypatch: pytest.MonkeyPatch
) -> None:
    """The re-review attack: rehash the baseline files and fingerprint together."""

    initialise(tmp_path, capsys, monkeypatch)
    baseline_path = tmp_path / "contracts" / "abi-baseline.json"
    data = json.loads(baseline_path.read_text(encoding="utf-8"))
    forged = {relative: "a" * 64 for relative in data["files"]}
    data["files"] = forged
    data["fingerprint"] = contracts_module._fingerprint(forged)
    data["abiVersion"] = contracts_module.KNOWN_ABI_VERSIONS["contracts/cg_types.h"]
    baseline_path.write_text(json.dumps(data, indent=2, sort_keys=True) + "\n", encoding="utf-8")

    code, lines = run_check(tmp_path, capsys)
    assert code == 1
    assert len(lines) == 1
    assert "does not match the live contract surface" in lines[0]


def test_baseline_files_edited_alone_fails(
    tmp_path: Path, capsys: pytest.CaptureFixture[str], monkeypatch: pytest.MonkeyPatch
) -> None:
    initialise(tmp_path, capsys, monkeypatch)
    baseline_path = tmp_path / "contracts" / "abi-baseline.json"
    data = json.loads(baseline_path.read_text(encoding="utf-8"))
    data["files"]["contracts/cg_types.h"] = "b" * 64
    baseline_path.write_text(json.dumps(data, indent=2, sort_keys=True) + "\n", encoding="utf-8")

    code, lines = run_check(tmp_path, capsys)
    assert code == 1
    assert len(lines) == 1
    assert "does not match the live contract surface" in lines[0]


def test_fixture_schema_edited_without_anchor_update_fails(
    tmp_path: Path, capsys: pytest.CaptureFixture[str], monkeypatch: pytest.MonkeyPatch
) -> None:
    initialise(tmp_path, capsys, monkeypatch)
    fixture = tmp_path / "contracts" / "golden" / "transforms.json"
    fixture.write_text('{\n  "schema": 2\n}\n', encoding="utf-8")
    _refresh_code_constants(tmp_path, monkeypatch)

    code, lines = run_check(tmp_path, capsys)
    assert code == 1
    assert "transforms.json declares version 2" in lines[0]
    assert "KNOWN_ABI_VERSIONS" in lines[0]

    code, output = run_update(tmp_path, capsys)
    assert code == 1
    assert "KNOWN_ABI_VERSIONS" in output


def test_update_refuses_while_the_code_constant_is_stale(
    tmp_path: Path, capsys: pytest.CaptureFixture[str], monkeypatch: pytest.MonkeyPatch
) -> None:
    initialise(tmp_path, capsys, monkeypatch)
    baseline_path = tmp_path / "contracts" / "abi-baseline.json"
    before = baseline_path.read_text(encoding="utf-8")
    write_contracts(tmp_path, field="\n  uint32_t flags;")

    code, output = run_update(tmp_path, capsys)
    assert code == 1
    assert "refusing to update" in output
    assert "KNOWN_FINGERPRINTS" in output
    assert "python/depcheck/contracts.py" in output
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
    tmp_path: Path, capsys: pytest.CaptureFixture[str], monkeypatch: pytest.MonkeyPatch
) -> None:
    initialise(tmp_path, capsys, monkeypatch)
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

    code, output = run_update(tmp_path, capsys)
    assert code == 1
    assert "refusing to update" in output
    assert "KNOWN_FINGERPRINTS" in output

    code, lines = run_check(tmp_path, capsys)
    assert code == 1
    assert "KNOWN_FINGERPRINTS" in lines[0]


def test_missing_baseline_fails(
    tmp_path: Path, capsys: pytest.CaptureFixture[str], monkeypatch: pytest.MonkeyPatch
) -> None:
    write_contracts(tmp_path)
    _refresh_code_constants(tmp_path, monkeypatch)
    code, lines = run_check(tmp_path, capsys)
    assert code == 1
    assert "missing ABI baseline" in lines[0]


def test_missing_contract_file_is_an_error(
    tmp_path: Path, capsys: pytest.CaptureFixture[str], monkeypatch: pytest.MonkeyPatch
) -> None:
    initialise(tmp_path, capsys, monkeypatch)
    (tmp_path / "contracts" / "cpp" / "result.hpp").unlink()
    code, lines = run_check(tmp_path, capsys)
    assert code == 1
    assert "missing contract file: contracts/cpp/result.hpp" in lines[0]


def test_strip_comments_splices_line_continuations() -> None:
    assert strip_comments("#in\\\nclude <x>\nnext") == "#include <x>\n\nnext"
    assert strip_comments("#in\\\r\nclude <x>\r\nnext") == "#include <x>\n\nnext"


def test_strip_comments_ignores_markers_inside_strings() -> None:
    assert normalise('s = "/*"; t = "*/";') == 's="/*";t="*/";'
    assert strip_comments('s = "//";\ncode();') == 's = "//";\ncode();'
    assert strip_comments('s = R"(/* raw */)"; x();') == 's = R"(/* raw */)"; x();'


def test_strip_comments_handles_csharp_literals() -> None:
    verbatim = 's = @"a // b /* c */";\ncode();'
    assert strip_comments(verbatim) == verbatim
    interpolated = 's = $"a // b"; t = $@"a /* b */";\ncode();'
    assert strip_comments(interpolated) == interpolated
    raw = 's = """\na /* b */ // c\n""";\ncode();'
    assert strip_comments(raw) == raw


def test_strip_comments_translates_digraphs_in_code_only() -> None:
    assert strip_comments("%:include <x>") == "#include <x>"
    assert strip_comments("a %:%: b") == "a ## b"
    assert strip_comments('s = "%:include <x>";') == 's = "%:include <x>";'
    assert strip_comments("// %:include <x>\ncode();") == "\ncode();"


def test_mask_string_literals_blanks_literal_contents() -> None:
    masked = mask_string_literals('var path = "System.IO.File"; Call();')
    assert "System.IO.File" not in masked
    assert "Call();" in masked
    assert '"' not in masked


def test_mask_string_literals_keeps_interpolation_holes_as_code() -> None:
    masked = mask_string_literals('var s = $"{System.IO.File.ReadAllText(p)}";')
    assert "System.IO.File.ReadAllText" in masked
    assert "$" not in masked
    assert '"' not in masked


def test_mask_string_literals_keeps_verbatim_interpolation_holes_as_code() -> None:
    masked = mask_string_literals('var s = $@"prefix {System.IO.File.Exists(p)} suffix";')
    assert "System.IO.File.Exists" in masked
    assert "prefix" not in masked
    assert "suffix" not in masked


def test_mask_string_literals_masks_interpolation_format_specifiers() -> None:
    masked = mask_string_literals('var s = $"{value:System.IO.File}";')
    assert "System.IO.File" not in masked
    assert "value" in masked


def test_mask_string_literals_handles_nested_interpolation() -> None:
    masked = mask_string_literals('var s = $"{Outer($"{System.IO.File.Exists(p)}")}";')
    assert "System.IO.File.Exists" in masked
    assert "Outer" in masked
    assert "$" not in masked


def test_mask_string_literals_keeps_escaped_braces_literal() -> None:
    masked = mask_string_literals('var s = $"{{System.IO.File}}";')
    assert "System.IO.File" not in masked


def test_mask_string_literals_does_not_close_a_hole_inside_a_nested_literal() -> None:
    masked = mask_string_literals('var s = $"{Lookup("}")(System.IO.File.Exists)}";')
    assert "System.IO.File.Exists" in masked


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


def test_new_contract_file_must_be_covered_or_exempted(
    tmp_path: Path, capsys: pytest.CaptureFixture[str], monkeypatch: pytest.MonkeyPatch
) -> None:
    initialise(tmp_path, capsys, monkeypatch)
    (tmp_path / "contracts" / "new_surface.h").write_text("typedef int cg_new;\n", encoding="utf-8")

    code, lines = run_check(tmp_path, capsys)
    assert code == 1
    assert "contracts/new_surface.h" in lines[0]
    assert "neither fingerprinted" in lines[0]


def test_exempting_a_new_contract_file_with_a_reason_passes(
    tmp_path: Path, capsys: pytest.CaptureFixture[str], monkeypatch: pytest.MonkeyPatch
) -> None:
    initialise(tmp_path, capsys, monkeypatch)
    (tmp_path / "contracts" / "notes.txt").write_text("not an ABI surface\n", encoding="utf-8")
    monkeypatch.setattr(
        contracts_module,
        "EXEMPT_CONTRACT_FILES",
        {**contracts_module.EXEMPT_CONTRACT_FILES, "contracts/notes.txt": "documentation only"},
    )

    code, lines = run_check(tmp_path, capsys)
    assert code == 0
    assert lines == []


def test_contract_file_cannot_be_covered_and_exempt(
    tmp_path: Path, capsys: pytest.CaptureFixture[str], monkeypatch: pytest.MonkeyPatch
) -> None:
    initialise(tmp_path, capsys, monkeypatch)
    monkeypatch.setattr(
        contracts_module,
        "EXEMPT_CONTRACT_FILES",
        {**contracts_module.EXEMPT_CONTRACT_FILES, "contracts/cg_types.h": "wrongly exempted"},
    )

    code, lines = run_check(tmp_path, capsys)
    assert code == 1
    assert "both fingerprinted and exempt" in lines[0]


def test_update_refuses_an_uncovered_contract_file(
    tmp_path: Path, capsys: pytest.CaptureFixture[str], monkeypatch: pytest.MonkeyPatch
) -> None:
    initialise(tmp_path, capsys, monkeypatch)
    (tmp_path / "contracts" / "new_surface.h").write_text("typedef int cg_new;\n", encoding="utf-8")

    code, output = run_update(tmp_path, capsys)
    assert code == 1
    assert "refusing to update" in output
    assert "new_surface.h" in output


def _git(root: Path, *arguments: str) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        ["git", "-C", str(root), *arguments],
        capture_output=True,
        text=True,
        check=False,
    )


def _commit(root: Path, message: str) -> None:
    _git(root, "add", "-A")
    committed = _git(
        root,
        "-c",
        "user.name=fixture",
        "-c",
        "user.email=fixture@example.com",
        "commit",
        "-q",
        "-m",
        message,
    )
    assert committed.returncode == 0, committed.stderr


def test_since_flags_a_surface_change_without_a_version_bump(
    tmp_path: Path, capsys: pytest.CaptureFixture[str], monkeypatch: pytest.MonkeyPatch
) -> None:
    assert _git(tmp_path, "init", "-q", "-b", "main").returncode == 0
    initialise(tmp_path, capsys, monkeypatch)
    _commit(tmp_path, "base surface")
    assert _git(tmp_path, "checkout", "-q", "-b", "pr").returncode == 0

    write_contracts(tmp_path, field="\n  uint32_t flags;")
    _refresh_code_constants(tmp_path, monkeypatch)
    assert main(["contracts", "--root", str(tmp_path), "--update"]) == 0
    capsys.readouterr()
    _commit(tmp_path, "change surface and baseline, no bump")

    code = main(["contracts", "--root", str(tmp_path), "--since", "main"])
    output = capsys.readouterr().out
    anchor = contracts_module.KNOWN_ABI_VERSIONS["contracts/cg_types.h"]
    assert code == 1
    assert f"stayed at {anchor}" in output
    assert "bump" in output


def test_since_passes_when_the_version_was_bumped(
    tmp_path: Path, capsys: pytest.CaptureFixture[str], monkeypatch: pytest.MonkeyPatch
) -> None:
    assert _git(tmp_path, "init", "-q", "-b", "main").returncode == 0
    initialise(tmp_path, capsys, monkeypatch)
    _commit(tmp_path, "base surface")
    assert _git(tmp_path, "checkout", "-q", "-b", "pr").returncode == 0

    bumped = contracts_module.KNOWN_ABI_VERSIONS["contracts/cg_types.h"] + 1
    write_contracts(tmp_path, version=bumped, field="\n  uint32_t flags;")
    _refresh_code_constants(tmp_path, monkeypatch, version=bumped)
    assert main(["contracts", "--root", str(tmp_path), "--update"]) == 0
    capsys.readouterr()
    _commit(tmp_path, "change surface with an ABI bump")

    code = main(["contracts", "--root", str(tmp_path), "--since", "main"])
    output = capsys.readouterr().out
    assert code == 0
    assert output.strip() == ""


def test_since_is_read_from_the_environment(
    tmp_path: Path, capsys: pytest.CaptureFixture[str], monkeypatch: pytest.MonkeyPatch
) -> None:
    assert _git(tmp_path, "init", "-q", "-b", "main").returncode == 0
    initialise(tmp_path, capsys, monkeypatch)
    _commit(tmp_path, "base surface")
    assert _git(tmp_path, "checkout", "-q", "-b", "pr").returncode == 0
    write_contracts(tmp_path, field="\n  uint32_t flags;")
    _refresh_code_constants(tmp_path, monkeypatch)
    assert main(["contracts", "--root", str(tmp_path), "--update"]) == 0
    capsys.readouterr()
    _commit(tmp_path, "unbumped change")

    monkeypatch.setenv("CG_CONTRACT_BASE_REF", "main")
    code = main(["contracts", "--root", str(tmp_path)])
    output = capsys.readouterr().out
    anchor = contracts_module.KNOWN_ABI_VERSIONS["contracts/cg_types.h"]
    assert code == 1
    assert f"stayed at {anchor}" in output


def test_without_since_the_local_no_git_fallback_still_passes(
    tmp_path: Path, capsys: pytest.CaptureFixture[str], monkeypatch: pytest.MonkeyPatch
) -> None:
    """Without a base ref the bump discipline stays review-enforced (TD-049 local)."""

    assert _git(tmp_path, "init", "-q", "-b", "main").returncode == 0
    initialise(tmp_path, capsys, monkeypatch)
    _commit(tmp_path, "base surface")
    write_contracts(tmp_path, field="\n  uint32_t flags;")
    _refresh_code_constants(tmp_path, monkeypatch)
    assert main(["contracts", "--root", str(tmp_path), "--update"]) == 0
    capsys.readouterr()

    code, lines = run_check(tmp_path, capsys)
    assert code == 0
    assert lines == []


def test_since_with_an_unresolvable_ref_is_an_error(
    tmp_path: Path, capsys: pytest.CaptureFixture[str], monkeypatch: pytest.MonkeyPatch
) -> None:
    assert _git(tmp_path, "init", "-q", "-b", "main").returncode == 0
    initialise(tmp_path, capsys, monkeypatch)
    _commit(tmp_path, "base surface")

    code = main(["contracts", "--root", str(tmp_path), "--since", "no-such-ref"])
    output = capsys.readouterr().out
    assert code == 1
    assert "cannot resolve" in output


def test_since_skips_when_the_base_has_no_baseline(
    tmp_path: Path, capsys: pytest.CaptureFixture[str], monkeypatch: pytest.MonkeyPatch
) -> None:
    assert _git(tmp_path, "init", "-q", "-b", "main").returncode == 0
    write_contracts(tmp_path)
    _refresh_code_constants(tmp_path, monkeypatch)
    _commit(tmp_path, "surface without a baseline")
    assert _git(tmp_path, "checkout", "-q", "-b", "pr").returncode == 0
    assert main(["contracts", "--root", str(tmp_path), "--update"]) == 0
    capsys.readouterr()
    _commit(tmp_path, "add the baseline")

    code = main(["contracts", "--root", str(tmp_path), "--since", "main"])
    assert code == 0
    assert capsys.readouterr().out.strip() == ""

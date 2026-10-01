"""Command-line entry point: ``python -m depcheck [--root PATH]``."""

from __future__ import annotations

import argparse
from collections.abc import Sequence
from pathlib import Path

from depcheck.rules import check_root


def main(argv: Sequence[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        prog="depcheck",
        description="Enforce Cubeglass inward-only dependency rules.",
    )
    parser.add_argument(
        "--root",
        type=Path,
        default=None,
        help="Repository root holding contracts/layers.json (default: current directory).",
    )
    args = parser.parse_args(argv)
    root: Path = args.root if args.root is not None else Path.cwd()

    violations = check_root(root)
    for violation in violations:
        print(violation)
    return 1 if violations else 0


if __name__ == "__main__":
    raise SystemExit(main())

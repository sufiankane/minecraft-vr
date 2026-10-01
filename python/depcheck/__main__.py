"""Command-line entry point: ``python -m depcheck [--root PATH]``."""

from __future__ import annotations

import argparse
from collections.abc import Sequence
from pathlib import Path

from depcheck.coverage_check import CoverageError, check_coverage
from depcheck.licences import check_licences
from depcheck.rules import check_root

_ROOT_HELP = "Repository root (default: current directory)."


def main(argv: Sequence[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        prog="depcheck",
        description="Enforce Cubeglass dependency rules and licence allowlisting.",
    )
    parser.add_argument(
        "--root",
        type=Path,
        default=None,
        help=f"{_ROOT_HELP} Default command checks contracts/layers.json.",
    )
    subparsers = parser.add_subparsers(dest="command")
    licences_parser = subparsers.add_parser(
        "licences",
        help="Check declared dependencies against contracts/licence-allowlist.json.",
    )
    licences_parser.add_argument(
        "--root",
        dest="licences_root",
        type=Path,
        default=None,
        help=_ROOT_HELP,
    )
    coverage_parser = subparsers.add_parser(
        "coverage",
        help="Enforce a line-coverage floor from a Cobertura XML report.",
    )
    coverage_parser.add_argument(
        "--report",
        type=Path,
        required=True,
        help="Path to a Cobertura XML coverage report.",
    )
    coverage_parser.add_argument(
        "--module",
        required=True,
        help="Module name to match (case-insensitive, e.g. core-math or Cubeglass.CoreMath).",
    )
    coverage_parser.add_argument(
        "--floor",
        type=float,
        required=True,
        help="Minimum line coverage percentage.",
    )
    args = parser.parse_args(argv)

    if args.command == "coverage":
        try:
            coverage = check_coverage(args.report, args.module, args.floor)
        except CoverageError as error:
            print(f"coverage error: {error}")
            return 1
        print(coverage.summary())
        return 0 if coverage.meets_floor else 1

    if args.command == "licences":
        licences_root: Path = args.licences_root or args.root or Path.cwd()
        missing = check_licences(licences_root)
        for package in missing:
            print(package)
        return 1 if missing else 0

    root: Path = args.root if args.root is not None else Path.cwd()
    violations = check_root(root)
    for violation in violations:
        print(violation)
    return 1 if violations else 0


if __name__ == "__main__":
    raise SystemExit(main())

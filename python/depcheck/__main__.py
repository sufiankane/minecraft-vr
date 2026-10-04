"""Command-line entry point: ``python -m depcheck [--root PATH]``."""

from __future__ import annotations

import argparse
import os
from collections.abc import Sequence
from pathlib import Path

from depcheck.benchregress import (
    DEFAULT_HISTORY_WINDOW,
    DEFAULT_THRESHOLD_PERCENT,
    BenchError,
    load_baseline,
    load_history,
    load_measurements,
    median_history,
    regressions,
    write_capture,
)
from depcheck.contracts import ContractError, check_contracts, update_baseline
from depcheck.coverage_check import CoverageError, check_coverage
from depcheck.golden import GOLDEN_FIXTURE_PARTS, GoldenError, verify_golden
from depcheck.licences import LicenceError, check_licences, licence_report
from depcheck.rules import check_root

_ROOT_HELP = "Repository root (default: current directory)."


def _run_benchregress(args: argparse.Namespace) -> int:
    try:
        current = load_measurements(args.current, args.format)
    except BenchError as error:
        print(f"benchregress error: {error}")
        return 1
    prefixed = {f"{args.prefix}/{name}": value for name, value in current.items()}
    if args.history is not None:
        # Read the window before writing this run's capture: the capture lives in
        # the history directory, and the current run must never be its own
        # baseline.
        try:
            summaries = load_history(args.history, args.history_window)
        except BenchError as error:
            print(f"benchregress error: {error}")
            return 1
        if args.capture is not None:
            write_capture(args.capture, prefixed)
        if not summaries:
            captured = (
                f"captured {len(prefixed)} benchmark(s) under prefix '{args.prefix}' to {args.capture}"
                if args.capture is not None
                else f"parsed {len(prefixed)} benchmark(s) under prefix '{args.prefix}'"
            )
            print(
                f"benchregress: no prior history in {args.history}; {captured}; "
                f"the next run compares against the median of the recorded summaries"
            )
            return 0
        baseline = median_history(summaries)
        source = f"the median of {len(summaries)} prior run(s)"
    elif args.baseline_from is not None:
        if args.capture is not None:
            write_capture(args.capture, prefixed)
        try:
            explicit = load_baseline(args.baseline_from)
        except BenchError as error:
            print(f"benchregress error: {error}")
            return 1
        if explicit is None:
            print(
                f"benchregress error: baseline file {args.baseline_from} does not exist; "
                f"--baseline-from requires an existing baseline (use --history for the "
                f"self-seeding nightly gate)"
            )
            return 1
        baseline = explicit
        source = str(args.baseline_from)
    else:
        if args.capture is not None:
            write_capture(args.capture, prefixed)
        try:
            committed = load_baseline(args.baseline)
        except BenchError as error:
            print(f"benchregress error: {error}")
            return 1
        if committed is None:
            captured = (
                f"captured {len(prefixed)} benchmark(s) under prefix '{args.prefix}' to {args.capture}"
                if args.capture is not None
                else f"parsed {len(prefixed)} benchmark(s) under prefix '{args.prefix}'"
            )
            print(
                f"baseline established: {args.baseline} is missing; {captured}; "
                f"upload the artefact and commit it as {args.baseline} to enable the regression gate"
            )
            return 0
        baseline = committed
        source = str(args.baseline)
    violations = regressions(baseline, prefixed, args.threshold)
    if violations:
        print(f"benchmark regression(s) against {source} (threshold {args.threshold:g}%):")
        for violation in violations:
            print(violation)
        return 1
    compared = len(set(baseline) & set(prefixed))
    print(f"benchregress: {compared} benchmark(s) within {args.threshold:g}% of {source}")
    return 0


def main(argv: Sequence[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        prog="depcheck",
        description="Enforce Cubeglass dependency rules, contracts, licences and benchmark budgets.",
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
    contracts_parser = subparsers.add_parser(
        "contracts",
        help="Check the frozen ABI surface against contracts/abi-baseline.json.",
    )
    contracts_parser.add_argument(
        "--root",
        dest="contracts_root",
        type=Path,
        default=None,
        help=_ROOT_HELP,
    )
    contracts_parser.add_argument(
        "--update",
        action="store_true",
        help="Regenerate contracts/abi-baseline.json (refused unless the live fingerprints match KNOWN_FINGERPRINTS).",
    )
    contracts_parser.add_argument(
        "--since",
        default=None,
        help=(
            "Compare the baseline against this base ref's merge-base baseline (also read from "
            "CG_CONTRACT_BASE_REF); flags a surface change without an ABI version bump."
        ),
    )
    coverage_parser = subparsers.add_parser(
        "coverage",
        help="Enforce a line-coverage floor from a Cobertura XML report.",
    )
    coverage_parser.add_argument(
        "--report",
        type=Path,
        required=True,
        help="Path to a Cobertura XML report or to a directory containing exactly one.",
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
    coverage_parser.add_argument(
        "--allow-empty",
        action="store_true",
        help="Allow a matched module with zero coverable lines (default: fail).",
    )
    bench_parser = subparsers.add_parser(
        "benchregress",
        help="Compare benchmark JSON against a committed baseline, an explicit baseline or a history median.",
    )
    baseline_source = bench_parser.add_mutually_exclusive_group(required=True)
    baseline_source.add_argument(
        "--baseline",
        type=Path,
        help="Committed baseline JSON; missing means bootstrap (capture only).",
    )
    baseline_source.add_argument(
        "--baseline-from",
        type=Path,
        help="Explicit baseline JSON that must exist; a missing file is an error.",
    )
    baseline_source.add_argument(
        "--history",
        type=Path,
        help="Directory of prior nightly summaries; compare against their median.",
    )
    bench_parser.add_argument(
        "--history-window",
        type=int,
        default=DEFAULT_HISTORY_WINDOW,
        help=f"Number of most recent history summaries to use (default: {DEFAULT_HISTORY_WINDOW}).",
    )
    bench_parser.add_argument("--format", choices=("google", "bdn"), required=True, help="Benchmark JSON format.")
    bench_parser.add_argument("--prefix", required=True, help="Source prefix for benchmark names, e.g. 'cpp'.")
    bench_parser.add_argument(
        "--current",
        type=Path,
        nargs="+",
        action="extend",
        required=True,
        help="Benchmark JSON produced by this run (one or more files; repeatable).",
    )
    bench_parser.add_argument(
        "--threshold",
        type=float,
        default=DEFAULT_THRESHOLD_PERCENT,
        help=f"Regression threshold in percent (default: {DEFAULT_THRESHOLD_PERCENT:g}).",
    )
    bench_parser.add_argument("--capture", type=Path, default=None, help="Write the parsed benchmarks here.")
    golden_parser = subparsers.add_parser(
        "golden",
        help="Cross-check contracts/golden/transforms.json with the independent Python reference.",
    )
    golden_parser.add_argument(
        "--root",
        dest="golden_root",
        type=Path,
        default=None,
        help=_ROOT_HELP,
    )
    golden_parser.add_argument(
        "--fixture",
        type=Path,
        default=None,
        help="Golden fixture path (default: <root>/contracts/golden/transforms.json).",
    )
    args = parser.parse_args(argv)

    if args.command == "coverage":
        try:
            coverage = check_coverage(
                args.report, args.module, args.floor, allow_empty=args.allow_empty
            )
        except CoverageError as error:
            print(f"coverage error: {error}")
            return 1
        print(coverage.summary())
        return 0 if coverage.meets_floor else 1

    if args.command == "licences":
        licences_root: Path = args.licences_root or args.root or Path.cwd()
        try:
            licence_failures = check_licences(licences_root)
            licence_warnings = licence_report(licences_root)
        except LicenceError as error:
            print(f"licences error: {error}")
            return 1
        for warning in licence_warnings:
            print(f"warning: {warning}")
        for package in licence_failures:
            print(package)
        return 1 if licence_failures else 0

    if args.command == "contracts":
        contracts_root: Path = args.contracts_root or args.root or Path.cwd()
        base_ref: str | None = args.since or os.environ.get("CG_CONTRACT_BASE_REF") or None
        try:
            if args.update:
                state = update_baseline(contracts_root)
                print(f"contracts: baseline updated for CG_ABI_VERSION {state.version} ({state.fingerprint})")
                return 0
            contract_violations = check_contracts(contracts_root, base_ref=base_ref)
        except ContractError as error:
            print(f"contracts error: {error}")
            return 1
        for violation in contract_violations:
            print(violation)
        return 1 if contract_violations else 0

    if args.command == "benchregress":
        return _run_benchregress(args)

    if args.command == "golden":
        golden_root: Path = args.golden_root or args.root or Path.cwd()
        fixture: Path = args.fixture or golden_root.joinpath(*GOLDEN_FIXTURE_PARTS)
        try:
            golden_failures = verify_golden(fixture)
        except GoldenError as error:
            print(f"golden error: {error}")
            return 1
        for failure in golden_failures:
            print(failure)
        if golden_failures:
            return 1
        print("golden: all fixture cases match the independent Python reference")
        return 0

    root: Path = args.root if args.root is not None else Path.cwd()
    rule_violations = check_root(root)
    for rule_violation in rule_violations:
        print(rule_violation)
    return 1 if rule_violations else 0


if __name__ == "__main__":
    raise SystemExit(main())

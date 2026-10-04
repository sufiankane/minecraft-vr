"""Command-line entry point: ``python -m depcheck [--root PATH]``."""

from __future__ import annotations

import argparse
from collections.abc import Sequence
from pathlib import Path

from depcheck.benchregress import (
    DEFAULT_THRESHOLD_PERCENT,
    BenchError,
    load_baseline,
    load_measurements,
    regressions,
    write_capture,
)
from depcheck.contracts import ContractError, check_contracts, update_baseline
from depcheck.coverage_check import CoverageError, check_coverage
from depcheck.licences import LicenceError, check_licences
from depcheck.rules import check_root

_ROOT_HELP = "Repository root (default: current directory)."


def _run_benchregress(args: argparse.Namespace) -> int:
    try:
        current = load_measurements(args.current, args.format)
    except BenchError as error:
        print(f"benchregress error: {error}")
        return 1
    prefixed = {f"{args.prefix}/{name}": value for name, value in current.items()}
    if args.capture is not None:
        write_capture(args.capture, prefixed)
    try:
        baseline = load_baseline(args.baseline)
    except BenchError as error:
        print(f"benchregress error: {error}")
        return 1
    if baseline is None:
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
    violations = regressions(baseline, prefixed, args.threshold)
    if violations:
        print(f"benchmark regression(s) against {args.baseline} (threshold {args.threshold:g}%):")
        for violation in violations:
            print(violation)
        return 1
    compared = len(set(baseline) & set(prefixed))
    print(f"benchregress: {compared} benchmark(s) within {args.threshold:g}% of {args.baseline}")
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
    bench_parser = subparsers.add_parser(
        "benchregress",
        help="Compare benchmark JSON against docs/perf/nightly-baseline.json (10 percent threshold).",
    )
    bench_parser.add_argument("--baseline", type=Path, required=True, help="Committed baseline JSON.")
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
        try:
            licence_failures = check_licences(licences_root)
        except LicenceError as error:
            print(f"licences error: {error}")
            return 1
        for package in licence_failures:
            print(package)
        return 1 if licence_failures else 0

    if args.command == "contracts":
        contracts_root: Path = args.contracts_root or args.root or Path.cwd()
        try:
            if args.update:
                state = update_baseline(contracts_root)
                print(f"contracts: baseline updated for CG_ABI_VERSION {state.version} ({state.fingerprint})")
                return 0
            contract_violations = check_contracts(contracts_root)
        except ContractError as error:
            print(f"contracts error: {error}")
            return 1
        for violation in contract_violations:
            print(violation)
        return 1 if contract_violations else 0

    if args.command == "benchregress":
        return _run_benchregress(args)

    root: Path = args.root if args.root is not None else Path.cwd()
    rule_violations = check_root(root)
    for rule_violation in rule_violations:
        print(rule_violation)
    return 1 if rule_violations else 0


if __name__ == "__main__":
    raise SystemExit(main())

"""`python -m calib`: run the S9 calibration pipeline on a `.cgrec` session."""

from __future__ import annotations

import argparse
from collections.abc import Sequence
from pathlib import Path

from calib.pipeline import format_report, run_calibration
from calib.session import SessionError
from calib.types import BoardSpec
from calib.writer import utc_now_iso, write_calibration


def _parse_board(text: str) -> tuple[int, int]:
    parts = text.lower().split("x")
    if len(parts) != 2:
        raise argparse.ArgumentTypeError("--board must look like 9x6 (inner corners)")
    columns, rows = int(parts[0]), int(parts[1])
    if columns < 3 or rows < 3:
        raise argparse.ArgumentTypeError("--board needs at least 3x3 inner corners")
    return columns, rows


def main(argv: Sequence[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="calib", description="Stereo calibration from a .cgrec session (S9).")
    parser.add_argument("--session", required=True, type=Path, help=".cgrec session directory")
    parser.add_argument("--board", default="9x6", type=_parse_board, help="inner corners, e.g. 9x6")
    parser.add_argument("--square", type=float, default=0.025, help="square size in metres")
    parser.add_argument("--out", type=Path, default=None, help="write calibration.json here")
    parser.add_argument("--serial", default="", help="device serial recorded in the file")
    parser.add_argument("--max-views", type=int, default=0, help="cap the views used (0 = all)")
    args = parser.parse_args(argv)

    columns, rows = args.board
    board = BoardSpec(inner_cols=columns, inner_rows=rows, square_m=args.square)
    try:
        run = run_calibration(args.session, board, max_views=args.max_views)
    except SessionError as error:
        print(f"calib: {error}")
        return 1

    print(format_report(run))
    if args.out is not None:
        document = write_calibration(
            run, args.out, created_utc=utc_now_iso(), device_serial=args.serial or None
        )
        print(f"calib: wrote {args.out} (schema {document['schema']})")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

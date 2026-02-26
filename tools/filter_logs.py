#!/usr/bin/env python3
import argparse
import re
import sys
from pathlib import Path
# Example usage: filter_logs.py input.txt -m Notice -o out.log


LEVELS = {
    "TRACE": 0,
    "DEBUG": 1,
    "INFO": 2,
    "NOTICE": 3,
    "WARN": 4,
    "ERROR": 5,
    "FATAL": 6,
}

LOG_HEADER_RE = re.compile(r"^\[(TRACE|DEBUG|INFO|NOTICE|WARN|ERROR|FATAL)\]")


def parse_min_level(value: str) -> int:
    key = value.strip().upper()
    if key in LEVELS:
        return LEVELS[key]

    try:
        numeric = int(value)
    except ValueError as exc:
        raise argparse.ArgumentTypeError(
            f"Invalid level '{value}'. Use one of: {', '.join(LEVELS)} or 0-6."
        ) from exc

    if numeric < 0 or numeric > 6:
        raise argparse.ArgumentTypeError("Numeric level must be in range 0-6.")
    return numeric


def filter_log(input_path: Path, min_level: int, output_path: Path | None) -> int:
    if not input_path.exists():
        print(f"Input file not found: {input_path}", file=sys.stderr)
        return 2

    in_handle = input_path.open("r", encoding="utf-8", errors="replace")
    out_handle = (
        output_path.open("w", encoding="utf-8", newline="")
        if output_path is not None
        else sys.stdout
    )

    written = 0
    current_should_include = False

    try:
        for line in in_handle:
            match = LOG_HEADER_RE.match(line)
            if match:
                level_name = match.group(1)
                current_should_include = LEVELS[level_name] >= min_level

            if current_should_include:
                out_handle.write(line)
                written += 1
    finally:
        in_handle.close()
        if output_path is not None:
            out_handle.close()

    return 0


def main() -> int:
    parser = argparse.ArgumentParser(
        description=(
            "Filter log lines by minimum severity level. "
            "Levels: Trace=0, Debug=1, Info=2, Notice=3, Warn=4, Error=5, Fatal=6."
        )
    )
    parser.add_argument("input", type=Path, help="Path to input log file")
    parser.add_argument(
        "-m",
        "--min-level",
        type=parse_min_level,
        required=True,
        help="Minimum level name or number (e.g. Notice or 3)",
    )
    parser.add_argument(
        "-o",
        "--output",
        type=Path,
        help="Optional output file path. If omitted, writes to stdout.",
    )

    args = parser.parse_args()
    return filter_log(args.input, args.min_level, args.output)


if __name__ == "__main__":
    raise SystemExit(main())

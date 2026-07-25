#!/usr/bin/env python3
"""Capture SGR terminal mouse reports from a real PTY for desktop acceptance."""

from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import re
import select
import sys
import termios
import time
import tty


ENABLE_MOUSE_REPORTING = b"\x1b[?1000h\x1b[?1002h\x1b[?1006h"
DISABLE_MOUSE_REPORTING = b"\x1b[?1006l\x1b[?1002l\x1b[?1000l"
REPORT_PATTERN = re.compile(rb"\x1b\[<(\d+);(\d+);(\d+)([Mm])")


def capture_reports(timeout_seconds: float) -> bytes:
    """Enable SGR mouse mode, capture reports until the bounded deadline, then restore the TTY."""

    input_fd = sys.stdin.fileno()
    output_fd = sys.stdout.fileno()
    previous_attributes = termios.tcgetattr(input_fd)
    received = bytearray()
    try:
        tty.setraw(input_fd)
        os.write(output_fd, ENABLE_MOUSE_REPORTING)
        deadline = time.monotonic() + timeout_seconds
        while time.monotonic() < deadline:
            remaining = max(0.0, deadline - time.monotonic())
            readable, _, _ = select.select([input_fd], [], [], remaining)
            if not readable:
                break

            chunk = os.read(input_fd, 4096)
            if not chunk:
                break

            received.extend(chunk)
    finally:
        os.write(output_fd, DISABLE_MOUSE_REPORTING)
        termios.tcsetattr(input_fd, termios.TCSADRAIN, previous_attributes)

    return bytes(received)


def classify_reports(payload: bytes) -> dict[str, object]:
    reports = [
        {
            "buttonCode": int(match.group(1)),
            "column": int(match.group(2)),
            "row": int(match.group(3)),
            "suffix": match.group(4).decode("ascii"),
        }
        for match in REPORT_PATTERN.finditer(payload)
    ]
    codes = [int(report["buttonCode"]) for report in reports]
    return {
        "receivedHex": payload.hex(),
        "reports": reports,
        "observedPress": any(
            code & 0b11 == 0 and report["suffix"] == "M"
            for code, report in zip(codes, reports, strict=True)
        ),
        "observedRelease": any(report["suffix"] == "m" for report in reports),
        "observedDrag": any(code & 32 for code in codes),
        "observedWheel": any(code & 64 for code in codes),
    }


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("output", type=Path)
    parser.add_argument("--timeout", type=float, default=4.0)
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    args.output.parent.mkdir(parents=True, exist_ok=True)
    result = classify_reports(capture_reports(args.timeout))
    args.output.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

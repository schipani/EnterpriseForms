#!/usr/bin/env python3
"""Print the slowest tests of every TRX file under a results directory (CI timing visibility only; never gates).

Appends a markdown table to $GITHUB_STEP_SUMMARY when set, otherwise prints it. Usage:
  python scripts/ci/summarize_trx.py artifacts/stage-b/test-results [--top 15]
"""
from __future__ import annotations

import argparse
import os
import xml.etree.ElementTree as ET
from datetime import timedelta
from pathlib import Path

NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}


def seconds(value: str) -> float:
    hours, minutes, rest = value.split(":")
    return timedelta(hours=int(hours), minutes=int(minutes), seconds=float(rest)).total_seconds()


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("results_dir")
    parser.add_argument("--top", type=int, default=15)
    args = parser.parse_args()
    rows = []
    for file in sorted(Path(args.results_dir).rglob("*.trx")):
        for result in ET.parse(file).getroot().iterfind("t:Results/t:UnitTestResult", NS):
            rows.append((seconds(result.get("duration", "0:0:0")), result.get("testName", "?"), result.get("outcome", "?")))
    rows.sort(reverse=True)
    total = sum(r[0] for r in rows)
    lines = [f"### Slowest tests ({len(rows)} executed, {total:.0f}s summed test time)", "", "| seconds | outcome | test |", "|---:|---|---|"]
    lines += [f"| {d:.1f} | {o} | {n} |" for d, n, o in rows[: args.top]]
    text = "\n".join(lines) + "\n"
    summary = os.environ.get("GITHUB_STEP_SUMMARY")
    if summary:
        with open(summary, "a", encoding="utf-8") as handle:
            handle.write(text)
    print(text)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

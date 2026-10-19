#!/usr/bin/env python3
"""Assert aggregate .NET test totals from xunit TRX files (Microsoft.Testing.Platform).

`--minimum-expected-tests` is evaluated per test executable, so a solution-wide floor cannot be expressed with it
(the Stage A branch CI failed with exit code 9 while all 133 tests passed). This script sums every TRX under the
results directory and fails when: fewer than --min-files result files exist, any test failed/errored/was not
executed, or the executed total is below --min-total. Zero discovered tests therefore remains a failed run.
Usage: python scripts/ci/assert_test_totals.py <results-dir> --min-total 130 --min-files 4
"""
from __future__ import annotations

import argparse
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("results_dir")
    parser.add_argument("--min-total", type=int, required=True)
    parser.add_argument("--min-files", type=int, default=1)
    args = parser.parse_args()
    files = sorted(Path(args.results_dir).rglob("*.trx"))
    total = passed = failed = 0
    for file in files:
        counters = ET.parse(file).getroot().find("t:ResultSummary/t:Counters", NS)
        if counters is None:
            print(f"FAIL  {file}: no counters")
            return 1
        t, p, f = (int(counters.get(k, "0")) for k in ("total", "passed", "failed"))
        other = t - p - f
        print(f"{file.name}: total={t} passed={p} failed={f} other={other}")
        total += t
        passed += p
        failed += f + other
    print(f"files={len(files)} total={total} passed={passed} failed_or_not_executed={failed}")
    if len(files) < args.min_files:
        print(f"FAIL  expected at least {args.min_files} TRX files")
        return 1
    if failed or passed != total:
        print("FAIL  not every test passed")
        return 1
    if total < args.min_total:
        print(f"FAIL  executed {total} tests, minimum is {args.min_total}")
        return 1
    print("PASS")
    return 0


if __name__ == "__main__":
    sys.exit(main())

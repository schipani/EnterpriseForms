#!/usr/bin/env python3
"""Assert aggregate .NET test totals from xunit TRX files (Microsoft.Testing.Platform).

`--minimum-expected-tests` is evaluated per test executable, so a solution-wide floor cannot be expressed with it
(the Stage A branch CI failed with exit code 9 while all 133 tests passed). This script sums every TRX under the
results directory and fails when: fewer than --min-files result files exist, any test failed/errored/was not
executed, or the executed total is below --min-total. Zero discovered tests therefore remains a failed run.

--expect-projects A,B,... (used by run_dotnet_tests.py) is strict: the results must be exactly one
<results>/<Project>/<Project>.trx per named project and no other TRX anywhere, so a lost, overwritten, duplicated or
stale result file fails the run instead of silently lowering the count.
Usage: python scripts/ci/assert_test_totals.py <results-dir> --min-total 130 --min-files 4 [--expect-projects A,B]
"""
from __future__ import annotations

import argparse
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}


def check(results_dir: Path, *, min_total: int, min_files: int = 1, expect_projects: list[str] | None = None) -> int:
    """Returns 0 on PASS, 1 on FAIL (and prints the reason)."""
    results_dir = Path(results_dir)
    files = sorted(results_dir.rglob("*.trx"))
    if expect_projects is not None:
        expected = {(results_dir / name / f"{name}.trx").resolve() for name in expect_projects}
        found = {f.resolve() for f in files}
        missing = sorted(str(p) for p in expected - found)
        unexpected = sorted(str(p) for p in found - expected)
        if missing or unexpected:
            for path in missing:
                print(f"FAIL  missing result file {path}")
            for path in unexpected:
                print(f"FAIL  unexpected result file {path}")
            return 1
    total = passed = failed = 0
    for file in files:
        counters = ET.parse(file).getroot().find("t:ResultSummary/t:Counters", NS)
        if counters is None:
            print(f"FAIL  {file}: no counters")
            return 1
        t, p, f = (int(counters.get(k, "0")) for k in ("total", "passed", "failed"))
        other = t - p - f
        print(f"{file.relative_to(results_dir)}: total={t} passed={p} failed={f} other={other}")
        if t == 0:
            print(f"FAIL  {file}: zero tests")
            return 1
        total += t
        passed += p
        failed += f + other
    print(f"files={len(files)} total={total} passed={passed} failed_or_not_executed={failed}")
    if len(files) < min_files:
        print(f"FAIL  expected at least {min_files} TRX files")
        return 1
    if failed or passed != total:
        print("FAIL  not every test passed")
        return 1
    if total < min_total:
        print(f"FAIL  executed {total} tests, minimum is {min_total}")
        return 1
    print("PASS")
    return 0


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("results_dir")
    parser.add_argument("--min-total", type=int, required=True)
    parser.add_argument("--min-files", type=int, default=1)
    parser.add_argument("--expect-projects", default=None, help="comma-separated project names; strict one-TRX-per-project layout")
    args = parser.parse_args()
    expect = [name for name in args.expect_projects.split(",") if name] if args.expect_projects else None
    return check(Path(args.results_dir), min_total=args.min_total, min_files=args.min_files, expect_projects=expect)


if __name__ == "__main__":
    sys.exit(main())

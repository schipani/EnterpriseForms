#!/usr/bin/env python3
"""Assert aggregate .NET test totals from xunit TRX files (Microsoft.Testing.Platform).

`--minimum-expected-tests` is evaluated per test executable, so a solution-wide floor cannot be expressed with it
(the Stage A branch CI failed with exit code 9 while all 133 tests passed). This script sums every TRX under the
results directory and classifies every individual test result:

  passed               outcome Passed.
  failed               outcome Failed, Error or Timeout.
  authorized skip      outcome NotExecuted whose project, exact test name AND exact skip reason match one entry of the
                       reviewed allowlist (--authorized-skips, normally scripts/ci/authorized_test_skips.json).
  unexpected skip      outcome NotExecuted that does not match an allowlist entry (identity or reason).
  aborted/not run      any other outcome (Aborted, PassedButRunAborted, NotRunnable, Disconnected, Inconclusive, ...).

The run fails when: fewer than --min-files result files exist; a file has no counters, zero tests, zero executed tests
or per-test results that do not reconcile with its counters; any test failed, was skipped without authorization or was
otherwise not executed; an allowlist entry names a test absent from its project's results (stale entry; an allowlisted
test that runs and passes is fine); or the
executed total (passed + failed; authorized skips are never counted as executed) is below --min-total. Zero discovered
tests therefore remains a failed run, and an authorized skip can never raise the executed count.

--expect-projects A,B,... (used by run_dotnet_tests.py) is strict: the results must be exactly one
<results>/<Project>/<Project>.trx per named project and no other TRX anywhere, so a lost, overwritten, duplicated or
stale result file fails the run instead of silently lowering the count.
Usage: python scripts/ci/assert_test_totals.py <results-dir> --min-total 130 --min-files 4 [--expect-projects A,B]
                                               [--authorized-skips scripts/ci/authorized_test_skips.json]
"""
from __future__ import annotations

import argparse
import json
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
PASSED = {"Passed"}
FAILED = {"Failed", "Error", "Timeout"}
SKIPPED = {"NotExecuted"}  # xunit v3 writes a skipped test (Assert.Skip*/Skip=) as NotExecuted with the reason in StdOut


class AllowlistError(ValueError):
    pass


def load_authorized_skips(path: Path | None) -> dict[tuple[str, str], str]:
    """(project, exact test name) -> exact skip reason. No file means no skip is authorized."""
    if path is None:
        return {}
    try:
        document = json.loads(Path(path).read_text(encoding="utf-8"))
    except (OSError, ValueError) as error:
        raise AllowlistError(f"cannot read authorized skips {path}: {error}") from error
    if not isinstance(document, dict) or document.get("schema_version") != 1 or not isinstance(document.get("skips"), list):
        raise AllowlistError(f"{path}: expected schema_version 1 and a 'skips' list")
    allowed: dict[tuple[str, str], str] = {}
    for index, entry in enumerate(document["skips"]):
        fields = {key: entry.get(key) if isinstance(entry, dict) else None for key in ("project", "test", "reason", "justification")}
        if not all(isinstance(value, str) and value.strip() for value in fields.values()):
            raise AllowlistError(f"{path}: entry {index} needs non-empty project, test, reason and justification")
        if any(ch in fields["test"] for ch in "*?[]") or any(ch in fields["project"] for ch in "*?[]"):
            raise AllowlistError(f"{path}: entry {index} must name one exact test, not a pattern")
        key = (fields["project"], fields["test"])
        if key in allowed:
            raise AllowlistError(f"{path}: duplicate entry for {key[0]} {key[1]}")
        allowed[key] = fields["reason"].strip()
    return allowed


def _skip_reason(result: ET.Element) -> str:
    for xpath in ("t:Output/t:StdOut", "t:Output/t:ErrorInfo/t:Message"):
        node = result.find(xpath, NS)
        if node is not None and node.text and node.text.strip():
            return node.text.strip()
    return ""


def _project_of(file: Path, results_dir: Path) -> str:
    relative = file.relative_to(results_dir)
    return relative.parts[0] if len(relative.parts) > 1 else file.stem


def check(results_dir: Path, *, min_total: int, min_files: int = 1, expect_projects: list[str] | None = None,
          authorized_skips: dict[tuple[str, str], str] | None = None) -> int:
    """Returns 0 on PASS, 1 on FAIL (and prints the reason)."""
    results_dir = Path(results_dir)
    authorized_skips = authorized_skips or {}
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

    total = executed = passed = failed = authorized = 0
    problems: list[str] = []
    present: set[tuple[str, str]] = set()
    examined_projects: set[str] = set()
    for file in files:
        root = ET.parse(file).getroot()
        counters = root.find("t:ResultSummary/t:Counters", NS)
        if counters is None:
            print(f"FAIL  {file}: no counters")
            return 1
        project = _project_of(file, results_dir)
        examined_projects.add(project)
        results = root.findall("t:Results/t:UnitTestResult", NS)
        t, p = int(counters.get("total", "0")), int(counters.get("passed", "0"))
        f = sum(int(counters.get(k, "0")) for k in ("failed", "error", "timeout"))  # TRX keeps Error/Timeout outside "failed"
        if t == 0:
            print(f"FAIL  {file}: zero tests")
            return 1
        file_passed = file_failed = file_authorized = 0
        for result in results:
            name, outcome = result.get("testName", ""), result.get("outcome", "")
            present.add((project, name))
            if outcome in PASSED:
                file_passed += 1
            elif outcome in FAILED:
                file_failed += 1
                problems.append(f"FAIL  {project}: {outcome}: {name}")
            elif outcome in SKIPPED:
                expected_reason = authorized_skips.get((project, name))
                reason = _skip_reason(result)
                if expected_reason is not None and reason == expected_reason:
                    file_authorized += 1
                elif expected_reason is not None:
                    problems.append(f"FAIL  {project}: skipped for an unauthorized reason: {name} (reason {reason!r}, authorized {expected_reason!r})")
                else:
                    problems.append(f"FAIL  {project}: unexpected skip: {name} (reason {reason!r})")
            else:
                problems.append(f"FAIL  {project}: not executed ({outcome or 'no outcome'}): {name}")
        if len(results) != t or file_passed != p or file_failed != f:
            print(f"FAIL  {file}: {len(results)} results ({file_passed} passed, {file_failed} failed) do not reconcile with counters total={t} passed={p} failed+error+timeout={f}")
            return 1
        file_executed = file_passed + file_failed
        print(f"{file.relative_to(results_dir)}: total={t} executed={file_executed} passed={file_passed} failed={file_failed} "
              f"authorized_skips={file_authorized} other={t - file_executed - file_authorized}")
        if file_executed == 0:
            print(f"FAIL  {file}: zero executed tests")
            return 1
        total += t
        executed += file_executed
        passed += file_passed
        failed += file_failed
        authorized += file_authorized

    # An allowlisted test may also run and pass (a developer with the Vault variables set); one absent from its project's
    # results is a stale entry (renamed or deleted test) and must be removed in review rather than left to authorize anything.
    for project, name in sorted(set(authorized_skips) - present):
        if project in examined_projects:
            problems.append(f"FAIL  stale authorized skip (test not found in {project} results): {name}")

    print(f"files={len(files)} total={total} executed={executed} passed={passed} failed={failed} "
          f"authorized_skips={authorized} rejected={len(problems)}")
    if len(files) < min_files:
        print(f"FAIL  expected at least {min_files} TRX files")
        return 1
    if problems:
        for problem in problems:
            print(problem)
        print("FAIL  not every test passed or was an authorized skip")
        return 1
    if passed != executed or passed + authorized != total:
        print("FAIL  not every test passed or was an authorized skip")
        return 1
    if executed < min_total:
        print(f"FAIL  executed {executed} tests, minimum is {min_total}")
        return 1
    print("PASS")
    return 0


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("results_dir")
    parser.add_argument("--min-total", type=int, required=True)
    parser.add_argument("--min-files", type=int, default=1)
    parser.add_argument("--expect-projects", default=None, help="comma-separated project names; strict one-TRX-per-project layout")
    parser.add_argument("--authorized-skips", default=None, help="reviewed allowlist JSON; without it no skip is accepted")
    args = parser.parse_args()
    expect = [name for name in args.expect_projects.split(",") if name] if args.expect_projects else None
    try:
        allowed = load_authorized_skips(Path(args.authorized_skips) if args.authorized_skips else None)
    except AllowlistError as error:
        print(f"FAIL  {error}")
        return 1
    return check(Path(args.results_dir), min_total=args.min_total, min_files=args.min_files, expect_projects=expect, authorized_skips=allowed)


if __name__ == "__main__":
    sys.exit(main())

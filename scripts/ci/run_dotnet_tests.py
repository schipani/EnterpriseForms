#!/usr/bin/env python3
"""Run every test project of a solution concurrently with a unique, deterministic TRX path per project.

`dotnet test --solution ... --results-directory X --report-xunit-trx` lets each test executable name its own TRX from
<user>_<machine>_<timestamp>; two executables finishing in the same instant then wrote the same file name and one
result silently overwrote the other (PR #15, first ci attempt on fcdb52a: 302 tests ran, 211 were counted).

This runner removes the shared directory: each test project writes exactly <results>/<Project>/<Project>.trx, the
results root is emptied first so no file from an earlier run can be counted, and the aggregate check then requires
exactly one TRX per discovered project (assert_test_totals.py --expect-projects).
Usage: python scripts/ci/run_dotnet_tests.py EnterpriseForms.All.slnx --results-dir artifacts/test-results \
           --configuration Release --no-build --minimum-expected-tests 10 --min-total 130
"""
from __future__ import annotations

import argparse
import re
import shutil
import subprocess
import sys
import xml.etree.ElementTree as ET
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import assert_test_totals  # noqa: E402


def test_projects(solution: Path) -> list[Path]:
    """Projects of the solution whose csproj declares <IsTestProject>true</IsTestProject>."""
    root = solution.parent
    projects = []
    for element in ET.parse(solution).getroot().iter("Project"):
        path = root / element.get("Path", "")
        if path.suffix == ".csproj" and re.search(r"<IsTestProject>\s*true\s*</IsTestProject>", path.read_text(encoding="utf-8"), re.IGNORECASE):
            projects.append(path)
    return sorted(projects)


def run(project: Path, results: Path, configuration: str, no_build: bool, minimum: int) -> tuple[str, int]:
    name = project.stem
    target = results / name
    target.mkdir(parents=True, exist_ok=True)
    command = ["dotnet", "test", "--project", str(project), "-c", configuration, "--results-directory", str(target),
               "--report-xunit-trx", "--report-xunit-trx-filename", f"{name}.trx"]
    if no_build:
        command.append("--no-build")
    command += ["--", "--minimum-expected-tests", str(minimum)]
    with (target / "console.log").open("w", encoding="utf-8") as log:
        completed = subprocess.run(command, stdout=log, stderr=subprocess.STDOUT, check=False)
    return name, completed.returncode


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("solution")
    parser.add_argument("--results-dir", required=True)
    parser.add_argument("--configuration", default="Release")
    parser.add_argument("--no-build", action="store_true")
    parser.add_argument("--minimum-expected-tests", type=int, default=10)
    parser.add_argument("--min-total", type=int, required=True)
    args = parser.parse_args()

    projects = test_projects(Path(args.solution).resolve())
    if not projects:
        print("FAIL  no test projects discovered")
        return 1
    results = Path(args.results_dir).resolve()
    shutil.rmtree(results, ignore_errors=True)  # nothing from an earlier run may be counted
    results.mkdir(parents=True)
    print("test projects (run concurrently):", ", ".join(p.stem for p in projects))
    with ThreadPoolExecutor(max_workers=len(projects)) as pool:
        outcomes = list(pool.map(lambda p: run(p, results, args.configuration, args.no_build, args.minimum_expected_tests), projects))
    for name, code in outcomes:
        print(f"{name}: exit code {code}")
        if code != 0:
            print((results / name / "console.log").read_text(encoding="utf-8")[-4000:])
    verdict = assert_test_totals.check(results, min_total=args.min_total, min_files=len(projects), expect_projects=[p.stem for p in projects])
    return 1 if verdict or any(code != 0 for _, code in outcomes) else 0


if __name__ == "__main__":
    sys.exit(main())

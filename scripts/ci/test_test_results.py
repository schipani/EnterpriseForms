#!/usr/bin/env python3
"""Self-tests for the .NET test-result accounting (run in CI: python -m unittest scripts/ci/test_test_results.py).

Proves the counter cannot be fooled by the failure seen on PR #15 (two concurrent executables, one overwritten TRX)
or by stale, duplicated, empty or failed result files, and that concurrently running projects writing to their own
deterministic paths are all counted.
"""
from __future__ import annotations

import io
import sys
import tempfile
import threading
import unittest
from contextlib import redirect_stdout
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import assert_test_totals  # noqa: E402

TRX = """<?xml version="1.0" encoding="utf-8"?>
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><ResultSummary outcome="Completed">
<Counters total="{total}" executed="{total}" passed="{passed}" failed="{failed}" /></ResultSummary></TestRun>"""

PROJECTS = {"Alpha.Tests": 34, "Beta.Tests": 66, "Gamma.Tests": 111, "Delta.Tests": 91}


def write(root: Path, project: str, total: int, passed: int | None = None, failed: int = 0, name: str | None = None) -> None:
    target = root / project
    target.mkdir(parents=True, exist_ok=True)
    (target / (name or f"{project}.trx")).write_text(TRX.format(total=total, passed=total if passed is None else passed, failed=failed), encoding="utf-8")


def verdict(root: Path, **kwargs) -> tuple[int, str]:
    out = io.StringIO()
    with redirect_stdout(out):
        code = assert_test_totals.check(root, **kwargs)
    return code, out.getvalue()


class StrictProjectLayout(unittest.TestCase):
    def setUp(self) -> None:
        self.tmp = tempfile.TemporaryDirectory()
        self.root = Path(self.tmp.name)

    def tearDown(self) -> None:
        self.tmp.cleanup()

    def strict(self) -> tuple[int, str]:
        return verdict(self.root, min_total=130, min_files=len(PROJECTS), expect_projects=list(PROJECTS))

    def test_concurrent_writers_with_deterministic_paths_are_all_counted(self) -> None:
        threads = [threading.Thread(target=write, args=(self.root, name, total)) for name, total in PROJECTS.items() for _ in range(3)]
        for thread in threads:
            thread.start()
        for thread in threads:
            thread.join()
        code, out = self.strict()
        self.assertEqual(0, code, out)
        self.assertIn(f"files=4 total={sum(PROJECTS.values())}", out)

    def test_a_lost_or_overwritten_result_fails(self) -> None:
        for name, total in list(PROJECTS.items())[:3]:
            write(self.root, name, total)
        code, out = self.strict()
        self.assertEqual(1, code)
        self.assertIn("missing result file", out)

    def test_the_pr15_collision_shape_fails_even_without_strict_mode_when_files_are_missing(self) -> None:
        # Old layout: one shared directory; the fourth executable's file overwrote another one.
        for name, total in list(PROJECTS.items())[:3]:
            write(self.root, ".", total, name=f"runner_2026-10-22_07_58_39.{name}.trx")
        code, out = verdict(self.root, min_total=130, min_files=4)
        self.assertEqual(1, code)
        self.assertIn("expected at least 4 TRX files", out)

    def test_stale_or_duplicate_result_files_fail(self) -> None:
        for name, total in PROJECTS.items():
            write(self.root, name, total)
        write(self.root, "Alpha.Tests", 34, name="tsch_machine_2026-10-21.trx")
        code, out = self.strict()
        self.assertEqual(1, code)
        self.assertIn("unexpected result file", out)

    def test_failed_empty_and_low_totals_fail(self) -> None:
        for name, total in PROJECTS.items():
            write(self.root, name, total, passed=total - 1 if name == "Beta.Tests" else None, failed=1 if name == "Beta.Tests" else 0)
        self.assertEqual(1, self.strict()[0])
        for name in PROJECTS:
            write(self.root, name, 0 if name == "Gamma.Tests" else PROJECTS[name])
        self.assertIn("zero tests", self.strict()[1])
        for name, total in PROJECTS.items():
            write(self.root, name, total)
        code, out = verdict(self.root, min_total=1000, min_files=4, expect_projects=list(PROJECTS))
        self.assertEqual(1, code)
        self.assertIn("minimum is 1000", out)


if __name__ == "__main__":
    unittest.main()

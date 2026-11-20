#!/usr/bin/env python3
"""Self-tests for the .NET test-result accounting (run in CI: python -m unittest scripts/ci/test_test_results.py).

Proves the counter cannot be fooled by the failure seen on PR #15 (two concurrent executables, one overwritten TRX)
or by stale, duplicated, empty or failed result files, and that concurrently running projects writing to their own
deterministic paths are all counted. PR #57: a skip is accepted only when its project, exact test name and exact reason
match the reviewed allowlist; unexpected skips, aborted/not-run tests, errors and unreconciled TRX files still fail, and
an authorized skip never counts towards the executed-test floor.
"""
from __future__ import annotations

import io
import json
import re
import sys
import tempfile
import threading
import unittest
from contextlib import redirect_stdout
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import assert_test_totals  # noqa: E402

TRX = """<?xml version="1.0" encoding="utf-8"?>
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results>{results}</Results><ResultSummary outcome="Completed">
<Counters total="{total}" executed="{executed}" passed="{passed}" failed="{failed}" error="{error}" timeout="{timeout}" /></ResultSummary></TestRun>"""

RESULT = '<UnitTestResult testName="{name}" outcome="{outcome}"><Output><StdOut>{reason}</StdOut></Output></UnitTestResult>'

PROJECTS = {"Alpha.Tests": 34, "Beta.Tests": 66, "Gamma.Tests": 111, "Delta.Tests": 91}
SKIP_REASON = "E2_VAULT_* not set (run scripts/dev/e2-vault-integration.ps1)"
VAULT_TEST = "Beta.Tests.VaultIntegrationTests.RealVaultWraps"
ALLOWED = {("Beta.Tests", VAULT_TEST): SKIP_REASON}
REPO = Path(__file__).resolve().parents[2]


def write(root: Path, project: str, total: int, passed: int | None = None, failed: int = 0, name: str | None = None,
          extra: list[tuple[str, str, str]] | None = None, counted_total: int | None = None) -> None:
    """`total` tests of which `passed` pass and `failed` fail, plus `extra` (name, outcome, reason) results."""
    passed = total if passed is None else passed
    rows = [(f"{project}.T{i}", "Passed", "") for i in range(passed)] + [(f"{project}.F{i}", "Failed", "") for i in range(failed)]
    rows += [(f"{project}.N{i}", "NotExecuted", "") for i in range(total - passed - failed)] + (extra or [])
    results = "".join(RESULT.format(name=n, outcome=o, reason=r) for n, o, r in rows)
    passes = sum(o == "Passed" for _, o, _ in rows)
    failures = sum(o == "Failed" for _, o, _ in rows)
    errors, timeouts = (sum(o == kind for _, o, _ in rows) for kind in ("Error", "Timeout"))
    target = root / project
    target.mkdir(parents=True, exist_ok=True)
    (target / (name or f"{project}.trx")).write_text(
        TRX.format(results=results, total=len(rows) if counted_total is None else counted_total, executed=passes + failures + errors + timeouts, passed=passes, failed=failures, error=errors, timeout=timeouts),
        encoding="utf-8")


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


class SkipAccounting(unittest.TestCase):
    """PR #57: passed, failed, authorized skip, unexpected skip, aborted/not run and missing results are distinct."""

    def setUp(self) -> None:
        self.tmp = tempfile.TemporaryDirectory()
        self.root = Path(self.tmp.name)

    def tearDown(self) -> None:
        self.tmp.cleanup()

    def run_with(self, beta_extra: list[tuple[str, str, str]], allowed: dict | None = None, min_total: int = 130) -> tuple[int, str]:
        for name, total in PROJECTS.items():
            write(self.root, name, total, extra=beta_extra if name == "Beta.Tests" else None)
        return verdict(self.root, min_total=min_total, min_files=len(PROJECTS), expect_projects=list(PROJECTS),
                       authorized_skips=ALLOWED if allowed is None else allowed)

    def test_an_authorized_skip_passes_and_is_not_counted_as_executed(self) -> None:
        code, out = self.run_with([(VAULT_TEST, "NotExecuted", SKIP_REASON)])
        self.assertEqual(0, code, out)
        self.assertIn(f"executed={sum(PROJECTS.values())} passed={sum(PROJECTS.values())} failed=0 authorized_skips=1", out)

    def test_an_unexpected_skip_fails(self) -> None:
        code, out = self.run_with([(VAULT_TEST, "NotExecuted", SKIP_REASON), ("Beta.Tests.Other.Test", "NotExecuted", SKIP_REASON)])
        self.assertEqual(1, code)
        self.assertIn("unexpected skip: Beta.Tests.Other.Test", out)

    def test_without_an_allowlist_no_skip_is_accepted(self) -> None:
        code, out = self.run_with([(VAULT_TEST, "NotExecuted", SKIP_REASON)], allowed={})
        self.assertEqual(1, code)
        self.assertIn(f"unexpected skip: {VAULT_TEST}", out)

    def test_an_allowlisted_test_skipped_for_another_reason_fails(self) -> None:
        code, out = self.run_with([(VAULT_TEST, "NotExecuted", "explicit test not run")])
        self.assertEqual(1, code)
        self.assertIn("skipped for an unauthorized reason", out)
        code, out = self.run_with([(VAULT_TEST, "NotExecuted", "")])
        self.assertEqual(1, code)

    def test_the_identity_includes_the_project(self) -> None:
        code, out = self.run_with([(VAULT_TEST, "NotExecuted", SKIP_REASON)], allowed={("Gamma.Tests", VAULT_TEST): SKIP_REASON})
        self.assertEqual(1, code)
        self.assertIn(f"unexpected skip: {VAULT_TEST}", out)

    def test_aborted_and_other_not_executed_outcomes_fail_even_when_allowlisted(self) -> None:
        for outcome in ("Aborted", "PassedButRunAborted", "NotRunnable", "Disconnected", "Inconclusive", "InProgress", "Pending", "Warning", ""):
            with self.subTest(outcome=outcome):
                code, out = self.run_with([(VAULT_TEST, outcome, SKIP_REASON)])
                self.assertEqual(1, code, out)
                self.assertIn("not executed", out)

    def test_errors_timeouts_and_failures_fail(self) -> None:
        for outcome in ("Failed", "Error", "Timeout"):
            with self.subTest(outcome=outcome):
                code, out = self.run_with([(VAULT_TEST, outcome, "")])
                self.assertEqual(1, code)
                self.assertIn(f"{outcome}: {VAULT_TEST}", out)

    def test_results_that_do_not_reconcile_with_the_counters_fail(self) -> None:
        for name, total in PROJECTS.items():
            write(self.root, name, total, counted_total=total + 1 if name == "Gamma.Tests" else None)
        code, out = verdict(self.root, min_total=130, min_files=4, expect_projects=list(PROJECTS), authorized_skips=ALLOWED)
        self.assertEqual(1, code)
        self.assertIn("do not reconcile", out)

    def test_a_stale_allowlist_entry_fails_but_an_allowlisted_test_that_ran_is_fine(self) -> None:
        code, out = self.run_with([])
        self.assertEqual(1, code)
        self.assertIn(f"stale authorized skip (test not found in Beta.Tests results): {VAULT_TEST}", out)
        code, out = self.run_with([(VAULT_TEST, "Passed", "")])
        self.assertEqual(0, code, out)

    def test_authorized_skips_never_satisfy_the_executed_floor_or_a_zero_executed_project(self) -> None:
        floor = sum(PROJECTS.values()) + 1
        code, out = self.run_with([(VAULT_TEST, "NotExecuted", SKIP_REASON)], min_total=floor)
        self.assertEqual(1, code)
        self.assertIn(f"executed {sum(PROJECTS.values())} tests, minimum is {floor}", out)
        for name, total in PROJECTS.items():
            write(self.root, name, total if name != "Beta.Tests" else 0, extra=[(VAULT_TEST, "NotExecuted", SKIP_REASON)] if name == "Beta.Tests" else None)
        code, out = verdict(self.root, min_total=1, min_files=4, expect_projects=list(PROJECTS), authorized_skips=ALLOWED)
        self.assertEqual(1, code)
        self.assertIn("zero executed tests", out)

    def test_a_missing_result_file_still_fails_with_an_allowlist(self) -> None:
        for name, total in list(PROJECTS.items())[:3]:
            write(self.root, name, total)
        code, out = verdict(self.root, min_total=1, min_files=4, expect_projects=list(PROJECTS), authorized_skips=ALLOWED)
        self.assertEqual(1, code)
        self.assertIn("missing result file", out)


class AllowlistFile(unittest.TestCase):
    def setUp(self) -> None:
        self.tmp = tempfile.TemporaryDirectory()
        self.path = Path(self.tmp.name) / "skips.json"

    def tearDown(self) -> None:
        self.tmp.cleanup()

    def load(self, document) -> dict:
        self.path.write_text(json.dumps(document), encoding="utf-8")
        return assert_test_totals.load_authorized_skips(self.path)

    def test_malformed_pattern_duplicate_or_unjustified_entries_are_refused(self) -> None:
        good = {"project": "P", "test": "P.T", "reason": "r", "justification": "j"}
        self.assertEqual({("P", "P.T"): "r"}, self.load({"schema_version": 1, "skips": [good]}))
        for bad in (
            [],
            {"schema_version": 2, "skips": []},
            {"schema_version": 1},
            {"schema_version": 1, "skips": [{**good, "justification": " "}]},
            {"schema_version": 1, "skips": [{**good, "reason": ""}]},
            {"schema_version": 1, "skips": [{**good, "test": "P.*"}]},
            {"schema_version": 1, "skips": [good, good]},
        ):
            with self.subTest(bad=bad), self.assertRaises(assert_test_totals.AllowlistError):
                self.load(bad)
        with self.assertRaises(assert_test_totals.AllowlistError):
            assert_test_totals.load_authorized_skips(Path(self.tmp.name) / "absent.json")

    def test_the_committed_allowlist_is_the_reviewed_opt_in_set_used_by_ci_and_level_3(self) -> None:
        import run_dotnet_tests  # noqa: PLC0415
        committed = REPO / "scripts" / "ci" / "authorized_test_skips.json"
        self.assertEqual(committed.resolve(), run_dotnet_tests.DEFAULT_AUTHORIZED_SKIPS.resolve())
        allowed = assert_test_totals.load_authorized_skips(committed)
        vault = r"^E\d_VAULT_\* .*not set \(run scripts/dev/e\d[\w-]*\.ps1\)$"
        # The two exact SEC-BL-01 skip reasons (C-161 owner-approved allowlist): the environment-dependent real-store tests and
        # the opt-in OBS-1 timing characterization tests. No other SEC-BL-01 reason is accepted.
        secbl01_stores = "SECBL01_* not set (run scripts/dev/sec-bl-01-wp2-azurite-integration.ps1)"
        secbl01_obs1 = "SECBL01_OBS1_CHARACTERIZE=1 not set (timing-dependent real-endpoint test)"
        authority = REPO / "tests" / "authority" / "AuthorizationAuthority.Tests"
        # Changing these numbers is deliberate: every new authorized skip needs review of both files.
        counts = {"vault": 0, "secbl01_stores": 0, "secbl01_obs1": 0}
        for (project, test), reason in allowed.items():
            self.assertTrue(test.startswith(project + "."), test)
            if re.match(vault, reason):
                counts["vault"] += 1
                source = REPO / "tests" / ("authority" if project.startswith("AuthorizationAuthority") else "features") / project / (test.split(".")[-2] + ".cs")
                self.assertIn(reason, source.read_text(encoding="utf-8"), f"{test}: the reason must be the test's own Assert.Skip* text")
                continue
            self.assertEqual("AuthorizationAuthority.Tests", project, test)
            test_class = test[len(project) + 1:].split(".")[0]
            declaring = [path for path in sorted(authority.glob("*.cs"))
                         if re.search(rf"\bclass {re.escape(test_class)}\b", path.read_text(encoding="utf-8"))]
            self.assertTrue(declaring, f"{test}: no source file declares class {test_class}")
            if reason == secbl01_obs1:
                counts["secbl01_obs1"] += 1
                self.assertEqual("ArchiveDeliveryOutageCharacterizationTests", test_class, test)
                self.assertTrue(any(reason in path.read_text(encoding="utf-8") for path in declaring), f"{test}: the reason must be the test's own Assert.Skip* text")
            elif reason == secbl01_stores:
                counts["secbl01_stores"] += 1
                # The reason is the Assert.SkipWhen text of the shared Wp2Azurite.Require() helper, which the class must call.
                helper = (authority / "JournalVerificationAzuriteIntegrationTests.cs").read_text(encoding="utf-8")
                self.assertIn("internal static class Wp2Azurite", helper)
                self.assertIn(reason, helper, f"{test}: the reason must be Wp2Azurite.Require()'s Assert.SkipWhen text")
                self.assertTrue(any("Wp2Azurite.Require()" in path.read_text(encoding="utf-8") for path in declaring),
                                f"{test}: class {test_class} must skip through Wp2Azurite.Require()")
            else:
                self.fail(f"{test}: unreviewed skip reason {reason!r}")
        self.assertEqual({"vault": 16, "secbl01_stores": 204, "secbl01_obs1": 3}, counts)
        self.assertEqual(223, len(allowed))


if __name__ == "__main__":
    unittest.main()

#!/usr/bin/env python3
"""Self-tests for the Stage B shard coverage checker (run in CI: python -m unittest scripts/ci/test_stage_b_groups.py).

Proves the split cannot silently lose a test: unassigned and doubly-assigned classes, stale manifest entries, empty groups,
missing / empty / short / failed result files, a missing shard and a wrong total all fail; the real manifest is consistent.
"""
from __future__ import annotations

import io
import sys
import tempfile
import unittest
from collections import Counter
from contextlib import redirect_stdout
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import stage_b_groups as sbg  # noqa: E402

NS = "Demo.Tests"
MANIFEST = {
    "namespace": NS,
    "minimumTotalTests": 5,
    "groups": [
        {"name": "alpha", "title": "Alpha", "classes": ["OneTests", "TwoTests"]},
        {"name": "beta", "title": "Beta", "classes": ["ThreeTests"]},
    ],
}
DISCOVERED = Counter({"OneTests": 2, "TwoTests": 1, "ThreeTests": 2})


def listing(counts: Counter) -> str:
    lines = [f"Discovered {sum(counts.values())} tests in assembly - x.dll"]
    lines += [f"  {NS}.{cls}.Method{i}" for cls, n in counts.items() for i in range(n)]
    return "\n".join(lines) + "\nDiscovered tests.\n"


def trx(root: Path, folder: str, cases: list[tuple[str, str]]) -> None:
    target = root / folder
    target.mkdir(parents=True, exist_ok=True)
    rows = "".join(f'<UnitTestResult testName="{n}" outcome="{o}" />' for n, o in cases)
    (target / "r.trx").write_text(
        '<?xml version="1.0"?><TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">'
        f"<Results>{rows}</Results></TestRun>", encoding="utf-8")


def passing(cls: str, n: int) -> list[tuple[str, str]]:
    return [(f"{NS}.{cls}.Method{i}", "Passed") for i in range(n)]


class CoverageTests(unittest.TestCase):
    def test_group_selection_for_release_runs(self) -> None:
        # C-131: a release run may select one, several or all groups; unknown names are refused, nothing is silently dropped.
        self.assertEqual([g["name"] for g in sbg.select_groups(MANIFEST, "all")[0]], ["alpha", "beta"])
        self.assertEqual([g["name"] for g in sbg.select_groups(MANIFEST, None)[0]], ["alpha", "beta"])
        self.assertEqual([g["name"] for g in sbg.select_groups(MANIFEST, " beta ")[0]], ["beta"])
        self.assertEqual([g["name"] for g in sbg.select_groups(MANIFEST, "beta,alpha")[0]], ["alpha", "beta"])
        self.assertEqual(sbg.select_groups(MANIFEST, "alpha,gamma")[1], ["gamma"])

    def test_complete_assignment_passes(self) -> None:
        self.assertEqual(sbg.check_coverage(MANIFEST, DISCOVERED), [])

    def test_unassigned_class_fails(self) -> None:
        errors = sbg.check_coverage(MANIFEST, DISCOVERED + Counter({"NewTests": 1}))
        self.assertTrue(any("NewTests" in e and "belongs to no group" in e for e in errors))

    def test_class_in_two_groups_fails(self) -> None:
        manifest = {**MANIFEST, "groups": MANIFEST["groups"] + [{"name": "gamma", "title": "G", "classes": ["OneTests"]}]}
        errors = sbg.check_coverage(manifest, DISCOVERED)
        self.assertTrue(any("OneTests" in e and "both" in e for e in errors))

    def test_stale_class_fails(self) -> None:
        manifest = {**MANIFEST, "groups": [{"name": "alpha", "title": "A", "classes": ["OneTests", "TwoTests", "GoneTests"]}, MANIFEST["groups"][1]]}
        self.assertTrue(any("GoneTests" in e and "stale" in e for e in sbg.check_coverage(manifest, DISCOVERED)))

    def test_group_discovering_zero_tests_fails(self) -> None:
        manifest = {**MANIFEST, "groups": MANIFEST["groups"] + [{"name": "empty", "title": "E", "classes": []}]}
        self.assertTrue(any("empty" in e for e in sbg.check_coverage(manifest, DISCOVERED)))

    def test_listing_parser_counts_and_rejects_inconsistent_totals(self) -> None:
        self.assertEqual(sbg.class_counts_from_listing(listing(DISCOVERED), NS), DISCOVERED)
        with self.assertRaises(ValueError):
            sbg.class_counts_from_listing(listing(DISCOVERED).replace("Discovered 5", "Discovered 9"), NS)


class ResultTests(unittest.TestCase):
    def setUp(self) -> None:
        self.tmp = tempfile.TemporaryDirectory()
        self.root = Path(self.tmp.name)
        self.addCleanup(self.tmp.cleanup)
        (self.root / "all.txt").write_text(listing(DISCOVERED), encoding="utf-8")

    def write_all(self, skip: str | None = None, alpha_extra: list[tuple[str, str]] | None = None) -> None:
        if skip != "alpha":
            trx(self.root / "res", "evidence-alpha", passing("OneTests", 2) + passing("TwoTests", 1) + (alpha_extra or []))
        if skip != "beta":
            trx(self.root / "res", "evidence-beta", passing("ThreeTests", 2))

    def verify_all(self) -> int:
        with redirect_stdout(io.StringIO()):
            return sbg.main(["--manifest", str(self.write_manifest()), "verify-all", "--artifact-prefix", "evidence-", "--list", str(self.root / "all.txt"), "--results", str(self.root / "res")])

    def write_manifest(self) -> Path:
        import json
        path = self.root / "manifest.json"
        path.write_text(json.dumps(MANIFEST), encoding="utf-8")
        return path

    def test_everything_executed_passes(self) -> None:
        self.write_all()
        self.assertEqual(self.verify_all(), 0)

    def test_failed_test_fails(self) -> None:
        trx(self.root / "res", "evidence-alpha", passing("OneTests", 1) + [(f"{NS}.OneTests.Method1", "Failed")] + passing("TwoTests", 1))
        trx(self.root / "res", "evidence-beta", passing("ThreeTests", 2))
        self.assertEqual(self.verify_all(), 1)

    def test_missing_shard_fails(self) -> None:
        self.write_all(skip="beta")
        self.assertEqual(self.verify_all(), 1)

    def test_short_shard_fails(self) -> None:
        trx(self.root / "res", "evidence-alpha", passing("OneTests", 2))
        trx(self.root / "res", "evidence-beta", passing("ThreeTests", 2))
        self.assertEqual(self.verify_all(), 1)

    def test_test_executed_in_wrong_shard_fails(self) -> None:
        self.write_all(alpha_extra=passing("ThreeTests", 1))
        self.assertEqual(self.verify_all(), 1)

    def test_duplicated_test_cannot_mask_a_missing_test_in_the_same_class(self) -> None:
        # OneTests has two tests (Method0, Method1). Method0 ran twice and Method1 never: the per-class count is still 2.
        dup = [(f"{NS}.OneTests.Method0", "Passed"), (f"{NS}.OneTests.Method0", "Passed")]
        trx(self.root / "res", "evidence-alpha", dup + passing("TwoTests", 1))
        trx(self.root / "res", "evidence-beta", passing("ThreeTests", 2))
        self.assertEqual(sum(sbg.class_counts(sbg.ids_from_trx(self.root / "res" / "evidence-alpha")[0], NS).values()), 3)
        buffer = io.StringIO()
        with redirect_stdout(buffer):
            code = sbg.main(["--manifest", str(self.write_manifest()), "verify-all", "--artifact-prefix", "evidence-", "--list", str(self.root / "all.txt"), "--results", str(self.root / "res")])
        self.assertEqual(code, 1)
        self.assertIn("did not execute Demo.Tests.OneTests.Method1", buffer.getvalue())
        self.assertIn("Method0 executed 2 times", buffer.getvalue())

    def test_theory_cases_are_identified_by_their_arguments(self) -> None:
        text = chr(10).join(["Discovered 2 tests in assembly - x", f'  {NS}.OneTests.Refused(input: "a.b")', f'  {NS}.OneTests.Refused(input: "c")'])
        self.assertEqual(sbg.ids_from_listing(text, NS), Counter({f'{NS}.OneTests.Refused(input: "a.b")': 1, f'{NS}.OneTests.Refused(input: "c")': 1}))

    def test_empty_result_file_fails(self) -> None:
        self.write_all()
        trx(self.root / "res", "evidence-beta", [])
        self.assertEqual(self.verify_all(), 1)

    def test_no_trx_in_shard_fails(self) -> None:
        self.write_all()
        (self.root / "res" / "evidence-beta" / "r.trx").unlink()
        self.assertEqual(self.verify_all(), 1)

    def test_total_below_baseline_fails(self) -> None:
        self.write_all()
        manifest = {**MANIFEST, "minimumTotalTests": 6}
        import json
        path = self.root / "m6.json"
        path.write_text(json.dumps(manifest), encoding="utf-8")
        with redirect_stdout(io.StringIO()):
            code = sbg.main(["--manifest", str(path), "verify-all", "--artifact-prefix", "evidence-", "--list", str(self.root / "all.txt"), "--results", str(self.root / "res")])
        self.assertEqual(code, 1)

    def test_verify_group_detects_a_filter_that_misses_a_class(self) -> None:
        trx(self.root / "gres", "x", passing("OneTests", 2) + passing("TwoTests", 1))
        (self.root / "g.txt").write_text(listing(Counter({"OneTests": 2})), encoding="utf-8")
        with redirect_stdout(io.StringIO()):
            code = sbg.main(["--manifest", str(self.write_manifest()), "verify-group", "--group", "alpha", "--list", str(self.root / "g.txt"), "--results", str(self.root / "gres")])
        self.assertEqual(code, 1)


MANIFEST_ENV = {
    "namespace": NS,
    "minimumTotalTests": 1,
    "groups": [
        {"name": "quiet", "title": "Quiet", "classes": ["OneTests"]},
        {"name": "sweep", "title": "Sweep", "stackEnvironment": {"IX1_SEARCH_SWEEP_INTERVAL_SECONDS": "10"}, "classes": ["TwoTests"]},
        {"name": "slower", "title": "Slower", "stackEnvironment": {"IX1_SEARCH_SWEEP_INTERVAL_SECONDS": "60"}, "classes": ["ThreeTests"]},
    ],
}


class StackEnvironmentTests(unittest.TestCase):
    """BH-1 (C-193): per-group environment of the disposable stack; allowlisted, range-checked, shortest interval wins."""

    def run_tool(self, manifest: dict, *args: str) -> tuple[int, str]:
        with tempfile.TemporaryDirectory() as d:
            path = Path(d) / "m.json"
            path.write_text(__import__("json").dumps(manifest), encoding="utf-8")
            out = io.StringIO()
            with redirect_stdout(out), __import__("contextlib").redirect_stderr(io.StringIO()):
                code = sbg.main(["--manifest", str(path), "stack-env", *args])
        return code, out.getvalue()

    def test_a_valid_declaration_passes_coverage(self) -> None:
        self.assertEqual(sbg.check_coverage(MANIFEST_ENV, DISCOVERED), [])

    def test_a_key_outside_the_allowlist_fails(self) -> None:
        bad = {**MANIFEST_ENV, "groups": [{**MANIFEST_ENV["groups"][0], "stackEnvironment": {"SearchIndex__Mode": "Off"}}]}
        self.assertTrue(any("not allowlisted" in e for e in sbg.check_stack_environment(bad)))

    def test_values_outside_the_range_or_not_integer_strings_fail(self) -> None:
        for value in ("0", "3600", "ten", 10, "-5", "1.5"):
            bad = {**MANIFEST_ENV, "groups": [{**MANIFEST_ENV["groups"][0], "stackEnvironment": {"IX1_SEARCH_SWEEP_INTERVAL_SECONDS": value}}]}
            self.assertEqual(len(sbg.check_stack_environment(bad)), 1, value)

    def test_the_shortest_interval_wins_when_groups_share_a_stack(self) -> None:
        self.assertEqual(sbg.stack_environment(MANIFEST_ENV["groups"]), {"IX1_SEARCH_SWEEP_INTERVAL_SECONDS": "10"})
        self.assertEqual(sbg.stack_environment(MANIFEST_ENV["groups"][2:]), {"IX1_SEARCH_SWEEP_INTERVAL_SECONDS": "60"})
        self.assertEqual(sbg.stack_environment(MANIFEST_ENV["groups"][:1]), {})

    def test_every_allowlisted_key_is_printed_and_empty_means_the_overlay_default(self) -> None:
        self.assertEqual(self.run_tool(MANIFEST_ENV, "--groups", "quiet"), (0, "IX1_SEARCH_SWEEP_INTERVAL_SECONDS=\n"))
        self.assertEqual(self.run_tool(MANIFEST_ENV, "--groups", "quiet,slower"), (0, "IX1_SEARCH_SWEEP_INTERVAL_SECONDS=60\n"))
        self.assertEqual(self.run_tool(MANIFEST_ENV, "--groups", "all"), (0, "IX1_SEARCH_SWEEP_INTERVAL_SECONDS=10\n"))
        self.assertEqual(self.run_tool(MANIFEST_ENV, "--defaults"), (0, "IX1_SEARCH_SWEEP_INTERVAL_SECONDS=\n"))

    def test_classes_resolve_to_their_groups_exactly_or_by_substring(self) -> None:
        self.assertEqual(self.run_tool(MANIFEST_ENV, "--class", "TwoTests"), (0, "IX1_SEARCH_SWEEP_INTERVAL_SECONDS=10\n"))
        self.assertEqual(self.run_tool(MANIFEST_ENV, "--class", "Two")[0], 1)  # exact names only
        self.assertEqual(self.run_tool(MANIFEST_ENV, "--class-pattern", "Tests"), (0, "IX1_SEARCH_SWEEP_INTERVAL_SECONDS=10\n"))
        self.assertEqual(self.run_tool(MANIFEST_ENV, "--class-pattern", "One"), (0, "IX1_SEARCH_SWEEP_INTERVAL_SECONDS=\n"))
        self.assertEqual(self.run_tool(MANIFEST_ENV, "--groups", "gamma")[0], 1)
        self.assertEqual(self.run_tool(MANIFEST_ENV, "--class-pattern", "Nothing")[0], 1)

    def test_exact_class_requires_one_manifest_class_bh1_corrective(self) -> None:
        """BH-1 corrective: --class (exact match) must resolve to exactly one manifest class. A partial name that could
        match multiple classes (via --class-pattern) is refused by --class, preserving the C-131 focused authorization
        exception which requires exactly one manifest class before touching the stack."""
        # --class requires the full class name and matches exactly one
        self.assertEqual(self.run_tool(MANIFEST_ENV, "--class", "TwoTests")[0], 0)
        self.assertEqual(self.run_tool(MANIFEST_ENV, "--class", "ThreeTests")[0], 0)
        # A partial name that could match multiple classes via --class-pattern is refused by --class
        self.assertEqual(self.run_tool(MANIFEST_ENV, "--class", "Tests")[0], 1)
        # Even a unique substring is refused by --class (exact match semantics)
        self.assertEqual(self.run_tool(MANIFEST_ENV, "--class", "Two")[0], 1)
        # --class-pattern with a substring that matches multiple groups still succeeds (merges their environments)
        code, out = self.run_tool(MANIFEST_ENV, "--class-pattern", "Tests")
        self.assertEqual(code, 0)
        # The shortest interval wins when multiple groups are matched
        self.assertIn("10", out)

    def test_an_invalid_manifest_is_refused_by_the_command(self) -> None:
        bad = {**MANIFEST_ENV, "groups": [{**MANIFEST_ENV["groups"][1], "stackEnvironment": {"IX1_SEARCH_SWEEP_INTERVAL_SECONDS": "0"}}]}
        self.assertEqual(self.run_tool(bad, "--groups", "all")[0], 1)


class RealManifestTests(unittest.TestCase):
    def test_every_test_class_source_file_is_assigned(self) -> None:
        """Offline guard (no build needed): each `[Collection]` class in Tests/ is named by exactly one group."""
        manifest = sbg.load_manifest(sbg.DEFAULT_MANIFEST)
        assigned = Counter(c for g in manifest["groups"] for c in g["classes"])
        self.assertEqual([c for c, n in assigned.items() if n > 1], [])
        tests = sbg.DEFAULT_MANIFEST.parent / "Tests"
        # A partial class may span files named <Class>.<Part>.cs (for example SearchIndexFoundationTests.Paired.cs): the class is the
        # part of the file name before the first dot.
        in_source = {p.name.split(".")[0] for p in tests.glob("*.cs")}
        self.assertEqual(sorted(in_source - set(assigned)), [], "test class files that belong to no group")
        self.assertEqual(sorted(set(assigned) - in_source), [], "manifest classes with no source file")

    def test_classes_that_wait_for_search_index_sweep_passes_declare_the_short_interval(self) -> None:
        """BH-1 (C-193): a test class that waits for Authority search-index sweep passes (S-A/S-B) must run on a stack with the
        10 s interval; the overlay default is 300 s. Detected from the test sources so that a new sweep test cannot be added to a
        quiet group unnoticed."""
        import re
        manifest = sbg.load_manifest(sbg.DEFAULT_MANIFEST)
        owner = {c: g for g in manifest["groups"] for c in g["classes"]}
        pattern = re.compile(r"\bS-[AB]\b|[Ss]earch-index sweep|sweep cycles|SearchIndexSweep|one S-A interval")
        tests = sbg.DEFAULT_MANIFEST.parent / "Tests"
        waiting = sorted({p.name.split(".")[0] for p in tests.glob("*.cs") if pattern.search(p.read_text(encoding="utf-8"))})
        self.assertIn("SearchIndexBuilderStackTests", waiting)
        for cls in waiting:
            interval = owner[cls].get("stackEnvironment", {}).get("IX1_SEARCH_SWEEP_INTERVAL_SECONDS")
            self.assertEqual(interval, "10", f"{cls} waits for sweep passes but its group '{owner[cls]['name']}' does not declare 10 s")
        self.assertEqual(sbg.check_stack_environment(manifest), [])


if __name__ == "__main__":
    unittest.main()

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


class RealManifestTests(unittest.TestCase):
    def test_every_test_class_source_file_is_assigned(self) -> None:
        """Offline guard (no build needed): each `[Collection]` class in Tests/ is named by exactly one group."""
        manifest = sbg.load_manifest(sbg.DEFAULT_MANIFEST)
        assigned = Counter(c for g in manifest["groups"] for c in g["classes"])
        self.assertEqual([c for c, n in assigned.items() if n > 1], [])
        tests = sbg.DEFAULT_MANIFEST.parent / "Tests"
        in_source = {p.stem for p in tests.glob("*.cs")}
        self.assertEqual(sorted(in_source - set(assigned)), [], "test class files that belong to no group")
        self.assertEqual(sorted(set(assigned) - in_source), [], "manifest classes with no source file")


if __name__ == "__main__":
    unittest.main()

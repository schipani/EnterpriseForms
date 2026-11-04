#!/usr/bin/env python3
"""Stage B integration sharding: group manifest, coverage accounting and result verification.

The .NET suite is split into groups (tests/integration/EnterpriseForms.StageBTests/stage-b-groups.json). This tool is the
gatekeeper that makes the split safe:

  plan          discovered test list + manifest -> coverage check (every class in exactly one group, no stale/empty group)
                and the GitHub Actions matrix.
  filter        the xunit v3 `--filter-class` values for one group.
  list          the group names.
  verify-group  one group's TRX results vs the tests discovered for that group (run inside the shard job).
  verify-all    every group's TRX results vs the full discovery: all groups present, each test executed exactly once and
                passed, executed total >= the baseline. Never trusts an exit code.

The discovered list is the output of `dotnet test --project <csproj> --no-build --list-tests [-- --filter-class ...]`.
"""
from __future__ import annotations

import argparse
import json
import re
import sys
import xml.etree.ElementTree as ET
from collections import Counter
from pathlib import Path

NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
DEFAULT_MANIFEST = Path(__file__).resolve().parents[2] / "tests/integration/EnterpriseForms.StageBTests/stage-b-groups.json"


def load_manifest(path: Path) -> dict:
    return json.loads(Path(path).read_text(encoding="utf-8"))


def _identity_pattern(namespace: str) -> re.Pattern:
    return re.compile(r"(" + re.escape(namespace) + r"\.(\w+)\.\w+.*)$")


def class_of(identity: str, namespace: str) -> str:
    match = _identity_pattern(namespace).match(identity)
    return match.group(2) if match else "?"


def ids_from_listing(text: str, namespace: str) -> Counter:
    """Full test identities (class.method plus theory arguments) from `--list-tests` output.

    xunit v3 prints each theory case on its own line with its argument text, and writes the identical string as the TRX
    `testName`, so discovery and execution can be compared identity by identity (verified on the 270-test suite)."""
    pattern = _identity_pattern(namespace)
    ids: Counter = Counter()
    for line in text.splitlines():
        match = pattern.match(line.strip())
        if match:
            ids[match.group(1)] += 1
    declared = re.search(r"Discovered (\d+) tests? in assembly", text)
    if declared and int(declared.group(1)) != sum(ids.values()):
        raise ValueError(f"listing declares {declared.group(1)} tests but {sum(ids.values())} were parsed")
    return ids


def class_counts(ids: Counter, namespace: str) -> Counter:
    counts: Counter = Counter()
    for identity, n in ids.items():
        counts[class_of(identity, namespace)] += n
    return counts


def class_counts_from_listing(text: str, namespace: str) -> Counter:
    return class_counts(ids_from_listing(text, namespace), namespace)


def ids_from_trx(results_dir: Path) -> tuple[Counter, Counter, int]:
    """(executed identities, non-passed identities, TRX file count)."""
    executed: Counter = Counter()
    bad: Counter = Counter()
    files = sorted(Path(results_dir).rglob("*.trx"))
    for file in files:
        for result in ET.parse(file).getroot().iterfind("t:Results/t:UnitTestResult", NS):
            name = result.get("testName", "?")
            executed[name] += 1
            if result.get("outcome") != "Passed":
                bad[name] += 1
    return executed, bad, len(files)


def check_coverage(manifest: dict, discovered: Counter) -> list[str]:
    errors: list[str] = []
    groups = manifest["groups"]
    errors += [f"duplicate group name '{n}'" for n, c in Counter(g["name"] for g in groups).items() if c > 1]
    owner: dict[str, str] = {}
    for group in groups:
        if not group["classes"]:
            errors.append(f"group '{group['name']}' has no classes")
        for cls in group["classes"]:
            if cls in owner:
                errors.append(f"class {cls} is assigned to both '{owner[cls]}' and '{group['name']}'")
            owner[cls] = group["name"]
            if cls not in discovered:
                errors.append(f"group '{group['name']}' names class {cls}, which has no discovered tests (stale or misspelled)")
    for cls in sorted(discovered):
        if cls not in owner:
            errors.append(f"test class {cls} ({discovered[cls]} tests) belongs to no group: add it to stage-b-groups.json")
    for group in groups:
        if sum(discovered.get(c, 0) for c in group["classes"]) == 0:
            errors.append(f"group '{group['name']}' would execute zero tests")
    return errors


def group_expected(manifest: dict, ids: Counter, group: str, namespace: str) -> Counter:
    """The discovered test identities that belong to `group`."""
    match = [g for g in manifest["groups"] if g["name"] == group]
    if len(match) != 1:
        raise KeyError(f"unknown group '{group}'")
    classes = set(match[0]["classes"])
    return Counter({i: n for i, n in ids.items() if class_of(i, namespace) in classes})


def check_results(expected: Counter, results_dir: Path, label: str) -> list[str]:
    """Identity-level exact-once check: every expected test executed exactly as often as discovered (once), nothing
    unexpected ran, nothing failed. A duplicated test can therefore never mask a missing one in the same class."""
    executed, bad, files = ids_from_trx(results_dir)
    if files == 0:
        return [f"{label}: no TRX result file under {results_dir}"]
    errors: list[str] = []
    if not expected:
        errors.append(f"{label}: expects zero tests")
    for identity in sorted(set(expected) | set(executed)):
        want, got = expected.get(identity, 0), executed.get(identity, 0)
        if got == 0:
            errors.append(f"{label}: did not execute {identity}")
        elif want == 0:
            errors.append(f"{label}: executed unexpected test {identity}")
        elif got != want:
            errors.append(f"{label}: {identity} executed {got} times, expected {want}")
        if bad.get(identity, 0):
            errors.append(f"{label}: {identity} did not pass")
    return errors


def read_ids(path: str, namespace: str) -> Counter:
    return ids_from_listing(Path(path).read_text(encoding="utf-8", errors="replace"), namespace)


def cmd_plan(args: argparse.Namespace) -> int:
    manifest = load_manifest(args.manifest)
    ns = manifest["namespace"]
    discovered = class_counts(read_ids(args.list, ns), ns)
    errors = check_coverage(manifest, discovered)
    total = sum(discovered.values())
    if total < manifest["minimumTotalTests"]:
        errors.append(f"discovered {total} tests, below the baseline of {manifest['minimumTotalTests']}")
    for error in errors:
        print("FAIL ", error)
    if errors:
        return 1
    for g in manifest["groups"]:
        print(f"{g['name']:36s} {sum(discovered[c] for c in g['classes']):4d} tests  ({len(g['classes'])} classes)")
    print(f"PASS  {total} tests in {len(manifest['groups'])} groups, each class in exactly one group")
    if args.matrix_out:
        matrix = {"include": [{"group": g["name"], "title": g["title"]} for g in manifest["groups"]]}
        Path(args.matrix_out).write_text(json.dumps(matrix), encoding="utf-8")
    return 0


def cmd_filter(args: argparse.Namespace) -> int:
    manifest = load_manifest(args.manifest)
    match = [g for g in manifest["groups"] if g["name"] == args.group]
    if len(match) != 1:
        print(f"unknown group '{args.group}'; groups: {', '.join(g['name'] for g in manifest['groups'])}", file=sys.stderr)
        return 1
    print(" ".join(f"{manifest['namespace']}.{c}" for c in match[0]["classes"]))
    return 0


def cmd_list(args: argparse.Namespace) -> int:
    for g in load_manifest(args.manifest)["groups"]:
        print(f"{g['name']:36s} {g['title']}")
    return 0


def cmd_verify_group(args: argparse.Namespace) -> int:
    manifest = load_manifest(args.manifest)
    ns = manifest["namespace"]
    # The shard's own listing was produced with the group's filter: it must equal the group's slice of the manifest.
    listed_ids = read_ids(args.list, ns)
    listed = class_counts(listed_ids, ns)
    wanted = {c for g in manifest["groups"] if g["name"] == args.group for c in g["classes"]}
    errors = [f"{args.group}: the filter discovered {c}, which the manifest does not assign here" for c in listed if c not in wanted]
    errors += [f"{args.group}: manifest class {c} was not discovered by the filter" for c in wanted if c not in listed]
    errors += check_results(group_expected(manifest, listed_ids, args.group, ns), Path(args.results), args.group)
    for error in errors:
        print("FAIL ", error)
    print(f"{'PASS' if not errors else 'FAIL'}  group {args.group}: {sum(listed.values())} tests expected")
    return 1 if errors else 0


def cmd_verify_all(args: argparse.Namespace) -> int:
    manifest = load_manifest(args.manifest)
    ns = manifest["namespace"]
    discovered_ids = read_ids(args.list, ns)
    discovered = class_counts(discovered_ids, ns)
    errors = check_coverage(manifest, discovered)
    executed = 0
    for group in manifest["groups"]:
        # actions/download-artifact puts each shard's artifact in its own directory named <prefix><group>.
        wanted = f"{args.artifact_prefix}{group['name']}"
        found = [d for d in Path(args.results).iterdir() if d.is_dir() and d.name == wanted] if Path(args.results).is_dir() else []
        if len(found) != 1:
            errors.append(f"{group['name']}: expected exactly one result artifact directory, found {len(found)}")
            continue
        errors += check_results(group_expected(manifest, discovered_ids, group["name"], ns), found[0], group["name"])
        executed += sum(ids_from_trx(found[0])[0].values())
    floor = max(args.min_total, manifest["minimumTotalTests"])
    if executed < floor:
        errors.append(f"executed {executed} tests, below the baseline of {floor}")
    if executed != sum(discovered.values()):
        errors.append(f"executed {executed} tests but {sum(discovered.values())} were discovered")
    for error in errors:
        print("FAIL ", error)
    print(f"{'PASS' if not errors else 'FAIL'}  executed={executed} discovered={sum(discovered.values())} groups={len(manifest['groups'])}")
    return 1 if errors else 0


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--manifest", default=str(DEFAULT_MANIFEST))
    sub = parser.add_subparsers(dest="command", required=True)
    plan = sub.add_parser("plan")
    plan.add_argument("--list", required=True)
    plan.add_argument("--matrix-out")
    plan.set_defaults(fn=cmd_plan)
    flt = sub.add_parser("filter")
    flt.add_argument("group")
    flt.set_defaults(fn=cmd_filter)
    sub.add_parser("list").set_defaults(fn=cmd_list)
    vg = sub.add_parser("verify-group")
    vg.add_argument("--group", required=True)
    vg.add_argument("--list", required=True)
    vg.add_argument("--results", required=True)
    vg.set_defaults(fn=cmd_verify_group)
    va = sub.add_parser("verify-all")
    va.add_argument("--list", required=True)
    va.add_argument("--results", required=True)
    va.add_argument("--min-total", type=int, default=0)
    va.add_argument("--artifact-prefix", default="stage-b-evidence-")
    va.set_defaults(fn=cmd_verify_all)
    args = parser.parse_args(argv)
    return args.fn(args)


if __name__ == "__main__":
    sys.exit(main())

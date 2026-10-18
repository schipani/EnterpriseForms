#!/usr/bin/env python3
"""Read-only verification of BUILD-00 repository protection (docs/delivery/BUILD00_REPOSITORY_PROTECTION.md).

Queries the GitHub REST API with a token from GITHUB_TOKEN (or the git credential helper) and prints PASS/FAIL per
control. It changes nothing. Exit code 1 if any mandatory control fails so it can run as a CI/evidence step.
Usage: python scripts/governance/verify_repository_protection.py [owner/repo]
"""
from __future__ import annotations

import json
import os
import subprocess
import sys
import urllib.error
import urllib.request

REPO = sys.argv[1] if len(sys.argv) > 1 else "schipani/EnterpriseForms"
REQUIRED_CHECKS = {
    "Specifications, manifest and integrity vectors",
    ".NET build, architecture tests, contract tests",
    "Frontend lint, typecheck, tests, build, generated client drift",
    "Docker Compose profiles validate",
}


def token() -> str:
    env = os.environ.get("GITHUB_TOKEN")
    if env:
        return env
    out = subprocess.run(["git", "credential", "fill"], input="protocol=https\nhost=github.com\n\n", capture_output=True, text=True, check=False).stdout
    for line in out.splitlines():
        if line.startswith("password="):
            return line.split("=", 1)[1]
    raise SystemExit("no GitHub token available (set GITHUB_TOKEN)")


def api(path: str):
    req = urllib.request.Request("https://api.github.com" + path, headers={"Authorization": "token " + token(), "Accept": "application/vnd.github+json"})
    try:
        with urllib.request.urlopen(req) as response:
            return response.status, json.load(response)
    except urllib.error.HTTPError as error:
        return error.code, None


def main() -> int:
    results: list[tuple[str, bool, str]] = []
    status, repo = api(f"/repos/{REPO}")
    if status != 200:
        raise SystemExit(f"cannot read repository ({status})")
    results.append(("3.1 repository is private", repo["private"] is True, f"visibility={repo.get('visibility')}"))
    results.append(("3.1 forking disabled", repo.get("allow_forking") is False, f"allow_forking={repo.get('allow_forking')}"))
    results.append(("3.1 default branch is main", repo.get("default_branch") == "main", repo.get("default_branch", "")))
    results.append(("3.2 owner is an organization (teams possible)", repo["owner"]["type"] == "Organization", f"owner type={repo['owner']['type']}"))

    status, rulesets = api(f"/repos/{REPO}/rulesets")
    active = [r for r in (rulesets or []) if r.get("enforcement") == "active"]
    results.append(("3.3 an active ruleset exists", bool(active), f"{len(active)} active ruleset(s)"))
    checks_ok = False
    bypass_ok = False
    for ruleset in active:
        status, detail = api(f"/repos/{REPO}/rulesets/{ruleset['id']}")
        if not detail:
            continue
        rules = {rule["type"]: rule for rule in detail.get("rules", [])}
        contexts = {c["context"] for c in rules.get("required_status_checks", {}).get("parameters", {}).get("required_status_checks", [])}
        if REQUIRED_CHECKS <= contexts and {"pull_request", "non_fast_forward", "deletion"} <= set(rules):
            checks_ok = True
            bypass_ok = not detail.get("bypass_actors")
    results.append(("3.3 ruleset requires PR + status checks + no force-push/deletion", checks_ok, "matches .github/rulesets/main-branch-protection.json" if checks_ok else "missing"))
    results.append(("3.3 ruleset has no bypass actors", bypass_ok, ""))

    status, protection = api(f"/repos/{REPO}/branches/main/protection")
    results.append(("3.3 legacy branch protection or ruleset present on main", status == 200 or checks_ok, f"branch protection http={status}"))

    status, environments = api(f"/repos/{REPO}/environments")
    names = {e["name"] for e in (environments or {}).get("environments", [])} if environments else set()
    wanted = {"dev", "staging", "production", "authority-staging", "authority-production"}
    results.append(("3.5 deployment environments exist", wanted <= names, f"present={sorted(names)}"))

    status, scanning = api(f"/repos/{REPO}")
    sec = (scanning or {}).get("security_and_analysis") or {}
    results.append(("3.7 secret scanning enabled", (sec.get("secret_scanning") or {}).get("status") == "enabled", json.dumps(sec)))
    results.append(("3.7 push protection enabled", (sec.get("secret_scanning_push_protection") or {}).get("status") == "enabled", ""))

    status, actions = api(f"/repos/{REPO}/actions/permissions")
    results.append(("actions enabled", bool(actions and actions.get("enabled")), json.dumps(actions)))

    failed = 0
    for name, ok, detail in results:
        failed += 0 if ok else 1
        print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}")
    print(f"\n{len(results) - failed}/{len(results)} controls verified; BUILD-00 is {'VERIFIED' if failed == 0 else 'NOT COMPLETE'}")
    return 0 if failed == 0 else 1


if __name__ == "__main__":
    sys.exit(main())

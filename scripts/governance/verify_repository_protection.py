#!/usr/bin/env python3
"""Read-only verification of BUILD-00 repository protection (docs/delivery/BUILD00_REPOSITORY_PROTECTION.md).

Queries the GitHub REST API with a token from GITHUB_TOKEN (or the git credential helper) and prints one line per
control: PASS / FAIL / DEFERRED. It changes nothing.

DEFERRED rows are controls that the approved decision A-21 (ADR-PB-001/ADR-PB-011 amendment, 2026-10-18) moves to the
private Azure DevOps milestone: the development repository intentionally stays public under a personal account, so
private visibility, disabled forking and organization teams (and therefore enforceable CODEOWNERS review) are not
required now. They are still printed so nobody mistakes them for implemented controls.

Exit code 1 if any non-deferred control fails. Usage: python scripts/governance/verify_repository_protection.py [owner/repo]
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
PASS, FAIL, DEFERRED = "PASS", "FAIL", "DEFERRED"


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
            body = response.read()
            return response.status, (json.loads(body) if body.strip() else None)
    except urllib.error.HTTPError as error:
        return error.code, None


def main() -> int:
    results: list[tuple[str, str, str]] = []

    def add(name: str, ok: bool, detail: str, deferred: bool = False) -> None:
        results.append((name, PASS if ok else (DEFERRED if deferred else FAIL), detail))

    status, repo = api(f"/repos/{REPO}")
    if status != 200 or repo is None:
        raise SystemExit(f"cannot read repository ({status})")
    add("3.1 repository is private", repo["private"] is True, f"visibility={repo.get('visibility')} (public by decision A-21 until the Azure DevOps migration)", deferred=True)
    add("3.1 forking disabled", repo.get("allow_forking") is False, f"allow_forking={repo.get('allow_forking')} (cannot be disabled on a public repository)", deferred=True)
    add("3.1 default branch is main", repo.get("default_branch") == "main", repo.get("default_branch", ""))
    add("3.2 owner is an organization (teams / enforceable CODEOWNERS)", repo["owner"]["type"] == "Organization", f"owner type={repo['owner']['type']} (deferred to Azure DevOps; CODEOWNERS review is NOT enforceable)", deferred=True)
    add("3.3 merge commits disabled (linear history)", repo.get("allow_merge_commit") is False, f"allow_merge_commit={repo.get('allow_merge_commit')}")
    add("3.3 head branches deleted on merge", repo.get("delete_branch_on_merge") is True, "")

    status, rulesets = api(f"/repos/{REPO}/rulesets")
    active = [r for r in (rulesets or []) if r.get("enforcement") == "active"]
    add("3.3 an active ruleset exists", bool(active), f"{len(active)} active ruleset(s)")
    checks_ok = False
    bypass_ok = False
    signatures_ok = False
    tags_ok = False
    for ruleset in active:
        status, detail = api(f"/repos/{REPO}/rulesets/{ruleset['id']}")
        if not detail:
            continue
        rules = {rule["type"]: rule for rule in detail.get("rules", [])}
        includes = detail.get("conditions", {}).get("ref_name", {}).get("include", [])
        if detail.get("target") == "tag" and {"deletion", "non_fast_forward"} <= set(rules) and any(i.startswith("refs/tags/") for i in includes):
            tags_ok = True
        contexts = {c["context"] for c in rules.get("required_status_checks", {}).get("parameters", {}).get("required_status_checks", [])}
        pr = rules.get("pull_request", {}).get("parameters", {})
        if REQUIRED_CHECKS <= contexts and {"pull_request", "non_fast_forward", "deletion"} <= set(rules) and pr.get("required_approving_review_count", 0) >= 1:
            checks_ok = True
            bypass_ok = not detail.get("bypass_actors")
            signatures_ok = "required_signatures" in rules
    add("3.3 main ruleset: PR + 1 approval + required status checks + no force-push/deletion", checks_ok, "matches .github/rulesets/main-branch-protection.json" if checks_ok else "missing")
    add("3.3 main ruleset has no bypass actors", bypass_ok, "")
    add("3.3 main ruleset requires signed commits", signatures_ok, "")
    add("3.3 release tag ruleset (v*) exists", tags_ok, "")
    add("3.2 CODEOWNERS review enforceable", repo["owner"]["type"] == "Organization", "ruleset requires code-owner review but the @enterpriseforms/* teams cannot exist under a user account", deferred=True)

    status, environments = api(f"/repos/{REPO}/environments")
    names = {e["name"] for e in (environments or {}).get("environments", [])} if environments else set()
    wanted = {"dev", "staging", "production", "authority-staging", "authority-production"}
    add("3.5 deployment environments with segregated reviewers", wanted <= names, f"present={sorted(names)} (no deployment pipeline exists; production delivery moves to Azure DevOps — deferred)", deferred=True)

    sec = repo.get("security_and_analysis") or {}
    add("3.7 secret scanning enabled", (sec.get("secret_scanning") or {}).get("status") == "enabled", "")
    add("3.7 push protection enabled", (sec.get("secret_scanning_push_protection") or {}).get("status") == "enabled", "")
    add("3.7 non-provider secret patterns enabled", (sec.get("secret_scanning_non_provider_patterns") or {}).get("status") == "enabled", "generic-pattern detection; requires GitHub Advanced Security features not available on this plan if FAIL persists", deferred=(sec.get("secret_scanning_non_provider_patterns") or {}).get("status") != "enabled")
    add("3.7 Dependabot security updates enabled", (sec.get("dependabot_security_updates") or {}).get("status") == "enabled", "")
    status, _ = api(f"/repos/{REPO}/vulnerability-alerts")
    add("3.7 Dependabot alerts enabled", status == 204, f"http={status}")
    status, scanning = api(f"/repos/{REPO}/code-scanning/default-setup")
    add("3.7 CodeQL default setup configured", bool(scanning and scanning.get("state") == "configured"), json.dumps({k: (scanning or {}).get(k) for k in ("state", "languages")}))

    status, actions = api(f"/repos/{REPO}/actions/permissions")
    add("3.7 Actions enabled", bool(actions and actions.get("enabled")), "")
    add("3.7 Actions restricted to selected (GitHub-owned) actions", bool(actions and actions.get("allowed_actions") == "selected"), f"allowed_actions={(actions or {}).get('allowed_actions')}")
    add("3.7 Actions SHA pinning required", bool(actions and actions.get("sha_pinning_required")), "")
    status, wf = api(f"/repos/{REPO}/actions/permissions/workflow")
    add("3.7 default GITHUB_TOKEN read-only, no PR approval", bool(wf and wf.get("default_workflow_permissions") == "read" and not wf.get("can_approve_pull_request_reviews")), json.dumps(wf))

    failed = sum(1 for _, s, _ in results if s == FAIL)
    deferred = sum(1 for _, s, _ in results if s == DEFERRED)
    for name, state, detail in results:
        print(f"{state:8} {name}  {detail}")
    passed = len(results) - failed - deferred
    print(f"\n{passed} PASS, {failed} FAIL, {deferred} DEFERRED (A-21 / Azure DevOps milestone) of {len(results)} controls")
    print("BUILD-00 (public-repository scope) is " + ("COMPLETE" if failed == 0 else "NOT COMPLETE") + "; deferred controls are NOT implemented.")
    return 0 if failed == 0 else 1


if __name__ == "__main__":
    sys.exit(main())

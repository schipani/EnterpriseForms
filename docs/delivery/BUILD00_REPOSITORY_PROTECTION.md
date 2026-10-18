# BUILD-00 — Repository Protection, CODEOWNERS and External Approvals

**Status (verified 2026-10-18 with `scripts/governance/verify_repository_protection.py`, read-only GitHub API):**
**NOT COMPLETE — 4 of 12 controls verified.** Everything expressible in the repository is committed; the remaining
items require GitHub repository/organization administration and Entra administration by a named human. Nothing in
this document claims an external protection is enabled unless the verification script prints PASS for it.

## 0. Verified live state of `github.com/schipani/EnterpriseForms` (2026-10-18)

| Control | Live state | Verdict |
|---|---|---|
| Repository visibility | **public**, forking allowed | **FAIL — violates ADR-PB-001/ADR-PB-011 ("private GitHub")**. Highest-priority administrative action. |
| Owner type | personal user account `schipani` (not an organization) | **FAIL** — GitHub teams (`@enterpriseforms/*` in CODEOWNERS) cannot exist; CODEOWNERS entries currently resolve to nothing. |
| Default branch | `main` | PASS |
| Rulesets / branch protection on `main` | none (0 rulesets, branch protection 404) | **FAIL** — force-push and deletion possible; no required reviews or status checks. |
| Deployment environments | none | **FAIL** |
| Secret scanning + push protection | enabled | PASS |
| GitHub Actions | enabled, all actions allowed, SHA pinning not required | PASS (enabled) — tighten per §3.7 |
| Dependabot security updates | disabled | FAIL (minor; alerts only) |

Re-run the verification at any time (changes nothing):

```sh
GITHUB_TOKEN=<token with repo scope> python scripts/governance/verify_repository_protection.py schipani/EnterpriseForms
```

## 1. What the repository provides (code)

| Artifact | Purpose |
|---|---|
| `.github/CODEOWNERS` | Mandatory security-owner review for the Trust Authority, integrity library, security specifications, workflows and infrastructure. **Placeholder teams** `@enterpriseforms/maintainers` / `@enterpriseforms/security-owners` — see §3.2 for the required change. |
| `.github/rulesets/main-branch-protection.json` | Importable ruleset for `main`: no deletion/force-push, signed commits, linear history, PR with 1 approval + code-owner review + last-push approval + resolved threads, strict required status checks matching the `ci.yml` job names, no bypass actors. |
| `.github/workflows/ci.yml` | PR gates: strict specification manifest, cross-language integrity vectors, .NET build/tests with `--minimum-expected-tests 100`, contract drift, frontend check, Compose validation; builds the Stage A security test project. |
| `.github/workflows/security-gate-a.yml` | Stage A gate (manual/scheduled): disposable Docker stack, SEC-P01..SEC-P12 with `--minimum-expected-tests 13`, manifest validation, evidence/log artifacts. |
| `.github/dependabot.yml`, `Directory.Packages.props`, pinned action SHAs | Reviewed, pinned dependencies (ADR-PB-011). |
| `.gitattributes` (`eol=lf`) + LF-writing generators | Deterministic digests across Windows/Linux checkouts (Phase 0 CI failure R-01). |
| `scripts/governance/verify_repository_protection.py` | Read-only evidence of the external settings; intended as a PB-12 evidence step. |

## 2. How to apply the ruleset (repository admin)

```sh
# Requires admin on the repository. Verify the JSON first, then import. Replace OWNER/REPO if the repository moves to an organization.
gh api -X POST repos/schipani/EnterpriseForms/rulesets --input .github/rulesets/main-branch-protection.json
gh api repos/schipani/EnterpriseForms/rulesets --jq '.[] | {name, enforcement}'    # expect enforcement: active
python scripts/governance/verify_repository_protection.py                           # expect the 3.3 rows to PASS
```

Required status check contexts are the CI job **names**; if a job is renamed the ruleset must be updated in the same PR.
Until the ruleset is active, `main` accepts force-pushes and deletions — treat every merge as unprotected.

## 3. Administrative actions that cannot be performed from code

Each row names the owner, the exact action, and the evidence to capture. None of these has been performed.

| # | Action (exact) | Owner | Evidence to record |
|---|---|---|---|
| 3.1 | **Make the repository private and disable forking:** Settings → General → Danger Zone → *Change visibility* → Private; Settings → General → uncheck *Allow forking*. API: `gh api -X PATCH repos/schipani/EnterpriseForms -f private=true -f allow_forking=false`. | Repository owner | `verify_repository_protection.py` rows 3.1 PASS; screenshot of visibility change timestamp. |
| 3.2 | **Create (or transfer to) a GitHub organization** so that teams exist: organization `enterpriseforms` (or chosen name) with teams `maintainers` and `security-owners`; transfer the repository or recreate it privately; then open a PR replacing the CODEOWNERS placeholders with the real team slugs. If an organization is not possible, replace the placeholders with explicit user logins of at least two people and record that team-based segregation is not available. | Owner + Security Owner | Team membership export (`gh api orgs/<org>/teams/security-owners/members`); CODEOWNERS PR URL. |
| 3.3 | Import the ruleset (§2) and keep `bypass_actors` empty. Add the same rule set for `release/*` and tag protection for `v*`. | Repository admin | `gh api repos/.../rulesets` output attached to the PB-12 register. |
| 3.4 | Require signed commits for maintainers (ruleset `required_signatures` already set); enable *vigilant mode* on each maintainer account and register GPG/SSH signing keys. | Each maintainer | Verified badge on subsequent commits. |
| 3.5 | Create environments `dev`, `staging`, `production`, `authority-staging`, `authority-production` with **required reviewers**; business deployers must not be reviewers of `authority-*` (ADR-PB-011 segregation); restrict each environment to protected branches/tags. | Repository admin + Security Owner | `gh api repos/.../environments` showing `protection_rules` per environment. |
| 3.6 | Entra workload identity federation: one app registration + federated credential per pipeline identity — `build`, `deploy-business`, `deploy-authority`, `bicep`, `migrations` — each trusting only `repo:<owner>/<repo>:environment:<name>`; separate Azure RBAC per identity (business identities get no Authority, HSM or ledger roles). | Entra administrator + Security Owner | Federated credential subjects and role assignments exported from Entra/Azure. |
| 3.7 | Actions hardening: Settings → Actions → *Allow actions created by GitHub and select non-GitHub actions*, enable *Require actions to be pinned to a full-length commit SHA*; enable Dependabot security updates; enable code scanning (CodeQL default setup). | Repository admin | `gh api repos/.../actions/permissions` shows `sha_pinning_required: true`; code scanning alerts page. |
| 3.8 | Name the independent cryptographic reviewer and record acceptance of `docs/security/INDEPENDENT_CRYPTO_REVIEW_PACKAGE.md` in the change/release system outside the business database. | Security Owner | Review record ID; reviewer identity. |
| 3.9 | Provision PB-12 evidence storage (Canadian immutable storage) and release-approver roles; archive the `security-gate-a` artifacts there. | Security Owner + Azure admin | Resource IDs, RBAC export, first archived run. |
| 3.10 | After 3.1–3.3 are complete, re-run `security-gate-a` and `ci` on the protected `main` merge commit and attach the run URLs to the Stage A gate closure. | Maintainer | Run URLs + artifact digests. |

## 4. Verification checklist (run after the human steps)

```sh
python scripts/governance/verify_repository_protection.py schipani/EnterpriseForms   # all rows PASS
gh api repos/schipani/EnterpriseForms --jq '{private: .private, allow_forking: .allow_forking, default_branch: .default_branch}'
gh api repos/schipani/EnterpriseForms/rulesets --jq '.[] | {name, enforcement}'
gh api repos/schipani/EnterpriseForms/environments --jq '.environments[] | {name, protection_rules: [.protection_rules[].type]}'
gh api repos/schipani/EnterpriseForms/actions/permissions
```

Record the outputs with the Stage A evidence (PB-12 register). They are configuration proof, not security-test proof.

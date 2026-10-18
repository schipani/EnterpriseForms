# BUILD-00 — Repository Protection, CODEOWNERS and External Approvals

**Status (2026-10-18):** Everything that can be expressed in the repository is committed. The items in §3 cannot be
established from code alone; they require GitHub organization/repository administration and Entra administration by a
named human. Until they are done, `BUILD-00` stays **Partially done** and PB-11/PB-12 evidence cannot be produced.

## 1. What the repository provides

| Artifact | Purpose |
|---|---|
| `.github/CODEOWNERS` | Mandatory security-owner review for the Trust Authority, integrity library, security specifications, workflows and infrastructure. Uses placeholder teams `@enterpriseforms/maintainers` and `@enterpriseforms/security-owners` (see §3.2). |
| `.github/rulesets/main-branch-protection.json` | Importable ruleset for `main`: no deletion/force-push, signed commits, linear history, PR with 1 approval + code-owner review + last-push approval + resolved threads, strict required status checks matching the `ci.yml` job names. |
| `.github/workflows/ci.yml` | PR gates: strict specification manifest, cross-language integrity vectors, .NET build/tests with `--minimum-expected-tests`, contract drift, frontend check, Compose validation; builds the Stage A security test project so it can never silently rot. |
| `.github/workflows/security-gate-a.yml` | Stage A gate (manual/scheduled): brings up the disposable Docker stack, runs SEC-P01..SEC-P12 with a minimum discovered-test count, validates the evidence manifest, uploads sanitized evidence/logs. |
| `.github/dependabot.yml` | Dependency update PRs for NuGet, npm and Actions. |
| `Directory.Packages.props`, pinned action SHAs | Reviewed, pinned dependency versions (ADR-PB-011). |

## 2. How to apply the ruleset

```sh
# Requires repository admin. Replace OWNER/REPO.
gh api -X POST repos/OWNER/REPO/rulesets --input .github/rulesets/main-branch-protection.json
gh api repos/OWNER/REPO/rulesets   # verify "enforcement": "active"
```

Required status check contexts are the CI job **names**; if a job is renamed the ruleset must be updated in the same PR.

## 3. Settings and approvals that cannot be established from code

| # | Item | Owner | Why code cannot do it | Evidence to record |
|---|---|---|---|---|
| 3.1 | Private repository exists at the approved organization; default branch `main`; forks disabled; wiki/issues per policy | GitHub org admin | Repository creation/visibility is an org-admin action | Repository URL, visibility screenshot/API output |
| 3.2 | Real GitHub teams `maintainers` and `security-owners` created; membership reviewed; placeholders in CODEOWNERS replaced in a PR | GitHub org admin + Security Owner | Team identity is organizational | Team membership export; CODEOWNERS PR |
| 3.3 | Ruleset §2 applied and `bypass_actors` left empty (no admin bypass) | Repo admin | Rulesets are repository settings | `gh api .../rulesets` output |
| 3.4 | Branch rule for `release/*` and tag protection for `v*` (same ruleset shape) | Repo admin | Settings | API output |
| 3.5 | GitHub Environments `dev`, `staging`, `production`, `authority-staging`, `authority-production` with **required reviewers**; business deployers excluded from the `authority-*` environments (ADR-PB-011 segregation) | Repo admin + Security Owner | Environment reviewers are settings | Environment protection rules export |
| 3.6 | Entra workload identity federation: separate app registrations/federated credentials for `build`, `deploy-business`, `deploy-authority`, `bicep`, `migrations`, each trusting only `repo:OWNER/REPO:environment:<name>` (narrow subject) | Entra admin | Cloud identity | Federated credential subjects, role assignments |
| 3.7 | Secret scanning + push protection, Dependabot alerts, code scanning (CodeQL) enabled | Repo admin | Security settings | Settings export |
| 3.8 | Commit signature verification policy (GPG/SSH/Sigstore) for maintainers; vigilant mode | Maintainers | Personal keys | Verified badge on commits |
| 3.9 | Independent cryptographic reviewer named and approved (docs/security/INDEPENDENT_CRYPTO_REVIEW_PACKAGE.md) | Security Owner | Human review | Signed review record outside BusinessDb |
| 3.10 | PB-12 evidence storage (Canadian immutable storage) and release-approver roles | Security Owner + Azure admin | Cloud resources | Resource IDs, RBAC export |

## 4. Verification checklist (run after the human steps)

```sh
gh api repos/OWNER/REPO --jq '{private: .private, default_branch: .default_branch, allow_forking: .allow_forking}'
gh api repos/OWNER/REPO/rulesets --jq '.[] | {name, enforcement}'
gh api orgs/OWNER/teams --jq '.[] | .slug' | grep -E '^(maintainers|security-owners)$'
gh api repos/OWNER/REPO/environments --jq '.environments[] | {name, protection_rules: [.protection_rules[].type]}'
```

Record the outputs with the Stage A evidence (PB-12 register) — they are configuration proof, not security-test proof.

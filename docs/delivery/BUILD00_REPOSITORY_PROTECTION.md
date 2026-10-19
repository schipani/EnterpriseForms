# BUILD-00 — Repository Protection, CODEOWNERS and External Approvals

**Governing decision (A-21, approved 2026-10-18; amends ADR-PB-001 and ADR-PB-011):** the GitHub repository
`github.com/schipani/EnterpriseForms` **intentionally remains public during development**. Private visibility is not a
prerequisite for development or for the Stage A gate. The production source-control environment will be a **private
Azure DevOps project within an organization**; that migration is a deferred infrastructure and governance milestone (§5).

**Status (verified 2026-10-18, read-only, `scripts/governance/verify_repository_protection.py`):** every protection a
public, user-owned GitHub repository supports is **applied and verified**; the controls that need an organization,
deployment pipelines or Azure DevOps are **deferred — not implemented, not validated** (§4). Nothing in this document
claims an external protection is active unless the verifier prints PASS for it.

## 1. Applied and verified on the public repository (2026-10-18)

| Control | How it is enforced | Verified |
|---|---|---|
| `main` cannot be deleted or force-pushed; linear history | Ruleset "main — Level 2 protected branch" (`.github/rulesets/main-branch-protection.json`), enforcement `active`, **no bypass actors** | PASS |
| Pull request required: 1 approval, stale reviews dismissed on push, last-push approval, all threads resolved | same ruleset | PASS |
| Required status checks (strict, up to date with `main`): the four `ci.yml` jobs | same ruleset | PASS |
| Signed commits required on `main` | same ruleset (`required_signatures`) | PASS |
| Release tags `v*` cannot be deleted, moved or updated; signed | Ruleset `.github/rulesets/release-tags-protection.json` | PASS |
| Merge commits disabled (squash/rebase only); head branches deleted on merge | repository settings | PASS |
| Actions: only GitHub-owned actions, **full-length SHA pinning required**, default `GITHUB_TOKEN` read-only, Actions cannot approve PRs | repository Actions permissions; workflows already pin `actions/*` by SHA and declare `permissions: contents: read` | PASS |
| Secret scanning + push protection | repository security settings | PASS |
| Dependabot alerts + security updates; `dependabot.yml` version updates | repository security settings | PASS |
| CodeQL code scanning (default setup) | repository code-scanning default setup (first analysis run 39123635443) | PASS (configured; first results pending) |
| Specification manifest, integrity vectors, contract drift, `--minimum-expected-tests` floors, Gate A evidence | `ci.yml`, `security-gate-a.yml` | PASS (runs green on branch head) |

Verifier output:

```text
DEFERRED 3.1 repository is private  visibility=public (public by decision A-21 until the Azure DevOps migration)
DEFERRED 3.1 forking disabled  allow_forking=True (cannot be disabled on a public repository)
PASS     3.1 default branch is main  main
DEFERRED 3.2 owner is an organization (teams / enforceable CODEOWNERS)  owner type=User (deferred to Azure DevOps; CODEOWNERS review is NOT enforceable)
PASS     3.3 merge commits disabled (linear history)  allow_merge_commit=False
PASS     3.3 head branches deleted on merge  
PASS     3.3 an active ruleset exists  2 active ruleset(s)
PASS     3.3 main ruleset: PR + 1 approval + required status checks + no force-push/deletion  matches .github/rulesets/main-branch-protection.json
PASS     3.3 main ruleset has no bypass actors  
PASS     3.3 main ruleset requires signed commits  
PASS     3.3 release tag ruleset (v*) exists  
DEFERRED 3.2 CODEOWNERS review enforceable  ruleset requires code-owner review but the @enterpriseforms/* teams cannot exist under a user account
DEFERRED 3.5 deployment environments with segregated reviewers  present=[] (no deployment pipeline exists; production delivery moves to Azure DevOps — deferred)
PASS     3.7 secret scanning enabled  
PASS     3.7 push protection enabled  
DEFERRED 3.7 non-provider secret patterns enabled  generic-pattern detection; requires GitHub Advanced Security features not available on this plan if FAIL persists
PASS     3.7 Dependabot security updates enabled  
PASS     3.7 Dependabot alerts enabled  http=204
PASS     3.7 CodeQL default setup configured  {"state": "configured", "languages": ["actions", "csharp", "javascript", "javascript-typescript", "python", "typescript"]}
PASS     3.7 Actions enabled  
PASS     3.7 Actions restricted to selected (GitHub-owned) actions  allowed_actions=selected
PASS     3.7 Actions SHA pinning required  
PASS     3.7 default GITHUB_TOKEN read-only, no PR approval  {"default_workflow_permissions": "read", "can_approve_pull_request_reviews": false}

17 PASS, 0 FAIL, 6 DEFERRED (A-21 / Azure DevOps milestone) of 23 controls
BUILD-00 (public-repository scope) is COMPLETE; deferred controls are NOT implemented.
```

### Consequences to be aware of

- With one human contributor, the 1-approval rule means **no pull request can be merged by its author alone**, including
  PR #9. That is the intended control; a second maintainer account (or the Azure DevOps migration) is the remedy, not a
  bypass actor.
- `required_signatures` applies to commits reaching `main`. Branch commits made so far are unsigned; a **squash merge
  through the GitHub UI** produces a GitHub-signed commit and satisfies the rule. Maintainers should register signing
  keys and enable vigilant mode (§6, item 3.4) so that rebase merges also pass.
- `require_code_owner_review` is enabled in the ruleset, but **no code owner can resolve** (§4.1); the rule is
  therefore inert today and must not be reported as an enforced security-owner review.
- Non-provider secret-scanning patterns stayed `disabled` after the API request (plan-dependent feature); it is listed
  as DEFERRED, not as enabled.

## 2. Secrets, credentials and confidential configuration

Rule (ADR-PB-001 consequences, hardened by A-21): no real secret, production credential, signing key, certificate,
customer data or confidential infrastructure configuration may enter the repository **or its history**.

| Measure | State |
|---|---|
| Full history scan (every blob reachable from every ref; 487 blobs; patterns for private keys, cloud/API tokens, Vault tokens, storage account keys, SAS, JWTs, connection-string passwords, public IPs, personal-data markers) | **Clean** on 2026-10-18. Only `${VAR}` placeholders, labelled `*-dev-only-change-me` values, the published Azurite well-known key and regex false positives in test code. |
| Secret scanning with push protection | Enabled (provider patterns). |
| `.env` and local credential files | Git-ignored (`infrastructure/docker/.env`); `env.example` holds synthetic values only. |
| Test identity keys, HS256 shared secrets, Vault dev root token | Synthetic, environment-gated (refused outside Development/Testing), documented in `docs/STAGE_A_CHANGE_REVIEW.md` §2. |
| Evidence artifacts | Synthetic data only; image fingerprints and revision numbers, no customer data (ADR-PB-011 control 6). |
| Incident rule | Any real secret found in history is a Critical finding: rotate first, then rewrite history, then re-run the scan and record it in the PB-12 register. |

## 3. What the repository provides (code)

| Artifact | Purpose |
|---|---|
| `.github/rulesets/main-branch-protection.json`, `.github/rulesets/release-tags-protection.json` | The rulesets applied in §1 (re-importable; keep in sync with job names). |
| `.github/CODEOWNERS` | Documents intended security-owner review scopes. **Not enforceable today** (§4.1). |
| `.github/workflows/ci.yml`, `security-gate-a.yml` | Required checks and the Stage A gate (`--minimum-expected-tests`, evidence artifacts). |
| `.github/dependabot.yml`, `Directory.Packages.props`, pinned action SHAs | Reviewed, pinned dependencies. |
| `.gitattributes` | LF normalization for deterministic digests; `docs/evidence/** -text`. |
| `scripts/governance/verify_repository_protection.py` | Read-only evidence of the live settings (PASS / FAIL / DEFERRED); exit 1 on any non-deferred FAIL. |

## 4. Controls that cannot be enforced yet (deferred — NOT implemented, NOT validated)

| # | Control | Why it cannot be enforced now | Where it will be enforced |
|---|---|---|---|
| 4.1 | **Independently authorized CODEOWNERS / security-owner review** of `src/authority`, `src/shared/EnterpriseForms.Integrity`, `trust_authority`, `security_hardening`, `security_validation`, `.github`, `infrastructure` | The repository is owned by a personal account; GitHub teams (`@enterpriseforms/*`) cannot exist, so the entries resolve to nobody and `require_code_owner_review` is inert | Azure DevOps required reviewers per path + branch policies; until then security-owner review is a **manual, recorded** step on each PR (reviewer named in the PR) |
| 4.2 | Segregation of business vs. Authority deployment approvers; protected environments | No deployment pipeline, identities or environments exist; production delivery moves to Azure DevOps | Azure DevOps environments with approvals and separate service connections (ADR-PB-011 controls 1, 2, 8) |
| 4.3 | Entra workload-identity federation for pipelines | No pipelines yet | Azure DevOps workload identity federation per pipeline identity |
| 4.4 | Private visibility; forking disabled | Decision A-21: public during development; forking cannot be disabled on a public repository | Private Azure DevOps project |
| 4.5 | Organization-level audit log of repository administration, SSO, IP allow lists | User-owned repository has none | Azure DevOps organization policies and audit stream |
| 4.6 | SBOM, image signing/attestation, digest-only promotion | No registry or build pipeline yet (BUILD-05) | Azure DevOps + ACR + Notation/attestations (ADR-PB-011 controls 5, 8) |
| 4.7 | PB-12 immutable Canadian evidence storage | Not provisioned | Azure (PB-12) |
| 4.8 | Non-provider secret-scanning patterns | Not available on the current plan | Azure DevOps secret detection in pipeline gates |

Because the repository is public, forks and pull requests from strangers are possible. `pull_request` events from forks
receive no secrets, the default token is read-only, and `security-gate-a` runs only on `workflow_dispatch`/schedule on
this repository; nothing privileged is reachable from a fork.

## 5. Deferred milestone — migration to a private Azure DevOps project (BUILD-00-ADO)

Recorded as an infrastructure and governance milestone (not part of Stage A or Stage B; a Level 2 production prerequisite):

1. Create the Azure DevOps organization/project (Canadian region per ADR-PB-010 where offered), import the repository
   with full history, re-run the history secret scan after import.
2. Branch policies on `main`/`release/*`: minimum reviewers (author cannot approve), **required reviewers per path**
   (replaces CODEOWNERS; security owners for the paths in 4.1), build validation = the `ci` jobs, comment resolution,
   linear history, signed-commit policy where supported.
3. Pipelines with workload identity federation to Entra; separate service connections and environments for
   business vs. Authority deployments with distinct approvers (ADR-PB-011).
4. Security scanning: secret detection, dependency scanning, SAST (CodeQL or equivalent) in the pipeline gates.
5. Write and run the Azure DevOps equivalent of `verify_repository_protection.py`; record the output in the PB-12
   register. Only then may §4 rows be marked implemented.
6. Archive the GitHub repository read-only after the cut-over.

## 6. Remaining human actions on the current repository

| # | Action | Owner | Evidence |
|---|---|---|---|
| 3.4 | Register GPG/SSH signing keys, enable vigilant mode; sign future commits so rebase merges pass `required_signatures` | each maintainer | verified badge on commits |
| 3.8 | Name and engage the independent cryptographic reviewer; record acceptance of `docs/security/INDEPENDENT_CRYPTO_REVIEW_PACKAGE.md` outside the repository — **still a mandatory Stage A completion requirement** | Security Owner | review record id |
| 3.11 | Add a second maintainer account so pull requests can receive the required approval without weakening the ruleset; record the manual security-owner review on each PR touching 4.1 paths | Repository owner | PR review history |
| 3.12 | Triage the first CodeQL and Dependabot results per ADR-PB-011 control 4 severity policy | Maintainer | alerts page |

## 7. Verification

```sh
python scripts/governance/verify_repository_protection.py schipani/EnterpriseForms   # 0 FAIL expected; DEFERRED rows are not implemented
gh api repos/schipani/EnterpriseForms/rulesets --jq '.[] | {name, enforcement}'
gh api repos/schipani/EnterpriseForms/rules/branches/main --jq '[.[].type]'
gh api repos/schipani/EnterpriseForms/actions/permissions
```

Record the outputs with the Stage A evidence (PB-12 register). They are configuration proof, not security-test proof.

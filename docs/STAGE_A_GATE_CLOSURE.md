# Stage A Gate Closure — Level 2 Security Foundation

**Date:** 2026-10-18 · **Branch:** `stage-a/level2-security-foundation` · **PR:** #9 · **Code commit under evidence:**
`e6df484b2ee35ed822cd011a06f41b3615ec10c8` (commits after it are documentation/manifest only; see §8).

## Gate result: **BLOCKED**

Stage A is implemented, reproducibly tested in a clean checkout and on a GitHub-hosted runner, and all twelve mandatory
adversarial tests pass against the committed code. The gate stays **BLOCKED** because three acceptance criteria are not
satisfied: the independent cryptographic review has not been performed (mandatory), independently authorized
CODEOWNERS review is not enforceable on the public user-owned repository (deferred to the Azure DevOps milestone by
decision A-21; the repository intentionally stays public during development), and the Azure Level B validation has not
started. Nothing in this
document waives a mandatory control, and Stage B has not begun.

## 1. Requirement-by-requirement assessment

| # | Stage A requirement | State | Evidence | Remaining action (owner) |
|---|---|---|---|---|
| 1 | Independent Authorization Authority: own DB, SpiceDB graph, authenticated service-to-service calls, least-privilege identities | **PASS (Level A)** | `src/authority/**`; SEC-P01–P03, P06, P07, P09, P11; `authority-migrate` log (runtime role without DDL/DELETE) | Level B: Entra workload identities, mTLS, separate Azure PostgreSQL servers (PB-05/PB-08) |
| 2 | Real Vault Transit provider behind `IIntegrityMacProvider`, signed-column registry, canonical serialization, F-03, key versioning, reproducible vectors | **PASS (Level A)** | `VaultTransitHmacProvider`; `SignedColumnRegistryTests`; `stage-a-vault-transit-vectors.json` (PASS, 7 cases + tamper); A-16 | None for Level A; Managed HSM provider is requirement 10/Level B |
| 3 | Authoritative security commands, durable recoverable journal, revision barriers, idempotency, reconciliation, fail-closed on lag/outage | **PASS (Level A)** with defect fixed | SEC-P06–P09, P12; `ArchiveProcessorTests`; A-F12 fix in `e6df484` | CONS-01..30 full matrix (post-Stage A) |
| 4 | Signed row integrity + Merkle manifests verified against independent content revisions; forged/modified/deleted/inserted/replayed rows rejected | **PASS (Level A)** | SEC-P04, P05, P10; `IntegrityBindingTests`; `tests/integrity` | MRK-01..32 full matrix |
| 5 | Minimal authenticated create/read/update workflow, no Accused UI | **PASS** | `src/backend/**`, contract tests, SEC-P01 steps | — |
| 6 | SEC-P01..SEC-P12 implemented and executed in Docker with real PostgreSQL, SpiceDB, Vault Transit | **PASS** | §3 (two runs on `e6df484`, 12/12 each) | — |
| 7 | Cross-tenant isolation, unauthorized signing prevention, revocation, damaged audit evidence, service interruption, partial-commit recovery verified | **PASS (Level A)** | SEC-P11 (tenant/network/signing), P06 (revocation), P12 + A-F12 (archive evidence), P07/P08 (interruption), P09 (partial commit) | — |
| 8 | Zero discovered tests = failed run; Windows path-length reliability | **PASS** | `--minimum-expected-tests 10` per executable + `assert_test_totals.py` (≥130/4 files); Gate A `13`; A-19 | — |
| 9 | BUILD-00 repository protection / CODEOWNERS advanced; what cannot be done from code documented | **PASS for the public-repository scope; deferred controls OPEN** | `verify_repository_protection.py` 2026-10-18 (after A-21): 17 PASS, 0 FAIL, 6 DEFERRED. Applied: rulesets on `main` and `v*`, required checks, PR controls, signed commits, restricted + SHA-pinned Actions, secret/dependency/code scanning. DEFERRED, not implemented: private visibility, organization teams and therefore enforceable CODEOWNERS review, segregated environments, Entra federation | §5; Azure DevOps milestone (`BUILD00_REPOSITORY_PROTECTION.md` §5) |
| 10 | F-05 kept open as Azure Level B; Docker isolation not Level 2 proof | **PENDING (Azure Level B)** | `docs/PREBUILD_OPEN_ITEMS.md`; SEC-P11 is Level A only | Azure staging validation (Managed HSM, Confidential Ledger, private networking, F-05) |
| 11 | Independent cryptographic review package; self-generated vectors do not count; Stage A BLOCKED until reviewed | **BLOCKED** | `docs/security/INDEPENDENT_CRYPTO_REVIEW_PACKAGE.md` (exact SHA, digests, vectors, R1–R9, blank §7) | Security Owner engages a reviewer independent of authorship; record outside BusinessDb |
| 12 | Evidence tied to tests, commits, image digests, config versions (ADR-PB-012) | **PASS (provisional storage)** | `docs/evidence/stage-a/2026-10-18-github-run-39121925152`, `…/2026-10-18-local-e6df484`; each `SEC-P*.json` carries `commit_sha` and `image_fingerprint` | PB-12 immutable Canadian evidence storage not provisioned (BUILD-00 3.9) |

Legend: PASS = demonstrated for the Stage A (Level A, Docker) scope; BLOCKED = acceptance criterion unmet; PENDING =
deferred by design to Azure Level B. "Level A" never implies Level 2 production assurance (ADR-PB-003).

## 2. Gate-closure work items (this authorization)

| Item | Result |
|---|---|
| 1 Change-set review | Done — `docs/STAGE_A_CHANGE_REVIEW.md`: no out-of-scope change, no secret, every dev shortcut guarded by environment, missing-test gaps listed. |
| 2 Dedicated branch + PR | Done — `stage-a/level2-security-foundation`, PR #9, two pushes, no force-push, no bypass (there is no protection to bypass; see item 4). |
| 3 Evidence on the exact committed SHA and image digests; clean CI checkout rerun | Done for `e6df484` — §3. The first clean-worktree rerun on `5e56238` **failed SEC-P12** and exposed A-F12; evidence is not carried over from `5e56238`. |
| 4 BUILD-00 | Decision A-21 applied: public GitHub during development, private Azure DevOps for production (deferred milestone). Rulesets, required checks, PR controls, signed commits, Actions restrictions and scanning **applied and verified read-only**; deferred controls listed explicitly as not implemented (`docs/delivery/BUILD00_REPOSITORY_PROTECTION.md` §1, §4, §5). Full history scanned for secrets: clean. |
| 5 Review package | Enriched — exact SHA + per-file digests, real Vault vectors, canonical rules, membership vs completeness, replay/recovery assumptions, R1–R9. |
| 6 No self-approval | §7 of the package is blank; no reviewer identity is recorded anywhere. |
| 7 Findings table | §4 below (severity, impact, owner, release gate, remediation) — supersedes `STAGE_A_STATUS.md` §4. |
| 8 Evidence reconciliation | §3 below — every mandatory test reconciled against both `e6df484` runs. |
| 9 Azure limitations | §6 — F-05, Managed HSM, Confidential Ledger kept as staging/production validation requirements. |
| 10 This document | — |

## 3. Evidence reconciliation (requirement 6/8/12)

Runs considered. Only runs against a committed SHA with a validated manifest are admissible.

| Run | Commit | Where | Result | Admissible | Location |
|---|---|---|---|---|---|
| R0 | `a9c0e74` + 92 uncommitted paths | local | 13/13 | **No** (uncommitted tree) | `docs/evidence/stage-a/2026-10-18-local-a9c0e74-provisional/` (historical record) |
| R1 | `5e56238` | GitHub `security-gate-a` run 39120999914 | 13/13 | **Superseded** — passed, but the archive read-back path was not reached (0 blob-level 409s in its Azurite log); A-F12 was latent | GitHub artifact only (not copied) |
| R2 | `5e56238` | local clean worktree `/c/t/gate` | **12/13 — SEC-P12 FAIL** (`projection_gate_state":"Quarantined"`, reason `ARCHIVE_BODY_MISMATCH`; 83/83 journal rows' `jsonb` bodies ≠ digest) | No (failed) | Artifacts not retained (overwritten by R4); failure detail reproduced in §4 A-F12 |
| R3 | `e6df484` | GitHub `security-gate-a` run 39121925152 | **13/13 PASS**, manifest PASS | **Yes** | `docs/evidence/stage-a/2026-10-18-github-run-39121925152/` (artifact zip sha256 `f8dadea65121773225514e5c1b240cbfd5f81c669b89e34ae740e3fe6a6d3406`) |
| R4 | `e6df484` | local clean worktree `/c/t/gate` (fresh images, `down -v` before/after) | **13/13 PASS** (2 min 05 s), manifest PASS, 138/138 unit, contract drift 0 | **Yes** | `docs/evidence/stage-a/2026-10-18-local-e6df484/` |

Image fingerprints: R3 `sha256:053f48c5…1caf` (api `6afbcd41…`, authority `37b1e243…`); R4 `sha256:02a6ab48…d46d`
(api `d8e44f76…`, authority `6cd2e344…`). The two differ because images were built on different hosts; each
`SEC-P*.json` records the fingerprint of the images it ran against. Reproducible image digests are a PB-11 deliverable.

Per-test reconciliation on `e6df484` (outcomes identical in R3 and R4):

| Test | R3 | R4 | Authority revision at end (R3/R4) | Control(s) |
|---|---|---|---|---|
| SEC-P01 forged owner | PASS | PASS | 3/3 | SEC-01, SIM-07 |
| SEC-P02 forged share rows | PASS | PASS | 4/4 | SEC-02 |
| SEC-P03 forged team membership | PASS | PASS | 7/7 | SEC-02 |
| SEC-P04 modified signed value / flipped tag | PASS | PASS | 3/3 | SEC-06, SIM-35 |
| SEC-P05 replayed / deleted rows | PASS | PASS | 3/3 | SEC-06, SIM-36 |
| SEC-P06 revocation barrier, cached token | PASS | PASS | 5/5 | SIM-07, SIM-30 |
| SEC-P07 SpiceDB outage gate | PASS | PASS | 4/4 | G02 |
| SEC-P08 Vault outage + idempotent retry | PASS | PASS | 4/4 | G04, SIM-06 |
| SEC-P09 crash after journal / after projection write | PASS | PASS | 5/5 | G02, G14, SIM-06 |
| SEC-P10 BusinessDb dump/restore | PASS | PASS | 5/5 | SEC-06, G14 |
| SEC-P11 network/tenant/signing boundaries | PASS | PASS | 3/3 | G08, A9 |
| SEC-P12 archive outage | PASS | PASS | 4/4 | G11, SIM-04 |

Known coverage gap stated honestly: in R3 and R4 the Azurite **blob** conflict path (`PUT` 409 → read-back → digest
compare) was not reached (the 409s in their logs are container `CreateIfNotExists` calls); it was reached only in R2,
where it correctly detected the mismatch. The digest-binding and conflict behaviour of `ArchiveProcessor` is covered
deterministically by `ArchiveProcessorTests` (in-memory archive); the `BlobSecurityEventArchive` read-back code is
exercised only by the timing-dependent Docker path. A deterministic Docker test for it is listed as A-F16.

## 4. Unresolved findings (requirement 7 of the closure request)

Severity: Critical = blocks any Stage A claim; High = blocks Level 2 production; Medium = must be closed before Azure
staging sign-off; Low = tracked. Gate column: SA = Stage A gate, LB = Azure Level B staging gate, L2 = Level 2 production.

| ID | Finding | Severity | Security impact | Owner | Gate | Remediation / status |
|---|---|---|---|---|---|---|
| A-F1 | Independent cryptographic review not performed | **Critical** | Format/protocol flaws (injectivity, replay, chain) would go undetected; author review is not independent | Security Owner | SA | Engage reviewer; record in release system; `INDEPENDENT_CRYPTO_REVIEW_PACKAGE.md` §7 |
| A-F2 | Evidence commit binding | Closed for `e6df484` | — | Maintainer | SA | R3 + R4 bound to `e6df484`; A-20 governs doc-only follow-ups |
| A-F3 | BUILD-00 | Closed for the public-repository scope (A-21) | Remaining: deferred controls in `BUILD00_REPOSITORY_PROTECTION.md` §4 (not implemented) | Repository owner + Security Owner | Azure DevOps milestone | Rulesets, checks, PR controls, signatures, Actions hardening, scanning applied and verified; 0 FAIL |
| A-F4 | Managed HSM provider and Confidential Ledger anchoring not implemented; Azurite archive is a test double | High | No production signer; no externally anchored anti-rollback receipts | Authority team | LB/L2 | Production refuses non-HSM providers (fails closed); implement G04/G11 providers before staging |
| A-F5 | SIG-11 same-key Vault vector | Closed | — | — | — | `stage-a-vault-transit-vectors.json` PASS (real Vault import, 7 cases + tamper) |
| A-F6 | CONS/MRK/SIG/PG/DR matrices not executed beyond the 12-test core | High | Residual protocol/recovery defects outside the tested paths | Authority team | L2 | Phase gates after Stage A; A-F12 shows the core alone is insufficient |
| A-F7 | Plain HTTP between planes; TestIdentityProvider instead of Entra | High | No transport authentication/confidentiality; test credentials | Platform team | LB | PB-05 mTLS/workload identity, PB-08 BFF; startup guards refuse test provider outside Dev/Test |
| A-F8 | O(n) edge loading per command; backward scan for content journal | Low | Performance only | Authority team | L2 (PB-09) | Index/benchmark |
| A-F9 | Business `audit_events` projection not implemented | Medium | Authority journal is the only durable security evidence | Audit feature owner | L2 | Phase 1C |
| A-F10 | F-05 Docker cross-bridge reachability | Medium | Docker isolation is not a Level 2 boundary | Platform team | LB | Azure private networking validation |
| A-F11 | `libgssapi_krb5` warning in chiseled images | Low | Cosmetic | Platform team | — | PB-11 image hardening |
| **A-F12** | **Journal body stored as `jsonb`**: PostgreSQL normalized the stored body so every archived body differed from `journal_event_digest`; detected when the Azurite conflict path compared bytes (R2) | **High (fixed in `e6df484`)** | Archived "replayable" evidence did not match its chained digest; replay/receipt verification would have failed; archive stored non-canonical bytes silently | Authority team | SA | Column is `text` (migration `JournalEventBodyCanonicalText`); `ArchiveProcessor` verifies `SHA-256(body)==digest` before delivery and quarantines on mismatch; `ArchiveProcessorTests`; R3/R4 PASS; DB-level check in §7 |
| A-F13 | Repository visibility is **public** | Accepted by decision A-21 (no longer a finding) | Source, CI and synthetic evidence are world-readable by design during development; no secrets or customer data may exist in history (scan clean 2026-10-18); forks receive no privileges | Repository owner | Azure DevOps milestone | ADR-PB-001/011 amended; private Azure DevOps project is the production environment |
| A-F14 | Independently authorized CODEOWNERS / security-owner review is **not enforceable** (user-owned repository, teams cannot exist; `require_code_owner_review` inert) | High (deferred control, not implemented) | PR #9 and later PRs have no machine-enforced security-owner reviewer; review is a manual recorded step | Repository owner + Security Owner | SA (manual review recorded) / Azure DevOps milestone (enforced) | Record the named security-owner review on each PR; enforce via Azure DevOps required reviewers per path |
| A-F15 | CI test floor misconfigured (`--minimum-expected-tests 100` per executable) | Closed in `e6df484` | Branch CI failed on Linux with all tests passing; a too-low floor would have hidden missing tests | Maintainer | SA | Per-executable 10 + aggregate ≥130 (A-19); Linux `ci` .NET job PASS on `e6df484` |
| A-F16 | Blob archive read-back/conflict path only reached by timing in Docker | Medium | A regression in `BlobSecurityEventArchive`'s conflict handling could pass Gate A unnoticed | Authority team | LB | Add a deterministic SEC test that pre-seeds a differing blob and expects quarantine, and one that pre-seeds identical bytes and expects Anchored |
| A-F17 | Specification manifest job failed on `e6df484` (`BUILD00_REPOSITORY_PROTECTION.md` digest) | Closed in the docs commit | None (detection working as designed) | Maintainer | SA | Manifest regenerated with the documentation commit; `ci` must be green on the branch head |
| A-F18 | Security-test harness writes evidence JSON with platform newlines, so Windows-produced evidence is CRLF; Git `eol=lf` renormalization silently broke the `evidence_sha256` binding of the a9c0e74 provisional copy | Medium | Evidence digests can stop validating after checkout, defeating ADR-PB-012 tamper detection | Maintainer | SA | `docs/evidence/** -text` in `.gitattributes` keeps bytes verbatim (applied now); make `Evidence.cs` write LF bytes with the next code change and rerun Gate A (A-20) |

No finding is waived. Critical and High findings owned by this team are either fixed or carried as explicit blockers.

## 5. BUILD-00 — state after decision A-21 (2026-10-18)

Applied and verified read-only (0 FAIL): rulesets on `main` (no deletion/force-push, linear history, signed commits,
PR with 1 approval + stale-review dismissal + last-push approval + resolved threads, strict required `ci` checks, no
bypass actors) and on `v*` tags; merge commits disabled; Actions restricted to GitHub-owned actions with mandatory SHA
pinning and read-only token; secret scanning + push protection; Dependabot alerts/updates; CodeQL default setup.
Full history scan for secrets: clean (487 blobs).

Deferred to the private Azure DevOps milestone (**not implemented, not validated**): private visibility and forking,
organization teams and therefore enforceable CODEOWNERS review, segregated deployment environments/approvers, Entra
federation, organization audit, image signing/SBOM, PB-12 evidence storage. Details: `BUILD00_REPOSITORY_PROTECTION.md`
§4 to §6. Remaining human actions on GitHub: signing keys (3.4), engage the independent reviewer (3.8), a second
maintainer for the approval rule (3.11), CodeQL/Dependabot triage (3.12).

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

## 6. Azure Level B requirements carried forward (requirement 9 of the closure request)

- **F-05** Docker Desktop cross-bridge reachability stays OPEN; Level 2 isolation is proven only with Azure private
  networking, separate PostgreSQL servers and workload identities (ADR-PB-005/010).
- **Azure Managed HSM**: `AzureManagedHsmProvider` not implemented; Production refuses to start without it (fails
  closed). Canada East is not a Managed HSM extended region; the manual security-domain recovery path (PB-10) is untested.
- **Azure Confidential Ledger**: no anchoring of journal/content checkpoint digests; receipts are not part of the
  effectiveness gate yet. The archive used in Stage A is an Azurite test double, not WORM storage.
- These are staging/production validation requirements (ADR-PB-003 Level B), not Stage A defects.

## 7. Verification log for this closure (commands actually run)

| Step | Where | Result |
|---|---|---|
| `dotnet build EnterpriseForms.All.slnx -c Release` | local, clean worktree, GitHub | succeeded, 0 warnings |
| `git diff --exit-code -- contracts/openapi` after build | local, clean worktree, GitHub | no drift |
| Solution tests `--minimum-expected-tests 10` + `assert_test_totals.py` (≥130, 4 files) | local (138/138), clean worktree (138/138), GitHub Linux (`ci` run 39121910757 .NET job PASS) | PASS |
| Gate A `--minimum-expected-tests 13` | clean worktree R4 (13/13, 2 min 05 s); GitHub R3 (13/13) | PASS |
| `validate_security_evidence.py` on R3 and R4 manifests (copied into `docs/evidence/stage-a/`) | local | PASS (shape + digests only) |
| `verify_repository_protection.py schipani/EnterpriseForms` | local, read-only | 4/12 PASS → BUILD-00 NOT COMPLETE |
| A-F12 database-level check (see below) | local | see §7.1 |
| `ci` on `e6df484` | GitHub | .NET/frontend/compose jobs PASS; manifest job FAIL on a changed doc digest (A-F17, fixed by regenerating the manifest in the documentation commit) |

### 7.1 A-F12 database-level check

Executed against a fresh stack on `e6df484` after running SEC-P12 once (not gate evidence; a direct verification that the
stored bytes now equal the digested bytes): see the appended result block at the end of this section.

```text
run: SEC-P12 only, fresh stack on e6df484 (then `down -v`)
  total: 1  failed: 0  succeeded: 1
information_schema: journal_event_body_json column_type=text
security_journal_entries: entries=6 digest_mismatches=0 anchored=6 verification_failed=0
organization_authorization_states: quarantined_orgs=0
(before the fix, on 5e56238: 83/83 entries mismatched their digest; organization quarantined with ARCHIVE_BODY_MISMATCH)
```

## 8. Commit discipline for this closure (A-20)

Evidence R3/R4 bind to `e6df484`. The documentation commit that follows contains only `docs/**`, `*.md`,
`spec-files.json`, `SHA256SUMS.txt`, `docs/dynamic-forms/control-traceability.csv`, the evidence-reference string in
`scripts/integrity/generate_control_checklist.py`, the `docs/evidence/** -text` attribute, and the manifest generator's
exclusion of `docs/evidence/` (A-F18). None of these is compiled into or configures the tested system; `git diff e6df484
<head> --stat` is recorded in the PR. Any later change outside that set requires a new Gate A run before the evidence can
be cited. The a9c0e74 provisional copy no longer validates (its bytes were renormalized before the attribute existed); it
is retained as a historical record only.

## 9. What would move the gate from BLOCKED to PASS

1. A-F1: independent review recorded with decision APPROVED (or APPROVED WITH CONDITIONS whose conditions are closed).
2. A-F14: a named security owner, independent of the author, reviews PR #9 and records the review (manual until Azure
   DevOps enforces it); the PR receives its required approval from a second maintainer account.
3. `ci` and `security-gate-a` green on the protected merge commit of PR #9, evidence archived per PB-12.
   The deferred BUILD-00 controls (§5) are tracked under the Azure DevOps milestone and are not Stage A exit criteria,
   but they remain Level 2 production prerequisites.
4. No new Critical/High finding opened by the reviewer or by the CODEOWNERS review.

Until then: **Stage A = BLOCKED. Stage B not started. No production infrastructure deployed. No control waived.**

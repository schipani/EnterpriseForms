# ADR-PB-003 — Mandatory Two-Level Adversarial Security Validation

- **Status:** Accepted
- **Decision date:** 2026-10-17
- **Approved by:** Product owner, explicit approval of recommendation for pre-build Finding PB-03
- **Depends on:** ADR-PB-001 canonical repository; ADR-PB-002 security-first Accused milestone; independent Level 2 Authorization Authority and existing SIM/CONS/MRK/SIG/DR security protocols
- **Major pre-build decision resolved:** Proving the independent security architecture (Major decision B)

## Decision

Use **two independent, mandatory security validation environments**. A written specification, passing isolated unit tests, a screenshot, a mocked authorization service, or a fabricated security-evidence record does **not** count as proof of the Level 2 threat model.

### Level A — Real local integration tests in disposable Docker infrastructure

Test against real PostgreSQL 18 instances for BusinessDb and AuthorityDb with **different credentials and administrative scopes**, real SpiceDB, Vault Transit (development signing provider), the separately running .NET Trust Authority, business API and real cryptographic/integrity code. Ledger and identity test doubles are explicitly labeled, controlled, and incapable of satisfying Azure-production requirements.

**Gate A:** The twelve mandatory `SEC-P01` through `SEC-P12` tests in `security_validation/SECURITY_PROOF_TEST_MATRIX.csv` must be implemented and reproducibly **PASS** against this integrated environment, with independently inspectable evidence and no unapproved skips. Only then may the protected Stage B Accused workflow be exposed. Non-protected scaffolding may be built in parallel. All additional relevant required SEC/SIM/CONS/MRK/SIG/PG/DR tests remain required at their corresponding phase/release gates; this short core matrix does not replace them.

- Deterministic security unit/contract tests and bounded Docker integration tests: every PR/merge to protected branches (CI).
- Destructive, multi-instance, point-in-time restore, long-running chaos and load tests: isolated nightly/scheduled or pre-release CI on disposable environments. Any mandatory Gate A test that cannot run on a PR still must have a current passing qualifying run before Gate A is approved.
- Distinguish a test intentionally inducing an outage from an unavailable/unexecuted test. `SKIP`, `BLOCKED`, `NOT_RUN`, `INCONCLUSIVE` and `FAIL` **never** count as `PASS`.
- Use synthetic data only. Do not run adversarial or destructive tests against production or shared tenant infrastructure.

### Level B — Real Azure security staging

Before **production Level 2 approval**, repeat the critical tests and expand them with the full Level 2 release matrices on a segregated Azure staging deployment using real Microsoft Entra identity controls, deployed least-privilege managed identities and networks, Azure Managed HSM, Azure Confidential Ledger, independent recovery evidence, Azure storage, and Azure-hosted PostgreSQL/SpiceDB. Local Vault Transit, emulated ledger, test identity or fake HSM proof cannot be used to attest production key custody, region/recovery guarantees, or service isolation.

**Gate B:** Required critical staging attack/recovery runs **PASS**, the full phase-appropriate Level 2 security release matrix passes, the authoritative identity/network/IaC configurations are independently reviewed, and a designated reviewer records signed release approval. Failing or missing mandated evidence blocks Level 2 production release. Risk acceptance cannot waive explicitly mandatory guarantees without a separately approved change to the governing threat model and corresponding ADR.

## Core invariants to verify

1. **Never trust business DB owner, share, team, membership, encrypted content, or audit projection rows as permission authority.** Forged or stale rows do not grant access.
2. **No unrestricted signing oracle:** authority authenticates callers, validates approved state transitions and key purpose, then signs. Application DB credentials cannot sign or administer the authority.
3. **Explicit monotonic revision barriers:** effective revocation cannot be bypassed by stale SpiceDB projections, caching, in-flight protected reads/downloads, old DB snapshots, or replayed worker mutations.
4. **Verify records completely:** independently committed field/child-link/document membership and current content head detect SQL updates, deletions, insertions and replays. A valid HMAC alone does not prove complete membership.
5. **Safety before availability:** lost independent cryptographic, journal or mandatory audit evidence causes bounded failure/quarantine, never an insecure allow or silent fallback.
6. **Recoverable evidence:** archived full security-command bodies plus trusted checkpoints support replay; ledger hashes alone cannot reconstruct state.
7. **Tests assert the external effect** (no unauthorized bytes or discovery), durable authority state, revisions, relevant logs/archive evidence, and successful recovery, not just an HTTP 403.
8. **Test cleanup:** inject faults only in isolated disposable infrastructure; contain and dispose secrets/credentials; record test environment and versions; do not store protected plaintext or tokens in artifacts.

## Test and evidence contracts

- Normative core test matrix: `security_validation/SECURITY_PROOF_TEST_MATRIX.csv`.
- Execution and environment contract: `security_validation/SECURITY_VALIDATION_RUNBOOK.md`.
- Machine-readable manifest JSON Schema: `security_validation/security-evidence.schema.json`.
- Evidence manifest verifier: `security_validation/validate_security_evidence.py`. This verifies a declared manifest and local evidence artifact checksums; **it does not execute attacks, validate authenticity of CI logs, or prove Azure guarantees**. Real running tests and trusted CI attestation are still necessary.
- Store test runner logs and concise assertions as immutable CI artifacts associated with commit, image digests, timestamps, environment/region, credentials role *names* (not secrets), expected and observed trusted revision, test ID, and reviewer. Require evidence integrity checks and an independent review for Azure production release.

## Acceptance and blockers

- Stage A: run all 12 local tests against the actual isolated service stack, attach valid evidence, qualify the exact code/image versions; record a `PASS` for each. If a required test fails, Stage B protected routes remain gated. Feature designers and mock UI may proceed offline but must not bypass authorization.
- Stage B is a feature-complete business milestone, not production certification.
- Production: re-run required tests in real staging and satisfy all remaining governance, data classification/search, privilege/DR, and privacy gates. **No production approval from local Docker evidence alone.**
- This ADR is architectural direction. The test services, CI workflows and Docker deployment are **not implemented** by writing this document; Claude must implement and execute them.

## Alternatives rejected

- Rely only on unit tests or mocked authorization provider: unable to prove distributed revision/race and privileged DB attacks.
- Delay security validation until after 69 screens: creates unsafe, difficult-to-correct coupling and an illusory secure UI.
- Substitute Vault Transit/local emulator results for Azure validation: does not attest managed identities, key custody, network separation, true ledger durability, or cloud DR.

# ADR-PB-002 — Security Foundation Followed by an Accused Management Vertical Slice

- **Status:** Accepted
- **Decision date:** 2026-10-17
- **Source:** Pre-build readiness audit PB-02; explicitly selected by the product owner.
- **Scope:** Prioritization and acceptance criteria for the first working implementation milestone of the Enterprise Forms platform.
- **Depends on:** ADR-PB-001 — canonical repository; Level 2 security standard and separately administered Authorization Authority.

## Decision

Do **not** attempt all 69 planned UI surfaces as the first implementation milestone. Implement a complete, tested, feature-owned **two-stage golden path**:

### Stage A — Executable Level 2 security foundation (must pass first)

1. Reproducible local Docker stack with separately credentialed Business PostgreSQL and Authorization PostgreSQL, SpiceDB, local Vault Transit, ASP.NET Core business API/BFF, separately deployed Authorization Authority, and test substitutes for services not locally available. Distinguish test doubles from staging/production integrations in build metadata and deployment policies.
2. Authenticated tenant/organization bootstrap and a minimal form/record lifecycle using real authoritative permission checks rather than trusting BusinessDb owner/share columns.
3. Authority-owned canonical security commands with idempotency, replayable durable journal, revocation fences, trusted security revisions, authorization decision consistency, and fail-closed handling of lag/outages.
4. End-to-end record content integrity covering signed rows and independently committed membership/record manifests, plus recovery-epoch and anti-rollback handling. Establish a real local signing flow, not an unrestricted signing API.
5. Baseline tenant isolation, privilege separation, encrypted protected content, and required canonical audit/event archiving. Integration tests exercise the actual boundaries; the ledger emulator is **not** evidence of production ledger integration.
6. Deliberate attack/failure tests: forged owner/share/team rows, direct content edits/deletes/replays, revoked principal with cached decision, delayed SpiceDB propagation, security-command crash/retry, signature issuance abuse, key/signing outage, audit archive outage, and restored stale BusinessDb state. Fail closed where proof is unavailable.
7. Produce machine-verifiable passing test evidence in CI and a list of controls not yet production-validated. Stage A permits an internal engineering proof only, **not** a declaration of Level 2 production readiness.

### Stage B — Complete Accused management business workflow (after Stage A)

1. Login, organization context and role-specific landing dashboard with independently loaded and refreshable cards.
2. Use the actual Designer workflow to define and publish an `Accused` form and a reusable child `AccusedAddress` form linked by an `AccusedAddressList` relationship. Support creating and editing multiple child records independently, with correct inheritance/discovery/share behavior.
3. Create, view, edit, save and audit an Accused record through the production-design React UI, .NET API and typed relational storage. Honor immutable form-version semantics and concurrency controls.
4. Attach multiple documents through the shared upload/document collection model. Uploads survive SPA route transitions; support progress, pause/cancel where feasible, interruption/resume policy, malware scanning, quarantine, encryption, signed metadata, authorization and safe download. Never promise continued browser transfer after all tabs close.
5. Demonstrate an authorized share, effective access verification and revocation (including one child-specific visibility case). Test another principal cannot discover unshared child records or download their files.
6. Apply an uploaded tenant logo and validated WCAG 2.2 AA color/typography choices, including black default primary buttons, accessible checkboxes, responsive layouts and keyboard operation.
7. Every delivered feature owns frontend components, backend handlers/endpoints, contracts, validation, persistence adapters, authoritative permission hooks, tests and audit behavior. Do not scaffold every non-milestone page or bypass the architecture to ship a demo.
8. Test a navigation-during-upload workflow, conflict handling, error/loading/empty states, independent widget refresh and normal recovery from offline/interrupted transfers.

## Release boundaries

- **Internal engineering demonstration:** Stage A passing locally with synthetic data, openly labeled test doubles and documented remaining risks.
- **Feature-complete first milestone:** Stage B end-to-end acceptance tests pass against the Stage A foundation; still not a production security certification.
- **Pilot:** requires separately agreed identity, data classification/search policy, real deployment boundaries, security proof and recovery gates, suitable synthetic or expressly approved pilot data.
- **Production Level 2:** requires all original mandatory Level 2, privilege, ledger, data-residency, cryptography, audit, adversarial search and DR release gates; this ADR does not waive or defer such controls for production.

## Explicit deferrals (breadth only)

All 69 PG-numbered screens remain in the product backlog and feature ownership map. Full advanced reporting, broad bulk imports, specialized admin console breadth, extensive localization, and nonessential additional dashboard variants may follow the first milestone. No mandatory privacy/authorization/integrity controls may be deferred if that would expose a protected route unsafely.

## Alternatives rejected

- **Implement all 69 screens concurrently:** excessive integration and security risk; likely to result in incomplete, weakly tested stubs.
- **Build UI before proving the authority:** unsafe because business owner/share fields may look authoritative while compromise cases remain untested.
- **Build a security-only project with no named business workflow:** delays integration feedback and prevents validating related child forms and background uploads.

## Ownership, evidence and follow-up

Track Stage A and B in the build backlog with explicit test/evidence links, feature owner and completion status. Do not claim the components are implemented until reproducible builds, migrations and tests run. PB-03 separately decides the **specific security proof criteria**; those criteria may strengthen Stage A without reversing this sequencing decision. PB-04 and PB-10 separately decide search behavior and data residency before applicable releases.

## Amendment A-23 (2026-10-18, project-owner decision)

Stage A is declared **ENGINEERING COMPLETE** and **ACCEPTED FOR CONTINUED DEVELOPMENT** by the project owner, who
personally reviewed the Stage A cryptographic implementation (recorded as an **owner review, not an independent
third-party cryptographic review**) and explicitly accepted the remaining development-stage risks. **Stage B is
AUTHORIZED TO PROCEED.** This amendment distinguishes *development acceptance* from *production Level 2 certification*:
the latter is **not granted** and still requires the independent cryptographic attestation, the Azure Managed HSM
signing provider, Azure Confidential Ledger anchoring, Azure Level B validation and enforced independent security-owner
review. None of those is waived or marked passed by this amendment. Stage A development blockers are not reopened unless
a new material security defect is discovered. Record: `docs/decisions/STAGE_A_DECISION_LOG.md` A-23,
`docs/STAGE_A_GATE_CLOSURE.md` §Gate result.

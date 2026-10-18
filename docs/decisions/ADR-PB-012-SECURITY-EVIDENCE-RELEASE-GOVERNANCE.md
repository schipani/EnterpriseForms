# ADR-PB-012 — Mandatory Security Evidence and Independent Release Approval

- **Status:** Accepted
- **Approved:** 2026-10-17 (pre-build Finding 12)
- **Implementation state:** Design requirement; evidence collector, policy evaluator and gates not yet built, configured or run
- **Related decisions:** ADR-PB-001 to ADR-PB-011
- **Binding standard:** `docs/governance/PB12_SECURITY_EVIDENCE_RELEASE_STANDARD.md`

## Context

Level 2 requires independently demonstrable protection from a malicious business database administrator. Written tests, a green generic pipeline and checksum-only manifests do not establish runtime security. The same feature may depend on a separate Trust Authority image, signer configuration, database revision, Entra claims policy, records schema and independent DR state.

## Decision

1. Maintain a **machine-readable release evidence register** mapping each binding requirement ID to owning feature, executable test ID, required stage/environment, run result, artifact digest/storage reference, Git source SHA, deployed image digests, versioned configuration/IaC, reviewer and release decision.
2. Release evidence is generated from real executions, not synthetic placeholders. `pass` requires verifiable test run metadata, artifacts, environment/configuration lineage and a validated source-to-image-to-deployment chain. `not_run`, `missing`, `failed`, `stale`, or `unverifiable` status **blocks** the release if mandatory.
3. Require an independent Security Owner sign-off for production Level 2 controls, and a separately permissioned Authority deploy approval. No self-approval of Trust Authority changes, no bypass by the business deployment workflow, and separation of developer/test executor/independent approver where required.
4. Persist evidence in approved **Canadian-region** storage with immutable retention policies, separate administrative credentials, object versioning and operational recovery. Use Azure Confidential Ledger to anchor **digests/checkpoint receipts only** where mandated; keep full evidence and queryable audit material elsewhere. **Retention duration** must be set by approved policy and legal requirements; do not invent a universal number.
5. Exceptions for **non-mandatory** findings are time-limited, ticketed, risk-owned, scoped to affected version/environment, independently approved, reviewed before expiry, and paired with compensating controls and re-test plan. **No waiver** for mandatory Level 2 authorization, integrity, cryptographic custody, anti-rollback, search-completeness, key/recovery, privilege-isolation, critical residency, or required independent evidence.
6. Production release policy must evaluate evidence against the **exact deployed image digests, migration/config versions, authority revision/epoch, test plan version and environment**. It must fail closed if evidence is stale or belongs to a different artifact.
7. Existing canonical `AuditEvents` and immutable security command journal remain the operational/forensic evidence systems. The release evidence register is **not** another runtime user-activity logging table. Do not duplicate full events in Ledger or operational telemetry.
8. Provide an internal **Release Security Dashboard** showing current posture, outstanding mandatory blockers, reviewed exceptions, artifact lineage and approval state. It is an operator view backed by policy-evaluator output, **not** an independent authorization source or a security override. Operative privileges remain scoped and independently checked.
9. CI policy evaluation must be independently protected; pull-request code from untrusted contributors must not be able to modify gate requirements, approve themselves or inject forged evidence. Protected workflows/branches/CODEOWNERS and Azure scoped identities follow ADR-PB-011.
10. Keep product design/accessibility/security tests linked to requirements, with clear "specified" vs "executed" status. A test-specification file's presence alone never counts as passing.

## Mandatory go/no-go gates

- **Stage A → B:** SEC-P01..SEC-P12 real Docker security tests, validated evidence, authorized review. No protected Accused flow until passed.
- **Staging → Production Level 2:** completed Azure staging security and tenancy/identity/Managed HSM/ledger/DR/residency tests plus app, document, verified search, supply-chain, accessibility and performance obligations; full release evidence and independently authorized security sign-off.
- **Trust Authority production rollout:** separately privileged approver and traceability of authority image, policy/migration changes, key/ledger configuration and recorded revisions. Business operators cannot silently approve or perform it.
- **Any mandatory missing/failed/stale/unverifiable evidence:** block. A build must not downgrade the environment or present unverified features as Level 2-compliant to proceed.

## Operational caveats

The schema and static validator can check shape, presence, and declared digests. They cannot by themselves prove artifacts came from a trustworthy runner or that a test executed honestly; establish cryptographic attestation and permissioned evidence ingestion in the actual CI/Azure deployment.

## Acceptance

Refer to `docs/testing/PB12_RELEASE_GOVERNANCE_ACCEPTANCE_TESTS.csv`. Validation and live execution remain outstanding.

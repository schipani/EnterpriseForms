# Level 2 Security Simplification — Release Notes

**Release type:** Architecture and implementation-contract update; no application source repository was supplied, and **no production functionality or deployment was changed**.

## User-approved objectives implemented in the specification

1. Audit: one canonical event identity and independently durable security-command journal, with queryable `AuditEvents` projection. Duplicate `SecurityAuditEvents`, `RecordShareEvents` and `OwnershipTransferEvents` writers are superseded by read-only compatibility projections after a migration/reconciliation gate. Audit capture and Azure Monitor remain required.
2. Azure Confidential Ledger: compact batched event/archive checkpoint commitments and independently verified receipts, **not** a second copy of full application event history. Durable replayable security-event bodies remain required outside the ledger.
3. Confidentiality: tenant policy sets minimum encryption classification, with unclassified free text and documents encrypted by default. Public/Internal plaintext search requires accepted policy and explicit acknowledgment that a BusinessDb reader can view it.
4. Search: V1 bounded authority-enumerated fully verified scan, plus V2 independently maintained trusted searchable-field index for scale. No completeness claim for unverified BusinessDb-only candidate search.
5. Authentication: verified fresh per-user phishing-resistant/FIDO2 step-up for sensitive actions; optional owner PIN/passphrase remains *additional*, and does not replace either sharing/record authorization or stronger mandatory assurance.
6. Integrity and benchmarks: initial production continues to require row HMAC + independently anchored complete record Merkle manifests; per-row HMAC removal is only a future security-reviewed protocol optimization. Add performance/reliability benchmarks with real target budgets.

## Keep / consolidate / defer

| Component | Decision | Risk if removed immediately |
|---|---|---|
| Independent Authorization Authority / SpiceDB / separate AuthorityDb | Keep | Critical: forged DB permissions could be accepted |
| Row HMAC plus Merkle manifest | Keep now; benchmark optional future protocol | Critical until replacement is proven against deletion/replay/tampering |
| AES-GCM and KMS | Keep | Critical confidentiality loss for protected values |
| BusinessDb RLS + least-privilege role separation | Keep | Increased exploitability of tenant/API mistakes |
| Azure Confidential Ledger | Keep with smaller payload | Independent checkpoint/anti-rollback evidence weakened without equivalent |
| Independent replayable archive | Keep | Cannot recover security changes from checkpoint hashes |
| Queryable AuditEvents | Keep as projection | Loss of practical who/what/when forensics |
| Azure Monitor, OpenTelemetry, selective pgAudit | Keep | Lost incident and privileged-operation visibility |
| SecurityAuditEvents/RecordShareEvents/OwnershipTransferEvents standalone writers | Consolidate after safe migration | Low if all source events preserved; high if dropped with missing history |
| Shared record PIN as only high-security gate | Replace its mandatory role with fresh user-specific step-up; retain optional owner passphrase | Reduced extra owner-held secret if entirely removed |
| Full copy of routine logs in Confidential Ledger | Stop after verified checkpoint pipeline | Low provided archive completeness and receipt chain tested |
| Arbitrary query completeness promise with untrusted BusinessDb | Remove the promise; build independently verifiable index | Critical false negative reporting without verified search |

## Affected artifacts

- `CLAUDE.md`: added precedence, §4T, Phase S0–S6 cross-reference and SIM-01..SIM-42 release tests. Existing normative SQL naming, FIDO2, Merkle, authority, document and related-form requirements retained.
- `security_hardening/SECURITY_SIMPLIFICATION_STANDARD.md`: authoritative six decision details and risk consequences.
- `security_hardening/SECURITY_IMPLEMENTATION_PLAN.md`: ordering, migrations, rollback and release gates.
- `security_hardening/SECURITY_ACCEPTANCE_TESTS.md`: 42 negative scenarios and evidence requirements.
- `security_hardening/SECURITY_CONTRACTS.reference.cs`: draft integration interfaces (not compiled).
- `security_hardening/audit-consolidation.reference.sql`: compatibility **view template** (not executed; no destructive DDL).
- `security_hardening/SECURITY_PERFORMANCE_MEASUREMENT.csv`: benchmark evidence template.
- `SECURITY_GAP_ASSESSMENT.md`, `SECURITY_THREAT_MODEL.md`, `ENTERPRISE_SECURITY_REDUNDANCY_AUDIT.md`: updated decision and open-status references.

## Outstanding production gates

This change specifies decisions, not a security certification. G01–G04, G06–G14 and related gaps remain open where implementations/tests are not verified. In particular, **G06 search completeness**, **G07 encrypted-data legacy migration**, **G09 strong IdP assurance**, **G11 independent evidence intake**, and **G14 archival/recovery** cannot be closed by documentation. No docker startup, C# compilation, PostgreSQL execution, Azure deployment, threat simulation or penetration test has been performed as part of this documentation update.

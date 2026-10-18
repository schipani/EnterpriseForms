# Security Simplification Standard — Level 2 Enterprise Dynamic Forms

**Decision ID:** ADR-SEC-2026-10-16-01  
**Status:** Approved *design direction*; implementations and penetration tests remain pending.  
**Threat boundary:** Attacker can read, update, insert, delete, suppress search results, and replay rows or backups in Business PostgreSQL, including DBA privileges or stolen BusinessDb credentials. The independent Authorization Authority, its PostgreSQL, journal/archive, signing identity/HSM, ledger, IdP and deployment controls are not compromised. This standard **does not** claim protection against malicious application code, authority compromise, or malicious IdP/HSM administrators.  
**Precedence:** This document resolves overlap among CLAUDE.md sections 2.5, 4C, 4D, 4H, 4J, 4L and 4M–4S. Existing Level 2 fail-closed requirements remain in force unless explicitly replaced here.

## Decision 1 — Canonical audit event ownership; consolidate duplicate audit tables

**Retain:** `AuditEvents` as a tenant-scoped *queryable projection*, `AuditFieldChanges` as optional redacted detail, `AuditEventTypes` catalog, reliable outbox, immutable independent replayable evidence archive, separate trusted Authority security-command journal, and OpenTelemetry/Azure Monitor for operational telemetry. **Consolidate:** `SecurityAuditEvents`, `RecordShareEvents`, `OwnershipTransferEvents` into views/DTO projections backed by a single canonical `AuditEventId` and authoritative security commands. No second independently mutable event history for the same occurrence. Historic values needed for business recovery are separate versioned recovery artifacts, not a second audit writer.

### Ownership and issuance

- Each material action has one stable `AuditEventId` (UUIDv7) and `OperationId`, emitted once logically and delivered at-least-once physically with idempotent de-duplication `(OriginServiceIdentifier, AuditEventId)`; **do not promise exactly-once transport**.
- Authorized security state changes: the **independent Authority journal owns the canonical semantic security event** (who requested, independently authenticated principal, tenant, target, old/new state, permission check, revision, result); a verified immutable replayable copy must be durably archived and checkpointed before a change is declared Effective. Business `AuditEvents` and SIEM entries are reconcilable projections with the same event ID. The app DB can neither originate trusted security commands nor amend them.
- Ordinary business changes: the business transaction commits its durable versioned change plus a canonical activity envelope and outbox atomically; it is externally archived and reconciled. A compromised BusinessDb can destroy unarchived entries: classify this as a residual evidence gap and do not claim full DBA-resistant audit capture for business activity until the separate trusted intake is confirmed. For high-classification accesses, use an independent audit intake acknowledgment **before protected plaintext/document bytes are released**, recording `AccessAuthorized` / `AccessStarted` and later `AccessCompleted/Failed` where observable. Do not claim the client saved or opened a completed download.
- Denials, searches, and reads cannot rely on PostgreSQL DML triggers; send through application or gateway audit pipeline to independently administered durable intake. Required-event failure policy: deny high-risk disclosures/administration when independent intake is unavailable; bounded queue for explicitly classified low-risk activity only with monitored drops *forbidden* for security-critical events.
- Technical logs retain exceptions, tracing, privileged SQL/DDL/role events via selective pgAudit; **do not emit raw SQL parameter values, access tokens, PINs, or protected field contents**. Logging and alerting must themselves be monitored.

### Event shape and chronology

Envelope: `AuditEventId`, `EventSchemaVersion`, `TenantId`, `OriginServiceIdentifier`, `ActorUserId` or a classified service identity, `EventAction`, `EventCategory`, `EventOutcome`, `EventOccurredOnUtc`, `EventReceivedOnUtc`, `OperationId`, `CorrelationIdentifier`, typed resource IDs (record/form/field/attachment/share/relationship), `AuthorityRevision` or `ContentRevision`, event classification, safe redacted event metadata, and optional verified archive receipt reference. All relational columns must use descriptive multiword snake_case; entity PKs use singular entity + `_id`.

UTC timestamps plus monotonically ordered **trusted journal positions** are the authoritative order for security commands. Business audit search sorts by time plus local sequence/UUID as a deterministic presentation order; this is **not** globally guaranteed commit order. For a combined timeline show verified source/sequence and time, and declare cross-service ordering ambiguous for concurrent events. Store no protected old/new values in cleartext audits; record identities and redaction flags instead.

### Archives and checkpoint roles

- Independent recoverable WORM-compatible archive retains complete necessary security event bodies and integrity-checked audit evidence under separately governed identities and tested restore; it is **not** just a digest collection.
- Batch contiguous archive event IDs/digests and immutable archive object digest plus previous checkpoint digest, accepted authority and content heads, key/schema versions into a compact checkpoint. Submit this to **Azure Confidential Ledger**; independently verify receipts. Do not send full access logs or PII to ledger by default.
- A security revision may not become **Effective** until the journal/event bodies are independently durable, checkpoint eligibility/receipts satisfy G02/G14, and SpiceDB meets the revision barrier. Checkpoint batching must not silently weaken revocation durability or admit unanchored revisions.
- `AuditEvents` is expendable/rebuildable from independently retained evidence. Reconciliation scans compare IDs/counts/digests by bounded range, verify archive + ledger receipts, and detect missing/duplicated/unexpected revisions. Tenant audit readers are authorized; no tenant reads another tenant's activity.

**Remove only after migration:** standalone mutable `security_audit_events`, `record_share_events`, or `ownership_transfer_events` writers. Before drop, backfill event IDs, preserve retention/legal holds, verify completeness and old API consumers, deploy read-only compatibility projections, then remove duplicate writers. No destructive schema drop in initial migration.

**Risk of simplification:** fewer redundant audit tables means loss of a secondary local forensic copy if the authoritative archive fails. Mitigation: tested independently governed archive, idempotent ingestion, health alarms, replay drills, checkpoint verification and explicit fail-closed policy. This simplification does NOT permit removing audit capture or selective PostgreSQL privileged-operation logging.

## Decision 2 — Organization data-classification and encryption floor

Security labels: `Public`, `Internal`, `Confidential`, `Restricted`, with monotonic organization minimum policies. The schema stores approved field/attachment classification as versioned form policy and accepted Authority security-policy state. Designer may strengthen, never weaken, tenant floor. **Default new free-text/customer content to `Confidential`** unless explicitly reviewed/classified otherwise; default new attachments to `Confidential` with application encryption. Organization owner may increase floors but cannot downgrade without a separately approved, versioned security migration subject to irreversible floors where configured.

| Class | Storage | Search | Audit / projection |
|---|---|---|---|
| Public | Structured plaintext allowed only where tenant floor permits | Explicit approved searchable | No secrets in audit; only permitted metadata |
| Internal | Plaintext *only* if accepted organization policy explicitly permits DBA visibility and business need | Explicit field-scoped search; don't imply DBA confidentiality | Mask/minimize relevant metadata |
| Confidential | **Application AEAD encryption required** for content, attachment bytes, sensitive filenames/metadata | Not indexed as plaintext. Optional separately reviewed exact blind index with explicit equality/frequency leakage acceptance | Redacted; only field ID/change operation |
| Restricted | Application AEAD encryption, restrictive key-release and per-user step-up; independent access evidence | No search by values by default | Most-restrictive redaction and retention |

A field classification is based on what data **actually may be entered**, not its label alone. A normal String field is not automatically safe for DBA-readable search. Existing broad typed-string search remains only for explicitly approved plaintext classes; do not silently make newly protected values searchable. On reclassification to Confidential/Restricted, require an audited migration that re-encrypts all active/history content, removes plaintext indexes/materialized projections, and reports exposure in WAL/backups/exports/caches/traces/old objects until their retention expires. Never claim erasure of data in already distributed backups without evidence.

Key provider access remains isolated; plaintext must not transit logs, archive, search snapshots, filenames/preview pipelines or browser caches unnecessarily. Authenticated encryption binds tenant/record/field/attachment/version and security policy via AAD. Encrypt metadata when sensitive; keep non-sensitive stable opaque IDs searchable. If non-encrypted fields are authorized, explicitly record the **residual direct DBA-read risk** in tenant security settings and acceptance evidence.

**Risk of adopting secure defaults:** loss of generic searches for unspecified fields, more crypto/KMS overhead and costly legacy migration. Trade-off is essential to confidentiality from hostile BusinessDb readers. Explicitly approved searchable data may remain readable to DBAs; users must not mistake this for encrypted data.

## Decision 3 — Trusted search and completeness

Default deny and field-scoped predicate authorization are distinct from result completeness. A hostile DBA can suppress candidate results from BusinessDb without forging a row HMAC. No complete-search claim can rest on verifying only returned rows.

Support two clearly labeled server-side modes:

**Mode V1: Bounded independently enumerated verified scan (mandatory safe baseline).** Obtain the authority's complete, accepted record-ID registry (and accepted content heads) for the requested tenant, form/partition and revision. Enforce `Discover` permissions and field-predicate permissions using authority state. Retrieve candidates by **explicit expected identities**; verify each against complete committed manifests, applying authorized filters to verified values. If a required row, child link or field is unavailable/tampered, return an integrity incident and fail the query/partition closed — never silently omit it. Enforce a configured maximum scan size, timeout and quota; exceeding them returns `SEARCH_VERIFICATION_LIMIT` rather than partial results falsely labeled complete. No unapproved decrypted-search over Confidential/Restricted values.

**Mode V2: Independent trusted search index (scalable target).** An independently administered search service/storage under the Authority boundary maintains a complete, revision-fenced index of *approved* searchable fields and record-to-predicate postings. Updates originate from accepted content events and verified field projections, never raw BusinessDb replay; checkpoint index revision, completeness and predicate catalog. A search binds the authority and indexed content revision, first determines authorized field predicates and discoverable record IDs, then evaluates and returns only authorized verified record projections. If index lags, block freshness-guaranteed searches or explicitly return an unavailable state; do not silently use stale results after revocation. Queries unsupported by the index use V1 within bounds, otherwise fail safely. Isolate search tenant data, protect metadata/terms against DBA-readable leakage in the authority/search domain; protect query audit inputs and execution time side channels.

Record Merkle proofs still prove each returned record's *integrity*, not query completeness. We must test forged omissions, uncommitted additions, restored index snapshots, index lag, unauthorized predicate enumeration, aggregate counts and paginated results under multiple ACL paths. The complete registry/index is independently recoverable and reconciled to authority accepted revisions. Do not claim broad, fully complete global substring/full-text search until the independently verifiable index supports that operator.

**Risk of avoiding this control:** DBA suppresses matching records and silently manipulates reports. **Cost:** higher independent storage, processing and possible disclosure of approved index fields to trusted search administrators; V1 is costly at scale, V2 is more complex.

## Decision 4 — Per-user FIDO2 step-up, optional owner passphrase

Retain federated OIDC authentication; add a separate provider-verified *fresh phishing-resistant assurance requirement* for high-risk operations: Restricted-record access, sensitive download, ownership transfer, delegation expansion, administration, and key-policy changes as per tenant policy. Do not accept self-declared WebAuthn success, a raw `amr=mfa` claim, arbitrary token refresh or a client clock as sufficient: configure and validate IdP-specific assurance signals, auth time, session binding, provider/issuer, subject, tenant, nonce, and policy requirement. Entra configuration can require phishing-resistant authentication without guaranteeing every first-screen sign-in prompt is passwordless; never overclaim. If strong assurance cannot be verified, deny the operation.

Owner-configurable record PIN/passphrase remains **optional as an additional gate** for eligible forms. For Restricted policies use per-user IdP step-up as the default mandatory mechanism, rather than a shared 4-digit team PIN. An owner cannot disable higher form/tenant mandatory step-up. If an owner sets a challenge, all non-exempt authorized owners/share recipients must satisfy it; knowledge of the passphrase alone grants zero data permissions. Store secret via Argon2id (or approved password hash), throttle and lock out by tenant/user/record/device, never log it or treat it as a raw encryption key. The authority binds unlock grants to record, individual user, device/session, scope, version, accepted ACL revision and short TTL; changes/revocations invalidate them.

Avoid automatic global replacement of existing mandatory challenge policies: migrate only with approval and equivalent-or-stronger assurance, preserving owners' declared additional factor when required. Define administrator recovery and dual-approval paths that do not become a universal step-up bypass. Verify fresh assurance per protected operation class, with maximum authentication age configurable by tenant minimum policy.

**Risk of removing shared PIN entirely:** loss of a separate owner-known secret gate and changed customer workflows. Optional retained challenge avoids regression but should not be positioned as equivalent to phishing-resistant user authentication.

## Decision 5 — HMAC per row and Merkle manifests: keep until proof

For the Level 2 rollout retain **both** versioned HMAC-SHA-256 `RowSignature` on security/content rows and independently committed complete record Merkle manifests. Current CLAUDE.md §4L and G03 remain mandatory. An HSM/KMS cryptographic adapter is **not** a raw signing oracle: the Authority validates authorized, expected-revision commands before issuing new commitments. Vault Transit Docker is development-only, Managed HSM production.

Later optimization candidate: verify accepted independent root/head once per record/revision; use domain-separated hashed canonical *persisted ciphertext* leaves (or another proven confidentiality-preserving construction) and complete trusted leaf registry to verify membership and detect altered, missing, additional or replayed rows. **Beware**: plain SHA-256 leaves of low-entropy plaintext values can leak the underlying value through offline guessing if exposed. Do not replace keyed row HMACs with exposed plaintext hashes for booleans, dates, option codes or other enumerable fields. Hash identity, tenant, immutable leaf kind, version, record/field/group references and encoded ciphertext consistently; protected root must bind all leaves, count and accepted revision. Explicit null and empty values require distinct encodings; preserve Merkle-v1 vectors when introducing v2.

Do NOT remove `RowSignature` or stop verifying it until independent review and adversarial tests demonstrate equivalent row integrity, collection completeness, anti-replay, confidentiality, partial-sharing proofs, deletion/cross-tenant/child-link handling, restore/key-rotation, and fail-closed behavior in both providers. Compare observed costs and failure rates first. The optional simplification is a future versioned protocol change, **not** an automatic schema migration or untested configuration toggle.

**Risk if prematurely removed:** field tampering or low-entropy value disclosure, incomplete proof coverage, rollback acceptance. **Risk if retained indefinitely:** storage/HSM cost and latency; batch verification and efficient provider calls are permissible without relaxing required checks.

## Decision 6 — Performance, reliability, and operational gates

Use a test matrix: small/medium/large records (10/100/1,000 typed fields), related child graphs (0/10/100 links), attachments (0/10/100 and large streams), high churn team ACLs, 100K/1M tenant records, 1/100 concurrent requests, and outages/latency in business PostgreSQL, SpiceDB, AuthorityDb, HSM, audit intake, ledger, and search index. Record p50/p95/p99 latency, HSM calls/request, authority and index CPU/memory, throughput, checkpoint lag, event size, WORM storage growth, revocation delay, failure closure correctness and recovery time. Do not invent performance pass numbers; set RTO/RPO/SLO and allowed budgets for each customer deployment before CI gating.

Optimize only after benchmark by: batching verification and permission checks; caching accepted content proofs **only** when bound to independently current revision/epoch and invalidated on revocation; ledger checkpoint batching while maintaining effective-command durability; replacing duplicate event writes by idempotent fan-out; range partitioning and keyset pagination for audits. Never cache a permission `Allow` across authority epoch/revision barriers, never skip field authorization, never emit plaintext indexes for Confidential/Restricted values. Controlled load tests shall include deliberately stale SpiceDB tokens and network partitions to prove safety under stress.

### Deployment/operational release gates

- G01/G02: independently hosted Authority, verified journal, bounded projection lag and fail-closed revocation barrier; no direct business DB grants authority.
- G03/G04/G18: full record/related-link integrity, canonical test vectors, historical key/version handling, working HSM provider and signed manifest tests.
- G06/G07: tenant/form/field-scoped trustworthy search completeness for each **claimed** query mode; explicit encrypted-field visibility, tested migration/PII redaction.
- G08: separate admin domains and production-equivalent RLS/role/network/pgaudit validation.
- G09/G10: real IdP assurance verification and revocation/long-stream controls.
- G11/G14: independent audit intake + replayable archive + ledger receipts + full restore rehearsal.
- G17: large-child/large-file resource budgets, abuse limits and service liveness.
- Record all exceptions as documented risk acceptance by an independent security reviewer; a missing cryptographic or authorization guarantee cannot be 'accepted' as equivalent to Level 2.

## Supersession map — remove redundant authority, not evidence

| Previous design text | Effective rule after this ADR |
|---|---|
| `SecurityAuditEvents` / `RecordShareEvents` / `OwnershipTransferEvents` independent persisted audit writers | Replaced by one canonical event/command ID; compatibility views/DTOs permitted; keep recovery-specific typed details in authority journal |
| `AuditEvents` as sole 'authoritative audit stream' | `AuditEvents` is the local **queryable projection**; independent event intake/archive authoritative for evidence; separate trusted security-command journal for rights reconstruction |
| Write all logs to Azure Confidential Ledger | Only ordered, batched commitments and independently verified receipts, not full routine audit/telemetry |
| Raw 'encrypted vs searchable' Designer toggle | Must respect organization classification/encryption floor; encryption + unrestricted plaintext search impossible under Level 2 DBA confidentiality |
| Searching filtered SQL rows equals completeness | Must use V1 bounded independently enumerated verification or V2 independent trusted index for guaranteed completeness |
| Shared PIN is the preferred high-security gate | Prefer individual verified fresh FIDO2 step-up; optional owner-managed passphrase may add friction, not substitute |
| Per-row HMAC can now be removed because Merkle exists | **False**; retain until domain-separated confidential leaves + complete root/registry proof and independent security review |

## References

- Local: `ENTERPRISE_SECURITY_REDUNDANCY_AUDIT.md`, `SECURITY_THREAT_MODEL.md`, `SECURITY_GAP_ASSESSMENT.md`; `trust_authority/*` security protocols; `form_model/RELATED_FORM_COLLECTIONS.md`.
- Microsoft: https://learn.microsoft.com/en-us/azure/confidential-ledger/write-transaction-receipts
- OWASP Logging: https://cheatsheetseries.owasp.org/cheatsheets/Logging_Cheat_Sheet.html
- SpiceDB consistency: https://authzed.com/docs/spicedb/concepts/consistency
- NIST 800-63B: https://pages.nist.gov/800-63-4/sp800-63b.html

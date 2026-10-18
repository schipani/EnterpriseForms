# Implementation and Migration Plan — Security Simplification ADR

**Scope:** Apply `SECURITY_SIMPLIFICATION_STANDARD.md` to the existing enterprise dynamic forms solution. This is a *design and implementation handoff*, not evidence that code has been deployed. Use the repository's actual solution and migrations; do not run reference SQL against production without review.

## Workflow, phase gates and rollbacks

### S0 — Inventory and freeze risky changes
1. Identify all current audit writes: `AuditEvents`, `SecurityAuditEvents`, `RecordShareEvents`, `OwnershipTransferEvents`, SIEM, pgAudit, ledger and archives. Diagram origin, transaction, outbox, delivery, retention, reader and RLS for each. Inventory every endpoint that renders/searches/downloads records or handles security commands.
2. Inventory existing field classifications, Standard searchable values, protected envelopes, prior plaintext backups/indexes/exports, and filenames/metadata. Measure what a BusinessDb reader can see today.
3. Enumerate all record-share/owner authority read paths and all caches. Assert the independent authority never falls back to mirrored BusinessDb security state.
4. Write migration mappings for event types and archived event IDs; record versioned protocol definitions and independently reviewed design approvals. **No table deletion, search exposure, or encryption weakening in S0.**

**Gate:** baseline inventory + threat-model matrix and replay test plan reviewed.

### S1 — One canonical audit event model (first implementation)
1. Implement a canonical event envelope, centralized writer, catalog of event types, tenant classification/redaction, `AuditEventId` idempotency and the queryable `AuditEvents` projection.
2. For security commands use AuthorityDb canonical command-journal ownership; archive verified semantic bodies before the command is Effective. Deliver derived audit projections with the *same* event ID. For ordinary business writes commit version + event + outbox transactionally; route sensitive access and denials to independently administered durable intake.
3. Preserve `AuditFieldChanges` only for safe redacted detail; **no competing security/share/ownership event writer**. Implement compatibility adapters/views and compare counts/digests during parallel-run migration. Do not give adapters ability to originate authoritative events.
4. Wire search, previews, exports, field reads, document milestone events, design publish diffs, ownership/share revocation and administrator operations into the event pipeline; include rejected operations. Enforce outage policy for required evidence.
5. Roll old reader APIs to new canonical projections. Reconcile archive and local audit; after retention/legal holds and migration sign-off, remove duplicate writers/tables in separate migrations.

**Gate:** event ID reconciliation, replay, redaction, independent audit intake failure tests SIM-01..SIM-09. **Rollback:** restore API readers/writers through compatibility layer; never accept lower audit durability or destroy archived events.

### S2 — Efficient Confidential Ledger checkpointing
1. Keep recoverable archive for full event bodies. Serialize ordered batch commitments using a versioned canonical format: range of event IDs and digests, archive object URI/digest, trusted authority and content high-water marks, previous checkpoint digest, epoch, algorithm/schema version. Exclude PII.
2. Make checkpoint anchoring a dedicated service with immutable archive and ledger receipt verification. Store receipt reference; independently reconcile ledger/archive/authority/SpiceDB/audit index.
3. Configure bounded batch size and time/revision lag based on actual revocation and DR durability guarantees; **do not declare a new security command Effective until its mandatory archival/checkpoint prerequisites hold**.
4. Remove full-event writes to ledger *only once* verified checkpoint/receipt path is proven. Never remove the standalone event archive or application audit logs.

**Gate:** ledger receipt verification, missing-body detection, pause/resume, ledger outage, rollback replay tests SIM-10..SIM-14. **Rollback:** retain old event-copy pipeline during dual validation, never break anti-rollback anchors.

### S3 — Organization minimum encryption and legacy conversion
1. Extend versioned field policies with security classification `Public/Internal/Confidential/Restricted`, origin, approved search permission, tenant minimum floor, immutable policy version and migration status. Authority controls accepted policy; BusinessDb is a projection.
2. Newly created unknown free-text/content fields default `Confidential`; documents and potentially sensitive filenames default application-encrypted. Designer can upgrade but cannot lower floor. Add disclosure for DBA-readable searchable fields.
3. Provide migration inventory, owner approval, scheduled backfill, verified AEAD round-trip, removal of plaintext indexes and safe quarantine. Review WAL, replicas, backups, old file versions, logs and exports separately; document irreducible historical exposure.
4. Do not automatically turn encrypted fields into searchable plaintext. Optional blind indexes require a separate security review, scoped disclosure of frequency/equality leakage and unique managed secret.

**Gate:** SIM-15..SIM-20 and explicit tenant policy acceptance; no sensitive plaintext in logs/indexes or archives. **Rollback:** restore a protected prior state from independently trusted records; do not silently downgrade encryption or leak recovered data.

### S4 — Trusted search correctness (critical outstanding gap G06)
1. Implement V1 complete, bounded enumeration from independently trusted record-ID set and accepted content heads. Apply predicate-authorization before execution, verify complete per-record content, fail closed on missing/tampered rows. Enforce safety ceiling returning `SEARCH_VERIFICATION_LIMIT` rather than incomplete output.
2. Implement independent V2 revision-fenced index for explicitly approved search fields. Index only committed verified content; derive posting lists and complete source inventory under the independent Authority boundary. Test delayed/out-of-order updates and rebuild from durable accepted event/record state.
3. Integrate with all discover, paging, count, facet, sort and export APIs. Never use BusinessDb unverified index as the sole evidence that no matching record exists.
4. Expose search assurance/mode and verified snapshot revision internally; avoid revealing unauthorized record presence through error messages, timing or counts.

**Gate:** SIM-21..SIM-28 and documented supported predicates, completeness scope, freshness SLAs, costs. If V2 is not implemented, permit only V1 bounded verified scan for guaranteed-complete queries; prohibit wide searches that cannot be verified.

### S5 — Identity-provider step-up and owner challenge
1. Define IdP adapters to verify trusted `Issuer`, `Subject`, `Tenant`, `AuthenticationTime`, assurance method, session binding and the minimum supported FIDO2/phishing-resistant policy. Validate provider-specific claims or introspection/assurance signals; `mfa` alone is insufficient.
2. Require fresh proof for Restricted view/decrypt/download, delegation escalation, ownership transfers, organization-security policy changes and administrator break-glass as policy demands.
3. Preserve existing owner PIN/passphrase challenge as *optional additional protection*. Do not silently remove a tenant- or form-mandatory challenge or downgrade it. Use Argon2id + rate limits; never treat passphrases as encryption keys.
4. Invalidate step-up sessions/challenge unlocks on user/team/share revocations and recovery-epoch changes; monitor IdP enforcement drift.

**Gate:** SIM-29..SIM-34 plus production-equivalent IdP integration evidence. If provider assurance cannot be proven, deny the sensitive operation.

### S6 — Benchmark and integrity-layer simplification decision
1. Keep per-row HMAC **on all required rows** and complete independently anchored Merkle manifests for initial deployment. Preserve existing canonical encoding/test vectors/rotation workflows.
2. Implement benchmark fixtures: 10/100/1,000-field records, 0/10/100 linked children, 0/10/100 attachments, 100K and 1M records, concurrent authorization and revocation, HSM latency/failure, ledger lag, audit backpressure, and trusted search.
3. Compare current per-row remote MAC verification to batched MAC verification/root authentication. Propose a **new protocol version** with canonical ciphertext leaf commitments only if formal security review establishes identical integrity, anti-replay, completeness and confidentiality against G03/G04 threat cases. Never use unsalted exposed SHA-256 of low-entropy plaintext as a substitute.
4. Capture latency p50/p95/p99, HSM requests per operation, error and denial rates, checkpoint propagation, proof sizes, memory, storage cost, search correctness and recovery RTO/RPO. Negotiate real performance and availability targets with system owners; do not invent benchmark pass numbers.
5. Canary/fail-safe protocol migration if approved, dual-verify old/new protocol, backfill and preserve historical proof/keys. Else retain both and optimize with batching/caching pinned to current authority revision.

**Gate:** SIM-35..SIM-42 plus adversarial proof and independently signed go/no-go. **Rollback:** dual-verification permits immediate rejection of new protocol; historical content must still verify.

## Non-negotiable release checklist

- Independent authority and PostgreSQL admin isolation; no permission path trusts owner GUIDs in BusinessDb.
- Canonical event stream, durable archive and ledger receipt chain; effective security commands replayable.
- Data classification and default encryption migration, with documented DBA-readable exceptions.
- Complete-search claim only where V1 verified enumerations or V2 independent index actually support it.
- Per-user authentic assurance, no shared challenge bypass; live revocation effective.
- HMAC + Merkle mandatory until equivalence proven, no signing oracle.
- PostgreSQL roles/RLS/pgAudit, key recovery and security journal DR exercised in staging.
- CI checks multiword column names; tenant-safe FK; no unsafe SQL, secret logging, unauthorized metadata or hidden data leakage.
- Executed and retained adversarial test evidence; all open gaps explicitly tracked.

## Risk acceptance

A proposed removal is approved only with: old/new data-flow diagram, exact threat being removed or transferred, rollback procedure, measured perf/cost, tests and sign-off by independent Security Reviewer + service owner. Downgrading a Level 2 guarantee is not a 'performance optimization'.

# Level 2 Trust Authority — Architecture and Integration Contract

> **Security simplification cross-reference:** For duplicate audit-writer consolidation, confidentiality defaults, trusted search completeness, high-assurance step-up and future HMAC/Merkle optimization, see `security_hardening/SECURITY_SIMPLIFICATION_STANDARD.md` (ADR-SEC-2026-10-16-01). This document remains normative for its own trust boundary and state machine; no prior anti-rollback/recovery requirement is weakened.


**Status:** Architecture decision for implementation. **Threat model:** attackers can read, insert, update, delete, replay, or roll back rows in the **business PostgreSQL database**, including through compromised business DB credentials or privileged DB administration. They **do not** control the Authorization Authority deployment, its separate data store, its privileged workload identity, Managed HSM, security journal, or Entra ID issuer. A complete application-runtime or identity-provider compromise is outside Level 2 and needs additional defenses.

**Technology decision:** .NET 10 Authorization Authority API; SpiceDB authorization engine; isolated PostgreSQL backing the authority; Azure Managed HSM for HMAC-SHA-256 operations; Azure Confidential Ledger (ACL) for external tamper-evident checkpoints; Entra/OIDC for authenticated user identity; ASP.NET Core business API, PostgreSQL 18 and encrypted object storage for application data.

**Non-negotiable:** Neither a business database row nor a valid RowSignature alone grants access. Access depends on the authority's current, independently maintained security graph **and** a verified committed content revision and manifest **and** relevant application-level challenges/encryption policies. A mutable local owner GUID, local team membership, cached JWT role claim, or business-DB share row is never the authority of truth.

## 1. Trust boundary and deployment

```
 User / browser -- OIDC --> Entra or approved IdP
       |
       v
 Business ASP.NET Core API [least-privileged workload identity]
       |        |                       |
       |        | business DATA only    | encrypted FILES only
       |        v                       v
       |   PostgreSQL (untrusted)    private Blob storage
       |
       | mTLS + workload token + original validated user evidence
       v
 Authorization Authority API [separate subscription/admin boundary]
       |              |                  |
       v              v                  v
 Authoritative       SpiceDB           Managed HSM
 command journal     query engine      organization HMAC keys
 and policy store        |                  |
       |                 v                 |
       +-------- isolated PostgreSQL ------+
       |
       +--------> Confidential Ledger (tamper-evident checkpoints)
```

The trust authority and SpiceDB datastore must not be writable with credentials available to any business PostgreSQL administrator or business runtime identity. No direct business API connection to SpiceDB `WriteRelationships` or its backing store. Separate network boundaries/private endpoints, workload managed identities, IAM/RBAC/PIM, database secrets, CI/CD deployments, HSM access roles, backups, incident response and monitoring. Do not place both data stores on one server where the same DBA identity is privileged over both. The business API receives only the authority's **decision interface**. An internal narrowly permitted command identity can call **specific** command endpoints; it never receives generic graph mutation or HMAC-sign privilege. The authority validates the authenticated human actor and action independently.

The identity mapping (`issuer`, `subject`, home organization, account enabled/revoked status) is owned by the authority, not copied from business-DB `users` records. Derive tenant and user from verified tokens and independently trusted mappings. Reject client-supplied `actor_user_id`, tenant reassignment, or free-form principal IDs. Cross-tenant relationship creation is rejected server-side even if SpiceDB schema could technically represent that edge.

**Key choice:** HMAC-SHA-256 `HS256` using an HSM-backed symmetric key (if provider capability/performance testing confirms viability). Keys are per organization and purpose; key versions are included in signed envelopes. Encryption DEKs, audit keys and row-integrity keys must be distinct. HSM performs cryptographic operations but **does not authorize ownership changes**. Never expose `/sign-any-payload` or KMS operation details to the business API. Confidential Ledger anchors accepted authority checkpoints, never replaces SpiceDB's runtime decision path. Treat all cloud-provider capability details as requiring IaC + integration-test verification.

## 2. Authoritative state vs data projections

**Authoritative state in isolated authority database**:
- Verified user identity bindings and principal lifecycle; organization membership and security-role assignments.
- Owning user or team for a record; team memberships; form-level Designer and Use grants; share scope, field/group/document grants; parent/child delegated share lineage and expiry/revocation.
- Organization security floors, passkey assurance requirements, record access challenge policy, document confidentiality requirements and approved policy revisions.
- Authorized **security commands**, idempotency keys, expected revisions, approvals, source actor, outcome, and monotonic organization authorization revision.
- Per-record **committed content manifest hash/root and revision**, key version and accepted schema version; per-item identities of rows, attachments and ciphertext metadata, with absence/deletion represented by a canonical membership commitment.
- A durable outbox containing projection operations and integrity-checkpoint requests.

**Derived business database state**: owner_user_id, owner_team_id, local record shares, local team membership and access projection may exist for convenience/reporting but are never used independently to authorize. Reconstructible from authority. A hacked projection may cause denials or incorrect non-sensitive display, not an access grant. A business DB `form_version` cannot turn off mandatory security floors or grant record exposure. Form design metadata affecting authorization, required encryption or visibility must be authenticated against an accepted authority-registered published form revision before use.

**SpiceDB** is a protected **authorization query projection**, built from independently validated security commands. The authority's command journal/revision is the commit source; SpiceDB reflects it. This additional source-of-truth layer is intentional: authority can block checks when projection lags, when a revocation is committed, or when a command is still pending. It also makes repair/reconciliation deterministic. Do not permit an external party to write SpiceDB relationships directly.

## 3. Authorization model and scoped sharing

- One record owner: `user` OR `team#member` (membership itself maintained only in authority). Members of the owning team have full *record-level* Owner rights, subject to mandatory field classification, access challenges and external policy.
- Ownership does not grant form-design rights. Designer role and form-specific grants are independent; system administrators do not automatically read content.
- Reader/Contributor/ShareManager permissions are explicitly scoped to **individual field instances**, nested repeating-group instances and **specific attachment identities**. A share's displayed record metadata is a separate permission from read on items. Owner full access is computed by authority from owner edge, not from a fake share.
- On EntireRecord sharing, the command captures a fixed snapshot of eligible **existing** field and document identities; no future fields/files become automatically shared.
- Multiple shares combine permissions by union, subject to mandatory denies, challenge state and field encryption policy. Revoking one path removes that path's contribution only; independent grants remain.
- A ShareManager can delegate only items and actions they **currently** possess AND are authorized to delegate; no amplification. Each delegated grant references its parent grant/authority lineage, evaluated and validated on every command. Parent revoke or scope reduction invalidates affected descendants; the authority revision gate prevents stale derived edges from authorizing during cascade propagation.
- Every sensitive item read checks current field/document permission, not just record discover/open. Search predicates, sorting, group counts, autocomplete and exports require permissions for each referenced field and must not reveal hidden metadata.
- Record PIN/passphrase is an additional gate, *not* key material and not permission. It cannot grant access by itself. Unlock context is verified at the authority/application policy gate and revoked on security changes. Plaintext should never enter trusted authorization logs.

`spicedb.schema.zed` is an **illustrative reviewed starting model**, not a validated production schema. It models owner, team membership and explicit item/share membership; more-complex rules (delegation lineage, form version floor, resource same-tenant validation, privilege transfer) are enforced by the authority command service and require integration tests. Run `zed validate` and permission assertions before use. Avoid caveats when normal relations suffice; fail closed on conditional/unknown permissionship.

## 4. Consistency contract and revocation barrier

**Authority security command writes:** a business API cannot edit authoritative relationships by SQL. The authority performs this sequence:
1. Validate service identity, JWT issuer/audience/signature/assurance, independent principal binding, tenant, record, field/item, actor operation privilege, immutable org policy, challenge/step-up requirements, multi-approver policy where required.
2. Load expected authoritative revisions and validate command preconditions, including current delegator scope and parent grant. Reject stale `expected_authorization_revision` (`409`). Reserve idempotency key + command ID atomically in authority journal (`Pending`).
3. In authority DB transaction, commit normalized **desired security state**, new monotonic `authorization_revision`, durable projection-outbox entry, source actor and audit record (`CommittedAwaitingProjection`). **On revocation, this immediately advances the minimum acceptable revision**; do not return allow on an older projection.
4. Project changes into SpiceDB with atomic `WriteRelationships` batches and preconditions. Record returned ZedToken and `projection_applied_revision` in authority store; make operation `Effective` when current projection fully reflects command and required invariants hold. Reconcile by command ID on timeout/crash; SpiceDB writes must be idempotent or compare-and-set, never blindly replay broad deletes. Use per-org (or finer-grained proven-safe) projection barrier.
5. Append accepted security revision/checkpoint to the external ledger using a durable outbox. Store immutable receipt/position and alert on lag. For particularly high-risk organization/ownership operations, the org policy may require external anchoring before acknowledgement, at a documented availability cost. Ledger is not an ordinary distributed transaction with SpiceDB.
6. Return command status. **Do not report an operation fully effective until projection barrier passes.** A committed revocation must immediately block stale permission decisions; a grant does not become available until confirmed effective.

**Permission checks:** Authority must first ensure the SpiceDB projection is at least as current as the authoritative required revision for that tenant/object/dependency; otherwise `503 AuthorityUnavailable` and deny content access. Use `fully_consistent` for high-value actions/revocations when appropriate, or `at_least_as_fresh` using a validated authority-held ZedToken for confirmed projection; **never** accept a caller-supplied/stored-in-business-DB ZedToken as sufficient freshness. ZedTokens are opaque consistency markers, NOT signed proofs of authorization or anti-rollback checkpoints. SpiceDB fully-consistent checks use a snapshot taken at the beginning of the API request, so test concurrent revocation/read races. For an exact instant-of-revocation guarantee, a reader/stream must participate in a shared authorization revocation barrier, e.g. an in-flight lease/checkpoint protocol—merely rechecking once at start cannot retract already streamed bytes.

**Content/data mutations:** no distributed transaction is assumed across business PostgreSQL and authority. Use **immutable/staged content revisions** (copy-on-write), never expose an uncommitted revision:
1. Fetch fresh authorization to edit permitted fields; verify current HMAC(s), accepted record membership manifest and current external content revision.
2. Prepare an operation with idempotency, expected record/content/authorization revisions, item delta, actor identity and validated canonical payload. The authority approves `PrepareContentChange`; the business service has no arbitrary signing endpoint.
3. Commit new signed **candidate** rows/attachments plus candidate manifest in business PostgreSQL, with `operation_id` and state `Prepared`. Preserve prior accepted revision. For encrypted attachments, ciphertext object and verified metadata must be durable/quarantined/scan-clean before candidate can be activated.
4. Submit evidence of the prepared candidate (canonical digest, expected old root, candidate item set membership), and let authority approve the transition and advance **committed content revision/root** in its independently controlled store. The authority is trusted to approve the actor operation and validated delta; the Level 2 boundary assumes the application worker is not compromised. The business DB cannot create an accepted new root by itself.
5. Readers select **only** the authority's latest committed content revision, verify item row signatures, complete membership root, ciphertext identities, policy revision and any required challenge. If candidate preparation succeeds but authoritative commit fails, continue reading previous committed revision if available and valid; never expose candidate. If authoritative commit succeeds but business data disappears/rolls back, **fail closed** for that record and quarantine pending repair.
6. Reconcile Prepared/Committed/Aborted using operation IDs and authority decisions. Do not auto-resign tampered business rows. Keep prior revisions according to recovery/retention policy. A single current-row overwrite strategy is **not sufficient** for the promised atomic semantics and must be redesigned to stage immutable revisions.

**Row signatures and manifests:** signed canonical payload uses HMAC-SHA-256 and includes tenant, entity/table domain, stable IDs, declared nullable/non-nullable protected columns, signed field names/types/null markers, record+item+group+attachment identities, security classifications, encryption envelope metadata, form/schema version, row revision and key version. Signed item-set membership plus externally accepted manifest detects deletion, insertion, relationship reparenting and replay; canonicalization versions are immutable and schema additions require controlled migration. HMAC is verification of content, *not* an independent policy decision. Key rotation retains verify capability for permitted historical revisions and updates roots through authorized workflows. A direct SQL writer cannot solicit new signatures for an arbitrarily reassigned owner.

**Signed manifests performance:** for a form record, use deterministic sorted item identities with content digests hashed into a Merkle root or equivalent canonical membership digest. Per-read materialization of every item is expensive; prefer immutable independently accepted snapshots/proofs and batch verification. Define exactly how completeness is demonstrated. Never silently skip required verification to meet throughput targets.

## 5. API contracts and semantics

All endpoints are **internal only** under `/internal/v1/`; protected with mTLS and Azure workload identity or equivalent, plus end-user delegated token evidence. Never expose them to browsers. HTTP DTOs and .NET contracts appear in `trust-authority.openapi.yaml` and `TRUST_AUTHORITY_DOTNET_CONTRACTS.cs`.

| Method | Endpoint | Action | Principal/policy controls |
|---|---|---|---|
| POST | `/authorization/check` | Authoritative check for action against record/field/attachment | Actor from verified token, strong consistency, current projection barrier; returns Allow/Deny only with assurance/revision info |
| POST | `/authorization/check-batch` | Same for bounded batch | Limit 100–500 items, no security downgrade, every item checked |
| POST | `/authorization/discover` | Cursor-based IDs of discoverable records | Trusted lookup/check-bulk; no forbidden record IDs, counts, or filter leaks |
| POST | `/security-commands` | Ownership/share/team/form/security policy mutation | Actor is bound to verified claims; optimistic revision, idempotency, delegation + dual approvals where needed |
| GET | `/security-commands/{command_id}` | Poll durable command/effectiveness state | Scoped actor/admin only |
| POST | `/content-changes/prepare` | Approve candidate content revision | Fresh edit right + old HMAC/root + exact item delta, no arbitrary-sign service |
| POST | `/content-changes/{operation_id}/commit` | Commit staged content root after validated candidate | Must match authorized preparation, prepared digest, current expected revision |
| GET | `/content-revisions/{record_id}` | Retrieve authoritative revision and manifest commitment | Only a permitted caller; no arbitrary historical root selection |
| POST | `/integrity/verify-batch` | Verify specified committed signed items and root | Read-only verification; caller cannot request signature creation or replace expected roots |
| POST | `/security-challenges/verify` | Confirm record challenge | Authorized recipient, rate-limited; return short-lived revocable proof, no PIN logging |

**No public API:** `SetOwner(Guid)`, `GrantUserRole(...)` without independent actor checks, arbitrary `SignRow(byte[])`, arbitrary SpiceDB `WriteRelationships`, `SetCurrentRevision`, `IgnoreSignature`, arbitrary KMS decrypt, or toggle-off FIDO2/org encryption floors.

**Error mapping:** `401` invalid identity, `403` real permission denied (avoid revealing record existence), `404` for object presence when concealment requires it, `409` stale expected revision/conflicting idempotency, `422` invalid command/illegal delegation, `423` confirmed integrity quarantine, `429` abuse limits, `503` authority/projection/KMS/unlock unavailable, fail closed; client must not interpret 503 as permission allow. Stable machine-readable `error_code`, `correlation_id`, `retryable` (true only for safe idempotent retrials), no sensitive data in errors. Do not rely on HTTP status as the sole access-control signal.

**Actor identity:** business API sends a service-bound token plus the original end-user identity token or a properly validated scoped on-behalf-of token; authority independently checks issuer/audience/subject/tenant/assurance against its own binding store. Do not accept only an arbitrary `user_id` string from business API JSON.

**Revision/consistency:** every check returns `authorization_revision`, `projection_revision` and `content_revision` where applicable; these are diagnostic consistency indicators, not bearer authorizations. Versioned responses must not be reused after security-state changes. Revocation checks on each sensitive operation and rechecks during long-running streams/exports.

## 6. Search and rendering

A valid record-level Discover grant permits a listing of approved minimal metadata; it does not imply permission to search on every field. Search algorithms must:
1. Validate search predicates, sorting, aggregation fields and any autocomplete requests against the user's current **item-level** permissions. For queries across arbitrary records, plan and restrict each candidate and field; avoid a predicate on unauthorized fields that changes result presence/counts.
2. Retrieve candidate IDs from indexed business data as untrusted hints, or use a verified authorized search projection; batch-check discovered records and matching field items using authority. Inspect row signatures and committed item/record manifests before results, values or snippets are returned.
3. If the attacker can delete business-DB index rows, the application cannot guarantee **complete** search results solely from the compromised business database. Integrity verification catches known affected records but does not prove no matching record was suppressed. For a completeness guarantee, a separately trusted index/checkpoint or reconciliation against the authoritative committed catalog is needed. **Do not claim complete or tamper-proof search** without this.
4. Produce authorized JSON layout projections: return only authorized fields, repeating-group instances, labels/dependencies that do not leak private fields, and explicitly shared attachment identities. Encryption and PIN gates still apply after item-level authorization.
5. Disallow queries on encrypted fields by default. Blind indexes are a separately risk-accepted exact-match policy, not generic secure search.

SpiceDB `LookupResources` can be expensive for users with very large scopes; bounded `CheckBulkPermissions` with cursor-based search and a separate trusted authorization index/projection is the likely scalable approach. Recheck before each disclosed page; no unbounded `IN` list or client-side filtering of forbidden rows.

## 7. Operational security, monitoring and recovery

- Authority outage, network partition, unacceptable projection lag, missing external checkpoint beyond policy maximum, unknown row-signature key or failed manifest verification: **deny sensitive access and writes**. Return diagnostic incident IDs only. Define SLOs and disaster procedures; do not add fail-open feature flags.
- External ledger checkpoint structure contains `organization_id`, `authorization_revision`, `projection_revision`, accepted tenant graph-root/journal hash, content checkpoint/root reference, schema version, HSM key version, prior checkpoint hash, operation and timestamp. No sensitive field values, user passphrases, access PINs, file names or plaintext. Checkpoint chain and receipts must be independently verified in scheduled jobs and recovery.
- Record audit actions: denied/opened/read field/decrypted/downloaded, grants/revokes/team joins/ownership transfer, policy changes, integrity failures, quarantine, command status transitions, revocation freshness and key use. Failed business transactions need independently committed audit events. Archive independently with immutable retention.
- Backup both PostgreSQL environments and protected object storage; test restore against HSM key availability, ledger receipts, authoritative revisions and manifest roots. On rollback of business DB, quarantine stale records rather than accepting previous valid signatures. On rollback/corruption of authority state, stop authorization until reconciled against independent checkpoint/restore source.
- Enforce PIM/dual approval for authority admin, schema migrations, key policy changes and break glass; no business DBA can get trusted signing privilege by default. Monitor separate identities, private endpoints, certificate rotation, HSM limits, SpiceDB latency and watch/outbox lag.
- Threat boundary caveats: this does **not** hide normal plaintext fields from a business DB reader; no system can retract bytes already delivered; a full compromise of business API execution or authority/IdP identities requires additional threat model and mitigation; RowSignature verification costs require benchmarking.

## 8. Acceptance/release gates (P0)

| Test | Expected behavior |
|---|---|
| DBA changes `owner_user_id`, `owner_team_id`, organization ID | No new authority grant; signature/manifest failure; denied/quarantine |
| DBA forges team membership or system role | No access: authority uses its own membership/roles; forensic alert |
| DBA inserts scoped share for a hidden field or attachment | Access denied; no hidden field in response, search, export, document retrieval |
| DBA changes `is_encrypted` or record PIN requirement | Policy floor from authority wins; access denied without required controls |
| DBA replays old signed row or deletes subset/whole record | Committed root/revision/manifest mismatch; denied, recovery workflow |
| DBA edits business DB `users` identity mapping | Actor still verified from IdP + authority identity store |
| Business runtime calls generic raw graph writer / HSM | Forbidden by IAM/network and API; no endpoint exists |
| ShareManager delegates unavailable item | Command rejected; parent authority scope enforced |
| Parent share revoked while child shares exist | New reads denied immediately on authority revision barrier; reconciler removes child effective edges |
| Grant writes journal but SpiceDB projection fails | Grant not effective; checks fail closed until repaired |
| Revocation journal commits but SpiceDB projection delayed | Stale SpiceDB allows never exposed; authority barrier denies |
| Content staged but authority commit unavailable | Old committed revision only (if intact); staged content invisible |
| Authority commits content but business DB rolls back | Read denied/quarantine; do not auto-re-sign restored older rows |
| Duplicate command/idempotent retry after timeout | One authorized effect, stable response and full audit chain |
| Concurrent owner transfer vs share change | Optimistic conflict/serialization, no unauthorized gain |
| DB search index silently deletes a matching row | Acknowledge completeness limitation; verified reconciliation catches drift; no false completeness assertion |
| KMS/authority/ledger critical outage | Fail closed per org policy; incident and measurable recovery |
| Restored backup and old valid HMAC | External auth/content revision exposes rollback and blocks stale data |
| Unauthorized provider or human administers security DB | Forbidden by deployment/IAM controls and audited attack simulation |
| Load test millions of values and high-cardinality memberships | Benchmark check-batch, root proof, search, event pipeline, HSM throughput and latency |

**Mandatory implementation order:** deployment isolation + IdP binding -> authority journal/projection barrier -> scoped SpiceDB model and permission tests -> command validation and idempotency -> signed content staging/root acceptance -> read/search/decrypt integration -> ledger checkpoints -> chaos/recovery and adversarial testing. Never mark this secure based on code review alone.

## 8A. Normative cross-system state machine (required)

**Source of truth:** `trust_authority/CROSS_SYSTEM_CONSISTENCY_PROTOCOL.md` is the detailed protocol governing security command state, per-organization fail-closed authorization gate, ordered SpiceDB projection, external revision/step fencing, idempotency/retries, immutable content staging and independently committed operation IDs, after-commit crashes, and strict vs bounded streaming revocation. Implement this before granting any real protected access. Use `trust_authority/consistency-schema-additions.reference.sql` as a reviewed additive schema sketch, not a ready-to-deploy migration. A PostgreSQL worker lease alone does **not** fence delayed SpiceDB writes: verify SpiceDB marker-CAS semantics in the deployed version. The consistency protocol controls when shorter prose in this file is ambiguous.

The content head and API must carry `committed_operation_id`, not just `committed_content_revision`. Every accepted revision >0 has a unique trusted operation ID; readers select immutable staged rows by that ID. No business-DB publication flag, cached ownership or stored ZedToken can replace a fresh authority decision. Do not falsely claim atomic distributed commits; explicitly implement and test each crash boundary.

## 9. Vendor references and validation

- SpiceDB consistency, ZedTokens and fresh reads: https://authzed.com/docs/spicedb/concepts/consistency
- SpiceDB relationships writes/preconditions: https://authzed.com/docs/spicedb/ops/data/writing-relationships
- SpiceDB query/check/list guidance: https://authzed.com/docs/spicedb/concepts/querying-data
- SpiceDB schema language and precedence: https://authzed.com/docs/spicedb/concepts/schema
- Azure Managed HSM symmetric HS256: https://learn.microsoft.com/en-us/azure/key-vault/managed-hsm/about-keys-details
- Azure Confidential Ledger receipts: https://learn.microsoft.com/en-us/azure/confidential-ledger/

Vendor capabilities/latency/pricing/IaC must be verified for the selected regions, versions and subscriptions; the text above is an architecture specification, not a claim of deployed safeguards.


## 10. Selected G03 record-content Merkle manifests

The authority stores and commits an independent per-record *complete* ordered leaf index and Merkle root, `committed_operation_id`, `content_revision`, leaf count, schema/algorithm/key versions and external checkpoints. It is the only trusted source of record completeness. The application reads exact committed staged data, not a business `published` flag. Partial sharing uses an **authority-derived authorized expected set**, never hiding unauthorized fields only in the browser. See `MERKLE_RECORD_MANIFEST_PROTOCOL.md` and `record-manifest.reference.sql`; production implementation is pending. This scope does not include ACL graph integrity, full-text search completeness or already-exfiltrated plaintext.


## Recovery as a separately trusted control plane (G14)

The independent trust boundary extends to disaster recovery. Implement a recovery controller with external monotonically fenced epochs, immutable replayable security-event archive with separately verified ledger receipts, and two-person cutover approval. Rebuild authority/SpiceDB from the independently accepted event stream; verify committed manifest heads against recovered immutable data bytes, signed metadata and historical HSM keys. Do not let BusinessDb timestamps, restored owner GUIDs or previous SpiceDB tokens establish the accepted security revision. Missing archived event bodies => quarantine; missing accepted content bytes => affected record quarantine. Refer to `COORDINATED_DISASTER_RECOVERY.md` and `DISASTER_RECOVERY_TEST_PLAN.md`. Architecture specified only; production tests open.

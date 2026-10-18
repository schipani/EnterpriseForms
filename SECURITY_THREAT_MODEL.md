# Dynamic Forms Platform — Enterprise Threat Model and Security Hardening Plan

**Status:** Mandatory design-review and implementation gate. This document evaluates the architecture described in `CLAUDE.md` as a **specification**, not a running implementation. Findings are design risks and required controls, not verified exploitable defects in deployed code. A control is not complete until demonstrated by integration tests and operational evidence.

**Review scope:** PostgreSQL 18, ASP.NET Core/.NET 10, EF Core/Npgsql, React renderer/designer, OIDC/Entra and alternative IdPs, multi-tenancy, RBAC/teams/ownership/scoped shares, versioned JSONB, typed values, application-level encryption, per-record challenges, file storage, search/reporting, audit/recovery, and organization settings.

**Security objectives:** (C) Confidentiality of record data and documents, (I) integrity of ownership/security policies/forms/audits, (A) availability and reliable recovery, (T) tenant isolation, (R) revocation and traceability. All operations deny by default, including background jobs, search metadata and historical views.

## 1. State the trust boundary explicitly

| Threat/attacker | Required property | Important limitation |
|---|---|---|
| Anonymous visitor, untrusted web client | No record enumeration or modification; prevent unauthenticated access | Public endpoints, browser and network are untrusted |
| Authenticated user/Designer/Owner from same tenant | Only permitted operations, fields, groups, files and security changes | IDs, JSON layouts, JWT contents not verified by middleware, and client-supplied tenant IDs are not authority |
| Tenant administrator/team manager | Tenant-scoped privileges only; sensitive content and keys not implicitly exposed | Team membership change can confer Owner access, so membership is security-critical |
| SQL read-only compromise | App-encrypted data/files remain confidential; standard plaintext rows and unencrypted metadata are NOT protected | To guarantee secrecy against DB readers, all sensitive data and sensitive metadata require appropriate encryption/minimization |
| Unauthorized SQL write to business/ACL tables | No change to authorization/decryption outcomes absent separately validated approval | Ordinary foreign keys, RLS and unverified DB-stored ownership are insufficient; high-assurance independent authorization is required |
| Database owner/superuser or migration-admin compromise | Detect interference and limit consequences through independent controls | Can bypass RLS, modify triggers, alter policy rows and potentially destroy data; cannot promise DB-enforced immutability against superuser |
| Application-service compromise | Minimize KMS, storage, auth and data exposure with workload isolation, least privilege and alerts | A service that can authorize and decrypt arbitrary tenant data is a high-value trusted computing base |
| KMS/key-management compromise | Prevent unauthorized key use with independent approval, scoping and audit | A complete compromise of all auth services, runtime and key control cannot be cryptographically prevented in this server-managed model |

**Two supported deployment assurance levels:**

- **Baseline enterprise (mandatory):** no routine human production DML; different migration/runtime DB roles; application-only security mutation workflows; non-owner non-BYPASSRLS runtime connections; authenticated tenant context; RLS plus service-side authorization; KMS isolated from DB; immutable external audit; monitoring and verified recovery. Baseline does **not** guarantee protection after an attacker gains privileged SQL write access to authorization tables.
- **Database-write-resistant authorization (required where hostile SQL write is in scope):** independently controlled authorization authority and verifiable, non-replayable revisions for Owner/Team/Share/Role/Security-policy changes. A mutable SQL row or tenant session variable alone is not permission proof. Current security state must be checked against an independent protected authority before decryption or sensitive content access. The verifier must reject missing, forged, stale and rolled-back authorizations. KMS/key-release permissions must not be derived solely from untrusted PostgreSQL rows. This is a separate service/trust boundary, not a HMAC key stored in the same database or an algorithm embedded in the same compromised application. Require formal design/security review of synchronization, failure behavior, recovery and latency before claiming this assurance level.

**Clarification:** AEAD associated authenticated data (AAD) that binds a ciphertext to tenant/record/field prevents ciphertext relabeling, **not** an authorized ciphertext being decrypted after someone edits a separate ownership/ACL field. Key Vault verifies workload identity/key permissions, not end-user record access by itself. All key releases still need independent, current authorization decisions.

## 2. Ranked findings and mitigations

Ratings are **potential pre-control severity**, not a finding from a penetration test. `P0` = security architecture blocker; `P1` = required before production; `P2` = important hardening. For every finding create a tracked security test and named control owner.

| ID | Priority | Weakness / abuse path | Required preventive and detective controls | Verification |
|---|---|---|---|---|
| SEC-01 | **P0** | SQL writer substitutes `owner_user_id`/`owner_team_id` with attacker GUID; app trusts record and decrypts | Restrict direct writes; dedicated verified ownership-transfer workflow; dual approval for high-impact operations. When hostile DB write in scope use independent authorization revision verification and off-DB monotonic high-watermark, fail closed | Change owner row directly with privileged test writer; sensitive read/decrypt still denied, alert emitted; attempt rollback/replay |
| SEC-02 | **P0** | SQL writer modifies `record_shares`, recipients, fields, attachments, delegations, team membership or user/system roles to forge rights | Protect **all** security graph edges, not just owner column; externally verifiable policy revisions for high-assurance mode; separate audit/approval; short-lived auth caches | Forge each edge in isolation, replay older valid share and old team membership |
| SEC-03 | **P0** | DB writer changes `field` encryption flag, form JSON, organization policy or challenge requirement to weaken protection | Server-side minimum policy separate from editable form layout; published policy revisions; prohibit silent downgrade; independent policy integrity at high assurance; detect drift | Tamper historic layout/protection; failed decrypt/discovery or publication with alerts |
| SEC-04 | **P0** | Runtime/migration DB roles or `SECURITY DEFINER` function bypass RLS; RLS trusts user-set session identifiers | Separate non-owner non-superuser runtime role, NO `BYPASSRLS`; `FORCE ROW LEVEL SECURITY` where suitable; narrowly audited maintenance identity; `SECURITY DEFINER` review and locked-down search path; authenticated transaction-scoped context; no arbitrary SQL path | Check `pg_roles`, ownership, policies, function `EXECUTE` grants, `EXPLAIN`; SQL injection attempts and connection pooling tests |
| SEC-05 | **P0** | Application trusts custom PostgreSQL `current_setting` tenant/user GUC set by arbitrary SQL | Treat GUC as defense in depth only, not an unforgeable identity attestation; establish it from validated server identity in every transaction; deny SQL injection and user-controlled SET; never trust client tenant header | Attempt `SET LOCAL` spoofing through any accepted query surface, prove query API denies |
| SEC-06 | **P0** | Stored DB grants are trusted by a KMS-capable backend, which decrypts for unauthorized users after policy tampering | Independent policy authorization before sensitive key use/decryption at high assurance; separate KMS access roles and decryption service identity, AAD, least privilege, rate limits | Forged DB grant must not unlock ciphertext; KMS logs show denied usage |
| SEC-07 | **P0** | Field-scoped access checked only when projecting response; hidden field used as filter, sort, count, condition, export or history query | Authorize **requested predicate fields** and output fields before query planning; parameterized allowlisted filter builder; RLS/field guards on typed and encrypted-value tables; non-inference tests | Same unauthorized sensitive value yields indistinguishable search behavior/results/errors |
| SEC-08 | **P0** | Delegated share widens rights through union of separate parents, cycles, an expired parent, a team membership change or missing lineage | Directed acyclic grant provenance; cap each delegated action/item to currently valid source rights; do not allow cross-parent privilege laundering; recompute on access; revocation versions and atomic changes | Property-based authorization graph tests, cycle injection, overlapping grant, removal/expiry under concurrency |
| SEC-09 | **P1** | User invitation or SSO auto-provision matches email rather than exact trusted IdP issuer/subject, taking over another account | Identity key = trusted issuer + subject + explicit tenant/provider binding; verified domain/invite workflow; immutable mapping changes via audited admin action; no trust of `email` for ownership | Same-email different-issuer and B2B guest tests; issuer/audience and tenant switching checks |
| SEC-10 | **P1** | Team administrators add themselves or proxies to an owning team; instantly acquire all owner rights | Treat team membership of owning teams as privileged security changes; step-up auth, scoped membership-management permissions, optional owner/second approval, alerts; prevent self-approval | Admin without owning-team membership cannot self-add without policy; change produces alert and audit |
| SEC-11 | **P1** | Owner/Share Manager grants a sensitive document indirectly via upload-control access, shared group, new version or replacement | Explicit attachment IDs and version/action semantics; share snapshots exclude future data; authorize each streamed download; no default inheritance beyond explicit policy | Add attachment and new field after share; cannot read until explicitly granted; prevent version bypass |
| SEC-12 | **P1** | Export, async report, background processing, search index or AI feature accesses broader scope than UI | Every worker has original requesting principal, tenant, policy revision and item scopes; evaluate again at execution/delivery; encrypt temp artifacts; expiring authorized downloads | Revoke share while job queued and before export delivered; delivery denied and cache purged |
| SEC-13 | **P1** | Protected data persists in typed columns, JSONB, audit history, materialized views, WAL, replicas, caches, telemetry or backups after 'encrypted' migration | Field-policy upgrade state machine; inventory/decommission every plaintext copy; block reads/searches until migration completed; retention-aware backup disclosure statement; scan/proof of no active plaintext | Canary values scanned in relevant stores; verify migration crash resume; confirm old backups risk documented |
| SEC-14 | **P1** | App decrypts valid ciphertext from different location by swapping record/field/file associations | AEAD AAD binds tenant ID + record ID + field ID + group ID + attachment version ID + algorithm/envelope version; immutable field IDs and canonical serialization; reject authentication failure | Swap bytes/metadata between records and tenants; integrity check fails |
| SEC-15 | **P1** | KMS unwrapping keys too broad; shared managed identity can decrypt every tenant; compromised worker uses authorized SDK | Service-identity and key-scope separation by tenant/sensitivity where appropriate; no humans with routine unwrap; KMS audit and abnormal-use detection; independent release policies for stronger mode; tested rotation/recovery | Try cross-tenant key use, compromised narrow worker identity, KMS denied/disabled key |
| SEC-16 | **P1** | Record challenge/pin brute force, hash extraction, unlock replay or bypass via team Owner or API | Minimum entropy passphrase or provider-assisted step-up; Argon2id plus separate server pepper when suitable; global per-user+record+IP limits, lockout and monitoring; revocable server-side short-lived unlock bound to auth session, tenant, record and policy revision | Offline guess effort review; parallel online attempts; replay after share revoke/rotation, different device and session |
| SEC-17 | **P1** | Entra policy set locally but external IdP permits weaker method; admin changes policy externally; FIDO irrevocability bypass via SQL | External Conditional Access/custom authentication strength verification, signed valid assurance evidence, periodic reconciliation and alert/fail closed; dual approval and provider-side monitoring; explicit limitations on initial password prompt | Lower-strength sign-in, changed CA policy, stale sessions, lost credential recovery |
| SEC-18 | **P1** | Previews/decrypted documents served via direct object URL, CDN, local disk, thumbnails or stale browser cache | Backend-mediated authorized decryption; object storage ciphertext only; disable plaintext CDN; `Cache-Control: no-store`; sandboxed preview; scrub temp files, bounded streaming; short-lived request-scoped access | Raw object access stays ciphertext; revoke during download; check temp/cache/log artifacts |
| SEC-19 | **P1** | Uploaded file malicious, scriptable PDF/SVG/HTML, decompression bomb, scanner bypass, cross-user filename or malicious replacement | Upload allowlists/type sniffing; quotas and size/decompression limits; quarantine; trusted malware scanning/CDR where applicable; sandbox workers; generated keys/names; `Content-Disposition: attachment` for dangerous types; CSRF defense | Polyglot, SVG script, huge archive, Content-Type spoof, replacement before scan completes |
| SEC-20 | **P1** | Data leakage in search result counts, timing, suggestions, error messages, existence of locked records, metadata or filenames | Default-deny discovery; authorize predicates before evaluating them; minimal locked previews; deny-or-generic-not-found where appropriate; bounded timing, query complexity limits; mask sensitive document names | Compare unauthenticated/unauthorized queries across existent/nonexistent values; check counts and timing |
| SEC-21 | **P1** | Draft/publish/restore of old form layout reintroduces weakened security, stale field IDs or disallowed dropdown options | Security policy revision independent of presentation version, immutable field identity, publish-time live mandatory-policy validation; version history never auto-undoes protection; controlled data migrations | Restore oldest version after encryption upgrade/option disable; policy remains effective |
| SEC-22 | **P1** | Races: check permissions once, then team removal/share revoke/owner transfer while update/export/decrypt proceeds | Transactional condition checks for writes, optimistic policy revisions, short-lived read tokens, revocation signals, resumable/interruptible streams where viable; document unavoidable already-delivered bytes | Concurrent revoke/save/download/export; no new access after revocation commit under defined bound |
| SEC-23 | **P1** | AuditEvents or audit metadata tampered, deleted, spoofed by SQL writer/DBA; missing access events or changed actor | Append-only runtime grants, event schema validation, request actor from trusted session, change+audit transaction, external independently administered immutable archive with checkpoints/signatures and alerting | Delete/alter events in DB; archive integrity still detects; audit sink outage and rollback tests |
| SEC-24 | **P1** | Operator uses production migration credential/service role for normal API; CI supply-chain compromise gives admin DML | Separate workload identities, read/write/migration/service DB roles, JIT MFA admin access, signed images/CI secrets and approvals, migrations run only in deployment window, break-glass playbooks | Privilege inventory and automated checks; can runtime `ALTER TABLE` or change `roles`? must be denied |
| SEC-25 | **P1** | JSON layout, translation, label, validation expression, regex, formula or file preview injects script or exhausts CPU | Schema-based JSON validation, safe declarative expression engine with strict limits, output encode, no arbitrary JS/SQL, regex timeouts, quotas; CSV formula escaping; CSP and CSRF defenses | Stored XSS, regex DoS, condition cycles, nested group explosion, CSV injection tests |
| SEC-26 | **P1** | API overposts OwnerUserId/Role/field IDs, ignores restricted properties or accepts a different tenant value | Strict allowlisted DTOs and typed PATCH commands; reject all unauthorized properties atomically; derive actor/tenant server-side; DB cross-tenant constraints | Mass assignment, forged hidden field, attachment path and cross-tenant IDOR tests |
| SEC-27 | **P1** | `uuidv7()` time-order makes IDs more guessable; relying on GUID secrecy as authorization | IDs are public opaque identifiers; never treat unpredictability as permission; use independent signed random download tokens when needed; test sequential enumeration | Enumerate nearby IDs: always authorization denied |
| SEC-28 | **P2** | PII retained indefinitely in audit/archive/logs, conflicting deletion obligations; legal hold missing | Classification, minimal logging, retention tiers, lawful-basis/legal-hold workflow, erasure/crypto-shredding policy; archive rotation and exception handling | Retention deletion drills, policy evidence and protected audit redaction |
| SEC-29 | **P2** | Availability or cost attacks through massive forms, repeated fields, search joins, encryption/decryption and uploads | Quotas per tenant/user, field/group/JSON nesting limits, query cost and timeout budgets, rate-limits, async jobs, bulk export approvals, concurrency caps, alerts | Load/DoS tests with worst-case inputs |
| SEC-30 | **P1** | Backup restores data but not keys, authorization versions, share revisions, external audit checkpoints or IdP policy | Coordinated disaster recovery across DB/blob/KMS/auth authority/IdP policy; secure immutable backups; periodic restore drills; detect replayed ACL states and stale cache | Restore to isolated region/date and prove authorization integrity + decryptability without downgrading FIDO |
| SEC-31 | **P1** | Conditional rules or calculated fields indirectly reveal hidden data via visibility, validation, errors, totals, comparison or default values | Server projects rule dependency graph only where safe; remove dangling unauthorized rules and computed values; refuse inference-producing calculations | Hidden salary cannot be inferred from a conditional branch or aggregate |
| SEC-32 | **P1** | Cross-tenant organization catalog association or default, IDP config, form definition or attachment reference points to another tenant | Typed tenant-consistent composite FKs/constraints and service authorization; isolate catalogs; enforce on nested references and imports | Tamper every FK and form JSON reference; DB rejects inconsistent tenancy |
| SEC-33 | **P2** | Organization default option disabled after save; historical value shown but saving unrelated field blocked | Clearly define active edit scope, transactional policy revision, replacement workflow and error; avoid silent data mutation and denial loops | Disabled value edit path preserves history, provides enabled replacement, doesn't leak hidden fields |

### Additional row-integrity attack paths (identified after the original 33 findings)

These extend—not replace—the original 33 findings. For the design requirements, see **CLAUDE.md Section 4L**. A signed value is **not** a trustworthy entitlement until the complete current authorization state has been verified against the independently protected accepted revision.

| ID | Priority | Weakness / abuse path | Required preventive and detective controls | Verification |
|---|---|---|---|---|
| SEC-34 | **P0** | Attacker deletes an otherwise valid signed owner/team/share/field row, changing access without leaving a row whose HMAC can fail | Independently anchored membership/graph commitments including row identities and signed digests; validate collections before use | Delete last share edge/field/document and test expected manifest + root mismatch |
| SEC-35 | **P0** | Attacker replays an old but valid signed owner/share/policy row or an entire old database snapshot | Externally protected accepted revision/high-watermark, rollback-resistant graph roots; fail closed on stale state | Restore revision N after accepted N+1; deny old access and alert |
| SEC-36 | **P0** | Application's general signer is used as an oracle: attacker changes an owner and asks normal write path to re-sign forged data | Purpose-isolated authorizing signer with verified actor/tenant/action, policy, expected revision, step-up, approvals; no generic sign endpoint; control KMS identity | Forge signed-by-service owner/role changes without legitimate authorization; sign denied |
| SEC-37 | **P0** | DB transaction and off-DB checkpoint commit in inconsistent order; stale checkpoint causes incorrect allowance or permanent lockout | Independently authoritative journal, idempotent operation/revision IDs, prepare/finalize or authoritative-first protocol, fail-closed pending state, tested reconciliation | Crash at each boundary, reorder/retry, replay pending operations |
| SEC-38 | **P1** | Canonicalization mismatch (NULL, decimal, timestamptz, Unicode, UUID bytes, JSONB), new columns, or signing mutable fields incorrectly yields false negatives or exploitable ambiguity | Versioned typed canonical binary format and column registry; deterministic vectors; explicit NULL and domain-separated table/PK/tenant; carefully migrated schema versions | Test .NET/DB and restored rows across schema versions, cultures, serializers |
| SEC-39 | **P1** | Rotated or compromised organization integrity keys allow forged rows or make historic signatures unverifiable | KMS-backed tenant key versions; separate verify/sign rights, incident rotation, historic verification/restore, trusted rebaseline; never bulk re-sign suspect rows | Compromised-key exercise; restore with old key, reject unknown key |
| SEC-40 | **P1** | Signature verification of only query results misses malicious row/index changes that suppress legitimate matches | Trusted or verifiable search projection and periodic manifest/index consistency sweeps where completeness is required; documented residual limits | Change indexed value to disappear from predicate; detect via signed-index or consistency proof |
| SEC-41 | **P1** | On integrity outage or invalid signature, application bypasses check, automatically repairs, exposes metadata, or fails open in cache | No unsigned fallback; fail closed with nonsensitive errors; alert/quarantine; operator-reviewed reconciliation; verified bulk checks and latency budgets | Key/checkpoint outage, invalid signature, cache retry and exception path fuzzing |
| SEC-42 | **P1** | Coverage blind spots: unsigned new authorization-conferring table/column, attachment association, option, group or form policy allows privilege escalation | Explicit coverage registry, CI migration gate, signed per-record and per-tenant relationship manifests, evolving schema contract | Add deliberately unsigned security column/table; CI fails, no production access expansion |

## 3. Security-critical implementation contracts

### 3.1 A single Authorization Decision Point

Create an explicit, testable policy evaluation contract for every sensitive action:

`Authorize(subject, tenant, action, record, field_or_attachment, requested_version, active_challenge, security_policy_revision) -> {allow/deny, effective_item_scope, decision_id, revision}`.

- The subject derives from a validated OIDC principal, **not** JSON, a URL ID, or client-supplied user/tenant.
- Apply it for discovery, predicates, reads, writes, metadata, downloads, previews, exports, audit, recoveries, ownership/shares and background jobs.
- Scope includes **individual fields, repeating instances, attachment versions and requested action**.
- Check current membership, lineage, valid owner, restrictions, record challenge, field classification and immutable/independently enforced security minimum.
- Deny if policy cannot be verified or required KMS/unlock state is unavailable.
- Emit decision reference and sensitive-operation audit without logging encrypted/plaintext values.
- **No `owner_id` check alone is allowed to authorize protected content.** In high-assurance mode a DB claim must be checked against independently verified current authorization state.

### 3.2 Security mutations and database identities

- Runtime DB credential has no `ALTER`, `CREATE ROLE`, `TRUNCATE`, `DISABLE TRIGGER`, `BYPASSRLS`, unrestricted security-table DML or audit deletion. The runtime may invoke narrowly scoped policy-change stored functions/services only after authenticated step-up and required approvals. PostgreSQL table owners must not be reused as runtime roles.
- Keep migrations/deployments on a separate audited, time-limited managed identity. No shared human DBA credentials for normal support; use JIT/Bastion and session recording where feasible.
- `SystemAdministrator` is an application role, not automatically a DB superuser or KMS administrator. OrganizationOwner is tenant-scoped only.
- Security mutations use optimistic concurrency, an idempotency key, actor+tenant provenance, reason, approvals when required, an authoritative revision, and an immutable external audit event. Entire operation fails when mandatory authorization state cannot be securely recorded.
- High-assurance mode: trusted authority signs/attests authorization revisions and stores **current** anti-rollback state outside the mutable operational database. The verifier must check external high-watermark and content digest/revision. Signing inside the same app process using a key it can always access is not independent defense against app compromise.

### 3.3 PostgreSQL RLS contract

- Enable and audit RLS on all tenant/record-sensitive tables; use `USING` for reads/deletes and `WITH CHECK` for inserts/updates as needed; restrict who can call privileged functions.
- App DB role: `LOGIN`, no superuser, no `BYPASSRLS`, not owner of protected tables. Consider `FORCE ROW LEVEL SECURITY` where owners might query.
- Session GUC (`app.tenant_id`, etc.) set via validated `SET LOCAL` inside transaction; explicitly reset with connection pooling. **Do not claim these settings resist arbitrary SQL execution**, since injected SQL under the same DB session may be able to set them.
- Strict tenant composite FKs plus service-side authorization; RLS alone does not prove that a user's field-scoped or delegated permissions are valid.
- Test `SELECT/INSERT/UPDATE/DELETE`, cross-tenant joins, direct typed values, attachments, view and SECURITY DEFINER paths, and connection pooling; include tenantless queries and null context default-deny.

### 3.4 Cryptography and protected metadata

- Envelope encryption with approved AEAD; different DEKs/scopes by sensitivity and tenancy where appropriate. KMS keys separate from DB and blob storage. Bind AAD to tenant/record/field/group or file version; verify before emitting plaintext.
- Classify **metadata too**: form/field labels, filenames, document titles, sizes, timestamps, search terms, email addresses, client IP addresses and audit context may reveal private facts. Mask, minimize or encrypt sensitive metadata where indicated; audit events themselves need not be encrypted but must contain only permitted nonsensitive metadata.
- Restrict KMS unwrap to the decryption backend with least privilege; for hostile-DB-write threat, perform authorization independently of mutable DB state before permitting sensitive decryption. Monitor decrypt rate per tenant/actor.
- An encryption upgrade is not complete merely because a row is ciphertext: existing plaintext copies in snapshots, indices, backups and exports must be inventoried. Strong guarantees for old backups may be impossible until retention/crypto-erasure milestones are reached.
- Treat encrypted data at rest vs **end-to-end encryption** distinctly. The current system is **server-managed** and can decrypt; no zero-knowledge guarantee.

### 3.5 Revocation semantics

- New requests after revocation commit must deny once current authorization state is consulted; do not allow a revoked user to continue using a stale projection or export. Prefer version-bound and invalidatable server sessions.
- Downloads already transmitted cannot be recalled. Streaming responses initiated before revocation may deliver additional bytes until stopped; specify maximum tolerated window and test it.
- Team membership and delegated-share changes invalidate affected permission caches and access challenges. Distributed caches carry policy revision and tenant scope; cache misses and version mismatch fail closed for protected operations.
- Independent surviving shares remain independently valid; chain-derived rights end when any required source grant ceases to authorize them.

### 3.6 Observability and incident response

- Denied request, suspicious admin action, direct policy DB mutation and external IdP/KMS drift must generate actionable alerts.
- Audit event stream is not proof against a privileged DBA; export promptly to an independently administered write-once archive with integrity checks/checkpoints. Retain source-independent login/DB/KMS/cloud-storage traces.
- Support investigation by tenant, actor, IP, resource, action and correlation ID; show tamper detection, decryption anomalies and high-volume exports.
- Document who handles an incident, how to suspend a principal/tenant, revoke shares/unlocks/tokens, pause exports, rotate keys, validate restore and preserve evidence.

### 3.7 HMAC row verification, set integrity and independent security state

- Implement `RowSignature = HMAC-SHA-256(organization-specific secret, canonical signed row payload)` with a dedicated integrity key, never a plain `SHA256(data || organizationKey)`. Keys never reside in PostgreSQL; signing and verification identities are least-privilege and separated by capability when feasible.
- Every trust-bearing row carries `row_signature` (`bytea` 32 bytes), `row_signature_key_version`, `row_signature_schema_version`, `row_revision`. Include **all security-relevant nullable and nonnullable fields**, tenant/entity/PK identity and revision; signatures are versioned and bound to canonical encoding. A stored valid tag without verified latest state is insufficient.
- Verify integrity at **every** protected read or write boundary, including ACL lookup, search, export, history, background jobs, values and KMS-gated decryption. Sign typed-value rows independently. Sign/anchor membership manifests for each record's field/option/group/file set and all authorization graph edges to detect unauthorized row insertion/deletion, not merely updated columns.
- Current accepted security revisions/commitments are protected **outside PostgreSQL** and used to detect replay/rollback. Maintain a trusted independent authorization authority for high-assurance hostile-SQL-write resistance; require specific approved commands/previous revision/actor to get a signature, not an arbitrary HMAC oracle.
- Define and prove cross-store update/failure semantics. Pending state must fail closed; a DB commit not yet recognized by the independent authority is not permission to read. Reconcile only through an independently verifiable journal; do not silently repair forged rows or assume two stores commit atomically.
- Versioned key rotation, schema migrations, signatures of NULL/absent values, dynamic-field insertion, cryptographic test vectors and restored snapshots must be tested. HMACs do not protect plaintext data from SQL readers; standard field confidentiality remains separate.
- Special caveat: signature checks on only matched search rows cannot establish that a malicious SQL writer didn't suppress other matching rows. Need a trusted search projection or a documented bound on integrity/completeness claims, backed by sweeps/alerting.

## 4. Minimum launch configuration checklist

- [ ] Approved threat model and whether hostile SQL writes must be resisted (baseline vs high-assurance) recorded per deployment.
- [ ] Production DB has no routine human DML and no public ingress; JIT DBA path with MFA and audit.
- [ ] Runtime PostgreSQL identity cannot bypass RLS, own tables, modify auth tables arbitrarily, disable audit or run migrations.
- [ ] Authorization mutations require trusted user/tenant, approval/policy checks and durable audit; external integrity controls enabled if high-assurance DB-write resistance promised.
- [ ] Every protected table has versioned canonical HMAC RowSignature and migration coverage; nullable fields/tenant/PK binding verified; unauthorized row INSERT/DELETE and rollback detected by independently anchored collection manifests and current revisions.
- [ ] A signing oracle cannot re-sign attacker-supplied owners, shares or policy without a verified approved transition; signing-key rotation and unknown key handling fail closed.
- [ ] Cross-store commit/reconciliation crash tests, verification/canonicalization vectors, full search-integrity tests, and integrity-service outage drills pass.
- [ ] Identity provider issuer/subject/tenant mapping, token validation, passkey enforcement and recovery tested per provider.
- [ ] Every record/field/file/search/export endpoint has automated tenant/record/field-level negative tests.
- [ ] Encryption policies prevent unencrypted copies in app views and caches; KMS isolated, audited, backed up and rotation tested.
- [ ] Record challenges are throttled and bound to identity, record, tenant, session, current policy; PIN is not a key.
- [ ] Object storage private, no plaintext direct links, scanner quarantines, CDR/sandbox rules tested.
- [ ] Share/ownership/team delegation revocation tests include concurrent access and database tampering cases.
- [ ] External append-only audit archive and monitored delivery have been verified, not only configured.
- [ ] Per-tenant limits, safe JSON/rules handling, API rate limits, CSP/CSRF, secure cookie/session config tested.
- [ ] Coordinated DB/blob/KMS/auth-state/IdP recovery drill passes and anti-rollback checks still hold.
- [ ] Independent penetration test covers IDOR/BOLA, BOPLA/mass assignment, SQLi/RLS, encrypted data, delegated ACL and management APIs.
- [ ] Security exceptions include risk owner, written residual risk, compensating controls and expiration/review date.

## 5. Required attacker simulations / acceptance tests

**HMAC, membership and replay suite:** HMAC alteration, nullable owner field tampering, targeted row deletion and arbitrary insertion, old signed row replay, cross-tenant row substitution, signing-oracle abuse, unsigned new security column, search-result suppression, key-version tampering, off-DB accepted-revision mismatch and fail-closed recovery. Verify high-assurance protected operations deny if any integrity or freshness proof is unavailable.

**DB tampering suite:** Direct SQL update to owner, recipient, share field, document, upstream share, team membership, global role, Designer configuration, mandatory encryption rule, PIN/challenge and Entra enforcement record; direct deletion/rewrite of audit rows; rollback to earlier legitimate grant or tenant policy. Assert that the system does not silently convert these into authorized access at the assurance level claimed; high-assurance mode must deny any out-of-band change. Baseline must alarm on unauthorized changes and document that a privileged DB-write attacker may bypass protections.

**Cross-tenant suite:** Reuse valid GUIDs from other tenants in every API and nested JSON, test users with the same email from different OIDC issuers, guest users and switching active organization. Assert no unauthorized response fields, predicate effects, existence leaks or side effects.

**Scoped-sharing suite:** Try cycles, overlapping parents, multi-team union, a Manager sharing fields they cannot view, hidden field calculation, nested repeated groups, attachment revisions, new fields after entire-record snapshot, revoke after search but before download, and asynchronous export completion after revocation.

**Crypto suite:** Copy ciphertext to another record/field/file, tamper with nonce/AAD/key-reference, deny unavailable KMS, exercise old DEK after rotation, restore DB without key and with correctly versioned key, scan logs/indexes/caches for known plaintext canaries, verify legitimate download vs raw ciphertext.

**Authentication/challenge suite:** Cross-account external-subject collisions, stale/missing `acr`/`amr`, altered Entra Conditional Access, unverifiable non-Entra provider, parallel PIN guesses, session fixation, unlock replay, admin impersonation, new account with no records, direct access after team removal.

**Operations/DoS suite:** Arbitrarily nested form layouts, malicious regex/rule cycles, large attachments/ZIP bombs, heavy cross-form EAV queries, mass scans, log flooding, repeated report exports, forced audit/KMS outage, emergency recovery and restore.

## 6. Release gates and ownership

- **Design gate:** assign an engineering owner and security reviewer to each SEC finding; annotate assurance-level selection and residual risks.
- **Implementation gate:** mandatory P0 controls in place **before** record/search/document endpoints can be exposed; P1 before production; P2 via tracked delivery and accepted risk if deferred.
- **Production gate:** all mandatory test evidence, real IdP and KMS integration tests, architecture review, penetration test and recovery drill completed. No claim of breach-proof security or zero-knowledge encryption.
- **Change gate:** re-review on new file provider, IdP, dynamic rules engine, AI/search integration, external sharing, cross-region deployments, new permissions or key hierarchy.

## 7. Supporting public guidance

- PostgreSQL 18 row security and bypass limitations: https://www.postgresql.org/docs/current/ddl-rowsecurity.html
- OWASP Authorization Cheat Sheet: https://cheatsheetseries.owasp.org/cheatsheets/Authorization_Cheat_Sheet.html
- OWASP API 2023 Object-level and Property-level Authorization: https://api-security.owasp.org/editions/2023/en/0xa1-broken-object-level-authorization/ and https://github.com/OWASP/API-Security/blob/master/editions/2023/en/0xa3-broken-object-property-level-authorization.md
- OWASP Cryptographic Storage and Key Management: https://cheatsheetseries.owasp.org/cheatsheets/Cryptographic_Storage_Cheat_Sheet.html and https://cheatsheetseries.owasp.org/cheatsheets/Key_Management_Cheat_Sheet.html
- OWASP File Upload Cheat Sheet: https://cheatsheetseries.owasp.org/cheatsheets/File_Upload_Cheat_Sheet.html
- OWASP Logging Cheat Sheet: https://cheatsheetseries.owasp.org/cheatsheets/Logging_Cheat_Sheet.html
- Microsoft Entra authentication strengths and limitations: https://learn.microsoft.com/en-us/entra/identity/authentication/concept-authentication-strengths

## G08 privilege hardening addendum — privileged PostgreSQL threat boundary (2026-10-16)

**Status: designed only; not operationally verified.** Apply the mandatory requirements from `trust_authority/PRIVILEGED_POSTGRESQL_ACCESS.md`, separate business/authority reference privilege templates, and `POSTGRESQL_PRIVILEGE_TEST_PLAN.md` (PG-01 through PG-20). These reinforce SEC-01/02/04/05/06/23/24/32 and the Level 2 RowSignature/authority commitments; they do not increase the originally numbered 42-threat count.

**Explicit threat scenarios and expected limits:**

- A malicious BusinessDb administrator can disable RLS, write forged ownership/sharing rows, read **unencrypted** values, destroy data, manipulate search results and restore old snapshots. The independently governed authority and committed Merkle roots must prevent that tampering from creating a valid *application-mediated* permission or accepted record content. No claim that RLS by itself resists the DBA.
- A stolen SQL-capable business runtime credential can set arbitrary custom PostgreSQL session GUCs (including a forged `app.verified_tenant_id`) even when the normal app sets them using `SET LOCAL`. RLS context is defense in depth; any raw SELECT of plaintext is a confidentiality exposure. Protected reads still need independently validated identity/tenant/field permissions and cryptographic content verification.
- Runtime role membership/`SET ROLE` escalation, accidental role grants or table ownership, newly introduced tables without FORCE RLS, default PUBLIC function EXECUTE, SECURITY DEFINER unsafe search_path, owner-rights views, replication/export/backup pathways and admin Entra group overlap must be inventoried and tested.
- Shared cloud administrative accounts across BusinessDb and AuthorityDb, or shared workload/deployment secrets, collapse Level 2's independent-trust assumption; block production until independently governed IAM and network boundaries are evidenced.
- Unexpected privileged SQL operations must be observed in an independently controlled audit system; BusinessDb local logs alone are mutable by some privileged attackers.

**Approval gate:** Real Azure PostgreSQL/Entra privileges and networking, proper independent AuthorityDb boundary, approved migrations, automatic role/RLS function audits and tests PG-01..PG-20 must be demonstrated and independently reviewed. G08 remains P0/open until then. G14 coordinated disaster recovery remains the next critical design milestone.


## G14 trust and disaster recovery threat addendum

**Security rollback after backup restore:** an attacker or operator choosing an old Business/Authority PostgreSQL snapshot might resurrect earlier valid signed owner/share/team relationships, undone revocations or weaker mandatory security floors. **Control:** independent immutable replayable security-event archive, externally verified latest accepted revision/ledger receipt, trust authority replay and SpiceDB rebuild; prohibit old snapshot from defining accepted state. **Status:** design specified, test open (DR-01..DR-10, DR-24).

**Missing committed payload/key after recovery:** Merkle roots, row HMACs, ledger receipts and archive hashes can prove historical commitments, but cannot recreate lost immutable field bytes, documents or full semantic event payloads. **Control:** separately retained versioned content and journal bodies, recoverable historical Managed HSM keys, per-resource quarantine and no blind re-sign or substitution. **Status:** design specified, test open (DR-11..DR-21).

**Disaster privilege escalation/split-brain:** old deployments, restored sessions, old SpiceDB tokens or recovery operators could bypass current approval. **Control:** independently fenced monotonic recovery epoch, fresh sessions/workload identities, separate operator roles and two-person cryptographically attested release; verify IAM and RLS before routing. **Status:** design specified, test open (DR-22..DR-36).


## Security simplification implementation handoff — ADR-SEC-2026-10-16-01

The current architecture now selects the six changes in `security_hardening/SECURITY_SIMPLIFICATION_STANDARD.md` and orders them in `security_hardening/SECURITY_IMPLEMENTATION_PLAN.md`, with SIM-01..SIM-42 as mandatory adversarial test specifications. This is a **design decision only**, not an implementation, verified attack resistance or permission to close previously open findings.

- Canonical journal/event ownership and external audit intake: design clarified. **G11 remains OPEN** until tested reliable delivery, independently durable access evidence, WORM replay and ledger receipts.
- Organization encryption floor and sensitivity classification: design selected. **G07/G12 remain OPEN** until actual data inventory, encryption backfill, metadata protection and legacy export/backup risk acceptance.
- Bounded authority-enumerated V1 search plus independent index V2: design selected. **G06 remains OPEN** until query-completeness attack tests and scale evidence pass. Never falsely declare a plain business SQL search complete against DBA omission.
- User-specific phishing-resistant step-up is preferred for sensitive access; optional additional record owner challenge remains. **G09/G10 remain OPEN** until provider assertion and real revocation testing.
- Row HMAC + independent complete Merkle remain mandatory. No elimination of per-row HMAC has been approved. **G03/G04/G18 remain OPEN** until implementation, proof and tests.
- Checkpoint batching and duplicate audit projections reduce complexity only while preserving mandatory recoverable security-event bodies and G02/G14 replay barriers. **G02/G14 remain OPEN** until actual crash/recovery exercises.

Supersede the audit's language proposing *removal* only after the dependent acceptance gates; no destructive legacy-table drop or encryption downgrades are authorized by this addendum.

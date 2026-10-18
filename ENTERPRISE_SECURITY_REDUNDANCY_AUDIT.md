# Enterprise Dynamic Forms Platform — Security and Redundancy Audit

**Review date:** 2026-10-16  
**Assurance target:** Level 2 — an attacker can read, alter, delete, or replay records in **Business PostgreSQL** (including privileged DBA access or stolen SQL credentials), but does not control the independently administered Authorization Authority, its data store, HSM, ledger, identity provider, deployment, or business application executable.  
**Review nature:** Specification and reference-contract audit, **not** a source-code review, penetration test, cloud-configuration audit, or verified implementation.  
**Documents reviewed:** `CLAUDE.md` (Sections 1–12 and 4M–4S), `SECURITY_THREAT_MODEL.md`, `SECURITY_GAP_ASSESSMENT.md`, the Level 2 authority, consistency, cryptographic-signing, Merkle, PostgreSQL hardening, disaster recovery, and related-form designs, plus sample SQL, OpenAPI and C# contracts available in the package.

## Executive decision

**Keep the independent Authorization Authority, field-level authorization, encryption for sensitive fields/documents, independently committed record manifests, privileged-role isolation, and a recoverable security-command journal.** These controls directly address the selected hostile-Business-PostgreSQL threat model. The biggest complexity reduction is **eliminating duplicate authoritative data and event streams**, not removing the trust boundary.

**Do not replace application auditing and technical logs with Azure Confidential Ledger.** Use the ledger for *small tamper-evident checkpoints and receipts*, a queryable audit index for *who did what and when*, a durable immutable archive for *recoverable event bodies*, and structured logs for *diagnostics/alerts*. Avoid writing four independent copies of each full event.

**The high-value simplifications:** (1) one canonical audit-event model and no competing `SecurityAuditEvents`/`RecordShareEvents` authorities; (2) one canonical security-command journal, with SpiceDB and business-DB ACL tables explicitly derived; (3) ledger anchoring of batched commitments rather than every ordinary event; (4) reconsider redundant per-row HMACs only after proving equivalent protection from an independently committed complete Merkle manifest; (5) strongly consider identity-provider step-up instead of a shared record PIN for high-security records.

## A. What Azure Confidential Ledger does — and does not do

- **Ledger:** independent, append-only tamper-evident confirmation that specified checkpoint digests or events were committed. Receipt verification provides evidence of inclusion. It is not automatically a queryable, indexed "who opened document X?" system; it cannot recreate a lost underlying event body if only a hash was written, and it does not provide request traces, exception telemetry, or searchable reporting.
- **`AuditEvents`:** indexed, business-aware timeline of reads, downloads, field/record changes, designer edits, sharing, and denials. It may be corrupted or deleted by a hostile business DBA; trusted forensic evidence therefore requires an independent archive/correlation record. Keep this as a *query projection*, not as the sole authoritative evidence repository.
- **Recoverable authority journal:** exact independently protected semantic events required to rebuild ownership, membership, shares, revocations, security floors and revocation order. Cannot be replaced by a ledger containing just hashes, ordinary telemetry, or untrusted DB audit rows.
- **Technical logs and OpenTelemetry/Azure Monitor:** identify crashes, performance spikes, infrastructure permission errors, KMS latency and abuse signals. They provide operational evidence but are not the single authoritative business audit stream.
- **pgAudit:** selectively log SQL DDL, ROLE, unexpected privileged operations and selected sensitive queries. It cannot identify which *field values* a user actually saw through a complex application query; full `READ`/`WRITE` on every typed EAV table can generate very high volume and risk logging sensitive SQL literals. pgAudit logs are best-effort and not transactionally guaranteed; treat them as corroborating evidence.

**Recommended simplified event pipeline**

1. A business operation produces one versioned canonical event envelope with `AuditEventId`, tenant, actor, action, resource IDs, UTC event timestamp, result, correlation and security/content revision, classification and redacted metadata.
2. For a successful business write: commit record state + audit-event *index* + delivery outbox atomically in the responsible database. For authoritative security changes: journal first in the **authority database**, not the business DB. A read/denied request generates an application activity event through a durable bounded queue with an explicit outage policy.
3. Ship exact authorized security command bodies and required audit evidence to a separately administered, immutable, **replayable archive**, indexed by immutable event IDs. The queryable `AuditEvents` table and any SIEM copy are projections of the canonical events, not independently edited logs.
4. Periodically batch ordered event digests, authority high-water revision, archive object digest and prior checkpoint hash; anchor the compact checkpoint in **Azure Confidential Ledger**. Retrieve and independently verify receipts. A checkpoint cannot become a trusted recovery head until its referenced event bodies are durable and verified.
5. Send operational logs, alerts, traces and privileged PostgreSQL diagnostics to Azure Monitor/SIEM under separately managed access controls. Correlate with event IDs; do not persist plaintext form values, short PINs, tokens, decrypted filenames or raw search terms.
6. Reconcile ledger receipt -> checkpoint digest -> archive event bodies -> authority journal revision -> SpiceDB projection -> business audit index; alert on gaps, missing receipts, delays, duplicated event IDs and unexpected event schema versions.

**Risk if the audit trail is removed:** No reliable record of who read a document, no practical actor/time/field forensic search, poor regulatory evidence, and no independently provable failed-access history. Ledger receipts would establish only the checkpoints that were submitted.

**Risk if the ledger is removed:** Business and authority administrators may be able to roll back or rewrite event/archive/checkpoint state unless an *equally independent, continuously verified anti-rollback high-water mark* is introduced. For the selected Level 2 design, keep the ledger or formally prove a substitute.

## B. Component-by-component keep / consolidate / remove matrix

| Component / overlap | Decision | Justification | Risk if removed or weakened |
|---|---|---|---|
| Independent .NET Authorization Authority and separately governed authority DB | **KEEP — P0** | Stops forged BusinessDb owner/team/ACL GUIDs from granting app-mediated access | **Critical:** stolen business SQL credentials can manufacture permissions; Level 2 claim fails |
| SpiceDB permission graph | **KEEP initially; evaluate cost** | Expresses team relationships, scoped record shares and delegated permissions; authority controls writes | Removing it is **not inherently insecure** if replaced by a single independently governed, formally tested SQL permission evaluator. But current design/contracts and revocation protocol must be redesigned |
| Normalized authority state + SpiceDB projection | **KEEP distinct ownership; avoid duplicate writers** | One trusted write journal, one materialized permission engine | Independent direct writes create split-brain, stale grants, and difficult revocation |
| BusinessDb `owner_user_id`, team/share mirror tables | **OPTIONAL READ-ONLY PROJECTIONS** | Can help display/reporting but must not influence decisions | Safe to remove if all views/query plans are adjusted; risk is performance/UX, not reduced security. **Never** allow them to become a fallback authority |
| PostgreSQL RLS + authority checks | **KEEP BOTH** | RLS adds tenant/role defense in depth; authority covers hostile DBA | Without RLS a simple endpoint/SQL mistake can leak plaintext rows; without authority, DBA can forge access |
| Separate BusinessDb and AuthorityDb administrative identities and networks | **KEEP — P0** | Essential trust boundary | **Critical:** one compromised DBA can alter both permission source and business data |
| Application-level AES-GCM encryption for sensitive fields/files | **KEEP** | Stops SQL readers and blob-store readers from seeing those protected contents | **Critical/High confidentiality loss** for protected PII, files and historical values. Unencrypted fields remain readable to DBAs today |
| HSM-backed integrity keys | **KEEP** | Protects MAC/signing keys away from BusinessDb | Attacker with SQL write/read plus key can forge local integrity if other controls depend on it |
| Vault Transit local Docker | **KEEP DEV ONLY** | Tests remote signer failure modes; same conceptual interface | Without a remote dev adapter, integration failures appear late; never treat Vault dev mode as production-grade |
| Every-row HMAC + complete Merkle manifest + external root | **CONDITIONAL SIMPLIFICATION** | HMAC proves a row was authenticated; Merkle root with complete leaf-index proves membership/completeness against BusinessDb manipulation | Dropping HMAC before independently rooted SHA-256 leaves, canonical serialization, fresh heads, and trusted read/commit paths are proven reopens tampering. Once proven, per-value HMAC may be cryptographically redundant; HMAC/sign the root/checkpoints instead and bind record/tenant/type/revision to each leaf |
| Record Merkle manifest + Confidential Ledger Merkle receipts | **KEEP BOTH** | One commits the set of record rows/attachments; the other proves checkpoint inclusion in the external ledger | Removing record manifest loses reliable field/document membership verification; removing external anchor enables rollback |
| ACL security graph membership commitment + authoritative journal | **CONSOLIDATE IF SAFE** | Authority journal can rebuild exact normalized relationships; a complete state digest is useful for checking projections | Separate per-row graph HMAC and full graph Merkle roots may be redundant if signed journal + trusted high-water mark + reproducible graph digest provide equivalent proof. Requires deterministic replay and adversarial tests |
| `AuditEvents` + `SecurityAuditEvents` | **ONE CANONICAL AUDIT STREAM** | Existing Section 4D already prohibits competing authoritative events | Duplicated independent writes disagree and introduce silent gaps; keep only an optional security-event *view/projection* |
| `RecordShareEvents` + `AuditEvents` | **CONSOLIDATE** | Treat share event as a typed audit event plus share-specific details, not a second event authority | Removing separate table is low risk if revocation provenance and replay semantics live in protected authority journal |
| `AuditFieldChanges` + historical typed-value versions | **KEEP SEPARATE PURPOSES, MINIMIZE PAYLOAD** | Audit points to what changed; immutable typed histories recover previous content | If both store old/new plaintext, unnecessary PII duplication; prefer redacted field metadata/pointers, restore exact values from access-controlled history |
| Audit archive + ledger | **KEEP FUNCTIONS, BATCH LEDGER** | Archive restores event bodies, ledger anchors integrity and high-water status | No archive = cannot reconstruct missing security commands; no independent checkpoint = rollback/downgrade may be undetected |
| Azure Monitor / OpenTelemetry + AuditEvents | **KEEP BOTH, NO FULL DUPLICATE EVENTS** | Operational traces differ from exact business audit evidence | Without telemetry, incidents/outages become difficult to detect; avoid paying to store full form-change payloads multiple times |
| pgAudit for all SQL activity | **NARROW, NOT EVERY QUERY** | Audit privileged DDL, ROLE and suspicious direct DML; direct data reads where risk requires; avoid logging all parameter values | Disabling all pgAudit removes ability to investigate privileged bypass/direct SQL activity; logging everything risks excessive cost/PII exposure |
| Record PIN/passphrase + Entra FIDO2 assurance | **OPTIONAL / PREFER STEP-UP** | Shared PIN is a second gate but is low entropy, redistributable and hard to attribute to one team member | Removing PIN reduces optional protection against a compromised unlocked session but improves usability/secret lifecycle. A fresh individual FIDO2 step-up and current authority checks can offer better assurance and attribution |
| `RowSignature` on all historical ordinary rows and `AuditEvents` | **QUESTION DEFAULT SCOPE** | High-security DB data integrity is needed; ordinary audit index is derived and independently archived | Omitting row signature from *untrusted query projections* is low risk if rebuilt/verified from independently committed source; omitting integrity for original protected content before alternative proof is high risk |
| FormVersion JSON + FieldDefinitions + FormVersionFields | **KEEP EXISTING TWO; DO NOT ADD FormVersionFields** | Versioned JSON is immutable display/validation snapshot; definitions are stable typed identities | Adding duplicate FormVersionFields increases drift and migration complexity without current need |
| Repeating groups + related child forms | **KEEP BOTH, LIMIT FEATURE OVERLAP IN UI** | Same-record repetition versus independently identifiable child records | Removing repeating groups forces heavyweight child records everywhere; removing child forms breaks reusable child security/versioning relationships |
| Organization option catalog + organization availability + user preferences | **KEEP, CONSOLIDATE OPTION RESOLUTION** | Global options, tenant allowlists/defaults and per-user presentation overrides serve distinct purposes | Removing policy resolver enables forbidden options; duplicating per-tenant country/timezone catalogs causes drift |
| Complicated global fail-closed revocation gate | **KEEP SAFETY, OPTIMIZE SCOPE LATER** | Simple organization-wide barrier stops stale privilege reuse while projection catches up | Removal without safe replacement enables stale revocations; keep initial design but measure blast radius under SpiceDB lag |
| Separate recovery controller / dual approvals | **KEEP FOR HIGH-ASSURANCE RECOVERY** | Restoring trusted revisions or keys can defeat all other controls | Without it, a privileged operator can resurrect revoked permissions or old HSM keys without evidence |

## C. Security risks and correctness gaps (new review)

Severity is **risk before control validation**, not proof that deployed code is vulnerable. Existing G01–G18 items remain open until implemented and tested.

### C1 — Critical release blockers

**C-01. Untrusted search completeness and inference — G06 / SEC-07.** The spec admits that verifying only rows returned by PostgreSQL cannot detect a malicious DBA suppressing matching records by changing an index, search projection or predicate data. Don't advertise complete/fair search under Level 2 until you implement a trusted indexed projection or verified exhaustive scan at an acceptable cost. Verify field *predicate* permissions before planning a query; protect counts, aggregations, joins across child forms, autocomplete, and ordering. A read-authorized user may still not be permitted to search that field. **Recommendation:** Build a restricted trusted search projection from verified committed data in independently administered storage, versioned by content and authority revision; or initially restrict Level 2 queries to bounded verified scans and disclose limitations.

**C-02. Raw SQL readers see unencrypted field values and potentially metadata — G07.** A business DBA can directly read typed strings, date values, filenames, record summaries and previous audit values. Signatures do nothing for confidentiality. **Recommendation:** default encryption for classes of PII and confidential documents, protect sensitive file names/metadata and audit payloads, perform live value-store classification inventory. Tenant security minimum floors override Designer choices. If DB reader confidentiality is a requirement for all customer data, choose encryption by default for all user-entered fields, accepting reduced search capability.

**C-03. Write/permission consistency is not proven — G01/G02/G14.** Authority DB journal, SpiceDB projection, WORM event archive, business DB candidate revisions, blob objects and ledger checkpoints cannot participate in one ordinary ACID transaction. A crash at each boundary must not open stale permissions or discard accepted bytes. **Recommendation:** implement the normative state machine in integration tests with one-process-at-a-time crashes, idempotency, recovery epochs and deny-by-default before enabling protected APIs. Don't declare G01/G02 closed from successful schema creation.

**C-04. Authority compromise is a critical trust concentration.** Though excluded from Level 2 attacker capability, a vulnerability in the Authority API can forge ownership, create signatures or approve decryption across tenants. The generic crypto provider interface alone does not prevent signing-oracle misuse. **Recommendation:** separate decision, command, signing and decrypt workload identities; minimal commands with actor assurance and policy revisions; forbid arbitrary `Sign(payload)` through externally callable interfaces; harden service-to-service identity (verified audience/issuer, mTLS or equivalent, private networks); independent security review. Log KMS use and alert on unusual rates. Maintain tenant-scoped key-purpose separation.

**C-05. Rollback-proof high-water must survive restore and ledger outages — G14.** A chain of hashes without an independently retained monotonic accepted revision cannot detect a fully rolled-back snapshot. An immutable ledger receipt proves a checkpoint but does not restore missing event bodies. **Recommendation:** archive full approved security-command bodies before effectiveness; anchor authoritative head/recovery epochs outside both DBs; verify ledger receipts; rehearse recovering a newer revocation after both DBs are restored from older points.

**C-06. Signed content integrity and membership contract must be executable — G03/G18.** Verify that canonical payload encoding covers tenant ID, form/record/field IDs, nullable state, logical type, version, document ciphertext hash and unique item ID. Missing or injected rows are verified against the *complete* independently accepted leaf index; per-row HMAC alone does not detect deletions. Add a schema-change registry that fails CI when new protected columns are unsigned. A proof of a subset is not a proof that all authorized results were returned.

### C2 — High-priority issues

**H-01. Excessive logging and immutable PII.** The documents permit unencrypted audit payloads and ordinary before/after field values. Users may type sensitive content into ostensibly ordinary fields. A permanent ledger/WORM archive magnifies retention and data-minimization mistakes. Default to **no raw field value, search term, secret, filename or document byte** in immutable channels. Store pseudonymous IDs and hashes with separately controlled lookup; keep redactable human descriptions in appropriately governed query projections. Confirm retention/legal hold and permitted deletion of sensitive data *before* anchoring.

**H-02. Shared record access PIN weakens attribution.** A team-wide secret can be forwarded, guessed if short, harvested from an application prompt, or retained by an ex-team member. Argon2id does not make a 4-digit shared PIN sufficiently secret after its hash is stolen. For protected records, prefer a verified **fresh user-specific Entra/passkey step-up**, with role/record scopes and short TTL; use strong passphrase only as an additional optional business requirement, not the main access factor. Audit individual identity, not just challenge success.

**H-03. Irreversible FIDO2 policy is not physically irreversible at the IdP.** A database or application rule cannot stop a privileged Entra administrator from changing Conditional Access externally, and Conditional Access may be evaluated after initial sign-in. Implement provider-policy conformance checks, periodic drift alarms, just-in-time privileged policy changes, recovery ceremony and verified token assurance. Treat non-downgrade as a **system control and monitored obligation**, not an unbreakable identity-provider guarantee.

**H-04. Fragile revocation and long-lived requests.** Delegated child shares, batch exports, signed URLs, cached layouts, in-flight streaming and queued background jobs may outlive a revocation. Define a measurable maximum revocation lag; recheck at download start and chunk/window boundaries as feasible; revoke cached authorization by policy revision; stop queued/export deliveries upon revocation. Bytes already released cannot be recalled.

**H-05. Site-wide outage amplification.** Authority-first + organization-wide gate blocks *all* protected reads for an organization whenever SpiceDB projection or mandatory ledger anchoring is delayed. A single large team change, transient ledger failure or slow worker could cause a tenant-wide outage (or attacker-induced DoS). Begin with the conservative barrier but add rate limits, fair tenant queues, projections SLA, incident alerts, and eventually an audited **resource-scoped gate** with proof of safe invalidation.

**H-06. Cryptographic signing costs / verification semantics.** Remote HSM HMAC on every typed value in every read/search can explode latency, service costs and error exposure. Use batch verification, independently anchored root/leaf proofs and per-request budgets; consider replacing inner per-field HMAC with SHA-256 digest leaves only after formal proof and adversarial tests. Explicitly test that the Vault Transit and Managed HSM adapters sign and verify **identical canonical bytes** (beware differences between APIs that expect prehashed input and those that hash inside their crypto operation). Keep keys and sign privilege in the Authority.

**H-07. Dangerous plaintext copies during encryption migration.** Switching a field from searchable plaintext to application-encrypted does not erase it from backups, WAL, search indexes, exports, historical audit values, thumbnails or temp files. Require an explicit versioned migration and inventory; show irreversible old-backup exposure windows instead of promising retroactive secrecy.

**H-08. Scoped sharing/related-form composition is an authorization graph, not simple ACL.** A parent share must not leak child existence, counts, search predicates, attachments, newly added child fields or replaced versions. Parent/child ownership and challenge floors must both hold; unlink/reparent cannot confer implicit access. Avoid granting item permissions from a stale parent manifest or mixing grants to forge stronger delegated rights. Property-based graph tests and cycle/edge tamper tests are mandatory.

**H-09. Optional security options and legacy invalid selections.** An organization disabling a country/time zone/format or a designer removing a field may make historical records unsavable even when an unrelated field is changed. This is not a direct security exposure but creates denial-of-service and data-consistency risk. Define per-field save validation, explicit migration/deprecation windows and precise grandfathering rules; do not silently change protected historical values.

**H-10. Overlapping persistence and recovery complexity.** Security events may be written to the authority journal, queryable `AuditEvents`, an outbox, an immutable archive, a SIEM and ACL. Without one event ID/schema and controlled projections, the same event may appear under conflicting timestamps/outcomes, be missing in one place or create unrecoverable gaps. Choose a canonical event ID and an explicit authoritative storage owner; outbox is a delivery mechanism, not a second business-history stream. Reconcile lags automatically.

**H-11. Security defaults without evidence.** Cloud isolation, RLS, FIDO2, private blob/HSM endpoints, service identities, vault configuration, audit export and backup protection are all currently specifications. Require production-equivalent IaC, policy-as-code, executable negative tests, external penetration test, and quarterly recovery drills before marking release gates passed.

**H-12. pgAudit and application audit are not interchangeable.** pgAudit is valuable for DBA DDL/ROLE events, unexpected SQL reads and direct SQL abuse; it is best-effort and can emit SQL text/parameters. Configure only required classes, verify Azure integration, monitor audit gaps, and never use pgAudit as the sole source of business reads. Disable sensitive parameter logging; audit new SQL functions/roles explicitly.

### C3 — Important medium risks

- **M-01 Threat-model drift:** the Level 2 boundary trusts the business API runtime and IdP. Document that SQL-only intrusion is covered, but a compromised business API or supply-chain artifact is a different threat; monitor for runtime compromise.
- **M-02 Retention versus immutable archival:** design data minimization, regulated/legal deletion and retention before choosing ledger body/public/private format; record identity is still metadata even if field plaintext is excluded.
- **M-03 Localization/canonicalization:** user-controlled locale/time zone must *never* affect HMAC/Merkle bytes; canonical serialize UTC instants, invariant decimal encoding, explicit nulls and normalized UTF-8.
- **M-04 Availability and cost:** benchmark 100K/1M records, large child lists, long retention and multi-tenant keys, document transfers, outbox/ledger batch sizes and journal replay time. Define RTO/RPO and denial budgets.
- **M-05 Incident handling:** corrupted signed projections should be quarantined without leaking values; classify attacker-caused downtime as a security event and define safe read-only workflows only where independently proven safe.

## D. Recommended reduced Level 2 architecture

```text
                           Microsoft Entra ID / approved IdP
                                       |
                               Business API / UI
                                       |
               +-----------------------+---------------------+
               |                       |                     |
     Business PostgreSQL       Ciphertext Blob Store   OpenTelemetry / Azure Monitor
     typed values + forms      scanned versioned files     health and alerts
     queryable AuditEvents
               |
     immutable row candidates
               |
               v
                  Independent Authorization Authority
               |                |                  |
          authority DB       SpiceDB           Managed HSM
        one security         derived graph     key custody +
        command journal                        approved MACs
               |
        immutable replayable SECURITY / AUDIT EVENT ARCHIVE
               |
     batched ordered digest + high-water / checkpoint hash
               |
             Azure Confidential Ledger + verified receipts
```

**One authoritative security event**, multiple purpose-specific read projections. The ledger is a tamper-evidence and anti-rollback anchor, not an alternate activity database. Keep document bytes and replayable security event bodies in versioned, recoverable stores. Keep ACL and record Merkle roots distinct because they commit different sets.

## E. Prioritized implementation and simplification plan

### Immediate decisions (before writing more code)

1. **Document the canonical event ownership.** Authority DB owns approved security commands, each with an immutable event ID and replayable archive. Business workflow owns business-change activity records; a shared event schema and immutable archive receipt provide cross-system correlation. Never perform two independent writes to two competing authoritative audit tables.
2. **Keep ledger but limit its payload.** Checkpoint per bounded time/revision batch, with externally verified receipts and complete WORM-archived event bodies. Include high-water authorization and content head revisions, prior checkpoint digest, archive digest, and schema/key version. Do not include PII or unencrypted raw record values.
3. **Make `AuditEvents` a search index/projection.** Use `AuditFieldChanges` for field identities and redacted changes; do not duplicate the complete recoverable typed-value history in audit JSON. Eliminate authoritative `SecurityAuditEvents` and `RecordShareEvents`, retaining typed views as necessary.
4. **Define unencrypted-field security floor.** Explicitly accept residual DBA-read exposure or require application encryption for all user-entered sensitive data. No ambiguity in organization policy/defaults.
5. **Implement a minimal trusted search solution** and clearly declare which operations offer completeness against hostile BusinessDb writers. Consider independent searchable read models for approved non-encrypted fields.
6. **Decide the record PIN policy.** Recommend fresh per-user provider step-up for higher assurance and phase out shared short PINs; if retained, mandate strong passphrases, throttle online attempts, protect hashes, and bind unlock to current authority revision.

### Before production

7. Execute authority + SpiceDB crash/revocation tests, document how one canonical event propagates and is recovered.
8. Verify RLS privileges, deny `BYPASSRLS`, isolate AuthorityDb IAM, enforce HSM narrow identities, set provider start-up guards, and run SQL-GUID substitution/replay tests.
9. Run a security/performance proof comparing **HMAC per row + Merkle root** against **domain-separated SHA-256 leaves + independently authenticated root + complete trusted index**. Remove the redundant MAC layer only after equivalence is demonstrated for threat model and recovery.
10. Run full replayable-journal and actual Azure DR test (BusinessDb/AuthorityDb/SpiceDB/Managed HSM/blob/ledger).
11. Add automatic audit/liveness reconciliation: no missing required events, authority revisions, checkpoint receipts, WORM object digests or search-projection heads.
12. Complete app pentest, threat-based property tests, review of all upload/preview paths, and signed build/deployment validation.

## F. Risk acceptance guide for proposed removals

| Proposed removal | Expected risk change | Decision |
|---|---|---|
| Remove all logging because we have ACL | **Unacceptable High/Critical forensic loss.** No field/document user-view timeline, weak operations visibility, checkpoints alone insufficient | **DO NOT REMOVE** |
| Remove full-copy security events from ACL but keep batched digests/receipts + immutable replayable archive | **Low to moderate**, provided receipt chain, completeness and WORM recovery are tested; fewer immutable PII exposures | **RECOMMENDED** |
| Remove second authoritative `SecurityAuditEvents`/`RecordShareEvents` table | **Low; often reduces risk** if one canonical event pipeline exists | **RECOMMENDED** |
| Remove pgAudit entirely | **High detection loss** for direct DBA SQL and unauthorized schema/role changes; not needed for every read though | **NARROW ONLY** |
| Remove PostgreSQL RLS | **High** developer-error/tenant-isolation exposure | **KEEP** |
| Remove SpiceDB without replacing checks | **Critical** share/owner permission bypass | **DO NOT REMOVE**; a full independent evaluator is possible, but not a drop-in deletion |
| Remove independent AuthorityDb / independent authority | **Critical** Level 2 breaks | **DO NOT REMOVE** |
| Remove per-row HMAC only after complete independent Merkle proof implementation | **Potentially low** against BusinessDb-only tampering, but requires strict formal proof and validation | **FUTURE OPTIMIZATION**, not immediate removal |
| Remove record Merkle manifests but keep row HMAC | **Critical** missing-row/replay undetected | **DO NOT REMOVE** |
| Remove Confidential Ledger without equivalent independent anti-rollback anchor | **Critical** integrity rollback/recovery assurance weakened | **DO NOT REMOVE** |
| Remove external immutable security journal/archive | **Critical** security replay/DR breaks; ledger hash cannot restore semantic events | **DO NOT REMOVE** |
| Remove application-level encryption | **High/Critical** raw SQL readers recover protected plaintext | **DO NOT REMOVE** for sensitive data |
| Remove shared PIN in favor of current per-user FIDO2 step-up | **Low or improved assurance** if implemented with session/policy/tenant/record binding; may lose a separate owner-held secret factor | **CONSIDER REPLACING** |
| Remove `FormVersionFields` (not currently used) | **Low; simplifies design** because immutable JSON already provides version-specific UI | **KEEP REMOVED** |

## G. Evidence and references

### Local design evidence
- `CLAUDE.md` §4D: one unified audit event stream; §§4K–4R: authority, integrity, consistency, privileges and recovery; §4S: related records.
- `SECURITY_GAP_ASSESSMENT.md` G01–G18 and `SECURITY_THREAT_MODEL.md` SEC-01–SEC-42.
- `trust_authority/TRUST_AUTHORITY_ARCHITECTURE.md`: authority/source of truth, SpiceDB projection, trust boundary.
- `trust_authority/CROSS_SYSTEM_CONSISTENCY_PROTOCOL.md`: authority-first revision barrier, pending/effective state.
- `trust_authority/MERKLE_RECORD_MANIFEST_PROTOCOL.md`: complete record membership proof and its limits for search completeness.
- `trust_authority/COORDINATED_DISASTER_RECOVERY.md`: replayable journal and separately restored content bytes.

### External technical guidance (verified 2026-10-16)
- Microsoft — [Azure confidential ledger overview](https://learn.microsoft.com/en-us/azure/confidential-ledger/overview); [secure ledger, receipts, monitoring and operational limits](https://learn.microsoft.com/en-us/azure/confidential-ledger/secure-confidential-ledger).
- OWASP — [Logging Cheat Sheet](https://cheatsheetseries.owasp.org/cheatsheets/Logging_Cheat_Sheet.html).
- PostgreSQL — [Row Security Policies](https://www.postgresql.org/docs/current/ddl-rowsecurity.html).
- Authzed — [SpiceDB Consistency and ZedTokens](https://authzed.com/docs/spicedb/concepts/consistency).
- Microsoft — [Managed HSM key types, operations and HMAC](https://learn.microsoft.com/en-us/azure/key-vault/managed-hsm/about-keys-details).
- pgAudit — [extension limitations and performance](https://github.com/pgaudit/pgaudit); Microsoft — [pgAudit in Azure Database for PostgreSQL](https://learn.microsoft.com/en-us/azure/postgresql/security/security-audit).
- Microsoft — [Entra authentication strengths limitations](https://learn.microsoft.com/en-gb/entra/identity/authentication/concept-authentication-strengths).
- Microsoft — [Azure Monitor diagnostic settings](https://learn.microsoft.com/en-us/azure/azure-monitor/platform/diagnostic-settings?tabs=por).

## H. Conclusion

**Retain multiple security layers when they enforce different properties; remove multiple *authoritative copies* of the same state.** With the Level 2 attacker model, the independent authority and anti-rollback commitments are not expendable. Logging is also non-expendable: make it less duplicative and less sensitive. Simplification must be accompanied by a concrete attack test, not only an architecture diagram. Currently these are only specifications, and no implementation code or cloud environment has been penetration-tested in this review.


## Security simplification implementation handoff — ADR-SEC-2026-10-16-01

The current architecture now selects the six changes in `security_hardening/SECURITY_SIMPLIFICATION_STANDARD.md` and orders them in `security_hardening/SECURITY_IMPLEMENTATION_PLAN.md`, with SIM-01..SIM-42 as mandatory adversarial test specifications. This is a **design decision only**, not an implementation, verified attack resistance or permission to close previously open findings.

- Canonical journal/event ownership and external audit intake: design clarified. **G11 remains OPEN** until tested reliable delivery, independently durable access evidence, WORM replay and ledger receipts.
- Organization encryption floor and sensitivity classification: design selected. **G07/G12 remain OPEN** until actual data inventory, encryption backfill, metadata protection and legacy export/backup risk acceptance.
- Bounded authority-enumerated V1 search plus independent index V2: design selected. **G06 remains OPEN** until query-completeness attack tests and scale evidence pass. Never falsely declare a plain business SQL search complete against DBA omission.
- User-specific phishing-resistant step-up is preferred for sensitive access; optional additional record owner challenge remains. **G09/G10 remain OPEN** until provider assertion and real revocation testing.
- Row HMAC + independent complete Merkle remain mandatory. No elimination of per-row HMAC has been approved. **G03/G04/G18 remain OPEN** until implementation, proof and tests.
- Checkpoint batching and duplicate audit projections reduce complexity only while preserving mandatory recoverable security-event bodies and G02/G14 replay barriers. **G02/G14 remain OPEN** until actual crash/recovery exercises.

Supersede the audit's language proposing *removal* only after the dependent acceptance gates; no destructive legacy-table drop or encryption downgrades are authorized by this addendum.

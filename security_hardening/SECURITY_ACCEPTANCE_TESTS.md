# Security Simplification — Adversarial Acceptance Test Matrix

**Status:** Test specifications only; no implementation, PostgreSQL service, Azure configuration or adversarial execution has occurred in this artifact creation.  
**Environment:** disposable PostgreSQL 18 BusinessDb and separate AuthorityDb, SpiceDB, Vault Transit Docker for developer tests and Managed HSM + Confidential Ledger + Entra in staging; separate identities, injected faults and independent evidence sink.  
**Passing rule:** validate business effect, trusted revision, projected UI/API result, and external audit evidence; a denied API call alone does not prove integrity or completeness.

| Test | Attack / scenario | Expected assertion |
|---|---|---|
| SIM-01 | Owner changes record and share in same operation | One canonical ID links approved authority command and derived local timeline; no double authority |
| SIM-02 | Forge `record_share_events` legacy row | Legacy view has no independent permission/write path; authority rejects grant |
| SIM-03 | BusinessDb DBA deletes projected `audit_events` | Archive reconciliation flags missing event, recovery rebuilds projection |
| SIM-04 | Access to Confidential PDF while audit intake down | Protected bytes not released until required independent event intake acknowledges or explicitly compliant durable path recovers |
| SIM-05 | Audit actor metadata includes newline/PII/token | Sanitized/redacted, no log injection, no sensitive values in archive/telemetry |
| SIM-06 | Retry outbox delivery 100 times | One logical event identity, no phantom duplicate activity/security command |
| SIM-07 | Revoke sharing while BusinessDb outbox tampered | Effective revocation remains in authority + replayable archive; no access after barrier |
| SIM-08 | Audit query cross-tenant/unauthorized field filters | No event or sensitive metadata from another tenant; audit access itself logged |
| SIM-09 | Same-time access events and ownership transfers | Canonical source sequence preserved; UI doesn't claim global commit order it cannot prove |
| SIM-10 | Submit ledger hash but delete referenced archive body | Checkpoint unrecoverable => quarantine; never treat hash as replayable event |
| SIM-11 | Batch ledger entries then independently verify receipt | Receipt inclusion and archive/event digests match accepted revisions |
| SIM-12 | Ledger unavailable before security change Effective | No unsafe Effective state or permission release; deterministic retry |
| SIM-13 | Replay prior valid checkpoint after revocation | Externally trusted head/recovery epoch rejects rollback |
| SIM-14 | Restore old authority snapshot and new ledger | Replay required events, SpiceDB rebuilt; revoked permissions remain denied |
| SIM-15 | New form field with unknown classification | Defaults Confidential; ciphertext storage; not globally plaintext indexed |
| SIM-16 | Designer tries to lower Restricted to Internal | Rejected by tenant floor/authority unless explicitly governed migration permitted; no silent plaintext release |
| SIM-17 | DBA reads Confidential string/file/cached filename | No raw plaintext in BusinessDb/object bytes/unsafe metadata; classified metadata encrypted |
| SIM-18 | Field encryption migration interrupted after write | No mixed state exposed or incorrect search index; quarantined or consistent committed state |
| SIM-19 | Old WAL/export/backup contains plaintext | Exposure inventoried, retention/cleanup limitations documented; no false secure-erasure claim |
| SIM-20 | Plaintext searchable Internal field approved | Authority confirms classification; DBA-read residual risk explicitly accepted |
| SIM-21 | DBA deletes matching BusinessDb record before search | V1 independently enumerates expected ID; query fails/quarantines, not silent zero matches |
| SIM-22 | DBA removes matching string row | Manifest detects missing leaf; no incorrect complete search result |
| SIM-23 | DBA injects fake matching record | Authority record registry rejects; no fabricated match |
| SIM-24 | Search V1 exceeds configured scan max | Explicit `SEARCH_VERIFICATION_LIMIT`; no silent incomplete result |
| SIM-25 | Trusted index V2 lags revocation | Query denied or waits for fenced revision; cached allow not honored |
| SIM-26 | Trusted index restored from older snapshot | Accepted head mismatch, rebuilding/quarantine before release |
| SIM-27 | User searches unauthorized restricted field with count/facet | Field predicate denied without revealing matching unauthorized data |
| SIM-28 | Partial-share child-collection search | Only explicitly shared child IDs/fields/documents considered, no counts of hidden siblings |
| SIM-29 | `amr=mfa` without proven FIDO2 fresh step-up | Restricted operation denied |
| SIM-30 | Team member revoked after step-up | Session binding and authority epoch invalidate permitted access |
| SIM-31 | Record Owner sets passphrase, recipient knows it but has no share | No record discover/read/decrypt privilege |
| SIM-32 | Owner passphrase rotated during active export | Subsequent protected bytes/authorization lease blocked within defined revocation guarantee |
| SIM-33 | IdP policy weakened outside application | Drift detected and stronger tenant floor enforced locally where provable; otherwise deny |
| SIM-34 | Recovery admin attempts step-up bypass | Requires separate authorized, time-bounded dual approval and auditable recovery policy |
| SIM-35 | Modify one signed row with old root | Row HMAC/Merkle/current head rejects |
| SIM-36 | Delete entire leaf and preserve remaining HMACs | Complete independently accepted manifest detects deletion |
| SIM-37 | Replay old valid record/child-link snapshot | Revision head/epoch rejects replay |
| SIM-38 | Prototype unkeyed SHA-256 of tiny plaintext field | Confidentiality review detects offline guessing risk; do not adopt unsafe optimization |
| SIM-39 | Compare candidate Merkle-only against existing HMAC+Merkle | All hostile DBA threats, key rotation, partial sharing, DR, schema evolution pass before protocol approval |
| SIM-40 | HSM and Vault Transit outage/slowdown | Required protected operations fail closed; measured error and latency budgets recorded |
| SIM-41 | Load 1M records and 100-child lists under concurrent revocations | No stale permissions, bounded memory/paging, recorded performance measures |
| SIM-42 | Restore business/authority/search archives with old tokens | Recovery epoch fences old nodes/sessions; accepted current security state restored |

## Security implementation evidence template

For each test: `TestIdentifier`, `GitCommitIdentifier`, `EnvironmentIdentifier`, `ExecutedOnUtc`, `TestActorIdentifier`, `ExpectedOutcome`, `ObservedOutcome`, `EvidenceArchiveReference`, `VerifiedAuthorityRevision`, `VerifiedContentRevision`, `LedgerReceiptIdentifier` (when applicable), `SecurityReviewerIdentifier`, `RetestResult`. Never put credentials, PINs, protected plaintext or raw access tokens in evidence files.

## Minimum code-level negative-test locations

- API controllers/middleware: unauthorized fields, authenticated issuer/subject, record challenge, audit intake outage.
- Authority state machine: duplicate commands, revocation barrier, tampered mirrored ownership, replay of archived command.
- BusinessDb + verifier: row HMAC, leaf-set manifest, missing expected row, unauthorized added record.
- Search engine: V1 full authority registry evaluation; V2 trusted index freshness/rollbacks/predicate support.
- Files: upload scanning, ciphertext-only storage, streaming/download audit and revocation.
- Disaster recovery: replayed security events, receipts, storage/key versions, epoch fencing.

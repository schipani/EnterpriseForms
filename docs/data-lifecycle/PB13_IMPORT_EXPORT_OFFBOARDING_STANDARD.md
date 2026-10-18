# PB-13 — Import/Export and Offboarding Implementation Standard

**Approved:** ADR-PB-013. **Status:** Design and acceptance contract; not implemented.

## Functional contract by feature

| Feature | Minimum first-release UX | Backend responsibility | Critical boundary |
|---|---|---|---|
| Import Center | upload CSV/JSON, choose target form/version, map headers, preview issues, start/cancel/retry job, per-row results | validate types and classification, stage content, resolve trusted identities, create records/relationships through verified commands, scan/encrypt files, idempotent progress | import privilege never equals unrestricted direct DB write or security grant |
| Export Center | scope selection, preview permitted fields/documents, step-up/approval, progress/expiry, secure download | snapshot authorized scope, export portable JSON+documents with checksums, envelope-encrypt, audit, time-bound access | admin status alone gives no decrypt/export capability |
| Organization Offboarding | request review, verified export receipt, hold/retention overview, approvals, freeze/unfreeze only where permissible, staged disposition status | Authority-enforced write freeze and revocation, journal, legal hold checks, storage cleanup scheduling, receipts | no silent deletion of held/immutable evidence or reactivation of revoked access |

## Import processing contract

`POST /api/v1/imports` initializes draft intake; chunk/session API follows PB-07. The import job has the states `Created`, `Uploaded`, `Validated`, `ReviewRequired`, `Approved`, `Processing`, `Completed`, `PartiallyCompleted`, `Failed`, `Cancelled` and `Quarantined`. Publish durable per-row error reports free of secrets. Every retry is linked to an idempotency key and recorded as an event; never duplicate committed records when a worker crashes after a DB commit. Use versioned mapping configuration; give valid row limits, file quotas and parsing safeguards. Defend against CSV formula injection on exported spreadsheet-compatible reports, zip bombs, deeply nested JSON and Unicode identifier spoofing.

Import policy must always run target form validation and security checks. If the importer submits a target owner, team ID or relationship, resolve and validate it through independent authority commands; reject source-supplied privilege claims. Prevent duplicate and cycle relationships, uphold UUIDv7 keys, descriptive PG column naming and all per-tenant constraints. Treat imported documents as hostile untrusted input until malware scanning, quarantine release and authorized signed storage commit are complete.

## Export contract and confidentiality

`POST /api/v1/exports` requests an authorized export, not a raw database dump. The API returns `202 Accepted` with an operation/status URL and no data until the job passes all required checks. Define `AuthorizedProjection`, `FullTenantAuthorized`, and `MigrationPackage` intents with distinct entitlements and documented review/step-up. No absolute assertion that a full export is available to a sole Organization Owner. Include a manifest: format/schema version, logical tenant and form identifiers, identity mapping requirements, field classifications, form and record revision references, relationship edges, ordered file versions, byte lengths and hashes, and export snapshot/authority context. Include format checksums; document both confidentiality and eventual integrity proof independently of self-signed metadata.

Re-evaluate authorization on beginning, during sensitive reads, before final package publication and at download time. A revocation during a batch invalidates downstream work and must not publish a stale snapshot. Package files with a strong encryption mode and envelope-wrapped keys managed under the approved security boundary. Retain immutable audit entries for initiation, selection, approval, decryption, creation, failed attempt and download without logging plaintext field values.

## Offboarding contract and retention

Define a durable state machine with an independently authorized `FreezeWrites` command and release/approval barriers. Idempotent steps must handle retry after crashes and resist business-DB forged offboarding status. Enumerate: active documents, quarantine, records, related records, business indexes, caches, exports, identity mappings, journal/protected evidence, disaster snapshots, object replicas, encryption wrapping keys, signing keys and external ledger receipt references. Separate irreversible deletion actions from reversible account suspension. Never delete shared keys or proofs required for other tenants. Do not infer crypto-erasure is valid if shared/retained backups still hold usable ciphertext and corresponding keys.

Deletion deadlines and retention periods are policy/legal decisions, not constants chosen by Claude. An offboarding receipt must distinguish `Deleted`, `ScheduledForDeletion`, `RetainedByLegalHold`, `RetainedByImmutableAuditPolicy`, `ProviderBackupPendingExpiry`, and `ExceptionRequiresManualReview`. Only claim confirmation when provider-specific evidence exists. Archive receipt/evidence in Canadian protected storage with scope, approver identity, completion time and policy revision.

## Presentation and operational rules

All three features own routes/pages within `CLAUDE-DESIGN.MD`'s 69-screen backlog, reusing shared components. Any new secondary screens require PG catalog and feature-ownership changes rather than untracked routes. Every data card loads independently and has a refresh button, accessible keyboard/screen-reader status, retry state and explicit security failure. Use global request coordinator for uploads/downloads and durable worker status polling with exponential backoff. A closed browser cannot be promised ongoing outbound upload transfer.

## Release gate

No production tenant onboarding until validated basic CSV/JSON import, authorized full-fidelity portable export, and offboarding flow have documented working tests under PB-03/PB-12, including proof that offboarding does not violate legal holds, revocations, Canada-first residency or immutable security history. Nonfatal data-source mapping errors may be handled per-item with clearly identified partial result; incomplete security proof is never a partial success.

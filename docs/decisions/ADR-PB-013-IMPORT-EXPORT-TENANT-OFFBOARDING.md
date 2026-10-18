# ADR-PB-013 — First-Release Import, Export, and Organization Offboarding

- **Status:** Accepted by product owner
- **Date approved:** 2026-10-17
- **Finding:** PB-13
- **Implementation status:** Specification only; no working ingestion, export or offboarding is claimed
- **Normative companion:** `docs/data-lifecycle/PB13_IMPORT_EXPORT_OFFBOARDING_STANDARD.md`

## Decision

Introduce three feature-owned modules for the first production release: **Import Center**, **Export Center**, and **Organization Offboarding**. Ship them after the security foundation (PB-02 Stage A) and complete Accused/AccusedAddress/document vertical slice (Stage B), but **before onboarding real production customers**. Do not hold Stage A/Stage B implementation waiting for these modules. More advanced external-system integration, automated synchronization, migration orchestration and fuzzy duplicate matching are deferred.

### Basic import, first release

- Accept structured **CSV and JSON** imports through authenticated, organization-bound, resumable upload sessions. Define templates / versioned schema mappings, encoding, type conversion, locale and timezone handling, file size and row limits, and dry-run preview of errors.
- Support importing form definitions/selected published form versions, records, reusable child-form relationships (e.g. Accused→AccusedAddress), and separate document ingestion with virus scanning, quarantine, per-document encryption, versioning, signed manifests and trusted content commits. A simple record-only CSV is acceptable as the first deliverable; production scope must eventually cover related records and documents with documented manifests.
- Reject invalid or unapproved field classifications and schema mappings; uphold stable FieldDefinition IDs, tenant isolation and form versions. Imported user references must be mapped to trusted identities and never trusted as grants or owner IDs simply because they appear in source files.
- Require import-specific scopes, target form create rights, duplicate detection where explicitly configured and review of conflicts. Every import uses durable jobs, idempotency, staged validation, bounded batches, restart/recovery and auditable partial-failure reporting. No acceptance of unverified bulk SQL writes.

### Authorized portable export, first release

- Export a **full-fidelity, re-importable logical representation** when independently authorized: published/draft form definitions and versions as policy allows, field metadata and options, verified record histories, relationships, authorized documents and versions, provenance, data classification, a machine-readable manifest and checksums. Format uses documented JSON + binary files in an encrypted archive; offer CSV only as a lossy tabular convenience, clearly marked as such.
- **Organization Owner is not an automatic data-reader or decryptor**. A tenant-wide export requires independent data-governance approval and explicit policy/authorization rights from the Trust Authority (including scopes for fields/documents and any required step-up); otherwise restrict export to the caller's authorized projection. Never circumvent field-level or file-level privacy controls for convenience.
- Snapshot scope and security revision/epoch at export initiation and revalidate permissions before sensitive decryption, package publication and download. On revocation or stale authorization, quarantine/abort and avoid leaking the export; revoke time-limited download credentials.
- All output is encrypted, expires according to policy, has a signed/verified manifest, is logged in canonical `AuditEvents`, and is delivered via a secure authorized channel without unapproved cross-border transfer. Backup/export artifacts are not duplicated into Confidential Ledger; ledger stores optional compact checkpoint hashes.

### Organization offboarding, first release

- Provide an explicit **Request → Verify authority → Approve → Freeze new writes → Export and verify receipt → Apply legal holds/retention → Revoke access and sessions → Purge eligible operational material → Complete/attest** state machine, with retries, crash recovery and distinct paused/failed states.
- Require independently authorized approval for tenant-wide operations; for destructive stages require enhanced step-up/two-person approval. Org Owner alone is not an unrestricted owner of all encrypted records. Freeze must be established in the independent Trust Authority, not just a business DB flag. Enforce idempotent and monotonic state transitions and prohibit reopening after irreversible disposition without an approved recovery procedure.
- Retention/legal hold rules outrank routine deletion. Define disposition for primary/secondary PG, blob versions, archives, caches, object replicas, keys, audit evidence, ledger checkpoints, backups and import/export temporary objects; immutable evidence may remain until lawful retention expiry and must never be falsely promised to be immediately deletable. Explicitly distinguish **logical deletion, crypto-erasure, and physical removal**, with documented provider-specific verification and exceptions.
- Produce a signed/attested offboarding receipt identifying retained classes and scheduled deletion, not protected record content. Keep narrowly scoped proof of closure and access revocations in trusted evidence storage consistent with Canada-first residency (PB-10).

## Architecture and boundaries

- Modules: `Features/Imports`, `Features/Exports`, `Features/OrganizationOffboarding` with feature-owned endpoints, commands, read models, worker job handlers, validators, contracts and tests; corresponding React feature pages and independently refreshing status cards.
- ASP.NET Core `/api/v1` Minimal API contracts, RFC 9457 errors, ETags, idempotency and generated TypeScript clients (PB-06). Browser request coordinator manages file transfers, .NET durable jobs govern server processing (PB-07).
- Independent Trust Authority is the sole trusted gate for any security transition, authorization, revocation and cryptographic integrity commitment (Level 2). Signed Merkle manifests apply to bulk-imported and exported record relationships/files; no direct SQL ingestion that could sidestep these mechanisms.
- All retained artifacts, transfers and cloud processing remain in approved Canadian regions (PB-10). Secrets, import contents and export bodies never enter broad app logs or public GitHub CI artifacts.
- Do not bypass existing retention, form revision history, immutable audit, document scanning, approval or disaster-recovery requirements.

## Implementation order and definition of done

1. After PB-02 Stage A and B gates: implement CSV/JSON record import with schema mapping, preview, validation and bounded job recovery; extend related forms and document ingestion against trusted mechanisms.
2. Implement encrypted authorized full-fidelity export with manifested archive, scope verification and controlled time-limited download.
3. Implement approved tenant offboarding with legal hold, export receipt, freeze/revoke, protected evidence and eventual provider-confirmed disposition.
4. Execute the tests in `docs/testing/PB13_DATA_LIFECYCLE_ACCEPTANCE_TESTS.csv`, including hostile-DB tampering, partial failures, unauthorized export, stale approval and recovery.
5. Production cutover is blocked until required scope, permissions, residency, recovery, integrity, audit and legal-hold checks pass and PB-12 evidence is approved. Human policy/legal decisions remain required; code alone cannot certify compliance.

## Explicitly deferred

Scheduled third-party connectors, real-time two-way external system synchronization, complex mapping languages, auto-remediation of ambiguous duplicates and large migration campaigns.

# PB-04 — Encryption and Verified Search Acceptance Tests

**State:** Requirements only; test cases are NOT executed. **Binding:** `ADR-PB-004`, `SEARCH_CAPABILITY_MATRIX.csv`, and existing `SIM-01..SIM-42`, `SEC-P01..SEC-P12` tests. Every test must run against the actual policy, .NET API, independent Authority, PostgreSQL and crypto implementations where relevant; mocked UI behaviour alone is insufficient.

## Required cases

### ENC-SR-01 — New unclassified free-text field defaults Confidential encrypted

**Pass:** No plaintext value, normalized search terms, or encrypted-derived snippets in business indexes or logs.

### ENC-SR-02 — New document and sensitive filename classified Confidential

**Pass:** Document bytes and protected metadata encrypted; no searchable plaintext shadow copy.

### ENC-SR-03 — Organization mandatory encryption floor

**Pass:** Designer cannot publish lower classification; API rejects stale/tampered request.

### ENC-SR-04 — Explicit Public/Internal search approval

**Pass:** Only policy-authorized field is typed-indexed; DBA-readable residual risk documented in admin UI.

### ENC-SR-05 — Confidential exact-match query submitted directly to API

**Pass:** Request denied regardless of frontend state; no matching value or existence leak.

### ENC-SR-06 — Restricted contains/prefix/range/sort/count/facet queries

**Pass:** All prohibited without plaintext fallback or side-channel leak.

### ENC-SR-07 — Unauthorized predicate field on otherwise visible records

**Pass:** Cannot filter, count, sort or infer hidden values.

### ENC-SR-08 — Business DBA deletes a matching record row

**Pass:** V1 proof fails or records unavailable; never returns false complete zero matches.

### ENC-SR-09 — Business DBA mutates a matching value/index to exclude a hit

**Pass:** Independent catalogue plus current signed value verification detects altered/missing data.

### ENC-SR-10 — Business DBA removes an entire record from local candidate index

**Pass:** Authority-enumerated candidate set detects omission.

### ENC-SR-11 — Business DBA injects a false match

**Pass:** Manifest and accepted committed revision reject untrusted injection.

### ENC-SR-12 — Enumeration/scan exceeds configured V1 limit

**Pass:** Typed failure; no HTTP 200 partial page or result counts.

### ENC-SR-13 — SpiceDB/Authority/manifest proof unavailable

**Pass:** Search fails closed; no direct unverified SQL fallback.

### ENC-SR-14 — Mid-search revocation and stale cursor replay

**Pass:** Subsequent pages denied/revalidated; no cached unauthorized content.

### ENC-SR-15 — Mid-search content or authorization snapshot changes

**Pass:** Consistent restart/reject; no mingled old/new revision result claims.

### ENC-SR-16 — Multi-tenant cross-query and child-record access

**Pass:** No unauthorized records, child links, counts or predicate-field leakage.

### ENC-SR-17 — Encrypted field policy strengthened / legacy migrated

**Pass:** Index/caches invalidated; re-encryption/integrity verification and legacy exposure tracked.

### ENC-SR-18 — Search failures, exports and telemetry

**Pass:** Audit codes but no protected query plaintext; consistent UI and API error handling.

## Phase C7 implementation status (2026-10-24; not a pass of any ENC-SR case)

M0 (C-57), M1 (C-62) and M2 (C-67) implement the gate, the contracts, the Authority catalogue and the Authority-held search approvals. No field-value search exists yet, so **no ENC-SR case has passed as specified**. The cases below are covered at the level M1 and M2 make testable. End-to-end execution evidence comes with M3.

| Case | Covered now | Test | Still pending |
|---|---|---|---|
| ENC-SR-05/06/07 | Request validation and the disabled gate (no predicate is evaluated; nothing leaks); M2: every condition authorized against the Authority's approvals (unapproved, encrypted, wrong type or operator, injected option → `SEARCH_FIELD_NOT_ALLOWED` / `SEARCH_OPERATOR_NOT_SUPPORTED`) before the closed gate | `PhaseC7FieldSearchTests`, `FieldSearchApprovalTests` (real stack), `advanced-field-search.spec.ts` | Execution semantics (M3) |
| ENC-SR-08 | Deleted value rows fail the whole verified scope (never fewer records) | `SearchCatalogueTests.DeletedValueRowsFailTheWholeScope…` (real stack), AC5 unit tests | Search result semantics (M3) |
| ENC-SR-09 | Altered or rolled-back values fail the scope | `…AlteredAndRolledBackValuesFailTheScope` | M3 |
| ENC-SR-10 | Moving or hiding a record in business columns does not remove it from the scope | `…BusinessMetadataManipulation…` | M3 |
| ENC-SR-11 | Injected business records are never in scope; an injected extra row fails its record | `…BusinessMetadataManipulation…` | M3 |
| ENC-SR-12 | Catalogue limit refused with no entries; byte and time limits (business) | `…LimitsRefuseWithoutTruncation…`, `PhaseC7ScopeVerificationTests` | M3 limit on executed searches |
| ENC-SR-13 | Authority/gate unavailable → typed failure, no SQL fallback (business unit level; architecture test) | `PhaseC7ScopeVerificationTests`, `FieldSearchHasNoDirectBusinessDatabaseAccess` | Real-stack Authority outage during a search (M3; SEC-P fail-closed suite covers reads) |
| ENC-SR-14 | Revocation invalidates the snapshot; the record leaves the next catalogue | `…SnapshotsDieOnRevocation…` | Stale cursor replay (M3 paging) |
| ENC-SR-15 | Content and authorization changes invalidate; changes elsewhere do not | `…SnapshotsDieOnRevocation…`, AC5 unit tests | M3 |
| ENC-SR-16 | Cross-organization snapshots and catalogues refused | `…LimitsRefuseWithoutTruncation…` | Child-record predicates (out of V1, D6) |
| ENC-SR-04 | Approval only by `administer_security`, bound to the Authority-registered version, Public/Internal plaintext only, operators per type, step-up gate, `field_searcher` query permission | `FieldSearchApprovalTests` (Authority unit + real stack), `field-search-approval.spec.ts` | Production step-up (PB-08); search with real results (M3) |
| ENC-SR-17 | Strengthening invalidates the approval in the same Authority command; the field leaves the capabilities at once | `FieldSearchApprovalTests` (unit + real stack) | Plaintext index removal and historical-revision search (M3) |
| ENC-SR-01..03, 18 | — (classification defaults and floors are pre-C7 controls; ENC-SR-18 needs execution) | — | M3 |

## Evidence and release rules

For each case provide executable test path, test run/commit/image digest, isolated environment, inputs and expected result, observed result, authority revision/snapshot evidence, checked absence of protected information, logs redacted of sensitive data, and independently reviewable pass/fail status. CI must gate on applicable tests; real Azure staging proof still applies to all Level 2 controls. A missing, skipped, mocked or inconclusive check is not a pass. **G06 search completeness remains OPEN until real results establish it.**

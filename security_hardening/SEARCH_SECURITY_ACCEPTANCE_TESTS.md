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

## Evidence and release rules

For each case provide executable test path, test run/commit/image digest, isolated environment, inputs and expected result, observed result, authority revision/snapshot evidence, checked absence of protected information, logs redacted of sensitive data, and independently reviewable pass/fail status. CI must gate on applicable tests; real Azure staging proof still applies to all Level 2 controls. A missing, skipped, mocked or inconclusive check is not a pass. **G06 search completeness remains OPEN until real results establish it.**

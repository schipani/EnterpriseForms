# ADR-PB-004 — Encryption-First Classification and Verified Search V1

- **Status:** Accepted
- **Decision date:** 2026-10-17
- **Approved by:** Product owner; explicit approval of recommended pre-build Finding PB-04
- **Finding:** PB-04; resolves **Major decision A — Encryption versus search**
- **Related requirements:** `CLAUDE.md` §§4C, 4L, 4T, Phase 4; `security_hardening/SECURITY_SIMPLIFICATION_STANDARD.md` Decisions 2–3; `trust_authority/MERKLE_RECORD_MANIFEST_PROTOCOL.md`; `ADR-PB-003`

## Binding decision

1. **Encrypt by default.** New, unclassified user-entered free-text values and document contents are **Confidential** and application-encrypted using the approved authenticated encryption service, with independently managed keys, signed membership/integrity, and no plaintext business-search index. Potentially sensitive filenames, identifiers and descriptive metadata must follow organization classification policy; storing encrypted file bytes does not alone conceal sensitive filenames.
2. **Four classifications:** `Public`, `Internal`, `Confidential`, `Restricted`. Organization security administrators define a mandatory minimum per field category/form, and Designers may strengthen—not weaken—that floor. Explicitly approved Public/Internal fields MAY be stored in typed plaintext indexes and searched within authorization scope. **No `Confidential` or `Restricted` general or exact-match field-value search in V1**, even by an otherwise authorized user. Authorized display/decryption by record ID is allowed after all gates, without creating searchable plaintext or telemetry copies.
3. **Document residual exposure.** A malicious Business PostgreSQL reader can read all approved indexed plaintext, including equality patterns, access metadata and certain public identifiers. Search approval is not proof of DBA confidentiality. An organization may decide certain fields cannot be downgraded at all, including identity data in regulated deployments. Bulk exports, previews, caches, error messages and analytics may not circumvent classification.
4. **V1 verified search only.** For claims of complete results, obtain the entire eligible discoverable-record catalogue (record IDs, tenant, form, accepted content heads, relevant authorization revision) from the independent Authorization Authority—not from Business PostgreSQL. Apply authorization for *the predicate fields* before execution, then retrieve and verify every eligible current record/field value and relationship that could match. Evaluate only approved operators over approved fields. A result is `complete` only after exhaustive verification at a coherent authorization/content snapshot. A BusinessDb index can accelerate fetching candidate bytes but cannot decide or prove the full candidate set. If snapshot proof, enumeration completeness, or record verification fails, fail closed. Do not return a seemingly complete truncated list.
5. **Bounded rather than deceptively scalable.** A configurable, tested V1 ceiling limits independently enumerated records, scan bytes and elapsed time. Before exceeding a limit, reject with a typed *not-executable-as-complete* response (no partial records/counts/facets) and clear UI guidance to narrow an authority-verifiable scope (e.g., selected authorized form). Narrowing solely with an untrusted BusinessDb predicate does not establish completeness. Pagination must be a proven snapshot-consistent partition of a fully verified result set, not repeated independent SQL queries claiming completeness. Revocation invalidates cached pages and cursors; a changed snapshot requires restart/failure instead of combining conflicting revisions.
6. **Confidential predicates denied server-side.** UI should hide/disable prohibited operators with explanatory text, but the .NET API must reject handcrafted or stale requests independently of UI flags. A request containing an unauthorized field predicate must not reveal field existence or value through timing, error details, counts, sorts, facets or side effects. All query plans are tenant- and authorized-discovery scoped; evaluate the actual field-level permission, not simply form/record visibility.
7. **Future improvements are separate decisions.** Equality-only keyed blind indexes and independently maintained V2 trusted indexes are **not** included in this approval. A future ADR must review inference/dictionary attacks on low-cardinality values, key isolation, lifecycle/rekey, consent, field-level query authorization, revocation, indexed historical versions, proof of index completeness, leakage budget and migration strategy. No opportunistic `pgcrypto` decrypt-in-SQL, deterministic encryption, plaintext shadow column or client-side decryption-scan workaround.

## V1 search capability table

The complete, machine-readable matrix is `security_hardening/SEARCH_CAPABILITY_MATRIX.csv`.

| Search operation | Explicitly approved Public/Internal plaintext | Confidential/Restricted application-encrypted |
|---|---|---|
| Equality on typed data | Yes, after field/predicate authorization + completeness proof | **No** |
| Contains/prefix on string field | Yes where explicitly enabled; no promise of scalable V1 global text search | **No** |
| Numeric/date range | Yes where field type supports it | **No** |
| Sorting by a field | Yes, over authorized complete result set | **No** |
| Facets/counts/aggregates | Only when derived exclusively from complete authorized results and policy permits | **No** |
| Display known authorized record ID | Yes | Yes, **after authorization, integrity verification and decryption** |
| Export/download | Only authorized, explicitly classified operation with independent audit | Authorized protected export only; **not a search capability** |

No arbitrary regex, substring of ciphertext, semantic/vector search over encrypted fields, low-entropy blind index, or global full-text promise in V1.

## Versioning and classification changes

- A published form-version's classification/encryption declaration is immutable. A new version may strengthen classification for **future** input; existing values and historic indexes must not be silently reclassified or exposed. To strengthen old data, use an approved audited migration with cryptographic re-encryption where needed, purge all searchable plaintext replicas/backups subject to retention/legal holds, invalidate search projections/caches, verify signatures/manifests and document legacy exposure.
- Downgrading encrypted Confidential/Restricted data for search is disallowed as an ordinary Designer edit. Any exception requires organization policy explicitly permitting it, security/privacy approval, authorization-authority audited migration command, retention analysis, and a new version; classifications must never fall below regulatory or organization floors.
- Stable `FieldDefinitionId` and version-scoped field policy must be resolved for each stored value before query execution; no new field or child relationship is implicitly searchable.

## API and UI requirements

- Search contract is versioned under `/api/v1`; support a declarative operator/field allowlist generated by the authoritative query-policy service. Never rely on the browser-provided classification or operator list.
- Return data only when every record/item in the returned result has been authorized and integrity-verified. Include a server-issued verified-snapshot reference and a narrowly scoped, expiring opaque cursor if pagination is offered. No trust in client-submitted revision, timestamp, record GUID, team or role claims.
- Standardized RFC 9457 Problem Details `errorCode` values: `SEARCH_FIELD_NOT_ALLOWED`, `SEARCH_OPERATOR_NOT_SUPPORTED`, `SEARCH_VERIFICATION_LIMIT`, `SEARCH_SNAPSHOT_CHANGED`, `SEARCH_VERIFICATION_UNAVAILABLE`. Choose consistent API status mappings in the eventual unified OpenAPI; ensure denied predicate-field existence is not leaked by detailed errors. For a complete-query guarantee, do not return partial HTTP 200 results after quota/proof failure.
- Show 'Not searchable (encrypted)' in field configuration and record search builder, explain why, and link to permission-controlled policy information. Results are explicitly scoped to authorized fields and may be unavailable for data-protection reasons; no suggestion that empty results prove no records exist when a valid complete search was not executed.
- Audit authorized search executions and denied/failed attempts without recording sensitive query strings or plaintext. Apply quotas, cancellation and per-organization isolation; worker/coordinator retries must not broaden permission or repeat expensive failed scans indefinitely.

## Gate and acceptance

- Enforce the feature matrix with negative backend tests even when UI hides forbidden controls.
- Execute the `ENC-SR-01` through `ENC-SR-18` tests defined in `security_hardening/SEARCH_SECURITY_ACCEPTANCE_TESTS.md` (cross-reference SIM and SEC-P attack suites); emit real evidence. In particular demonstrate a malicious BusinessDb actor cannot suppress a match without incomplete-proof failure; and a search for an encrypted field cannot expose a value or a match.
- Search readiness remains **SPECIFIED, NOT IMPLEMENTED OR TESTED**. The feature is not approved for production solely because this ADR is written.

## Consequences / rejected alternatives

- **Benefit:** No accidental protected-field plaintext indexes, truthful completeness guarantees against hostile business DB writes, minimal leakage from protected fields.
- **Cost:** V1 full verification is intentionally bounded, can be slow, and may reject broad searches; customers wanting full encrypted-field text search cannot get it in V1.
- **Rejected:** Search all encrypted values via SQL plaintext shadow index; blindly trust BusinessDb result filtering; promise complete results with incomplete proof; deploy deterministic encryption or blind indexes without threat review.

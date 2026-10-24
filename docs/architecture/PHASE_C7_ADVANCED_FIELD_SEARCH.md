# Phase C7 — Advanced Field Search: architecture assessment and security checkpoint

- **Status:** PROPOSED — awaiting owner review. **Security checkpoint reached (§0).** No search engine, Authority change, API endpoint, migration or UI for C7 has been implemented.
- **Branch:** `phase-c7/advanced-field-search` (from `main` 4c00ce7).
- **Decision:** C-55 (`docs/decisions/PHASE_C_DECISION_LOG.md`).
- **Binding sources:** `CLAUDE.md` §§4C.6, 4H, 4L.5, 4T.4, 4T.1 and Phase 4; `docs/decisions/ADR-PB-004-ENCRYPTION-SEARCH-CAPABILITY.md` (+ Amendment A1); `security_hardening/SEARCH_CAPABILITY_MATRIX.csv`; `security_hardening/SEARCH_SECURITY_ACCEPTANCE_TESTS.md` (ENC-SR-01..18); `SECURITY_GAP_ASSESSMENT.md` G06 and G19; `trust_authority/MERKLE_RECORD_MANIFEST_PROTOCOL.md`; `docs/ui/FORM_CONTROL_LAYOUT_STANDARD.md`; `docs/PHASE_C6_STATUS.md` §16.5 (owner-approved UI direction).
- **Not claimed:** G06 and G19 stay **OPEN**. Nothing here is implemented or tested, and nothing in it is production Level 2 evidence.

## 0. Checkpoint verdict

ADR-PB-004 §4 requires the **Authorization Authority**, not Business PostgreSQL, to supply the complete eligible catalogue: record ids, tenant, **form**, **accepted content heads** and the **authorization revision**, at one coherent snapshot. The Authority today supplies only part of this:

| Required by ADR-PB-004 / the C7 brief | Exists today? |
|---|---|
| Complete enumeration of the caller's discoverable records at one authorization revision | **Yes**: `POST /internal/v1/authorization/discover` (registry ∩ SpiceDB `discover`, fully enumerated, gate re-read, C-28) |
| The same enumeration **narrowed to one Authority-registered form** | **No.** The registry stores the form on every `form_record` resource (`authority_resources.parent_resource_entity_id`), but discovery does not filter by it or return it |
| Accepted content head (revision, operation id, root, quarantine) **for every catalogue entry, at the catalogue's snapshot** | **No.** Heads are only available one record at a time (`GET content-revisions/{org}/{record}`), each read at a different moment |
| A coherent authorization + content **snapshot token** that later pages can re-check cheaply | **No.** `organization_authorization_states.content_integrity_revision` exists, but no API exposes it |
| Per-record verification of the complete value set, with the caller's readable subset | **Yes**: `POST /internal/v1/integrity/verify-record-projection` (complete leaf set, HMAC, Merkle root, `authorized_items` / `readable_items`) |
| Authority-held **search approval** for a field (explicit Public/Internal approval, not weakenable by the business database) | **No.** `field_definitions.is_searchable` and `field_classification` are unsigned business rows; `is_searchable` is always written `false` and no workflow can set it |
| Organization policy that permits plaintext searchable fields | **No.** The Authority policy has `plaintext_internal_fields_permitted` and `plaintext_downgrade_permitted`, but nothing about search |

Therefore, in line with the brief's security checkpoint, **the security-sensitive implementation stops here for owner review**. Two Authority capabilities are proposed (**AC-5** search catalogue, **AC-6** field search policy, §4). §10 lists the owner decisions that block or shape the work.

A V1 that is *technically* complete **could** be built on today's primitives alone (§2.8, option B). It would have to verify **every** record the caller can discover across all forms, because a form selection could not be trusted to narrow the set. It would make 2–3 Authority calls per record per search, and its search approval would rest on business-database metadata. It is described so the owner can choose it knowingly. **It is not recommended**, and it would not give G19 its completeness basis.

## 1. Scope (V1 as briefed)

* One Advanced Search section on My Records, results in the same table (§7).
* Predicates are allowed **only** on fields of the **selected form** that are explicitly approved Public/Internal, permitted by organization policy, published as searchable in the form version and readable by the caller on the record. No Confidential/Restricted predicate of any kind, including equality.
* Operators follow the field's type and server capability (§5.3). Conditions are joined by **AND** only. No OR, no nesting, no expressions, no regex, no semantic/vector search, no blind indexes, no ciphertext scanning.
* Out of V1: related-form (child) predicates, repeating-group predicates, documents, sorting on field values, facets on field values, and export. Each needs its own proof of relationship/occurrence authorization and completeness (REL-xx, G06).
* The existing exact reference/UUID lookup (C-48) and metadata filters keep working unchanged.

## 2. Assessment (the eight questions of the brief)

### 2.1 Trusted enumeration capabilities today

* `DiscoverAsync` returns the caller's complete set of discoverable `form_record` ids (registered **active** ∩ SpiceDB `discover` at the Authority-held ZedToken), sorted, cursor-paged and bound to the authorization revision. It fails closed if the state moved during the lookup (`NOT_READY`), on more than 50,000 records (`LIMIT_EXCEEDED`, nothing partial), and with `CONTINUATION_INVALIDATED` on a revision change.
* Per record, `GetCommittedContentRevision` returns the accepted head, and `VerifyRecordProjection` proves that the observed staged rows **equal** the Authority's complete leaf index for that head. The proof covers every row HMAC and the recomputed Merkle root, and it returns the subset the caller may release. On mismatch it quarantines the head (AC-4) and journals an incident.
* The signed record header (`BusinessSignedSchemas.EncodeHeader`) binds `form_definition_id` and `record_status`. Plaintext values (`field_string_values` …) and ciphertext (`field_encrypted_values`) are different signed row schemas. A verified record therefore proves which form it belongs to, its status, and whether each value is plaintext or encrypted.
* Missing: form-scoped enumeration, catalogue-with-heads, a snapshot token, and Authority-held search policy (§0).

### 2.2 What prevents G06 from being closed

G06 needs proof that a search returned **every** authorized match. Four things are missing:

1. **Bounded trusted scope.** A form-scoped catalogue does not exist. Narrowing by the business `form_records.form_definition_id` column is exactly the untrusted narrowing ADR-PB-004 §5 forbids. My Records' Form filter is labelled unverified for this reason (`ListRecordsHandler` remarks).
2. **Snapshot.** Nothing proves that all candidates were evaluated at one authorization revision **and** one content state.
3. **Policy trust.** Which fields are searchable, and with which operators, is not Authority-held.
4. **Evidence.** ENC-SR-01..18 have never run; G06 also requires scale measurement (PB-09).

Items 1–3 are design gaps that this document resolves (§4). Item 4 requires implementation, hosted runs and independent review.

### 2.3 G19 dependencies that affect search

G19: the field-protection migration worker verifies only the records that the **business database lists** for a form. A hostile business DBA can hide records from that list, so "all records encrypted" is not a completeness guarantee. This affects search in three ways:

* **The same missing primitive.** AC-5's form-scoped Authority catalogue is the "independently maintained, revision-fenced enumeration of each form's records" that G19's remediation names. Building it for search gives the migration worker its completeness basis. G19 stays open until the worker uses it and adversarial tests run.
* **Mixed-protection fields.** While a field is migrating (strengthening, or an A1 release), some records hold ciphertext and others plaintext. A field with an Authority `field_protection_requirement`, or with a pending/active reprotection, is **never searchable**. Search never decrypts. If a verified candidate holds an **encrypted** leaf for a predicate field, the whole search fails (`SEARCH_VERIFICATION_UNAVAILABLE`, reason `PREDICATE_VALUE_ENCRYPTED`). Treating the record as a non-match would silently drop it (§5.6).
* **Strengthening never exposes history.** AC-6 approval is cleared by any strengthening command. The historic plaintext values of a field that was later encrypted are reached only through historic reads (C6 M2), which are not a search surface (ENC-SR-17).

### 2.4 Detecting deletion, omission, rollback or manipulation by a business DBA

The **catalogue comes from the Authority**. Business rows are then only a source of bytes, and every byte is verified against the head that the catalogue fixed.

| Attack (business DBA) | Detection | Search outcome |
|---|---|---|
| Delete a matching record's rows (ENC-SR-08) | Catalogue still lists it; staged rows missing → leaf-set mismatch | Fail `SEARCH_VERIFICATION_UNAVAILABLE` (record quarantined); never "0 matches" |
| Alter or delete a value or index row to drop a hit (ENC-SR-09) | HMAC or leaf-set mismatch against the accepted manifest | Fail closed, same as above |
| Remove a record from local indexes or listings (ENC-SR-10) | The catalogue does not use business listings | The record is still evaluated |
| Inject a false match (ENC-SR-11) | A business row not in the catalogue is never read; an extra value row breaks the leaf set | Not shown; the candidate fails |
| Roll back rows to an earlier valid signed revision | Rows of the old operation do not match the **current** head's manifest | Fail closed |
| Move a record to another form in business columns | The form comes from the signed header and the Authority registry parent | Not affected |
| Flip `is_searchable` / downgrade `field_classification` / change `field_data_type` | Search approval comes from AC-6 (Authority); the value type comes from the signed row schema | Not affected |
| Stop the Authority or SpiceDB, or corrupt the projection | Gate closed / not ready | `SEARCH_VERIFICATION_UNAVAILABLE` (ENC-SR-13) |
| Read approved plaintext values directly | **Not prevented** (ADR-PB-004 §3 residual risk) | Documented; shown to the approver |

### 2.5 Independent authorization of every predicate

Two layers, both evaluated before any value is compared. Both depend on Authority answers, never on browser input:

1. **Field-policy layer (once per request, before any record is touched).** Each `fieldDefinitionId` must be in the server-computed capability list for (caller, form, current published version). That list is AC-6 approval ∩ organization policy ∩ published `searchable` snapshot ∩ field type ∩ "no protection requirement or pending reprotection". Any other id is rejected with **one** response: `SEARCH_FIELD_NOT_ALLOWED`. The response is the same for an unknown id, a hidden field, a Confidential field or a field of another form, so it reveals nothing about existence. The operator is checked only after the field is allowed (`SEARCH_OPERATOR_NOT_SUPPORTED`).
2. **Per-record layer.** For each catalogue record, `VerifyRecordProjection(Read)` returns `readable_items`. A record is a **candidate for evaluation** only if every value leaf of every predicate field is in the readable set, **and** the caller's share scope covers the field. A missing value cannot be distinguished from an unshared field, so a share-scoped caller must hold the field in their scope (Authority `field_item`/share field list), not merely lack a value. A record where any predicate field is not readable is **excluded before evaluation**. It is excluded identically whatever its value, so the exclusion reveals nothing. Completeness is therefore stated precisely as: *all records of the form you can discover **and** whose predicate fields you can read, at snapshot S*. The UI says so.

### 2.6 Coherent snapshot

AC-5 returns, from **one** Authority read transaction behind the organization gate:

* `authorizationRevision` and `projectionRevision`;
* the form-scoped catalogue entries `(formRecordId, committedContentRevision, committedOperationId, manifestRootBase64, quarantined)`;
* a **snapshot token**: an Authority MAC over (organization, actor, form, authorization revision, sorted entries).

Every per-record verification must report the **same** authorization revision **and** the catalogue's head for that record. Otherwise the search restarts once and then fails `SEARCH_SNAPSHOT_CHANGED`. At the end of execution, and before every later page, the business API calls `POST …/search-catalogue/check` with the token. The Authority recomputes the token from current state, so the check is one call rather than N, and fails if any head or the authorization revision moved. Results are never mixed across revisions (ENC-SR-15). Revocation advances the authorization revision, so every outstanding snapshot and cursor dies (ENC-SR-14).

### 2.7 Safe limits (initial values; must be measured — PB-09)

| Limit | Initial value | On breach |
|---|---|---|
| Catalogue entries per search (form-scoped) | 500 | `SEARCH_VERIFICATION_LIMIT` **before** any verification work; guidance to narrow scope |
| Conditions per search | 5 | `400` validation (not a search code) |
| Bytes of staged rows read per search | 64 MiB | `SEARCH_VERIFICATION_LIMIT` |
| Elapsed verification time | 15 s (cancellation-aware) | `SEARCH_VERIFICATION_LIMIT`; no partial output |
| Concurrent searches per user / per organization | 1 / 4 | `429` (existing rate-limit Problem) |
| Text value length; minimum length for contains/prefix | 200 chars; 3 chars | `400` validation |
| Verified result retention for paging | 5 min, per API instance, bound to snapshot token + user + session | Cache miss → full re-execution at the **same** token, or `SEARCH_SNAPSHOT_CHANGED` |

Rationale: per-record verification is two Authority calls plus one business staged-row read. 500 records is at most about 1,000 internal calls. That is acceptable for an interactive request only if measured; the number is a starting point, not a promise.

### 2.8 Work required before trustworthy pagination, sorting and counts

* **Pagination** is a slice of one verified, deterministically ordered match list bound to the snapshot token. Page *n* is served only after the token check succeeds. It is never a fresh SQL query.
* **Counts** (`totalCount`) are returned only for a complete verified result, and only as the size of that result, never as a count of the form or of excluded records.
* **Sorting** applies to the existing columns, using **verified** header values (status, created/modified where signed). Columns that come from unverified projection metadata (form name, creator display name) keep the existing "local metadata" caveat. Sorting by field values is deferred (matrix: allowed later over complete results).
* **Facets:** none in V1. The Form list is a selector, not a facet of the result.

**Option B (not recommended):** without AC-5, scope = every discoverable record organization-wide (the 500 limit is hit quickly). Heads would be re-read per record at the end (N extra calls), and search policy would come from business rows, accepted as a documented residual risk. It is complete, but it gives no G19 basis and does not scale.

## 3. Threat analysis

| ID | Threat | Control | Test |
|---|---|---|---|
| T1 | Handwritten request with a Confidential/Restricted/unknown field | Field-policy layer before execution; single indistinguishable error | ENC-SR-05/06, C7-API-03 |
| T2 | Unauthorized predicate on visible records (share scope) | Per-record readable-set exclusion before evaluation; no counts from excluded records | ENC-SR-07 |
| T3 | Business DBA suppresses, injects, rolls back or alters a match | Authority catalogue + per-record full manifest verification + snapshot check | ENC-SR-08..11 |
| T4 | Scope too large → silent truncation | Limit check on catalogue size before work; typed error | ENC-SR-12 |
| T5 | Authority/SpiceDB/manifest unavailable → SQL fallback | No fallback path exists; typed 503 | ENC-SR-13 |
| T6 | Revocation mid-search or stale cursor | Authorization revision in token; check before every page; cursor bound to user + session + token | ENC-SR-14 |
| T7 | Content changed mid-search | Per-record head must equal the catalogue head; final token check | ENC-SR-15 |
| T8 | Cross-tenant or child-record leakage | Catalogue is organization- and form-scoped by the Authority; child predicates out of V1; child records listed only when their own registry form matches | ENC-SR-16 |
| T9 | Classification strengthened → old plaintext searchable | AC-6 approval cleared on strengthening; protection-requirement fields never searchable; encrypted leaf ⇒ failure | ENC-SR-17 |
| T10 | Predicate values in logs, telemetry, URLs, audit | POST body only; values never logged; audit records field ids, operators, counts, outcome | ENC-SR-18 |
| T11 | Timing or side channels between denied and unknown fields | Decision taken from the precomputed capability list; no record access before it | C7-API-04 |
| T12 | Field names disclosed to users who may not know the form's fields | Capability list returned only to callers with `CreateRecord` or `DesignForm` on the form, or owners of a record of the form (D5) | C7-API-05 |
| T13 | Search used as a decryption or verification oracle | Never decrypts; verification failures quarantine and fail the whole search | C7-SEC-01 |
| T14 | Expensive repeated scans (DoS) | Concurrency limits, cancellation, no automatic retry of `SEARCH_VERIFICATION_LIMIT` by the request coordinator | C7-API-07 |
| T15 | Designer weakens protection by ticking Searchable | Search approval is an Authority command by an organization owner/security administrator; impossible on encrypted or Confidential/Restricted fields | ENC-SR-03/04 |

Residual (documented, not mitigated): a business DBA can read approved Public/Internal plaintext (ADR-PB-004 §3); the plaintext of a shown match has already been delivered; the Authority runtime is trusted (§4M).

## 4. Proposed Authority capabilities (require owner approval; Authority changes need production review like C-12/C-28/AC-1/AC-2)

### AC-5 — Form-scoped search catalogue (also G19's completeness basis)

* `POST /internal/v1/search-catalogue` `{organizationId, formDefinitionId, maximumEntries}` → `{authorizationRevision, projectionRevision, entries:[{formRecordId, committedContentRevision, committedOperationId, manifestRootBase64, contentHeadQuarantined}], snapshotToken}`.
  * Registry: `form_record` resources with `resource_lifecycle_state = Active` **and** `parent_resource_entity_id = formDefinitionId`, ∩ SpiceDB `discover` for the actor at the Authority-held token; heads read in the same transaction; gate re-read after the lookup (as C-28).
  * More entries than `maximumEntries` → `LIMIT_EXCEEDED` (422) with **no entries**. The count itself is not returned, because it would reveal the form's size beyond the caller's limit tier. Records without content (revision 0) are excluded and reported as a count.
* `POST /internal/v1/search-catalogue/check` `{organizationId, snapshotToken}` → `{current: bool}`. The token is recomputed from current state.
* Workload operation for the migration worker (G19): the same catalogue **without** a discover filter, callable only by the worker identity, for its post-check. This is a later step, not part of the search milestone.
* Tests: registry/discover intersection, form isolation, quarantined heads, token tamper, token after revoke/edit, lookup paging >1000, limit with no partial output.

### AC-6 — Field search policy held by the Authority

* Organization policy gains `plaintext_search_permitted` (default **false**), changed only by `UpdateMandatoryPolicy` (owner/security administrator).
* New security command `ApproveFieldSearch` / `WithdrawFieldSearch` `{formDefinitionId, fieldDefinitionId, classification ∈ {Public, Internal}, operators[], approvalReference}`.
  * Allowed only for an organization owner or system administrator.
  * Rejected while the field has a `field_protection_requirement`, a pending reprotection or a pending reclassification, or when the organization policy forbids plaintext search or Internal plaintext.
  * Registered as a `field_search_approval` resource and journaled like every security command.
  * Any strengthening command (`PublishFormSecurityRevision` to encryption, a Confidential/Restricted classification) **revokes** it in the same revision.
* `GET /internal/v1/forms/{org}/{form}/search-policy` → the approved fields and operators at the current authorization revision.
* The business API publishes a field as searchable in a form version only if AC-6 approves it at publish time. It re-checks AC-6 at **every** search. The published snapshot can narrow but never widen AC-6.

## 5. Business search design (built only after approval)

### 5.1 Ownership

`Search` feature (ADR-ARCH-001): `Application/Features/Search/FieldSearch/` (`FieldSearchQuery`, `FieldSearchValidator`, `FieldSearchCapabilitiesHandler`, `FieldSearchHandler`, `VerifiedCandidateEvaluator`, `SearchResultCache`). It uses `FormRecords`' public verified-read port, which gains a **non-decrypting, non-auditing-per-record** search variant of `VerifiedRecordReader` that releases only plaintext typed values of the predicate fields. `FormDefinitions` provides the published-version field snapshot through its public port. No new tables are expected; if a result cache table is ever needed it is owned by `Search`.

### 5.2 Public API (`/api/v1`, PB-06; requests and responses in the generated client)

* `GET /api/v1/form-records/search-capabilities?formDefinitionId=` → `{formDefinitionId, searchAvailable, unavailableReasonCode?, fields:[{fieldDefinitionId, displayLabel, fieldDataType, control, operators[], optionsSnapshot?}], notSearchableFields:[{fieldDefinitionId, displayLabel, reasonCode: "ENCRYPTED" | "NOT_APPROVED"}], policyRevision}`. `notSearchableFields` is returned only to callers allowed to know the form's fields (D5). Otherwise both lists are empty and `searchAvailable=false`.
* `POST /api/v1/form-records/field-search` `{formDefinitionId, conditions:[{fieldDefinitionId, operator, value | lowerBound/upperBound}], recordStatus?, includeArchived?, includeChildren?, sortBy?, sortDirection?, pageSize}` → `{items: RecordListItemResponse[], totalCount, pageSize, snapshotReference, nextCursor?, previousCursor?, completeness: "Complete", scopeStatement}`. Predicate values are in the body only, never in the URL.
* `POST /api/v1/form-records/field-search/page` `{cursor}` → the same shape. The cursor is Data Protection-sealed and contains organization, local user, session, snapshot token, query digest and offset. It holds no predicate values; the query digest is a keyed hash.
* Problem Details `errorCode` mapping (RFC 9457, safe detail only):

| errorCode | HTTP | Retryable | When |
|---|---|---|---|
| `SEARCH_FIELD_NOT_ALLOWED` | 422 | no | Any predicate field not in the caller's capability list (single indistinguishable response) |
| `SEARCH_OPERATOR_NOT_SUPPORTED` | 422 | no | Operator or value shape not allowed for an allowed field |
| `SEARCH_VERIFICATION_LIMIT` | 422 | no | Catalogue, bytes or time ceiling; extension `limitKind` (`records`/`bytes`/`time`) |
| `SEARCH_SNAPSHOT_CHANGED` | 409 | yes (restart from page 1) | Authorization or content moved during the search or since the snapshot |
| `SEARCH_VERIFICATION_UNAVAILABLE` | 503 | yes (after a delay) | Authority/SpiceDB unavailable, gate closed, a candidate failed verification (extension `unverifiableRecordCount`, the caller's own records only), or an encrypted predicate leaf |

### 5.3 Operators (V1)

| Field type | Operators |
|---|---|
| String | `equals` (exact, case-sensitive, NFC); `startsWith`, `contains` only if approved per field (AC-6 `operators`) |
| Integer, Decimal | `equals`, `greaterThan`, `greaterThanOrEqual`, `lessThan`, `lessThanOrEqual`, `between` (inclusive) |
| Date | `equals`, `before`, `after`, `between` (date-only, never zone-shifted) |
| DateTime | `before`, `after`, `between` (UTC instants; the UI converts from the user's effective zone) |
| Option (single/multi) | `is` (option id); for multi-select `includes` |
| Boolean | `isTrue`, `isFalse` |
| Guid, File, encrypted, any field in a repeating group | none |

### 5.4 Execution flow

1. Authenticate, resolve tenant and session. Validate shape (≤5 conditions, lengths).
2. Read AC-6 policy and the published version snapshot, compute capabilities, then reject disallowed fields or operators (§2.5 layer 1). No record has been touched yet.
3. Call AC-5 with `maximumEntries = 500` → catalogue + token (or `LIMIT_EXCEEDED` → `SEARCH_VERIFICATION_LIMIT`).
4. Filter by `recordStatus` / archived / children **after** verification, using signed header values.
5. Verify each catalogue entry in bounded parallel batches: staged rows of `committedOperationId` → `VerifyRecordProjection(Read)`. Require the same authorization revision and head. Any failure aborts the search (§5.6). Track bytes and time against the limits.
6. Exclude records where a predicate field is not readable (§2.5 layer 2). Evaluate the AND of predicates over verified plaintext values. An encrypted leaf on a predicate field aborts the search.
7. Order deterministically by the sort key, then by `formRecordId`.
8. `search-catalogue/check(token)`; if stale, restart once, then return `SEARCH_SNAPSHOT_CHANGED`.
9. Cache the ordered match ids under the token. Return page 1 with Edit permission decisions as in `ListRecordsHandler`.
10. Audit one `RECORD_SEARCH_EXECUTED` event: field ids, operators, result count, catalogue size, duration and outcome. **No values.** Denials and failures are audited with their error code.

### 5.5 Pagination

A page request unseals the cursor and checks user, session and query digest, then calls `search-catalogue/check(token)`. If the token is still current, the page is served from the cache. On a cache miss (another API instance), steps 3–8 are re-run and the new token must equal the cursor's token. Otherwise the response is `SEARCH_SNAPSHOT_CHANGED`.

### 5.6 Failure semantics

The whole search fails if any single candidate fails verification. One deleted row can then make search unavailable for that form, which is an availability trade-off accepted by ADR-PB-004 §4. The failed record is also quarantined by the Authority, which is a security event. The UI never shows "No records found" for a failure.

## 6. Form Designer and policy integration

* **Configure Field → "Search" section** (existing section pattern):
  * shows classification and protection;
  * for ApplicationEncrypted, Confidential or Restricted fields: "Encrypted for privacy — not searchable", with no control;
  * for Public/Internal plaintext fields: "Searchable in Advanced Search", enabled only when AC-6 has approved the field; otherwise "Requires approval by an organization owner", with a request link.
  * Ticking it in the draft only *publishes* an already approved capability. It cannot change classification or protection.
* **Organization settings → Data protection:** `plaintext_search_permitted` switch and the per-field approval list, with the DBA-readable residual-risk statement (ENC-SR-04). The approval action needs step-up (PB-08) and a governance reference.
* Published versions stay immutable. A new version can withdraw searchability; withdrawal and strengthening take effect at the next search immediately (AC-6 is re-checked every time).

## 7. UI (owner-approved layout, C6 §16.5)

```
Search records
[ Record reference or ID ................ ] [🔍 Search]
[ Form ▾ ]                    [ Status ▾ ]
[ Sort by ▾ ] [ Rows per page ▾ ]  ☐ Include related  ☐ Include archived

Advanced Search
  Condition 1
  [ Field ▾ ]                 [ Condition ▾ ]
  [ Value .................................. ]
  [ + Add condition ]  [ 🔍 Search records ]
```

* Advanced Search is enabled only when a Form is selected and `searchAvailable` is true. Otherwise it explains why ("Select a form to search its fields", "This form has no fields approved for searching", "Encrypted for privacy — not searchable").
* The Field list comes from `search-capabilities`. Condition options follow the selected field's server operators. The Value control is typed: text, number, date or date range, option select, true/false.
* Each condition is a `fieldset` with a legend ("Condition 2") and a Remove action. Conditions are joined by AND, and the UI says so.
* Results render in the existing My Records table with the same columns, Actions, sort, rows per page and pagination. A scope line reads: "Complete for records of *Form* that you can access and whose searched fields you can read."
* Errors use `StatusMessage` with the codes in §5.2 and retry/narrow guidance. `SEARCH_SNAPSHOT_CHANGED` offers "Run search again".
* Predicate values stay in component state and are never written to the URL. The URL keeps only the existing metadata parameters.
* Layout follows `FORM_CONTROL_LAYOUT_STANDARD.md` (label → control → help → error; buttons aligned with inputs), Lucide `ActionIcons` (`search`, `addCondition` new), black primary buttons, organization theme, and one column at 320 px.

## 8. Test plan

* **Security (real Authority, PostgreSQL, SpiceDB, Vault; isolated `sectest` fixtures):**
  * ENC-SR-01..18 → `tests/integration/FieldSearch*` plus Authority tests for AC-5/AC-6.
  * ENC-SR-08..11 use the hostile-DBA fixture (direct SQL on BusinessDb).
  * ENC-SR-13 uses the existing fault injection.
  * ENC-SR-14/15 use revoke and edit between pages.
  * ENC-SR-17 strengthens a field after approval.
  * Additional: C7-API-01..08 (validation, single error, timing parity sampled, capability disclosure D5, cursor binding, concurrency limits, no URL/log values). C7-SEC-01 (no decryption call during search, asserted on the encryption service).
* **Functional:** text equals / prefix / contains (only when approved), number and date ranges, option and multi-option, boolean, multiple AND conditions, combined Form/Status, sort, pages, empty result, Clear search, Clear filters, C-48 lookup regression, existing My Records tests unchanged.
* **Browser (Playwright, dev stack + hosted):** layout matches §7; fields refresh on Form change; operators follow type; add/remove conditions; results in the same table; aligned controls; keyboard-only path; axe; 320/768/1440 px; 200%/400% zoom.
* **Performance:** catalogue of 100/250/500 records with 10/50 fields; p50/p95/p99, Authority calls per search (PB-09 matrix rows).

## 9. Proposed milestones

| Milestone | Content | Security-sensitive |
|---|---|---|
| **M0** | Approved UI layout restructure + `search-capabilities` and `field-search` contracts behind a server switch that returns `searchAvailable=false` / `SEARCH_VERIFICATION_UNAVAILABLE`; ActionIcons; layout/a11y tests | No (no data path) |
| **M1** | AC-5 Authority catalogue + check (with Authority tests) | **Yes** |
| **M2** | AC-6 policy, `ApproveFieldSearch` command, org settings UI, Designer Search section | **Yes** |
| **M3** | Business `FieldSearchHandler`, verified evaluator, paging cache, audit, ENC-SR suite, browser acceptance | **Yes** |
| **M4** | Hosted verification, performance measurement, owner acceptance | — |

## 10. Owner decisions required

| # | Decision | Recommendation |
|---|---|---|
| D1 | Build Authority AC-5 (form-scoped catalogue + snapshot token), or accept option B (organization-wide exhaustive verification on today's APIs) | **AC-5** |
| D2 | Search approval held by the Authority (AC-6, owner/security administrator approves per field) or by business metadata with a documented residual risk | **AC-6** |
| D3 | One unverifiable candidate fails the whole search (§5.6) | **Yes** (ADR-PB-004 §4) |
| D4 | Initial limits in §2.7 (500 records, 64 MiB, 15 s, 5 conditions) | Accept, then measure |
| D5 | Who may see a form's searchable and not-searchable field list: form users/designers and owners of a record of the form; share-only recipients see none in V1 | Accept |
| D6 | V1 excludes child, repeating-group and document predicates, field-value sorting, facets and export | Accept |
| D7 | Authorize M0 now (safe UI/contracts) while D1/D2 are reviewed | Yes |

## 11. Open risks and review requirements

* G06 and G19 remain **OPEN** until AC-5, the worker reconciliation and the ENC-SR suite have run, been measured and been independently reviewed.
* AC-5/AC-6 are Authority changes and need independent production review (like C-12, C-28, AC-1, AC-2).
* Verification cost per record is not measured. The 500 limit may have to drop.
* Approved plaintext stays readable to a business DBA (ADR-PB-004 §3).
* Unverified display metadata (form name, creator name) in result rows keeps its existing caveat.
* G18 (form-version metadata integrity) is not addressed. Labels and operators shown come from business rows, but AC-6 bounds what can be searched.

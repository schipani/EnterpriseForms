# Indexed search projection (Option C): architecture and security design — revision 1

- **Status:** **Revision 1, superseded in part by revision 2** ([INDEXED_SEARCH_PROJECTION_DESIGN.md](INDEXED_SEARCH_PROJECTION_DESIGN.md)). Received conditional architectural approval with conditions C1–C4 (C-120, 2026-11-06). Sections that revision 2 cites as "R1 §…" remain part of the design unless revision 2 changes them; where they differ, revision 2 governs. Nothing here is implemented.
- **Original status:** Design proposal for owner decision. Prepared 2026-11-05 by Claude (Opus) under the owner's design-only authorization of Option C. This is not an independent review.
- **Scope restrictions observed:**
  * no schema, worker or API change;
  * no separate Search Service and no Azure AI Search;
  * M4-1 not expanded, M4-2 not started;
  * no push, PR, merge or deploy.
- **Working tree:** written on `phase-c8/m4-governance-audit` but **not part of M4-1**. Uncommitted, so it can be moved to its own branch.
- **Binding sources:**
  * `CLAUDE.md` §§4C.6, 4H, 4L.5, 4M–4R, 4T.4, 4T.1, 4U;
  * ADR-PB-004 §7. It requires a separate ADR for any V2 trusted index, and §I maps this design to that ADR's review items;
  * ADR-PB-009 (capacity targets), ADR-PB-010 (residency), ADR-PB-013 (offboarding);
  * `docs/architecture/PHASE_C7_ADVANCED_FIELD_SEARCH.md` (D1–D7, OD-1–OD-8, AC-5, AC-6);
  * `docs/architecture/PHASE_C8_M4_1_DESIGNS_M1_S1.md` (S1);
  * `docs/architecture/PHASE_C8_M4_1_P_S1_1_SEARCH_OPTIMIZATION_PROPOSAL.md` (O-1–O-4).
- **Not claimed:**
  * G06, G19 and G21 stay **OPEN**;
  * no performance figure in this document is measured, unless it is quoted from existing evidence;
  * nothing here is production Level 2 evidence.

---

## 0. Summary

**The one central decision is where the projection lives.**
- A projection in **Business PostgreSQL** cannot prove completeness. A hostile business DBA can delete postings, and the omission is undetectable without verifying the whole scope again, which is exactly the cost we are trying to remove.
- So the proposal places the projection in **PostgreSQL administered by the Authority**: a separate `authority_search` schema in AuthorityDb.
- The Authority writes it **inside the content-commit transaction**, from the canonical bytes it already verifies at commit (`ContentService.CommitAsync` → `VerifyStagedAgainstLeavesAsync`). It writes postings only for fields with an **Active AC-6 approval**.

**Consequences:**

1. **Trust base unchanged.** Today's V1 search already trusts AuthorityDb's content heads and leaf index. V2 adds postings derived in the same transaction, from bytes verified in that transaction. **Business PostgreSQL leaves the search path entirely.**
2. **No lag in steady state.** A posting can never be ahead of or behind its head, because both commit together. Index lag exists only while a newly approved field is being backfilled (§C.3).
3. **No integrity verification at search time.** Search needs **no MAC operation and no HSM call**: integrity was established at commit. Today a 480-record search needs 3,840 HMAC verifications (P-S1-1 §5).
4. **The Authority stays the only source of permission decisions.**
   - The candidate population is the caller's SpiceDB `discover` set, read at an S1-pinned snapshot.
   - Field readability comes from SpiceDB `read`. It is computed for all of the caller's own records independently of the predicate values (§E.3), so counts cannot leak.
   - The index holds **no permission data**.
5. **Opening a record is unchanged.** The full verified read (HMAC, complete leaf set, root) still runs.

**One semantics change the owner must decide (IX-D3, §E.7).** Today a search fails when a matching record's business bytes were deleted or altered (ENC-SR-08/09). With the index, the search answers from **Authority-accepted content**. Business-side tampering is then detected:
- when the record is opened, which is fail-closed with quarantine as today;
- by the reconciliation sweep (§C.6).

It is no longer detected by the search.

---

## A. Existing implementation assessment

### A.1 Advanced Field Search today (C7 M3)

**Path:** `FieldSearchHandler.SearchAsync` → `TrustedScopeVerifier.VerifyAsync`.

1. **Predicate authorization:** `IFieldSearchPolicyAuthority.GetFieldSearchCapabilitiesAsync`, from AC-6 approvals and `query_fields`. Every condition is checked against it.
2. **Admission:** 4 searches per instance, 1 per user.
3. **Catalogue** (`SearchCatalogueService`, Authority):
   - S1 pin;
   - SpiceDB `LookupResources(form_record, discover)`, ceiling `MaximumDiscoverableRecords` = 50,000;
   - intersection with the registry for the form;
   - heads read in one transaction;
   - snapshot row and entry vector persisted;
   - over `MaximumEntries` (500) → `LIMIT_EXCEEDED`, with no entries.
4. **Per 50-record chunk:**
   - `IAcceptedRecordItemsReader.ReadAcceptedContentAsync`, one BusinessDb read per record, sequential;
   - then `VerifyRecordProjectionsAsync`. Inside the Authority, per record: one SpiceDB check for `read`, then `discover`, then field leaves; per-leaf MAC verification (8 per record in the benchmark); complete-set and root checks.
5. **Snapshot validation** (`ValidateSearchCatalogueAsync`).
6. **Evaluation in the business API** (`FieldSearchEvaluator`) on decoded, verified rows:
   - `Evaluable` requires every top-level value of each predicate field to be a readable item;
   - string comparisons are `OrdinalIgnoreCase` after `Normalize`.
7. **Sort and cursor:**
   - in-memory sort on verified metadata;
   - the complete ordered match list is carried **inside the Data Protection cursor**;
   - pages are served from the cursor after Authority re-validation.
8. **Evidence:** `RecordSearchExecutionAsync`. A completed search without evidence is refused.

**Measured cost** (P-S1-1 probe at `f117995`; C7 hosted figures):
- about 1,006 ms per 50-record Authority batch: 54% MAC, 19% SpiceDB, 27% AuthorityDb reads;
- about 12 ms per record of BusinessDb reads;
- 480 records takes about 18 s on the laptop, refused at the 15 s budget;
- hosted: about 130 verified records/s, Authority CPU-bound.

**Shape: the cost is linear in the discoverable scope, not in the matches.** Every search re-verifies every record the caller can discover in the form.

### A.2 My Records today

**Path:** `ListRecordsHandler` → `AuthorizedRecordEnumerator`.
- All pages of `DiscoverAuthorizedRecordsAsync` (250 per page) are read at one authorization revision; the business list is capped at 10,000.
- `q` is a record-id or reference lookup only (`RecordLookupQuery`), not field text.
- Filters and sorts run on **unverified BusinessDb metadata** (status, form, timestamps, creator), as the handler's own comment says. G06 remains open for that reason.

### A.3 Which O-1 to O-4 items remain necessary under indexed search

| Item | Indexed query path | Still needed for | Recommendation |
|---|---|---|---|
| **O-1** batch-level bulk SpiceDB checks in `VerifyProjectionsAsync` | The query path does not use `VerifyProjectionsAsync` | The V1 fallback (§G.2), single-record opens, the backfill builder's verification | **Split O-1.** (a) **Hardening of the bulk-check client:** results mapped by echoed request; a per-item error fails the call. **Required** by V2, which relies on bulk `field_item` checks (§E.3). Implement it with IX-2. (b) **Restructuring `VerifyProjectionsAsync`:** only needed while V1 stays in service. Optional |
| **O-2** set-based BusinessDb content reads | Not on the query path | **Backfill and rebuild** (§C.3, §C.7), V1 fallback | **Keep.** No authorization change. Reused as the builder's reader |
| O-3 concurrent MAC verification | Not used at query time | Backfill throughput against Managed HSM | Defer to PERF-01 sizing, as proposed |
| O-4 batched AuthorityDb reads | Not used at query time | Backfill and V1 | Defer |

**Net effect:** O-1 and O-2 stop being the route to scalability. They remain useful for the V1 fallback and the builder. The P-S1-1 disposition (an open, known-failing benchmark) is unaffected until V2 is accepted.

---

## B. PostgreSQL schema (Authority domain)

### B.1 Placement

| Option | Where | Completeness under a hostile business DBA | Lag | Verdict |
|---|---|---|---|---|
| C-BIZ | BusinessDb, business worker | **None.** Suppression is undetectable; injection is detectable only by full verification | Async | **Rejected** for Level 2 |
| **C-AUTH-INLINE** | AuthorityDb, schema `authority_search`, written in the content-commit transaction | **Yes**, relative to Authority-accepted state | **None** | **Recommended** |
| C-AUTH-ASYNC | A separate PostgreSQL database administered by the Authority, with an asynchronous projector from the content journal | Yes, but only with a watermark and an S1-style marker protocol | Async: fail closed or fall back while behind | Keep as the **future split** if AuthorityDb load requires it. The schema below is designed so it can move |
| External engine (Azure AI Search, OpenSearch) | Another service | Needs its own completeness proof, ACL trimming and residency review | Async | **Rejected** (§H). Not needed at PB-09 scale |

**Why inline is safe to do now:**
- the commit path already holds the organization lock;
- it has already decoded and verified every staged item;
- it already writes the manifest, leaves, head and checkpoint in one transaction.

Deriving postings adds only row inserts: no new MAC, no new network call.

### B.2 Tables

Naming follows `CLAUDE.md` §0 rules 4 and 5: UUID v7 surrogate PKs named after the entity, multiword snake_case columns. `organization_id` leads every key and every index.

**`search_projection_states`**, one row per organization:
- `search_projection_state_id` PK, `organization_id` unique;
- `projection_schema_version`;
- `projection_status_name`: `Active` | `Rebuilding` | `Quarantined`;
- `projection_content_integrity_revision`: equal to `organization_authorization_states.content_integrity_revision` after every commit (inline);
- `projection_status_reason_code`, `projection_modified_on`.

**`search_field_coverages`**, one row per approval:
- `search_field_coverage_id` PK, `organization_id`, `form_definition_id`, `field_definition_id`, `field_search_approval_id` (FK, unique);
- `indexed_value_type_name`;
- `coverage_state_name`: `Building` | `Complete` | `Withdrawn`;
- `covered_field_classification`, with CHECK `IN ('Public','Internal')`;
- `coverage_started_on`, `coverage_completed_on`, `coverage_attested_journal_entry_id`, `coverage_withdrawn_on`, `coverage_withdrawal_reason_code`.

**`search_record_entries`**, one row per registered, contentful record:
- `search_record_entry_id` PK, `organization_id`, `form_definition_id`, `form_record_id` (unique per organization);
- `indexed_operation_id` and `indexed_content_revision`: must equal the record's current `record_content_heads` row;
- verified header facts, decoded at commit from the **signed header leaf**: `record_status_name`, `parent_form_record_id`, `created_by_business_user_id`;
- `record_registered_on`, `head_modified_on`;
- `indexed_postings_digest`: SHA-256 over the record's canonical sorted posting tuples (§C.6);
- `entry_written_on`.

**Typed posting tables:**
- `search_string_postings`, `search_integer_postings`, `search_decimal_postings`, `search_date_postings`, `search_datetime_postings`, `search_boolean_postings`, `search_option_postings`;
- each has its own PK (for example `search_string_posting_id`);
- common columns: `organization_id`, `form_definition_id`, `field_definition_id`, `form_record_id`, `manifest_item_id` (the `field_item` id, used for readability checks), `indexed_operation_id`;
- one typed value column:
  * `normalized_string_value text COLLATE "C"`: `Normalize(text).ToUpperInvariant()` computed **in .NET by the evaluator's own function**, never by PostgreSQL `lower()`/`upper()` (§B.4). Only the normalized form is stored (minimization);
  * `integer_value bigint`;
  * `decimal_value numeric(19,4)`;
  * `date_value date`;
  * `datetime_value timestamptz`;
  * `boolean_value boolean`;
  * `field_option_id uuid` (one row per selection leaf).

**`search_record_field_anomalies`:**
- `search_record_field_anomaly_id` PK, `organization_id`, `form_record_id`, `field_definition_id`, `indexed_operation_id`;
- `anomaly_kind_name` (`EncryptedValuePresent`).
- It records that an approved field holds an encrypted leaf on that record. The query fails with `PREDICATE_VALUE_ENCRYPTED` (C7 parity, architecture §3.5) and never indexes ciphertext.

**`search_form_states`**, one row per (organization, form):
- `search_form_state_id` PK, `form_content_revision`;
- the revision is incremented inline on every commit, registration or lifecycle change of a record of that form;
- used to validate paging (§E.5).

**`search_result_snapshots`** and **`search_result_snapshot_entries`:**
- Authority-held ordered match lists for paging (§E.5);
- they hold record ids and sort keys only, never values;
- expiry equals the catalogue snapshot (5 to 15 minutes); purged like `search_catalogue_snapshots`.

**Not indexed in V2** (D6 unchanged):
- repeating-group values (`FormRecordGroupId` not null);
- related or child predicates;
- documents and attachment metadata;
- Confidential and Restricted values, and encrypted leaves;
- history (only the current accepted revision is indexed);
- labels and display text.

### B.3 Indexes and candidate queries

| Table | Index | Serves |
|---|---|---|
| `search_record_entries` | `(organization_id, form_definition_id, form_record_id)` unique; `(organization_id, form_definition_id, head_modified_on DESC, form_record_id)`; `(organization_id, form_definition_id, record_registered_on, form_record_id)` | Scope join and metadata sort |
| string | btree `(organization_id, field_definition_id, normalized_string_value, form_record_id)`; GIN `gin_trgm_ops` on `normalized_string_value`, **only for fields approved with `contains`** (§B.4) | `equals`, `startsWith` (`LIKE 'x%'` under `C` collation), `contains` (`LIKE '%x%'`, minimum 3 characters) |
| integer, decimal, date, datetime | btree `(organization_id, field_definition_id, <value>, form_record_id)` | equality and ranges |
| boolean | btree `(organization_id, field_definition_id, boolean_value, form_record_id)` | `isTrue`/`isFalse` |
| option | btree `(organization_id, field_definition_id, field_option_id, form_record_id)` | `is`/`includes` |
| every posting table | `(organization_id, form_record_id)` | delete and replace at commit, purge |

**Candidate query shape** (one parameterized statement per request, REPEATABLE READ):

```sql
-- Illustrative only. $scope is the caller's readable-for-predicate set from §E.3, passed as uuid[] (unnest) or a temp table.
WITH scope AS (SELECT unnest($scope::uuid[]) AS form_record_id)
SELECT e.form_record_id, e.record_status_name, e.parent_form_record_id, e.created_by_business_user_id,
       e.record_registered_on, e.head_modified_on
FROM search_record_entries e
JOIN scope s USING (form_record_id)
JOIN authority.record_content_heads h
  ON h.organization_id = e.organization_id AND h.form_record_id = e.form_record_id
 AND h.committed_operation_id = e.indexed_operation_id          -- binding check (defence in depth)
WHERE e.organization_id = $org AND e.form_definition_id = $form
  AND <metadata filters on verified header facts>
  AND EXISTS (SELECT 1 FROM search_string_postings p
              WHERE p.organization_id = $org AND p.form_record_id = e.form_record_id
                AND p.field_definition_id = $f1 AND p.indexed_operation_id = e.indexed_operation_id
                AND p.normalized_string_value LIKE $pattern1 ESCAPE '\')
  AND ... -- one EXISTS per condition, from a fixed allowlisted template per (value type, operator)
ORDER BY e.head_modified_on DESC, e.form_record_id DESC;
```

**Rules:**
- SQL text comes only from fixed templates chosen by `(value type, operator)`. Field ids, values, bounds and patterns are always parameters. `LIKE` metacharacters in values are escaped. No identifier is ever derived from input.
- The **consistency guard** runs in the same transaction, over the scope (not the matches). The search fails closed if any record in the scope has:
  * a contentful head but no entry;
  * an entry whose `indexed_operation_id` differs from its head;
  * a quarantined head;
  * an `EncryptedValuePresent` anomaly on a predicate field.
- `statement_timeout` and `lock_timeout` are set per transaction. A timeout is `SEARCH_VERIFICATION_LIMIT` (`time`), never partial results.

### B.4 String semantics and parity

- The evaluator uses `Normalize`, then `OrdinalIgnoreCase`. PostgreSQL case functions do not reproduce .NET simple case mapping. Both sides therefore compare **byte-exact** under `COLLATE "C"` on a value normalized once, in .NET, by the same code.
- The trigram GIN only accelerates `LIKE`. Correctness comes from `LIKE`'s recheck under `C` collation. pg_trgm's locale-dependent character classes can affect index selectivity, never results.
- Long values: GIN trigram cost grows with text length. The trigram index is created per approved field with `contains`, as a partial index on `field_definition_id`. Size per organization is a PERF-01 measurement. Partial indexes are DDL, so creating them at approval time needs a migration-time strategy (§B.6). The alternative is one shared GIN for all string postings.

### B.5 Partitioning and growth

- **Start unpartitioned.** `organization_id` leads every key.
- At the PB-09 envelope:
  * at most 100K records × assumed 20 approved fields ≈ 2M postings per organization;
  * 25 organizations ≈ 50M posting rows across tables;
  * very roughly 0.4 to 0.6 KB per posting including indexes, plus trigram GIN on string fields. On the order of **1 GB per fully populated organization** and **25 GB platform-wide**.
  * These are estimates. PERF-01 replaces them with measurements.
- **Trigger to revisit:** about 10M rows in one table, or vacuum or bloat measurements. HASH partitioning by `organization_id` uses a fixed partition count, so the runtime role needs no DDL privilege (G08). PKs then become `(search_*_id, organization_id)`.
- LIST partitioning per organization would make PB-13 disposal a `DETACH`/`DROP`, but it needs runtime DDL. It is not recommended without a separately privileged, journaled partition-management command.

### B.6 Privileges (G08)

- NOLOGIN owner `authority_search_owner` owns the schema.
- The Authority commit path's role gets `INSERT, DELETE` on postings and entries and `UPDATE` on coverage and state rows. It has no DDL.
- A **separate read-only role** `authority_search_reader` (its own connection string in the Authority) gets `SELECT` on `authority_search` plus `SELECT` on `record_content_heads` and `authority_resources` only. The query endpoint uses only this role.
- ENABLE and FORCE RLS on every `authority_search` table, keyed on the transaction-local organization GUC. It is defense in depth, not authorization (`CLAUDE.md` §4Q.3).
- `postgres-privilege-inventory.sql` and the CI inventory are extended. pgAudit and the logging configuration for the reader role **must not log bind parameters**: `log_parameter_max_length = 0` and `log_parameter_max_length_on_error = 0` for that role, and no `auto_explain` with parameters. Otherwise predicate values reach the logs (T10).
- Partial trigram indexes per field are not created at runtime. Either (a) one shared trigram GIN, or (b) a JIT migration identity creating them in a reviewed deployment. **Recommendation: (a)**, then measure.

---

## C. Projection lifecycle

### C.1 Steady state: derivation at commit (inline)

Inside `ContentService.CommitAsync`, after `VerifyStagedAgainstLeavesAsync` succeeds and before `scope.CommitAsync`:

1. Load the **Active** approvals and coverages for the record's form: `Building` or `Complete`, never `Withdrawn`.
2. From the verified staged items, take the header leaf and every top-level `FieldValue`/`FieldSelection` leaf whose field has an approval. A leaf that is application-encrypted (by envelope or protection mode, per the Authority-registered field manifest, **not** business metadata, G21) produces an `EncryptedValuePresent` anomaly and no posting.
3. Delete the record's prior postings, anomalies and entry. Insert the new ones bound to `operationId`.
4. Compute `indexed_postings_digest`.
5. Increment `search_form_states.form_content_revision`. Set `search_projection_states.projection_content_integrity_revision = state.ContentIntegrityRevision`.

**Derivation rules:**
- Derivation is a **pure, versioned function** (`SearchPostingDerivation/v1`) of verified canonical bytes plus the Authority-held approval. It has golden test vectors.
- Any derivation error **aborts the commit**. Its cost is row inserts only.
- A commit can therefore not succeed while the projection is wrong. The alternative is to commit and mark the projection `Rebuilding`; that trades availability of search for availability of writes.
  * **Recommendation:** abort, because derivation is deterministic and local.
  * **Owner decision IX-D7.**

**Same-transaction rule for other events** that change what search may see:
- record registration (an entry at revision 0 is not written; pending creations are counted as today);
- lifecycle change or unregistration: entry and postings deleted;
- reprotection or deprotection commits (A1): the same derivation, so newly encrypted leaves stop being indexed;
- quarantine: the head flag is read by the guard; postings stay but are unusable.

Authorization changes (shares, teams, roles, ownership) **do not touch the index**: it holds no permissions.

### C.2 Approval changes

| Event (AC-6 security command) | Same Authority transaction |
|---|---|
| Approval becomes Active | Coverage `Building`; journal event `SearchIndexCoverageStarted`. From this commit on, inline derivation includes the field |
| Withdrawal, or invalidation (classification strengthened, version re-registration that invalidates under AC-6 §4.3, organization floor raised) | Coverage `Withdrawn`; **delete every posting and anomaly of that field**; journal `SearchIndexCoverageWithdrawn`. The capability list stops offering the field in the same revision |
| Operators narrowed | Nothing is deleted. The query refuses operators not in the current approval (already enforced by AC-6) |

**Residual (R-IX-3):** deleted plaintext remains in WAL, dead tuples (until VACUUM) and AuthorityDb backups until their retention expires. This is the same class of issue as DR-C7-4 (history disposal). A forced `VACUUM` of the affected tables after withdrawal shortens the window but does not remove backups.

### C.3 Backfill for records committed before the approval

Postings cannot be derived without the canonical bytes, and the Authority does not store values. So backfill re-verifies:

1. A separate workload identity **`search-index-builder`** (OD-1 pattern: own credential, network policy and Authority role, operator plane, no user assertion) is triggered by `SearchIndexCoverageStarted`.
2. The builder asks the Authority for the **organization-scoped population** of the form (the migration-catalogue pattern: all `Active` and `Quarantined` records, never exposed to users). It reads BusinessDb content for records whose entry is missing or older than the approval. It does so set-based (O-2) and submits them in batches.
3. The Authority verifies each record exactly as `VerifyForProtectionCensusAsync` does: complete leaf set, every HMAC, root, current head. Then it derives postings in its own transaction, conditional on the head being unchanged (CAS on `committed_operation_id`). The builder never supplies postings, only bytes, so **it cannot inject values**: unverifiable bytes are refused. A mismatch quarantines the record exactly as V1 does.
4. **Completion is attested by the Authority, not claimed by the builder.** Under the organization lock, the Authority checks that every contentful, registered record of the form has an entry at its current head. It then sets coverage `Complete` and journals `SearchIndexCoverageAttested` (counts only). Records committed during the backfill were already indexed inline.
5. Resumable rounds, an idempotency key per batch, and a bounded rate are configured from PERF-01 Managed HSM limits (8 MAC verifications per record: about 800K for 100K records).

While a coverage is `Building`, the field is **not searchable through the index** (§G.2 fallback rules).

### C.4 Ordering, retries and deduplication

- **Inline:** ordering is the organization lock plus the head CAS. There is no queue, so nothing to reorder or deduplicate.
- **Builder:**
  * idempotent per (record, head operation);
  * a stale submission (the head moved) is refused as `HEAD_CHANGED` and retried from the new head, or skipped when inline derivation has already covered it;
  * retries use exponential backoff;
  * a quarantined record blocks `Complete` for that form, which is fail closed and reported to the security administrator.

### C.5 Stale or inconsistent projection

| Detected condition | Where | Response |
|---|---|---|
| Entry missing or stale against its head, for a record in a query's scope | Query guard (§B.3) | Search fails `SEARCH_VERIFICATION_UNAVAILABLE` (`SEARCH_INDEX_INCONSISTENT`); projection state set to `Quarantined` under the organization lock after a lock-confirmed re-read (S1 pattern); Critical alert; audit-stream event |
| `projection_content_integrity_revision` ≠ organization content revision | Query start | Same |
| `indexed_postings_digest` mismatch | Sweep (§C.6) | Same |
| Coverage `Building` for a predicate field | Query authorization | `SEARCH_INDEX_NOT_READY` (retryable), or V1 fallback (§G.2) |

**Projection quarantine is scoped to search.**
- It does **not** close the organization's authorization gate, and record reads and writes continue.
- It must not be confused with the S1 permanence trigger, which is for authorization integrity.
- Clearing it requires a journaled `SearchProjectionRebuild` operator command and a completed rebuild (§C.7).
- **Owner decision IX-D7.**

### C.6 Reconciliation

- **Cheap sweep** (Authority worker, hourly; no HSM, no BusinessDb):
  * every contentful registered record has exactly one entry at its head;
  * no entry, posting or anomaly exists for an unregistered, non-Active or other-organization record;
  * no posting exists for a `Withdrawn` coverage;
  * recomputed `indexed_postings_digest` equals the stored value;
  * the projection revision equals the content revision.
- **Deep sampled sweep** (builder, daily, rate-limited): re-derive postings for a random sample from verified BusinessDb bytes and compare. This catches a derivation bug, or an Authority-domain alteration that also recomputed the digest.
  * A mismatch is a Critical integrity finding. It quarantines the projection.
  * A business-byte mismatch quarantines the record, as today.
- **Evidence:** counts only, journaled, visible on the Release Security Dashboard (PB-12) as health.

### C.7 Rebuild and recovery (G14 interaction)

- **Rebuild** = state `Rebuilding`: delete the organization's projection, set every coverage to `Building`, then run builder rounds and Authority attestation as in §C.3. Search for the organization is index-unavailable until coverage is `Complete` again.
- **After an AuthorityDb restore and journal replay:**
  * Postings restored with AuthorityDb are coherent with the restored heads (same database).
  * Content-journal replay can advance heads, and checkpoint bodies carry no leaf bytes. Those records' entries become stale. The guard detects this, and the projection goes `Rebuilding`.
  * The postings are **never** treated as evidence for recovering content. They are derived data.
- **PB-13 offboarding:** delete the organization's rows in the disposal transaction. Exports never read the index.
- **PB-10:** AuthorityDb now holds customer Public/Internal plaintext. Update the AuthorityDb row of `PB10_DATA_FLOW_RESIDENCY_REGISTER.csv`, covering backups, retention and legal hold.

---

## D. Security architecture

### D.1 Trust statement

**Trusted, as today:**
- the Authority runtime;
- AuthorityDb and its administrators;
- SpiceDB, under S1;
- the MAC provider.

**Untrusted, as today:**
- Business PostgreSQL;
- the business API's inputs.

**New:** Authority administrators can now read approved Public/Internal **values** in `authority_search`, not just their digests.
- This is a small increase: the Merkle leaf index already holds `CanonicalPayloadDigest` of these low-entropy values, which is dictionary-attackable (MRK confidentiality note).
- A business DBA can already read the same values (ADR-PB-004 §3).
- **Owner decision IX-D2.**

### D.2 Threat model

| ID | Threat | Control | Tests |
|---|---|---|---|
| IX-T01 | **Cross-organization leakage** | `organization_id` from the verified DUA binding, never from the request body; leading column of every key and predicate; composite FKs to heads; FORCE RLS on the reader role; snapshots bound to organization, actor and session | IX-01, IX-02 |
| IX-T02 | **Confidential field exposure** | Postings only for fields with an Active AC-6 approval whose **Authority-registered** classification is Public or Internal and protection is Standard; CHECK on coverage classification; encrypted leaves produce an anomaly and never a posting; business metadata cannot change the derivation input (G21) | IX-03, IX-04, IX-05 |
| IX-T03 | **Stale searchable values after a classification change** | Coverage withdrawn and postings purged **in the same Authority transaction** as the invalidating command; the query requires an Active approval and `Complete` coverage in its own transaction | IX-06, IX-07 |
| IX-T04 | **Unauthorized record existence disclosure** (matches, counts, sort, pagination, limits) | The population is the caller's own discover set at an S1-pinned snapshot; predicate-field readability is computed over the caller's records **independently of predicate values** (§E.3); counts come only after that; every limit refusal depends only on the caller's own set sizes; no suggestions, facets, field-value sort or raw counts (D6); an unknown form looks like a non-existent one | IX-08…IX-12 |
| IX-T05 | **Timing side channel** (query plan cost depends on organization-wide matches outside the caller's scope) | Scope-driven join (scope ⋈ entries ⋈ postings by record); admission limits; no per-match decisions. Residual: low-bandwidth timing (**R-IX-1**) | IX-13 (measure and report) |
| IX-T06 | **Projection tampering by a business DBA** | No access: separate cluster and roles (G08); no business-written path; the business API never supplies postings | IX-14, IX-15 |
| IX-T07 | **Projection tampering or partial restore in the Authority domain** (operator error, bug) | Head binding at query time; projection revision check; postings digest; cheap and deep sweeps; projection quarantine; Critical alert | IX-16, IX-17 |
| IX-T08 | **Replay or rollback** (old postings, or an old AuthorityDb snapshot) | Postings bound to the head operation id; an AuthorityDb rollback is G14's domain (journal replay, ledger checkpoints) and stale entries are then detected; a restore of `authority_search` alone is detected by revision and binding checks | IX-18, IX-19 |
| IX-T09 | **Authorization change during a search** | S1 pin, decide (all lookups and checks at the exact snapshot), verify, then an AuthorityDb re-read; revision moved → `SEARCH_SNAPSHOT_CHANGED` with one retry, as today; paging re-validates (§E.5); revocation → page 2 refused | IX-20, IX-21 |
| IX-T10 | **Content change during a search** | One REPEATABLE READ statement sees heads and postings committed atomically; paging invalidated by `form_content_revision` | IX-22 |
| IX-T11 | **Quarantined organization or record** | Organization gate closed → refused as today (423/503); a quarantined head in scope → whole search fails (D3 parity); projection quarantine → index search unavailable | IX-23, IX-24 |
| IX-T12 | **Compromised indexing worker** | Steady state has no worker: derivation is in-process. The builder submits **bytes only**, which the Authority verifies against heads, so it cannot inject or alter values; it can only withhold, which leaves coverage `Building` (fail closed). `Complete` is attested by the Authority from the registry. The builder cannot run queries or read results | IX-25, IX-26 |
| IX-T13 | **Unavailable worker or HSM during backfill** | Coverage stays `Building`; resumable; the query never treats `Building` as complete | IX-27 |
| IX-T14 | **Predicate values in logs, telemetry or audit** | POST bodies only; no parameter logging on the reader role (§B.6); evidence records field ids and operators only (OD-8) | IX-28 |
| IX-T15 | **Expensive or abusive queries** | At most 5 AND conditions; `contains` needs at least 3 characters; `statement_timeout`; admission per instance and per user; Authority-side concurrency cap | IX-29 |
| IX-T16 | **Forged predicate field or operator** | AC-6 capability check in the business API **and again in the Authority query** (field, operator, value type, approval id, coverage) | IX-30 |
| IX-T17 | **Partial-share inference** (matching on a value the caller cannot read) | Exclusion before evaluation, at item level: every posting item of a predicate field must be readable (C7 `Evaluable` parity) | IX-09, IX-10 |
| IX-T18 | **Search used as an integrity oracle or bypass** | Search never returns values; opening a record performs the full verified read; ENC-SR-08/09 detection moves to read and sweep (IX-D3) | IX-31, IX-32 |

**Residual risks** (documented, not mitigated):
- **R-IX-1:** timing residual (IX-T05).
- **R-IX-2:** Authority administrators read approved plaintext (IX-D2).
- **R-IX-3:** deleted plaintext persists in WAL and backups until expiry (§C.2).
- **R-IX-4:** business-byte tampering is not detected by search (IX-D3).
- **R-IX-5:** the 50,000 discover ceiling limits a single user's searchable scope (§F.4).
- From C7, unchanged:
  * a delivered match has already been seen;
  * the Authority runtime is trusted.

### D.3 Authority as the sole decision-maker

- No permission is stored in, derived from or cached by the projection.
- No SQL evaluation of desired edges (Alternative D, §H).
- The query endpoint makes its decisions through the S1 `VerifiedProjectionSnapshot` only. It is the same type-level enforcement as every other SpiceDB access.

---

## E. Search execution

### E.1 One Authority call per search

New private Authority operation: `POST /internal/v1/search/indexed-query`. It uses the business workload credential and the delegated user assertion, and is published only in the Authority's private OpenAPI.

The business `FieldSearchHandler` keeps validation, the capability check and admission. It replaces `TrustedScopeVerifier` plus the in-memory evaluation with a single call when the engine is `Indexed` (§G.2).

Inside the Authority:

1. **Bind and gate:** bind the actor and organization; gate open; projection `Active`. Otherwise refuse with the existing codes.
2. **Authorize predicates:**
   - `query_fields` on the form;
   - each condition's field has an Active approval, the operator is in that approval, the value type matches;
   - coverage is `Complete`.
   - Otherwise the same indistinguishable `SEARCH_FIELD_NOT_ALLOWED` / `SEARCH_OPERATOR_NOT_SUPPORTED`, or `SEARCH_INDEX_NOT_READY`.
3. **S1 pin.**
4. **Population** (§E.3).
5. **Candidate query and consistency guard** (§B.3) in one REPEATABLE READ transaction. It also records `content_integrity_revision` and `form_content_revision`.
6. **Edit flags for the first page:** one bulk `edit` check at the pinned snapshot, folded in to remove today's separate call.
7. **S1 verify,** then an AuthorityDb re-read of the authorization revision. If either moved, fail with `SEARCH_SNAPSHOT_CHANGED` (the business retries once, as today).
8. **Persist and journal:** the result snapshot (§E.5) and `FieldSearchExecuted` evidence (engine `Indexed`, field ids, operators, counts, outcome), **in the same transaction** as the result snapshot. No result is released without evidence. This removes today's separate evidence call and its failure window.
9. **Return** the first page: ids, verified header facts, edit flags, total count, snapshot id, revisions.

### E.2 Calls per search

| | V1 today (480 records) | V2 indexed |
|---|---|---|
| Business → Authority calls | 1 catalogue + 10 verify batches + 1 validate + 1 evidence + 1 edit batch | **1** |
| BusinessDb reads | 480 | **0** (display names only, as today) |
| SpiceDB | pin + lookup + about 480 to 1,440 single checks + verify | pin, verify, 2 lookups (paged), ⌈k / B⌉ bulk `field_item` checks (k = items of share-only records, §E.3), 1 bulk `edit` |
| MAC / HSM operations | 3,840 | **0** |

### E.3 Population and field readability without predicate inference

Schema facts (`authority.schema.zed`):
- `form_record.discover = effective_owner + shared_via->read`;
- `form_record.read = effective_owner`;
- `field_item.read = parent_record->effective_owner + shared_via->read`.

Steps, all at the pinned snapshot:

1. `D` = `LookupResources(form_record, discover, user)`. This is the existing call, with the 50,000 ceiling.
2. `O` = `LookupResources(form_record, read, user)`: records the caller effectively owns, directly, through a team, or through a parent. **Every field item of these is readable.**
3. `S` = (`D` \ `O`) restricted to the form: share-only records. The restriction uses `search_record_entries`, not the predicate.
4. For each record in `S` and each **predicate field**, take every posting `manifest_item_id` of that field. Bulk-check `field_item#read` for all of them, using the O-1-hardened client: echoed-request mapping, per-item error fails the call, chunked below the confirmed SpiceDB maximum (CT-7).
   - **The checked set depends only on the caller's records and the predicate field ids, never on the predicate values.**
   - Records lacking a posting for the field have no item to check. They cannot match, exactly as in C7, where a missing value never matches.
5. The **readable-for-predicate scope** = `O ∩ form` ∪ {r ∈ `S` : every predicate-field posting item of r is readable}. This is the `$scope` of §B.3.

**Limits:**
- All limits are on caller-owned quantities: `|D|`, `|S|` and the item count `k`. Exceeding one returns `SEARCH_VERIFICATION_LIMIT`.
- The proposed initial limit is `k` ≤ 20,000 items, configurable downwards only, and measured.
- A refusal reveals the size of the caller's own sets, which the caller can already see in My Records.
- Nothing about records outside `D` is revealed, so there are no "too broad" refusals based on organization-wide matches.

**Partial readers:** discover-only callers (a share with no field items) end up with an empty `S` contribution. Their results equal C7's, because `Evaluable` excludes them.

**C-141 amendment (owner-authorized correction of UF-25, 2026-11-09; implemented, not independently reviewed).** Steps 4 and 5 and the limit on `k` above are superseded as follows; the scope they define, and therefore the results, are unchanged ([INDEXED_SEARCH_UF25_SHARE_ONLY_ITEM_CHANNEL.md](INDEXED_SEARCH_UF25_SHARE_ONLY_ITEM_CHANNEL.md) §8).
- Step 4 enumerated every posting item, including items the caller cannot read, so the check set and the limit depended on unauthorized content (UF-25); "depends only on the caller's records and the predicate field ids" was imprecise.
- **4′.** Candidates are read from the caller's own grants at the pinned authorization revision: the caller's team memberships, the shares whose reader/contributor/manager subject is the caller or one of those teams, those shares' records in `S`, and the field items shared through them, each joined **by item id** to a current posting of a predicate field. SpiceDB confirms every candidate (`field_item#read`). A candidate SpiceDB denies at a consistent pin is escalated by Q4 (design §17.2) and fails the search.
- **5′.** r ∈ `S` is evaluable on field f iff the number of **distinct** confirmed items of f in r equals the accepted total count of f in r, `derived_field_item_counts` of r's entry (derivation version 3). The union over all paths is taken first. An empty field (0 = 0) is evaluable, as before.
- **Limit.** `k` is the number of candidates, which are items the caller is permitted to read. The 20,000 default is unchanged.
- **Fail closed.** Missing or malformed counts, more readable items than the total, or a candidate outside `S` or the predicate fields refuse the search; a version 2 entry is not ready (503). Every share-only match is re-checked on bound data: its bound postings must be exactly its count, all confirmed readable.

### E.4 Sort, count and pagination semantics

- **Sort:** metadata only (`modifiedOn`, `createdOn`, `recordStatus`; `formName` is constant within one form), in SQL, with the total order `… , head_modified_on, form_record_id`. Field-value sort stays excluded (D6).
- **Count:** the exact number of authorized matches at the snapshot. It never includes, and never reveals, the number of unauthorized matches.
- **Completeness statement:** "Complete: all N records of this form that you can find were evaluated against their accepted content at one Authority snapshot (indexed)…". The wording changes because records are no longer re-verified at search time (IX-D3).

### E.5 Pagination

- Today the entire ordered match list lives in the Data Protection cursor. That does not scale: 10,000 matches would make a cursor of several hundred KB.
- **Proposal:** the Authority stores the ordered result as `search_result_snapshot_entries`. These hold ids and sort keys only, expire like the catalogue, and are capped at **10,000 matches**, the same as the My Records ceiling. Above the cap: `SEARCH_VERIFICATION_LIMIT` (`results`), over authorized matches only.
- The business cursor carries only the snapshot id, offset, query digest, user, session and expiry, still protected by Data Protection.

`POST /internal/v1/search/indexed-query/{snapshotId}/page` re-validates:
- snapshot owner, organization, actor and expiry;
- the gate is open and `authorization_revision` is unchanged (OD-5: any security change invalidates);
- `form_content_revision` is unchanged (any change to a record of the form invalidates; it replaces the per-entry slow path, is cheaper and is at least as strict);
- the projection is `Active`.

It then returns the slice plus fresh `edit` flags at a newly pinned snapshot.

**The result list is never kept in BusinessDb,** where a DBA could reorder or suppress page entries.

**UX risk:** in a form under continuous edits, page 2 may often return `SEARCH_SNAPSHOT_CHANGED`. This is to be measured. A relaxation (for example, invalidating only when a matched or in-scope record changed) needs a separate approval.

### E.6 My Records (optional, IX-5)

The same machinery answers My Records without field predicates:
- `D` from the lookup, as today;
- metadata filters and sort over `search_record_entries` (**verified** header facts instead of unverified BusinessDb columns, which narrows the G06 caveat noted in `ListRecordsHandler`);
- exact counts;
- Authority-held paging.

**Kept unchanged:**
- the UX and response contract;
- the record-reference lookup (`q`);
- `includeChildren` and `includeArchived`;
- unavailable-id reporting, renamed to the precise category: "registered without accepted content".

Form names for display and the `formName` sort stay as today (cosmetic, unsigned) unless the owner prefers the Authority's `FormTitleSnapshot`. This is a separate stage and approval.

### E.7 How the index avoids verification without weakening guarantees

| Guarantee | V1 basis | V2 basis |
|---|---|---|
| Content a match is evaluated on is the accepted content | HMAC and leaf-set verification of BusinessDb bytes at search time | Postings derived from the same verification **at commit**, bound to the head operation, read in the same trusted store as the head |
| No suppressed match | Catalogue-driven scan of the full scope | Catalogue-equivalent scope, plus a guard that every in-scope contentful record has a current entry |
| No injected match | Catalogue drives; injected rows never read | Index is written only by the Authority; the business never writes it |
| Rollback | Rows of an old operation fail the head | Postings of an old operation fail the binding check; AuthorityDb rollback is G14 |
| Business bytes still present and intact | **Checked at search time** | **Not checked at search time**; checked at open and by sweeps (IX-D3) |

The only property V2 gives up is the last row, and it is not a confidentiality or authorization property. The verification rule of §4L.5 ("verify before trusting, rendering, … using in a search predicate") still holds for **business** rows: search no longer consumes any business row. Postings are Authority-held derived state with the same trust as heads and leaf digests.

---

## F. Performance and operational design

### F.1 Expected cost model (to be measured; nothing here is measured)

| Component | Grows with | Expectation |
|---|---|---|
| `LookupResources` × 2 | the caller's discoverable and owned records, organization-wide | Measured today only inside the catalogue: 178 ms at 480. **Unknown at 10K to 50K: PERF-01's main risk** |
| Bulk `field_item` checks | share-only records × predicate fields × items | 0 for owners; ⌈k / B⌉ calls otherwise |
| Candidate SQL | scope size, plus selective index scans | Expected tens to low hundreds of ms at 100K with selective predicates; `contains` on broad text is the expensive case |
| Commit overhead | approved fields per record | A few row deletes and inserts plus GIN pending-list maintenance; target **≤ +15 ms p95 per commit** (proposed) |
| HSM at search time | none | **0** |
| HSM at backfill | records × leaves | 8 per record in the benchmark form; about 800K for 100K records, rate-limited |

### F.2 Benchmarks

**Datasets:**
- **Records in the form's scope:** 50, 150, 480, 1,000, 10,000, 100,000. The 100K case is one organization at the PB-09 envelope.
- **Fields per record:** 10, 50 and 250, with 5 and 20 approved.
- **Predicates:** 1 to 5.
- **Selectivity:** 0.1%, 10%, 100%.
- **String length:** short, and up to the field maximum.

**Caller shapes:**
- owner of all;
- team owner;
- share-only full reader;
- share-only partial reader of 10% of fields;
- discover-only;
- callers who discover 100, 1K, 10K and 50K records in a 100K organization.

**Load:**
- concurrency 1, 4, 8 and 16;
- concurrent commit load of 0, 5 and 25 commits/s in the same form, which measures paging invalidation and lock contention.

**Measure:**
- end-to-end p50, p95 and p99, and Authority time by stage (lookups, checks, SQL, persist);
- SpiceDB calls and items;
- `EXPLAIN (ANALYZE, BUFFERS)` for every operator template at 10K and 100K;
- commit p95 with derivation on and off;
- index and table size per 100K records, and bloat after 1M updates;
- backfill records/s and HSM ops/s;
- rebuild time;
- shadow differential (must be zero);
- page-2 invalidation rate.

**Targets** (PB-09, unverified objectives):
- verified search p95 < 3 s at the approved load;
- record write p95 < 2 s, with derivation's added p95 ≤ 15 ms (proposed);
- dashboard card < 2 s.

**Environments:**
- local stacks (relative numbers only);
- **Azure staging PERF-01 with Managed HSM** decides.

### F.3 Managed HSM implications

- Search: no HSM traffic. This removes the PB-09 / PERF-01 concern raised in P-S1-1 §5 for queries.
- Backfill and rebuild are the only bulk MAC consumers. They must be throttled below Managed HSM service limits, which are unmeasured and must come from PERF-01. They run as background work with bounded concurrency (O-3 sizing).
- An HSM outage during a backfill leaves the coverage `Building`. Search on other `Complete` fields is unaffected.

### F.4 Operational behaviour

- **Index lag:** zero for commits by construction. For a new approval, lag equals the backfill duration, surfaced as coverage `Building` with progress counts on the security administrator's Search approvals page.
- **Alerts** (Critical): projection quarantine; sweep mismatch; derivation error.
- **Alerts** (Warning): coverage `Building` beyond its SLO; builder failure rate.
- **Metrics:** meter `AuthorizationAuthority.SearchIndex`.
- **Known capacity limit (R-IX-5):** a caller whose discover set exceeds 50,000 cannot search. If PB-09 needs single users with 100K-record scope, the options are (a) an Authority-side SQL evaluation of desired edges (Alternative D), or (b) a SpiceDB-side form-scoped permission. **Both need a separate decision.**

---

## G. Migration and rollout

### G.1 Flags

**Business:** `FieldSearch:Engine` = `VerifiedScan` (default; today's path) | `Shadow` | `Indexed`.
- The startup environment guard is the same as `ExecutionEnabled`: Development and Testing only until the production prerequisites exist.

**Authority:**
- `SearchIndex:DerivationEnabled`: write postings at commit; can be on while querying is off;
- `SearchIndex:QueryEnabled`;
- `SearchIndex:BuilderEnabled`.

Each flag fails closed and is validated at startup.

### G.2 Fallback rules (no silent mixing)

- With engine `Indexed`, a search on a field whose coverage is `Building` (or a projection that is `Rebuilding`) may run **V1 only when** the scope is within V1's limits. The response then states the engine (`VerifiedScan`).
- Otherwise it returns `SEARCH_INDEX_NOT_READY`, which is retryable.
- One search never combines results from both engines.
- **Owner decision IX-D8.**

### G.3 Shadow comparison

In `Shadow`:
- the business returns the **V1** result, which remains authoritative for the user;
- for a bounded sample, it also runs the indexed query under a separate admission slot;
- it compares the ordered id lists, the total count and the error code;
- a difference produces evidence (ids and counts only, no values) and a Warning alert.

**Exit criterion:** zero differences over the full Stage B search shards, the IX adversarial suite and N production-like synthetic searches (N set at approval).

Known **legitimate** differences, excluded from comparison and listed:
- ENC-SR-08/09 scenarios, where V1 fails and V2 answers (IX-D3);
- scopes above V1's 500-record limit.

### G.4 Rollback and compatibility

- Flags off → today's path, unchanged. V1 code (`TrustedScopeVerifier`, the evaluator, `VerifyProjectionsAsync`) is retained until V2 is accepted. Removing it needs approval.
- Schema changes are additive (expand only). The projection is derived data, so it can be truncated or dropped without loss.
- **The public contract is unchanged:** `FieldSearchResponse`, error codes and the cursor stay opaque. New: one optional `unavailable_reason` value (`SEARCH_INDEX_NOT_READY`) and an engine indicator inside the scope statement.
- The Authority private OpenAPI gains two operations, published separately.
- **The index is never authoritative** for record contents (opening a record still verifies) or for access (SpiceDB decides).

---

## H. Alternatives

| | Alternative | Why not (or not now) |
|---|---|---|
| A | V1 verified scan with O-1 to O-4 only | Cost stays linear in the discoverable scope, with 8 HSM operations per record per search. 480 records is about 12 to 13 s at best locally; 100K is not reachable |
| B | BusinessDb projection as an untrusted accelerator | Cannot detect suppression without the full V1 scan; no completeness |
| C2 | Separate Authority-administered database, async projector | Better isolation of plaintext from the security journal and independent scaling, but lag, a watermark and marker protocol, and another DR plane. **Future split**, if measured AuthorityDb load requires it |
| D | Authority SQL evaluation of desired edges instead of SpiceDB lookups | Removes the lookup ceiling, but creates a second evaluator of the permission model alongside SpiceDB (S1 protections, schema drift). Separate decision |
| E | Azure AI Search, OpenSearch or a separate Search Service | Another trust plane and residency review; ACL trimming and completeness would need proofs this design gets from PostgreSQL in the trusted store. Not justified at the PB-09 envelope (§F.1). Prohibited without owner approval |
| F | Keyed blind indexes for Confidential values | Excluded by ADR-PB-004 §7; separate ADR |
| G | Cache of verified heads to skip re-verification (DR-C7-2) | Contrary to §4L.5 ("never cache `RowSignatureValid=true`"). V2 achieves the effect structurally, by deriving at commit, instead of by caching |
| H | Caching lookup results per (user, authorization revision) | Possible later optimization; it bypasses the per-decision S1 verify. Separate approval |

## I. ADR-PB-004 §7 review items (the ADR this design needs)

| Item | Answer in this design |
|---|---|
| Inference or dictionary attacks on low-cardinality values | Plaintext Public/Internal only, already visible to a business DBA; no keyed or deterministic tokens; to users: authorized counts only, no facets or suggestions |
| Key isolation, lifecycle, rekey | No index keys (no blind index). Integrity keys unchanged; backfill uses existing verification |
| Consent | AC-6 approval by `administer_security` with a governance reference; PB-08 step-up still required in staging and production |
| Field-level query authorization | `query_fields`, plus the Authority re-check per condition, plus item-level readability (§E.3) |
| Revocation | Shares: no index change; snapshot invalidation. Approvals: same-transaction purge |
| Indexed historical versions | None: only the current accepted revision |
| Proof of index completeness | Inline derivation with the head CAS; query-time guard over the scope; Authority-attested coverage; sweeps |
| Leakage budget | R-IX-1 to R-IX-5 |
| Migration strategy | §G |

**Recommendation:** record the accepted outcome as **ADR-PB-004 Amendment A2 (V2 trusted index)**. A new ADR number would also work, but A2 keeps the search decisions in one place.

---

## J. Decisions requested

| # | Decision | Recommendation |
|---|---|---|
| **IX-D1** | Placement: AuthorityDb schema `authority_search`, inline derivation (C-AUTH-INLINE) | Approve |
| **IX-D2** | Accept that AuthorityDb holds approved Public/Internal plaintext (R-IX-2), and update the PB-10 register | Approve |
| **IX-D3** | Semantics: search answers from Authority-accepted content; business-byte tampering is detected at open and by sweeps, not by search (R-IX-4); restate the expected outcomes of ENC-SR-08/09 for the indexed engine | Approve, with the amended test text in the ADR |
| **IX-D4** | Permission strategy of §E.3 (two lookups at the pin, value-independent bulk `field_item` checks with O-1 hardening, caller-only limits) | Approve |
| **IX-D5** | Authority-held result snapshots (10,000 cap) and `form_content_revision` paging invalidation | Approve; measure the invalidation rate |
| **IX-D6** | Coverage lifecycle and the `search-index-builder` workload identity (bytes only, Authority attestation) | Approve |
| **IX-D7** | A derivation error aborts the commit; an inconsistency quarantines **the search projection only**, cleared by a journaled rebuild | Approve |
| **IX-D8** | Fallback: V1 only within V1 limits and declared in the response; otherwise `SEARCH_INDEX_NOT_READY`; never mixed | Approve |
| **IX-D9** | ADR vehicle: ADR-PB-004 Amendment A2 | Approve |
| **IX-D10** | O-1 and O-2 disposition (§A.3): O-1 client hardening with IX-2; O-2 reused for the builder; the O-1 restructuring optional | Approve |
| **IX-D11** | Unchanged exclusions: no suggestions, autocomplete, facets, field-value sort, repeating groups, children or documents in V2 | Confirm |
| **IX-D12** | My Records on the index (§E.6) as a separate later stage | Defer the decision to after IX-3 |

## K. Acceptance criteria

The indexed engine may replace V1 for a deployment only when all of the following hold:

1. **Tests:** IX-01 to IX-32 pass on a fresh security-test stack (real Authority, PostgreSQL × 2, SpiceDB, Vault). So do the existing ENC-SR-01 to 18 (08/09 per IX-D3), AC5, AC6, C7-SEC, S1 RB-01 to 15 and CT-1 to 6, and CT-7.
2. **Differential:** shadow mode shows zero unexplained differences.
3. **Derivation:** golden vectors pass; derivation is deterministic across restarts and locales.
4. **Purge:** withdrawal leaves zero postings in the same transaction, verified by query.
5. **Coverage:** `Complete` is reachable only through Authority attestation. A withheld record keeps it `Building`.
6. **Guard:** every injected inconsistency (missing entry, stale entry, digest mismatch, revision drift) yields fail-closed, projection quarantine and a Critical alert, with no result released.
7. **Logging:** no predicate value appears in Authority, PostgreSQL, pgAudit or telemetry logs (log scan in IX-28).
8. **Privileges:** the privilege inventory passes. The reader role cannot write, the business roles cannot connect, and RLS is forced.
9. **Performance:** the benchmarks of §F.2 are recorded. PB-09 targets are met in **Azure staging** (PERF-01), or the failures are recorded as blockers.
10. **Review:** independent security review of the design and the implementation, and owner approval.

**Test catalogue** (to be detailed at IX-1; names indicative):
- IX-01, 02: organization isolation (forged organization, RLS through the reader role, pooled connection);
- IX-03 to 05: confidential and encrypted exclusion, including a forged business classification and an encrypted leaf on an approved field;
- IX-06, 07: withdrawal purge, and a strengthening race with a concurrent search;
- IX-08 to 12: non-inference (counts, the limit refusal independent of unauthorized matches, partial reader, discover-only, unknown form);
- IX-13: timing measurement report;
- IX-14, 15: a business DBA cannot reach the index; business-supplied postings are impossible;
- IX-16, 17: Authority-side tamper and partial restore detected;
- IX-18, 19: replay of an old operation; AuthorityDb restore followed by journal replay → `Rebuilding`;
- IX-20, 21: revocation mid-search and between pages;
- IX-22: a commit during a search, and paging invalidation;
- IX-23, 24: organization and record quarantine;
- IX-25, 26: builder byte forgery refused; withholding keeps the coverage `Building`;
- IX-27: HSM or builder outage;
- IX-28: log scan;
- IX-29: limits and timeouts;
- IX-30: forged field, operator or approval id at the Authority;
- IX-31, 32: ENC-SR-08/09 under IX-D3 (match returned; open fails and quarantines; the sweep detects).

## L. Implementation stages (each needs its own authorization)

| Stage | Content | Gate |
|---|---|---|
| **IX-0** | ADR-PB-004 A2 drafted from this design; owner decisions IX-D1 to IX-D12; independent design review | Owner approval |
| **IX-1** | Authority schema and migrations; `SearchPostingDerivation/v1` with vectors; inline derivation **dark** (written, never queried); coverage lifecycle; purge on withdrawal; cheap sweep; privileges inventory; commit-latency benchmark | IX-03 to 07, 14 to 17 and 28; commit p95 delta |
| **IX-2** | O-1 client hardening; `search-index-builder` with set-based reads (O-2); Authority attestation; deep sweep; rebuild command | IX-18, 19 and 25 to 27; backfill throughput |
| **IX-3** | Authority `indexed-query` and `page`; business `Shadow` engine; differential evidence | Zero-difference exit criterion; IX-08 to 13 and 20 to 24 |
| **IX-4** | `Indexed` engine for Advanced Field Search in Development/Testing; fallback rules; UI scope-statement wording; Stage B shards; local benchmarks 50 to 100K | All IX tests; owner functional acceptance |
| **IX-5** | (Optional, IX-D12) My Records on verified entries | Separate approval |
| **IX-6** | Azure staging PERF-01 with Managed HSM; DR rehearsal (rebuild after restore); independent implementation review | Production gate (PB-12 evidence) |

**Unchanged by this design:**
- M4-1 stays open pending independent review;
- the P-S1-1 benchmark remains an open, known-failing performance test until V1 is fixed (O-1/O-2) or retired.

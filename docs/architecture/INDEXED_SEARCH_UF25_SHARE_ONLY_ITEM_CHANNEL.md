# UF-25: Tier 1 share-only item check set and unauthorized content (C-140 investigation, C-141 correction)

- **Status:**
  * **C-141 (2026-11-09):** the owner confirmed UF-25 as a Tier 1 security defect and authorized correction U-1b. It is **implemented** (§8): Development and Testing only, focused tests only, **not independently reviewed**, not pushed or merged.
  * **C-140:** investigation, reproduction and proposed correction. §1 to §7 below are the C-140 record, unchanged except where marked.
  * Prepared by Claude (Opus). Not an independent assessment; no independent review has occurred.
- **Code baseline:** `22732d8`. The finding concerns `IndexedSearchService.SearchAsync` (lines 114–132) and its page path (lines 331–334), and `SearchQueryReader.ReadPredicateItemsAsync`.
- **Tests:** `tests/integration/EnterpriseForms.StageBTests/Tests/IndexedSearchShareOnlyItemChannelTests.cs`. This is a focused development class (C-131), not a Stage B run. Results are in §3.

## 1. Mechanism

For a caller's share-only records S (discoverable but not owned, restricted to the form), Tier 1 computes the readable-for-predicate scope (R1 §E.3):
1. `items` = **every** posting `manifest_item_id` of each predicate field, over all records in S (`ReadPredicateItemsAsync`). This includes items the caller cannot read.
2. If `|items| > MaximumShareOnlyItems` (default 20,000), the search is refused: `SEARCH_VERIFICATION_LIMIT`, detail `items`. The business API passes the detail through to the user (`FieldSearchIndexedEngine`).
3. Bulk `field_item#read` checks on all `items`, in chunks of 1,000.
4. A record stays in scope only if every one of its predicate-field items is readable.

**Consequences:**
- The size of `items`, and so the SpiceDB check count, the posting rows read and the limit decision, depends on the **presence and number of values in fields the caller cannot read**. Two examples: an unshared single-value field contributes one item if it holds a value and none if it is empty; an unshared multi-select contributes one item per selected option.
- The predicate **values** do not influence it, as R1 §E.3 states. The R1 statement that every limit is on "caller-owned quantities" is **imprecise** for these items.

## 2. Questions (C-140) and answers

| Question | Answer | Evidence |
|---|---|---|
| Do unauthorized field-item counts influence permission-check counts? | **Yes, by construction.** The check set is `items` (step 1). The test shows `items` growing from 1 to 4 while the caller's readable items stay at 1 | Code; test 1 (`status_items_in_reader_scope_before/after`) |
| Does unauthorized content affect response timing? | **The work grows with unauthorized items** (SpiceDB checks, posting rows). Measured latency: §3.2 | Test 2 (informational) |
| Does the item limit expose information through refusal behaviour? | **Yes.** The same caller, with unchanged authorization and the same query, moves from 200 to 422 `SEARCH_VERIFICATION_LIMIT` (`items`) solely because the owner wrote values the caller cannot read | Test 1, with a limit of 2 set for the test only |
| Is the effect observable by an unauthorized caller? | **Observable by a caller who can discover the record but is not authorized to read the field:** a share recipient whose share does not cover the predicate field. A caller who cannot discover the record is unaffected (records outside D never enter S; IX-08, IX-09). No cross-tenant path | Test 1; IX-08/IX-09 |
| Across direct sharing, team sharing and overlapping paths? | **Identical in all three.** S and the checks are computed for the user, with the union over paths | Test 1, three cases |
| Does a proposed fix change results or authorization semantics? | **The proposed correction (§5) does not:** the evaluable scope is provably the same set (§5.3) and is tested by differential (§6). A separate, **result-level** effect of the same rule (§4.3) can only be removed by changing search semantics, which is an owner decision | §4.3, §5, §6 |

## 3. Reproduction

### 3.1 Deterministic: `UF25UnauthorizedItemsChangeTheItemsRefusalButNotTheResults`

- **Setup.**
  * Run id `20261109-054439-3d97fe06` (first version of the test) and the run in §3.2.
  * The Authority container was recreated with `SearchIndex__Query__MaximumShareOnlyItems=2`, through the new security-test compose variable `IX_SHARE_ONLY_ITEM_LIMIT`; the default of 20,000 is unchanged. It was restored afterwards (verified: `20000`).
- **For each of direct, team and overlapping (direct + team) sharing:**
  1. **Records:**
     - one control record with `case_status = open`, shared to the Reader on `exhibit_count`, `case_status` and `topic_tags`;
     - three records shared on `exhibit_count` only, with no status;
     - one record shared on `exhibit_count` and `topic_tags` (`t01`).
  2. **Before:** the Reader's search `case_status is open` returns the control record (200). The check set holds 1 item. `topic_tags includes t01` returns the control and the partial record.
  3. The owner writes `case_status = open` into the three share-only records. The Reader's authorized projections still contain no `case_status` value (asserted).
  4. **After:** the identical search by the same caller returns **422 `SEARCH_VERIFICATION_LIMIT` (`items`)**. The check set holds 4 items, still with 1 readable.
  5. **Default limit restored:** the search returns the control record only. **Results unchanged by the unauthorized values.**
  6. The owner adds the selection `t02` to the partially shared record (new items are never shared automatically, CLAUDE.md §4H). The Reader's `topic_tags includes t01` now returns **the control record only**. The partial record dropped out although its readable selection `t01` is unchanged (§4.3).
- **Evidence:** `artifacts/ix-5/uf25-reproduction.json` (not committed; `artifacts/` is ignored).

### 3.2 Informational: `UF25UnauthorizedItemCountChangesTheShareOnlyCheckWork` (Benchmark trait)

- 25 share-only records, shared on `exhibit_count` only, and one control record readable on `topic_tags`.
- The Reader's `topic_tags includes t01` is run 21 times with no unreadable selections, and 21 times after the owner writes 40 selections into each share-only record (1,000 unreadable items).
- Results identical.
- Latency: **see §3.3** (filled from `artifacts/ix-5/uf25-work.json`).
- **Not a gate; not repeated.** Machine on battery power at the time of the run.

### 3.3 Results

| Run | Test | Outcome |
|---|---|---|
| `20261109-054439-3d97fe06` | 3.1 (first version, without the step-2 `topic_tags` assertion) | Passed (1 test, 87 s) |
| `20261109-054736-a619042e` | 3.1 (final) and 3.2 | **Passed (2 tests, 182 s)** |

**3.1, all three paths identical:**
- check set 1 → 4 items, readable items 1;
- before: 200, one result; after: **422 `SEARCH_VERIFICATION_LIMIT` (`items`)**;
- default limit: results unchanged (control only);
- `topic_tags includes t01`: control + partial before the unshared selection is added, control only after it.

**3.2, informational** (client-measured Authority round trips; single run; **on battery power**):

| | Check set | p50 | p95 | min | max |
|---|---|---|---|---|---|
| Before | 1 item | 119.6 ms | 227.8 ms | 100.2 ms | 371.7 ms |
| After (1,000 unreadable items) | 1,001 items | **162.1 ms** | 252.4 ms | 132.5 ms | 467.5 ms |

- About **+42 ms at p50 (≈ +36 %)** and +32 ms at the minimum for 1,000 unauthorized items. That is roughly 30–40 µs per unauthorized item on this stack.
- Results identical in both phases.
- The effect is measurable, but small next to network variance for a few items. It grows linearly with the unauthorized item count.

## 4. Security impact assessment

### 4.1 What leaks

Metadata about fields the caller can discover but not read, **aggregated over all the caller's share-only records of the form**: whether values are present, and how many items, for example how many options are selected in a multi-select. Never the values themselves, and never the predicate's match.

### 4.2 Channels

| Channel | Precision | Preconditions | Assessment |
|---|---|---|---|
| `items` limit refusal | Binary per search: is the aggregate above the limit? | The aggregate must cross 20,000 items (default). The caller cannot narrow S to one record (S is D ∩ form; metadata filters apply after the scope) | Deterministic but coarse. Practical only for callers with very large share-only scopes. Can be watched over time (refusal flips as the owner edits) |
| Work and timing | Proportional to the unauthorized item count; noisy | Many unauthorized items in S | Measured in §3.2; small per item, network-noisy |
| **Result exclusion** (§4.3) | **Per record** | A field partly shared at item level (arises when the owner adds selections after sharing) | The most precise channel, and it **is the specified semantics** (R1 §E.3; C7 parity) |

### 4.3 Result-level effect of the parity rule (not removed by §5)

- **The rule.** A share-only record is evaluated on field f only if every item of f is readable, so an added unshared selection removes the record from the caller's results.
- **What the caller learns.** That the record has acquired an item of f they cannot read. The value stays hidden.
- **Why the rule is there.** It keeps answers correct: `is` means exactly one selection, so evaluating only the readable items would report `is t01` true for a record whose actual selections are `t01` and `t02`.
- **Options (owner decision; outside the C-140 correction):**
  * keep the rule (current; correct answers; this residual);
  * evaluate on readable items only for monotone operators (`includes`) and exclude `is` on partially readable fields. That changes results and V1 parity, so it would need its own specification and tests.

### 4.4 Severity (design author's reading; for the owner and reviewer)

- **Confidentiality:** Low to Medium. Presence and cardinality metadata of unshared fields, to an authenticated recipient who can already discover the record. No values, no cross-tenant or undiscoverable-record exposure, no integrity or availability impact. The result-level effect (§4.3) is the most precise channel, but it is part of the specified search semantics.
- **Not affected:** owners (`O`; no checks); callers without discover; Tier 1 integrity, seals, completeness and fail-closed behaviour.
- **V1 (verified scan):** not assessed by these tests. Its limits are on records and verified bytes of authorized projections. To be confirmed by the reviewer.

## 5. Proposed correction (for owner review; not implemented)

### 5.1 Goal

No item the caller cannot read may influence:
- (a) the SpiceDB check count;
- (b) any limit decision;
- (c) the database work beyond a constant per (record, field);
- (d) the results.

Results stay exactly as today, including §4.3, unless the owner separately changes the semantics.

### 5.2 Design U-1

1. **Readable candidates from the caller's own grants, not from postings.**
   - In the existing reader transaction, which already requires `authorization_revision` = the pinned marker revision, read from `authority_relationship_edges` the `field_item#shared_via` edges of shares on records in S whose role subject is in `P` = {caller} ∪ {caller's teams}.
   - The search reader already has SELECT on `authority_relationship_edges` (`SearchReaderAuthorityTables`, design §5.1), so no new privilege is needed.
   - Keep the edges of predicate fields only, joined to the record's current posting items **by item id** (exact key; only the caller's shared items are touched).
   - The work is O(the caller's shared items), an authorized quantity.
2. **Confirm with SpiceDB** at the pin: bulk `field_item#read` on exactly those candidate items. SpiceDB remains the decider. A candidate SpiceDB denies is excluded, and an inconsistency with S1 consistent is raised as for any divergence (Q4).
3. **Item count per (record, field) in O(1).**
   - **U-1a (interim):** an SQL count per (record, field) over the postings. It removes channels (a) and (b), but database work still grows with unauthorized items (µs scale, channel (c) residual).
   - **U-1b (complete):** a derived, sealed `field_item_count` per (entry, field), written with the postings in W1. It needs a Tier 1 derivation-version change (a new derivation version; rebuild; S-B/S-C coverage) and is a Tier 1 schema change, so it needs owner approval under FD-5 and IX-4's accepted state.
4. **Evaluable:** a record is in scope for f iff (distinct confirmed-readable items of f in r) = `field_item_count(r, f)` > 0. This is the same distinct-items rule as B2's INV-T2-13 (multi-path union).
5. **The `items` limit applies to the confirmed-readable candidate items** (caller-owned).
6. **The same on the page path.**

### 5.3 Why results do not change

For a share-only record (r ∉ O), `field_item#read(i) = shared_via → read` (zed; `effective_owner` cannot apply, since r ∉ O).

The candidate set in step 1 is exactly the items with a `shared_via` edge to a share whose role subject is in `P`. When S1 is consistent at the pin, canonical edges and SpiceDB are equivalent (CROSS_SYSTEM_CONSISTENCY_PROTOCOL), so:
- the confirmed set equals {i ∈ postings(r, f) : SpiceDB grants i};
- "every item readable" equals "confirmed count = item count".

The scope is the same set, so the results are the same.

**Failure behaviour:**
- an edge read or SpiceDB error, or a limit, refuses the search (typed, no partial results);
- a candidate denied by SpiceDB with S1 consistent is a projection divergence. It is escalated through S1 (Q4), and the search fails.

### 5.4 Alternatives considered

| Alternative | Why not |
|---|---|
| Pad the check set to a fixed size | No safe fixed size; still leaks above it; wasteful |
| SpiceDB `LookupResources(field_item, read)` | Includes every item of the caller's **owned** records: unbounded and unrelated |
| Drop the limit | Removes channel (b) only; work still depends on unauthorized items; loses a safety bound |
| Evaluate on readable items only | A semantics change (§4.3); does not by itself bound the check set |

## 6. Regression requirements for the correction (to be reviewed with the design)

| # | Requirement |
|---|---|
| RQ-1 | **Differential:** old and new readable-for-predicate scopes are identical over generated configurations, at unit level and on the stack. Configurations: direct, team and overlapping shares; several shares per record; owned plus shared records; single, multi-select and partially shared fields; removed and added items; children (the parent's shares never reach child fields) |
| RQ-2 | **Invariance (the inverse of test 1):** with a small limit, adding, removing or changing unauthorized items never changes success or refusal. A counting SpiceDB double shows the check count is invariant under unauthorized content |
| RQ-3 | The existing IX-08 to IX-11, IX-36, the shadow matrix (0 unexplained differences) and the page tests pass unchanged |
| RQ-4 | **Fail closed:** edge-read error, SpiceDB unavailability, pin mismatch, limit, or a SpiceDB-denied candidate ⇒ typed refusal, no partial result. A denied candidate with S1 consistent escalates (Q4) |
| RQ-5 | The `items` limit counts confirmed-readable items only. Documentation and the R1 §E.3 text are amended |
| RQ-6 | U-1b only: derivation-version and seal tests, rebuild, S-B/S-C coverage of the count, PD-9 plan-shape (the count is record-keyed, not value-keyed) |
| RQ-7 | The informational timing measurement (§3.2) is repeated on AC power after the change |
| RQ-8 | §4.3 (result-level) behaviour is unchanged and asserted, unless the owner changes semantics |

## 7. Summary

- **Reproduced:** unauthorized content (presence and number of items in fields the caller cannot read) changes:
  * the SpiceDB check set (1 → 4; 1 → 1,001);
  * the `items` refusal (200 → 422);
  * measured latency (+42 ms p50 for 1,000 items, informational).
- **Not changed:** search results. The same holds for direct, team and overlapping paths, and callers who cannot discover the record are unaffected.
- **Impact:** Low to Medium confidentiality (presence and cardinality metadata of unshared fields, aggregated over the caller's share-only scope).
- **Proposed correction:** U-1 (§5), with regression requirements RQ-1 to RQ-8 (§6). **Awaiting owner review; not implemented.**
- **Result-level residual (§4.3):** specified semantics; an owner decision if it is to change.
- **Test-only change:** `infrastructure/docker/compose.security-test.yaml` passes `SearchIndex__Query__MaximumShareOnlyItems` from `IX_SHARE_ONLY_ITEM_LIMIT` (default 20,000, the code default). The new class is registered in the `search-execution` group of `stage-b-groups.json`. No product code changed.

## 8. C-141: implementation of U-1b

**Authorization.**
- Owner, C-141: correct UF-25 with U-1b as the permanent design, keeping the approved search semantics, including partially shared multi-selects.
- No Tier 2.
- U-1a must not be substituted. Stop if U-1b needs an architectural change outside the approved Tier 1 security invariants.

### 8.1 Pre-implementation review of the stored count

| Question | Assessment |
|---|---|
| **Integrity protection** | The count is not added to the seal message. It is a pure function of the sealed posting list: the number of the entry's postings carrying each derived approval. Every check that verifies the seal recounts the verified postings: S-B, every served page record (`SearchEntryVerification.Verify`, `ENTRY_FIELD_ITEM_COUNT_MISMATCH`), and S-C's re-derivation from Authority-verified bytes (`CompareWithStored`, `FIELD_ITEM_COUNT_MISMATCH`). The count is therefore protected by the keyed seal transitively, exactly like the existing `indexed_posting_count` (precedent: `ENTRY_COUNT_MISMATCH`). Sealing the count itself would add nothing at query time: verifying a seal needs every posting, including the hidden ones, which is the UF-25 channel. S-A adds an hourly unkeyed SQL consistency check |
| **Tied to the accepted record and field revision** | Stored on the entry row (`derived_field_item_counts`), positionally aligned with `derived_field_search_approval_ids`. The entry is bound to the head's committed operation (I-1 `SCOPE_ENTRY_NOT_AT_HEAD`, I-2 `MATCH_ENTRY_NOT_AT_HEAD`); the approval id is the field's searchability revision. A count can be stale only if its entry is, and a stale entry is already a finding |
| **Transactional maintenance** | Written by W1 and builder backfill in the same `SearchRecordEntry` row as the entry, from the same derived postings, in the content-commit transaction. Removed with the entry by every existing delete path. Never updated: the runtime has no UPDATE grant on the column. No new table, no new delete path, no change to the writer's transaction structure |
| **Backfill** | No count is written from unverified data. Existing entries (derivation version 2) keep NULL. The S-A **derivation upgrade** returns a Complete coverage whose form still has version 2 entries to Building, journaled (`SearchIndexDerivationUpgradeStarted`) and alerted (`SEARCH_INDEX_DERIVATION_UPGRADE`, Medium). The builder then re-derives those entries from bytes the Authority verifies (version 3), and only a fresh attestation re-establishes Complete |
| **Compatibility with sealed data** | Seal format, seal key and signatures unchanged; existing entries stay valid. A version 2 entry is still bound (I-2) and sealed. Only the share-only step needs counts, and it refuses version 2 as **not ready** (503, retryable), never answering without them. Owned-only searches are unaffected during the upgrade. The constraint `ck_search_entry_field_item_counts` requires NULL below version 3 and well-formed counts from version 3 |
| **Rollback** | Forward-only migration; `Down` refuses. An image before C-141 ignores the column, writes version 2 entries with NULL counts and treats version 3 as current. Returning to this image re-derives those entries through the same upgrade |
| **Outside the approved Tier 1 invariants?** | **No.** No seal-format change, no new privilege (the reader already has SELECT on `authority_relationship_edges`), no new value-keyed index (PD-9: the record probe index gains a trailing item id), no change to transaction structure, fail-closed preserved. The derivation-version step follows the IX-3 precedent (v1 → v2). Implementation therefore proceeded |

### 8.2 Changes

- **Count.** `SearchRecordEntry.DerivedFieldItemCounts`, derivation version 3, written by `SearchIndexWriter`. The rule is in `SearchFieldItemCounts` and is used by the writer, S-B and page verification, S-C, S-A and the query path.
- **Readability rule.** `ShareOnlyReadability`, a pure class:
  * `ValidateCounts` refuses missing, malformed or contradictory metadata before anything is read, and a version 2 entry as not ready;
  * `Decide` compares distinct confirmed items with the stored count per (record, field), over the union of paths;
  * `VerifyBoundRecord` re-checks each share-only match on its bound data.
- **Candidates.** `ISearchQuerySession.ReadShareReadableItemsAsync` replaces `ReadPredicateItemsAsync` (removed).
  * Edges are read through `ix_authority_edges_subject`, always starting from the caller: memberships, the caller's shares, those shares' records in S, the shared items.
  * Postings are probed per candidate item on the record index `(organization_id, form_record_id, field_definition_id, manifest_item_id)`.
  * The relation literals are pinned to `authority.schema.zed` by architecture tests (`ShareOnlyReadabilitySchemaTests`).
- **Service.** `IndexedSearchService.EvaluateShareOnlyAsync` is used by the search and by every page.
  * The `items` limit applies to the candidates.
  * A SpiceDB denial of a candidate is escalated by Q4 (FD-4): S1 verification of the same pin first, then a search-index quarantine `SHARE_CANDIDATE_DENIED_BY_PROJECTION`.
  * I-1 still quarantines version 1 entries.
- **S-A.** Adds the derivation upgrade and the count-consistency check `ENTRY_FIELD_ITEM_COUNT_MISMATCH`.
- **Migration** `AuthoritySearchIndexUf25FieldItemCounts`: the column, two constraints (`ck_search_entry_derivation` now allows version 3), and the seven record probe indexes rebuilt with the trailing item id.
- **Testing only.** `IIndexedSearchProbe` / `IndexedSearchTestingProbe` and the endpoint `GET /internal/v1/testing/search/share-only-probe/{correlationId}` report counts and an outcome code per share-only step. They are registered only in the Testing environment and never published in OpenAPI.
- **Architecture test.** `ProjectionIntegrityBoundaryTests.EveryDecisionPathPinsAndCompletes` now ignores exactly the Q4 completion (`verifier.CompleteAsync(pin, path + "-q4", …)`), a second completion of the same pin on a failure path whose decision is never used.
- **R1 §E.3** carries a marked C-141 amendment (RQ-5).

### 8.3 RQ-1 to RQ-8 results

The runs below were on the security-test stack (local Docker, synthetic data). They tested the working tree on top of `67ecad8`, which is recorded as the artifacts' `git_commit`; the remediation commit is listed in the decision log (C-141). Focused classes only (C-131); Stage B was not run.

| # | Requirement | Evidence | Result |
|---|---|---|---|
| RQ-1 | Old and new scopes identical | Unit `RQ1TheNewRuleDecidesExactlyTheR1Scope`: 600 generated organizations (direct, team, overlapping and stranger shares; several shares per record; owned and shared records; 1–4 fields with 0–3 items; partly shared fields; shared non-predicate items), each with up to 3 predicate choices. The scope equals R1 §E.3 on every one. Stack: the three paths in `UF25HiddenItems…`, revocation and membership in `RevocationAndTeamMembership…`, with results as R1 predicts | **Pass** |
| RQ-2 | Hidden content changes neither checks nor refusal | Unit `RQ2HiddenContentNeverChangesTheCheckCountOrTheLimitDecision`: 400 configurations with hidden items added (unshared, or shared only with another user). Check count and the limit decision are equal for every limit tried, and untouched records are decided identically. The R1 rule's check count moved in the generated cases. Stack, limit 2: for direct, team and overlapping paths the probe shows **1 check before and 1 after** while the index holds 1 → 4 status items, and the search is **200 before and 200 after** (C-140: 200 → 422). The timing test shows 1 → 1 checks with 1 → 1,001 posting items | **Pass** |
| RQ-3 | Existing tests pass unchanged | Unit 461/461; architecture 97/97 (one fitness test narrowed for Q4, §8.2); integrity 60/60; API contract 575/575; stack classes listed in §8.4 | **Pass, except one intermittent existing test (§8.4, R-51)** |
| RQ-4 | Fail closed | Unit `MissingStaleOrContradictoryCountMetadata…`, `TheDecisionCounts…`, `ABoundShareOnlyRecord…`, `AChangedItemCount…`. Stack: a **lowered** count on a partly readable record gives 503 `SEARCH_INDEX_INCONSISTENT` before release (index quarantined, organization not); a **raised** count is quarantined by S-A within its interval (`ENTRY_FIELD_ITEM_COUNT_MISMATCH`); a version 2 entry gives 503 `SEARCH_INDEX_NOT_READY` and is re-derived; a **SpiceDB denial** of a granted candidate is escalated by Q4 (S1 consistent) to `SHARE_CANDIDATE_DENIED_BY_PROJECTION`, nothing released, organization not quarantined | **Pass** |
| RQ-5 | Limit counts readable items only; text amended | Stack: with limit 2, `exhibit_count equals 1` (readable on five records) is refused, with **3 candidates read** (limit + 1) and **0 checks**; the status search with 4 indexed items (1 readable) is not refused. R1 §E.3 carries the C-141 amendment | **Pass** |
| RQ-6 | U-1b: derivation version, seal coverage, rebuild, S-B/S-C, PD-9 | Unit: version 3 written with counts; S-B/page `ENTRY_FIELD_ITEM_COUNT_MISMATCH`; S-C `FIELD_ITEM_COUNT_MISMATCH`; S-A finding and upgrade. Stack: upgrade end to end (journal `SearchIndexDerivationUpgradeStarted`, entry re-derived to version 3, same result); on the existing stack data, 1,620 of 1,646 entries were re-derived automatically after deployment (§8.6 R5). PD-9 plan-shape test in §8.4 | **Pass** |
| RQ-7 | Timing on AC power | §8.5 | **Measured** (informational) |
| RQ-8 | Result-level semantics unchanged | Stack, all three paths: the partly shared record is returned for `topic_tags includes t01` before an unshared selection is added and excluded after; checks 2 (the two readable t01 items, never the hidden t02). Unit RQ-1 includes partly shared fields | **Pass** |

Runs:
- unit `c141-unit-20261109-191737` (461);
- architecture `c141-arch-20261109-192932` (97);
- integrity and contract `c141-EnterpriseForms.Integrity.Tests-223349`, `c141-EnterpriseForms.Api.ContractTests-223357`;
- stack `20261109-222430-39ae5268` (5/5, `IndexedSearchShareOnlyItemChannelTests` without the Benchmark trait);
- timing `c141-uf25-after-*`.

The first stack run, `20261109-192842-d7c0bf70`, had 3 of 5 passing:
- one wrong test expectation: the reader stops at limit + 1 candidates;
- one failure caused by the **host going to sleep** for 2 h 47 min. Windows Kernel-Power 42 / Power-Troubleshooter 1 record sleep from 00:32:00Z to 03:19:36Z. On resume, an expired delegated assertion was rejected (401). The business API's audit insert then failed `ck_audit_events_outcome` and surfaced 503 `STORE_UNAVAILABLE`, a separate, existing business-API defect noted for the owner (§8.6 R9).


### 8.4 Existing Tier 1 stack classes (RQ-3, result equivalence)

Focused `-Class` runs, Benchmark trait excluded, on the security-test stack after the C-141 rebuild (not Stage B):

| Class | Run | Result |
|---|---|---|
| `IndexedFieldSearchEngineStackTests` (shadow and engine equivalence) | `20261109-225042-8a33ee12` | 5/5 |
| `SearchIndexFoundationTests` | `20261109-225258-d14b9f23` | 9/9 |
| `SearchIndexBuilderStackTests` | `20261109-230009-1780ed50` | 11/11 |
| `FieldSearchExecutionTests` | `20261109-231010-b870df6c` | 13/13 |
| `IndexedSearchQueryStackTests` (IX-08 to IX-11, IX-36, pages, PD-9 plan shape) | `20261109-223335-b41091ff`, retry `20261109-231518-c780ecea` | **20/21 both times** |

**The one failure is `IX79IX80SessionEndAndSessionTimeoutsRefuseThePage`, line 650.** That assertion expects a result snapshot to survive right after the test makes its session idle by SQL. The snapshot purge job, every 5 s on this stack, deletes the snapshots of idle sessions, and its log shows a deletion inside the failing window (`IX3-SNAPSHOT-PURGE deleted=1` at 04:37:13Z).
- Run alone, the test passed once and failed once (`c141-ix79-1-234242` passed, `c141-ix79-2-234452` failed). It is therefore intermittent.
- It passed in seven runs on 2026-11-08.
- C-141 changes neither the purge, sessions nor snapshots.
- The design author's reading is a pre-existing race between the assertion and the purge, made visible by timing, but the rate change is **not fully explained**. It is recorded as R-51 and **not counted as passing**.

### 8.5 Before-and-after measurements (AC power; informational)

**Share-only item checks and latency.** `UF25UnauthorizedItemCountChangesTheShareOnlyCheckWork`: 25 share-only records, 1 readable selection, 1,000 hidden selections added. 21 searches per phase, client-measured Authority round trips, one run per configuration. AC power was recorded in each artifact.

| Code | Phase | Posting items in scope | Item checks | p50 | p95 | min | max |
|---|---|---|---|---|---|---|---|
| Before (`67ecad8`, old rule) | before hidden items | 1 | 1 | 123.8 ms | 254.4 ms | 84.8 ms | 635.9 ms |
| Before (`67ecad8`, old rule) | after 1,000 hidden items | 1,001 | **1,001** | 163.0 ms | 335.4 ms | 128.9 ms | 1,222.7 ms |
| After (C-141) | before hidden items | 1 | 1 | 118.0 ms | 154.6 ms | 85.4 ms | 1,299.8 ms |
| After (C-141) | after 1,000 hidden items | 1,001 | **1** | 111.6 ms | 461.6 ms | 82.0 ms | 1,383.6 ms |

- Old rule: the hidden items raised the check count 1 → 1,001 and p50 by about +39 ms (minimum +44 ms).
- C-141: the check count is unchanged (1 → 1). p50 and minimum did not grow (118.0 → 111.6 ms, 85.4 → 82.0 ms).
- The C-141 p95 (154.6 → 461.6 ms) and both maxima are driven by a few outliers in 21 samples on a single-node laptop stack. They do not show a systematic dependence, and the sample cannot rule one out at small magnitudes.
- **No constant-time claim is made.** Residual database work still differs by a constant per (record, field) and by PostgreSQL cache state (§8.6 R6).

**Commit overhead of the stored counts.** `SearchIndexFoundationTests.MeasureCommitLatencyWithAndWithoutTheIndex`, before and after, on the same stack state and AC power. 35 samples per form type, first 5 excluded.

| | Indexed p50 | Indexed p95 | Plain p50 | Plain p95 | Δ p95 | DB commands (indexed) | Derive stage p50 / p95 | Writes stage p50 / p95 |
|---|---|---|---|---|---|---|---|---|
| Before | 40.88 ms | 54.20 ms | 28.07 ms | 38.45 ms | 15.75 ms | 16 | 0.12 / 0.19 ms | 2.34 / 3.33 ms |
| After | 37.86 ms | 51.90 ms | 28.11 ms | 45.52 ms | 6.38 ms | 16 | 0.11 / 0.28 ms | 2.03 / 4.27 ms |

- The counts add no database command (they are a column of the entry row already inserted).
- They add no measurable time at this sample size: the derive stage, which computes the counts, stays near 0.1 ms.
- The extended record index (one more 16-byte key column per posting) shows no measurable write-time change here. Its storage and maintenance cost at 100K/250K records is not measured (R-43).
- **The 20 ms p95 incremental-commit gate (R-02) remains recorded as FAIL** until independently demonstrated otherwise (C-141 §4). These two local samples (Δ p95 15.75 and 6.38 ms) are not that demonstration.

### 8.6 Remaining security risks (design author's reading; not independent)

| # | Risk | Assessment |
|---|---|---|
| R1 | **A raised (inflated) count cannot be detected at query time** | If the count exceeds the readable items, the record looks partly shared and is excluded. Telling this apart from a genuine partial share would require reading the hidden items, which is UF-25 itself. Detected by S-A (hourly deadline in production; 10 s on this stack) and S-B, then the index is quarantined. Proven on the stack. This is the same class as a deleted posting: Tier 1 does not claim query-time completeness against a writer of the search schema (G06 open) |
| R2 | A coordinated rewrite (hidden posting deleted **and** count lowered) | Passes the unkeyed I-2 and the bound-record check. It is caught by the keyed seal on every served page record and by S-B. Until then the search's `total_count` may include the record. This is the existing OR-1 limitation of I-2 and **no regression**: under the old rule, deleting the posting alone sufficed |
| R3 | Result-level parity effect (§4.3) | Unchanged by direction (C-141): a partly shared field still removes the record from results |
| R4 | Version 2 + NULL counts written into a version 3 entry | Re-derived from verified bytes after a journaled, alerted (Medium) upgrade, instead of an I-1 index quarantine. Nothing is released meanwhile (503), so integrity and confidentiality are not weakened, but the alarm is lower than I-1's. A version 2 entry **with** counts is still a finding. For the owner: whether this pattern should quarantine instead (it would make every legitimate upgrade quarantine the index) |
| R5 | Deployment upgrade | Each organization with Complete coverages and version 2 entries returns to Building. Searches answer 503 not ready until the builder re-derives and attests. Before the first S-A pass, only share-only searches over version 2 entries are refused. On this stack, 26 version 2 entries remain, all in fail-closed states where search is refused anyway: 16 in S1-quarantined organizations, 5 in quarantined search indexes, 5 with quarantined heads |
| R6 | Residual timing | The step's work is now driven by the caller's grants, the candidates (items the caller may read) and one stored count per share-only record and field: all caller-authorized or caller-owned. PostgreSQL buffer-cache, planner and visibility effects remain a general residual (UF-22 class). Small samples cannot prove the absence of a small dependence |
| R7 | Schema coupling | The reader's relation literals mirror `authority.schema.zed`; `ShareOnlyReadabilitySchemaTests` fails the build if either side changes |
| R8 | Q4 availability | Any SpiceDB/edge contradiction quarantines the organization's search index (rebuild required), per FD-4. This is a fail-closed availability cost |
| R9 | **Separate existing defect (business API), not caused by C-141** | When an Authority call returns 401 after a delegated assertion expires, the business API's audit insert violates `ck_audit_events_outcome` and the request surfaces as 503 `STORE_UNAVAILABLE`. Observed after host sleep. Not fixed (out of scope) |
| R10 | Migration | The seven record indexes are rebuilt non-concurrently inside the migration, which blocks posting writes while it runs. Production scale needs a maintenance window or a concurrent-build plan; not measured at 100K/250K |
| R11 | Testing probe | Counts and codes only, held in memory, registered only when the environment is `Testing` |
| R12 | Review | Not independently reviewed (TQ-24) |

### 8.7 Summary (C-141)

- **Corrected** in `7157f89`:
  * the share-only check set, the `items` limit and the posting reads no longer depend on items the caller cannot read;
  * the scope, the results and the partially shared field semantics are unchanged;
  * every failure refuses with nothing released.
- **Not claimed:** constant-time behaviour, query-time detection of an inflated count (R1), production readiness, or independent review.
- **Open:** R-46 (until reviewed), R-47 to R-51, and TQ-24 for the reviewer.

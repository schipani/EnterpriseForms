# UF-25: Tier 1 share-only item check set and unauthorized content (C-140 investigation)

- **Status:** investigation, reproduction and **proposed** correction for owner review.
  * **No Tier 1 behaviour was changed.** The correction is **not implemented**: the current behaviour is exactly what the approved design R1 §E.3 and IX-D4 specify, so no existing specification unambiguously authorizes changing it (C-140).
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

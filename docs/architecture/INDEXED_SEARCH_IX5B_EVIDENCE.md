# C-148 evidence: R-53 remediation, R-54 correction, P-5 status and IX-5b

- **Status (2026-11-10):** implemented for Development/Testing under GA-IX5-1 and the approved design acceptance record. Owner-led; **not independently reviewed.** Tier 2 serves no search; IX-5c is not started.
- **Labelling (workflow rule 5):** "code fact" means checked by inspection; "tested" means executed test evidence with a run id.

## 1. R-53: permanent item identity at the Authority's content boundary

**Root cause (code fact).** For record owners, `ContentService.ClassifyMutations` matched a retained item only by its key and payload digest. Nothing bound an existing item id to its field, group occurrence, group definition or form. Nothing stopped a removed id from returning, or an id accepted in another record or organization from being reused. Share-editor writes were already bound (`ContentDeltaRules.ClassifyShareScoped`); owner and protection-migration writes were not.

The consequence: an owner-authorized write could re-key a shared item id onto another field, and SpiceDB, Tier 1 and Tier 2 (all keyed by item id) would then disclose that field to the share. The product's write paths never did this (TQ-6), but nothing at the Authority prevented it.

**Correction (all in the Authority; no application-layer-only change):**

| Rule | Where | What |
|---|---|---|
| Current snapshot on every write | `ContentService.BindAcceptedSnapshotAsync` (prepare, every path) | For a record with accepted content, the complete current snapshot must be supplied and proven equal to the accepted head (D4: coverage, digests, header bindings, root, leaf index). A mismatch is journaled as `CurrentSnapshotMismatch` and refused; a missing snapshot is refused (`CURRENT_SNAPSHOT_REQUIRED`). Previously this applied to share editors only |
| I-1 continuity | `ContentItemIdentityRules.EnforceAtPrepareAsync` | A retained item keeps its identity anchor; content may change. Values and selections: field and group occurrence. Attachments: field (a new version keeps the id). Group entries: group definition. Header: form. Violation: `ITEM_IDENTITY_CHANGED` |
| I-2 no reuse | the same | A new item id must be neither bound nor present in any accepted manifest of any organization. So a removed id cannot return under its own field or another, and an id cannot be copied from another record. Duplicate ids within a candidate are refused too. Violation: `ITEM_IDENTITY_REUSED` |
| I-3 permanence | `ContentItemIdentityRules.BindAtCommitAsync` (commit, on the verified staged bytes) | Every committed item must match its binding in `content_item_identities`; unbound items are bound in the commit transaction. The table's item id is unique across organizations, because SpiceDB item objects are global; this also closes concurrent races. Bindings are insert-only for the runtime (no UPDATE or DELETE grant) |

- **Business change:** `RecordCandidateStager` now sends the accepted current snapshot (the head operation's staged rows) on every write. The Authority treats it as untrusted until bound.
- **Legacy items** accepted before the bindings existed:
  * retained ones are bound at their next commit, after the I-1 check against the verified current snapshot;
  * removed ones are caught by the "accepted before" check.
- **Not repaired:** existing records are not rewritten. No identifier is changed silently. Content signatures, audit and integrity checks are unchanged.
- **Fixtures corrected:**
  * `SearchIndexTests.Rows()` moved a group value to a new occurrence on every write (exactly what I-1 forbids); it now keeps the occurrence;
  * owner-path unit fixtures now send the current snapshot.

**Regression results:**

| Test | Result |
|---|---|
| R-53 unit tests (`ContentItemIdentityTests`, in `ContributorWriteProtocolTests`) | **11/11** |
| Architecture: every prepare and commit goes through the rules; bindings insert-only (`ContentItemIdentityBoundaryTests`) | 2/2 |
| Full Authority unit suite | 509/509 (`artifacts/ix-5/c148-unit-full.txt`) |
| Business contract tests (stager change) | 575/575 |
| Stack regression of the write paths (below) | §5 |

The unit tests cover:
- an existing item updated in its field (accepted);
- moved to another existing field or a new one (refused);
- a removed item reused under another field, and reintroduced under its original field (refused);
- a re-entered value with a new id (accepted);
- multi-select removal and replacement, option change, and moving a selection (refused);
- repeating-group edit and reorder (accepted), a value moved between entries (refused), and a removed entry's ids returning (refused);
- an attachment's new version (accepted) or field move (refused);
- direct and team share editors: edit (accepted), move (refused), owner move of a shared item (refused);
- ownership transfer (the new owner is bound by the same rules);
- adversarial payloads:
  * missing snapshot;
  * forged snapshot (`CURRENT_SNAPSHOT_MISMATCH`);
  * duplicate id across kinds;
  * a snapshot sent for an empty record;
  * an id accepted in another record;
  * a legacy unbound removed item;
  * a commit racing another binding of the same new id (refused at commit).

## 2. R-54: nondeterministic digest test

- **Correction.** `MembershipAdministrationTests.Stale` replaces the first hex digit with a different one ('0' becomes '1', anything else becomes '0') and asserts the result differs. Production cryptography is unchanged.
- **Stability:** 100 consecutive runs of the affected test, 100 passed (`artifacts/ix-5/c148-r54-repeat.txt`).

## 3. P-5: commit-latency measurement (on AC power)

**Run.** The existing `SearchIndexFoundationTests.MeasureCommitLatencyWithAndWithoutTheIndex`, unchanged, three times.
- **Environment:** the host on AC power (`PowerOnline` true, checked before each run); local Docker security-test stack on the rebuilt C-148 images (R-53, IX-5a, IX-5b); Vault Transit dev ES256 seal.
- **What is measured:** the Authority-side `ContentService.CommitAsync` duration from the Testing-only timing log.
- **Samples:** 35 per form per run (sequential creates, the first 5 excluded); an indexed form against an identical plain form. The Tier 2 maintainer runs throughout (FD-5-ALT, outside the commit).
- **Raw output:** `artifacts/ix-5/p5/commit-latency-ac-{1,2,3}.json`.

| Run | Indexed p50 / p95 | Plain p50 / p95 | Delta p50 | Delta p95 | DB commands (indexed / plain) | Test run |
|---|---|---|---|---|---|---|
| 1 | 42.62 / 62.92 | 32.56 / 82.32 | 10.07 | **−19.40** | 17 / 15 | `20261110-224738-e4eab440` |
| 2 | 40.70 / 67.15 | 33.37 / 44.51 | 7.33 | **22.64** | 17 / 15 | `20261110-225125-ae172313` |
| 3 | 40.95 / 53.33 | 32.74 / 49.51 | 8.21 | **3.82** | 17 / 15 | `20261110-225508-44ead046` |

All values are in milliseconds.

- **Baseline comparison.** The established post-remediation baseline (IX-1 evidence §6.4, C-128; measured on battery) had a median delta p50 of about 10.8 ms and 2 added commands, with per-run p95 deltas between −7 and +43 ms. On AC now: delta p50 7.3–10.1 ms and still exactly 2 added commands. **No observable latency regression** from R-53, IX-5a or IX-5b. Tier 2 adds no commit-path work (architecture-tested), and R-53's identity reads apply to both forms equally.
- **Gate.** 20 ms p95 delta: met in runs 1 and 3, missed in run 2 (22.64 ms). The per-run p95 of 35 samples is still unstable (−19.4 to +22.6 ms), and C-128 requires the gate to be met reliably, so **R-02 stays FAIL**. The threshold is unchanged.
- **IX-5a final acceptance:** the P-5 evidence condition now has its measurement; the gate itself remains FAIL, for your decision.

## 4. IX-5b: dark B2 value projection

**Source of truth.** Value rows are copied only from Tier 1 postings of each record's entry at its accepted head, under a Complete coverage of a Public or Internal field (`SearchAccessStore.GetSearchablePostingsAsync`):
- the entry's operation must equal the head's committed operation;
- the coverage state must be `Complete` and its classification `Public` or `Internal`.

Tier 1 postings are derived by the Authority inside the content commit from verified accepted bytes, and sealed. They never contain encrypted or Confidential values, and Tier 1 never derives tokens from ciphertext. Nothing is read from business data or from staged candidates. Content journal events carry no values and are used only to decide which record to re-derive.

**Rows.** `search_access_value_postings` holds one row per (reading principal, posting):
- owners read every item; share principals read the items of their shares;
- a principal reached by several paths gets one row (the distinct-item union);
- teams stay principals and members are never expanded;
- every index leads with (organization, principal type, principal, field), with one partial index per value type;
- forced RLS; runtime-only grants (no UPDATE); no grant to the search reader.

**Maintenance (FD-5-ALT, three inputs, one transaction per event):**
1. **Authorization stream:** as in IX-5a; the affected records' value rows are re-derived with their new access rows. A membership change affects no record and rewrites no value row.
2. **Content stream:** event n must be the content checkpoint's successor and pass the same byte, envelope and chain checks, and be one of the stream's three known event kinds. The record it names is re-derived from its accepted head.
3. **Coverage set:** a coverage that becomes Complete, is withdrawn, or returns to Building in a Tier 1 rebuild changes which values are searchable, without a content event. When the digest of the Complete Public/Internal coverages differs from the checkpoint's, all value rows are re-derived.

Rows and the checkpoint change together by compare-and-set. Gaps, replays, tampering, unknown kinds and a checkpoint ahead of the organization quarantine Tier 2.

**Readiness (complete condition).** All must hold:
- the authorization revision and digest, the content-integrity revision and digest, and the coverage digest each equal the live state;
- the current protocol (now 2);
- state `Maintaining`;
- Tier 1 `Active` or absent.

Any new command, commit or coverage change closes readiness immediately, before maintenance runs, so a revoked grant's rows can never be served.

**Verification** adds to M1: the stored value rows must equal the derivation of the canonical access rows over the accepted searchable postings (`ACCESS_VALUES_DIVERGED` otherwise; quarantine, evidence kept). The journal-replay check now also verifies the content stream's complete chain to its head.

**Query behaviour.** There is no search path. A Testing-only internal evaluation (`EvaluateForTestingAsync`) proves multi-condition correctness:
- it refuses unless ready;
- P = {user} ∪ teams;
- share-only records apply Tier 1's UF-25 item rule.

It is not the search endpoint, and nothing connects Tier 2 to search.

## 5. Focused test results

| Suite | Result | Run |
|---|---|---|
| Authority unit (11 IX-5b, 11 R-53 new) | 509/509 | `artifacts/ix-5/c148-unit-full.txt` |
| Architecture | 104/104 | `artifacts/ix-5/c148-arch.txt` |
| Business contract tests | 575/575 | local |
| `SearchAccessValueProjectionStackTests` (IX-5b) | **5/5** | `20261110-222325-04ba82c2` (fresh stack, rebuilt images) |

R-53 write-path regression on the same rebuilt images (every owner, share-editor, restore, lifecycle, document and child write now carries the current snapshot and passes the identity rules):

| Class | Result | Run |
|---|---|---|
| `SearchAccessItemIdentityTests` (TQ-6) | 2/2 | `20261110-222622-7eb5f024` |
| `RepeatingGroupsTests` | 5/5 | `20261110-222650-b1e01b16` |
| `ContributorWriteTests` (direct and team share editors) | 7/7 | `20261110-222739-bcfadd72` |
| `DocumentTests` (attachment versions) | 3/3 | `20261110-222847-92993501` |
| `RecordRestoreTests` (rule I-R: restore never names a removed id) | 11/11 | `20261110-222926-16999051` |
| `RecordLifecycleTests` | 12/12 | `20261110-223109-7e8b0a6e` |
| `ParentChildTests` (related records, unlink) | 2/2 | `20261110-223341-eb9ec436` |
| Tier 1 `IndexedSearchShareOnlyItemChannelTests` (Tier 1 authorization behaviour) | 6/6 | `20261110-223415-f3451683` |
| Tier 1 `SearchIndexFoundationTests` | 9/9 | `20261110-223810-5d194ff4` (its latency measurement ran on battery; numbers not reported) |
| `SpiceDbBehaviourContractTests` | 7/7 | `20261110-224149-e0cffa3c` |
| `SearchAccessProjectionStackTests` (IX-5a) | 9/10 on the first run (`20261110-222440-400cfa93`); rerun below | |

**The IX-5a failure was a test-control race, now fixed.** In the `chain` tamper case the event had already been consumed when the test tampered it. The Testing pause flag was read once per pass over all organizations, so a pass already in flight still reached this organization; IX-5b's longer passes made this visible. The pause is now checked per organization, and pausing waits for any in-flight organization step (`SearchAccessMaintenanceSwitch`). The projection itself was not at fault: the case expects the event unconsumed.

## 6. Copy amplification and maintenance cost

- **Observed (IX-5b stack test, `artifacts/ix-5/ix5b-copy-amplification.json`):** 4 searchable postings produced 6 value rows (1.5×) with an owner, a direct share and a team share. After the revocation and the transfer to the team, 4 postings produced 5 rows (1.25×).
- This matches the structural formula of P-3: the factor is 1 plus the distinct share principals per item, independent of team size.
- **No copy cap was imposed.**
- **Maintenance cost:**
  * an authorization event re-derives its affected records' rows;
  * a content event re-derives one record;
  * a coverage change re-derives the whole organization (measured only at these small sizes).

  Timings were not measured: the host was on battery.

## 7. Remaining defects and risks

- **R-02 FAIL:** P-5 measured on AC (section 3): no regression, but the gate is met in 2 of 3 runs, not reliably.
- **A coverage change re-derives the whole organization** in one transaction. This is unmeasured at scale (R-43) and may need a per-form scope at IX-5c.
- **Intermediate states:** value rows are re-derived from the current accepted head, not from the head at the consumed event. Intermediate states between events can therefore reflect a newer head; only the exact-equality state is ever ready. This is the design's choice, recorded here.
- **The value source is Tier 1 postings.** Their integrity relies on Tier 1's seals and sweeps (S-A, S-B, S-C). Tier 2 adds equality with those postings, not an independent re-derivation from the content bytes.
- **The internal evaluation** supports equality and integer ranges only (String, Integer, Option, Boolean); other operators belong to IX-5c.
- **Stack scenarios not covered:** coverage withdrawal and Tier 1 rebuild (unit only); Decimal, Date and DateTime value types.
- **P-2** latency and throughput measured (section 8); its call-amplification counter is not implemented. **P-4 not run** (it needs a new focused stack test). **P-6 measured (section 8): a regression against C-136, R-55.**
- **IX-5c questions open:**
  * timing leakage during fallback (TQ-23);
  * external journal archive and checkpoint requirements;
  * safe live-serving readiness;
  * large-organization scaling;
  * controlled operational recovery (an operator-plane rebuild with approvals).
- No independent review; production requirements are unchanged.

## 8. P-2 and P-6 (on AC power)

**P-2** (SpiceDB v1.56.2 `LookupSubjects`, local; probe mode `ix5-perf`; `artifacts/ix-5/p2-lookupsubjects-perf.txt`; zero errors).

Per-lookup latency, 200 calls per subject count:

| Subjects | p50 | p95 | p99 |
|---|---|---|---|
| 2 | 3.84 ms | 5.38 ms | 7.95 ms |
| 11 | 3.57 ms | 4.76 ms | 5.07 ms |
| 101 | 4.23 ms | 5.28 ms | 8.62 ms |
| 501 | 7.76 ms | 13.93 ms | 23.97 ms |

Sustained over 60 s at 101 subjects:

| Concurrency | Rate | p50 | p95 |
|---|---|---|---|
| 1 | 308/s | 3.07 ms | 4.36 ms |
| 4 | 965/s | 3.75 ms | 6.18 ms |
| 16 | 1,756/s | 8.02 ms | 15.82 ms |

- Latency grows with subject count only at the 501-subject team.
- Not measured: the API-call amplification per Authority operation (the call counter is not implemented), staging, and multi-instance SpiceDB.
- These are local indicative numbers, not PERF-01 evidence.

**P-6** (`SealSweepLockMeasurementTests`, run `20261110-230303-53b8e1cc`, 1,012 s, AC power, 1,000 records; `artifacts/ix-5/p6-seal-sweep-lock.json`), against the C-136 baseline (IX-4 evidence §11.3, AC power, same test):

| Seal transaction size | Longest lock hold p50 / max (ms) | Entries/s | Searches (40) | Search answered p95 (ms) | Saves p50 / p95 (ms) | C-136 baseline (longest / entries/s / saves p50) |
|---|---|---|---|---|---|---|
| 2,000 | 12,702 / 14,653 | 75.6 | 37 answered, **3 × 503** | 4,740 | 561 / 12,856 | 9,564 / 102.8 / 387 |
| 200 | 3,122 / 3,566 | 68.6 | 40 answered | 3,551 | 690 / 12,059 | 2,213 / 99.8 / 527 |
| **50 (default)** | **1,075 / 1,494** | **57.8** | 40 answered | 1,941 | **2,820 / 6,569** | **685 / 84.8 / 838** |

Seeding: 1.61 records/s, save p50 384 ms and p95 2,216 ms (C-136: 1.98 records/s, 363 and 1,841 ms).

- **Finding R-55 (regression, open, for your decision).** With the C-148 build, seal verification is about 32 % slower and holds the organization lock up to twice as long. Saves during sweeps are slower, and at the 2,000 size three searches timed out (C-136: none).
- **Most likely cause (not yet isolated):** the dark Tier 2 maintenance loop. Every 250 ms in the security-test stack it opens three transactions for every organization (authorization, content, coverage), about 60 organizations here, even when nothing changed. R-53's extra reads per write add a little (seeding −19 %).
- **Proposed mitigation, not implemented:**
  * skip an organization unless one cheap read-only check shows its authorization or content revision, or its coverage set, has moved past the checkpoint;
  * use a longer interval outside the tests.

  Then re-measure P-6 against C-136.
- **Deadlines (extrapolated, estimate only):** at 57.8 entries/s a full seal pass over 100K entries is about 29 minutes, within the §17.10 24-hour deadline. R-43 is needed for real evidence.


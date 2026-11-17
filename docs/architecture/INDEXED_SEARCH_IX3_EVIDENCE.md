# Indexed search (Option C): IX-3 implementation evidence report (C-132)

- **Status:** IX-3 implemented on local branch `ix-1/authority-search-index`, on top of IX-2 (`eef3393`) and the Stage B policy commits (`b282cbb`, `6428710`). Returned for owner acceptance. **Not accepted, not independently reviewed, not pushed, not merged, not deployed.** Stage B intentionally not run (C-131).
- **Authorization:** C-132 (owner, 2026-11-07, message headed "C-130"): conditional acceptance of IX-2; authorization of **IX-3** within the approved design. IX-4 to IX-7 unauthorized; Tier 2 design-only; OBS-IX-1, R6, G14 and M4-2 unauthorized.
- **Data:** synthetic only. The query path exists only where `SearchIndex:Query:QueryEnabled` is set, which is refused outside Development/Testing, and only with a separate reader credential; the business `Shadow` engine is likewise Development/Testing only. Every other environment is unchanged.
- **Kind:** implementation evidence for an owner-led review. Not an independent assessment; no attestation is created or implied.
- **Implementation decisions:** design §21 (D-IX3-1 to D-IX3-17); owner decisions requested: §21.1 (PD-1 to PD-5).

## 1. Scope confirmed before implementation (C-132 §5)

| Step | Result |
|---|---|
| 1. Approved IX-3 specification | Design §16 IX-3 row; R1 §E.1–§E.5, §G.1–§G.3; §6.3 (I-1, I-2), §8 (Tier 1 flow, scope-driven probing, limits, pagination), §17.2 (quarantine precedence Q1–Q5), §17.3 (stored results), §17.9.2 (cross-instance invalidation X1–X4), §18.1.4 (per-page seal verification, C-124; ES256 per C-126), §18.4 (stored-result lifecycle) |
| 2. Deliverables and acceptance criteria | `indexed-query` and `page`; I-1, I-2; result snapshots; plan-shape test; business `Shadow` engine. Exit gate: zero unexplained shadow differences across the Stage B search shards plus N synthetic searches; IX-08–13, 20–24, 33, 36; and §18.4: IX-78–82 |
| 3. Dependencies on IX-1 and IX-2 | Postings with source bytes and ES256 entry seals (IX-1); INV-R; coverage attestation by the builder (IX-2); the strict bulk-check client (O-1, IX-2) |
| 4. Security invariants | Section 4 |
| 5. Unresolved decisions | **Security architecture:** none blocking (FD-2's X2 applied to S1 quarantine transactions is implemented as approved and flagged, PD-3). **Acceptance:** the exit gate names the Stage B search shards, which C-131 makes release-only (PD-1); N is unset (PD-2); IX-33 is a Tier 2 test (PD-5). **Gaps in the IX-1 implementation found and closed within the approved design:** entries lacked the verified header facts of R1 §B.2 (D-IX3-1); the result-snapshot tables, the reader login and the purge did not exist (§18.4 already said so) |

## 2. Files and schema

**Authority (new):**
- `Application/Features/SearchIndex/IndexedSearchService.cs` (search, page, purge; Testing-only plan explain);
- `IndexedSearchPredicates.cs` (validation and V1-equivalent evaluation; `IndexedSearchOptions`, section `SearchIndex:Query`);
- `IndexedSearchBinding.cs` (I-2);
- `SearchEntryVerification.cs` (the S-B seal check extracted unchanged into a pure function; the sweep now calls it);
- `Application/Common/Ports/ISearchQueryReader.cs`; `Infrastructure/Persistence/SearchQueryReader.cs` (reader-login sessions, fixed candidate templates);
- `Api/Features/SearchIndex/IndexedSearchEndpoints.cs`; `Contracts/V1/IndexedSearchContracts.cs`;
- migrations `20261108042541_AuthoritySearchIndexIx3` and `20261108051053_AuthoritySearchIndexIx3PageServed`.

**Authority (changed):** `SearchPostingDerivation.cs` (header facts, derivation version 2); `SearchIndexWriter.cs`; `SearchIndexBuilderService.cs` (`IsIndexedAtHead` requires version 2; S-C compares header facts); `SearchIndexSweepService.cs` (uses the shared verification); `ISearchIndexStore.cs`, `SearchIndexStore.cs` (snapshot operations, purge); `SearchIndexEntities.cs`, `SearchIndexConfigurations.cs`, `AuthorityDbContext.cs`; `AuthorityRuntimeGrants.cs`; `MigrateCommand.cs` (`Database:SearchReaderLoginName`); `SearchIndexComposition.cs` (query options, reader, purge loop); `Program.cs`.

**Business (new):** `Application/Common/Authority/IIndexedSearchAuthority.cs`; `Application/Features/Search/FieldSearch/FieldSearchShadowComparer.cs`; `Api/Features/Search/FieldSearchShadowLogging.cs`.
**Business (changed):** `FieldSearchHandler.cs` (V1 unchanged; `ExecuteAsync` adds the comparison); `FieldSearchLimits.cs` (`Engine`, shadow limits and environment guard); `RequestContext.cs` and `BusinessAuthentication.cs` (session digest); `AuthorityHttpClient.cs`, `BusinessInfrastructureRegistration.cs`; `Program.cs`; `SearchTestingEndpoints.cs` (Testing-only shadow endpoint); `FieldSearchContracts.cs` (Testing response record).

**Infrastructure:** `infrastructure/docker/postgres/init-authority-roles.sh` (optional reader login); `compose.security-test.yaml` (synthetic reader credential, `SearchIndex:Query`, `FieldSearch:Engine=Shadow`).

**Contracts:** `contracts/openapi/authorization-authority-internal-v1.json` gains the two private operations (generated by the build). The public business contract (`enterprise-forms-v1.json`) and the generated TypeScript client are unchanged.

**Schema (AuthorityDb, additive):**
- `search_record_entries`: `header_source_canonical_bytes`, `parent_form_record_id`, `created_by_business_user_id`, `entry_derivation_version` (CHECK: version 2 carries header bytes and creator);
- `search_result_snapshots` (owner, session, form, engine and tier, authorization, content and form revisions, predicate field and approval ids, operators, 32-byte query digest, entry count, created, expiry, creating instance, `last_page_served_on`) and `search_result_snapshot_entries` (snapshot, position, record id, head operation); no values;
- five invalidation triggers (`SECURITY INVOKER`) on `organization_authorization_states`, `search_projection_states`, `field_search_approvals`, `search_field_coverages`, `authenticated_sessions`;
- data step: Complete coverages return to Building (D-IX3-1).

**Grants:** runtime `SELECT, INSERT, DELETE` on both snapshot tables plus `UPDATE (last_page_served_on)`; the reader login is a member of `authority_search_reader` (SELECT-only) with `default_transaction_read_only = on` and the no-parameter logging settings.

## 3. Tests executed

All builds Release; AC power. **Stage B was not run** (C-131): no group, no `-All`, no browser suite, no 480-record benchmark. The new Docker class was run alone with `stage-b.ps1 -Class` on fresh images, as a focused test of the changed code.

### 3.1 Unit, contract and architecture tests

| Suite | Result | New or changed |
|---|---|---|
| `AuthorizationAuthority.Tests` | **435/435** | `IndexedSearchQueryTests` (14): V1-equivalent text, number, date, instant, boolean and option semantics; strict parsing refusals; conjunction; I-2 binding (honest match, posting value, source bytes, leaf digest, leaf count, duplicate leaf, header facts, derivation version, head operation, approval binding, predicate not satisfied by the verified value, coordinated rewrite passing I-2 as OR-1 states); version 2 header facts and `IsIndexedAtHead`; every template parameterized; digest bound to nonce and query; V1 sort; option bounds; snapshot and reader grants |
| `EnterpriseForms.Api.ContractTests` | **533/533** | `FieldSearchShadowComparerTests` (6): Match across pages with one nonce; order, missing and extra ids are Differences; error comparison and the two documented legitimate differences; skips without session, sample or engine; time budget; engine validation and environment guard. `PhaseC7FieldSearchTests.TheHandlerHasNoDependencyThatCouldReadBusinessRecords` extended: the comparer added to the pinned list, and the comparer's own dependencies pinned (Authority client, observer, limits, clock) |
| `EnterpriseForms.ArchitectureTests` | **94/94** | New: `TheIndexedQueryUsesTheReaderLoginAndHoldsNoInstanceLocalState` (IX-56), `TheIx3MigrationInstallsTheSnapshotInvalidationTriggersOnTheInvalidatingTables`. Extended lists (not loosened): the IndexedSearchService added to the S1 pin-and-complete consumers (2 pins, 2 completes) and to the search-schema owners; the result tables added to the naming test; the UPDATE-grant test now pins both column-limited UPDATE grants exactly |
| `EnterpriseForms.Integrity.Tests` | **60/60** | Unchanged |

One contract-suite run during a concurrent Docker image build reported 1 failure out of 533; its name was not captured. Three later runs, one of them without load, passed 533/533. Recorded as an unexplained intermittent failure (register R-28).

### 3.2 Docker integration tests (fresh images)

Fresh images (`-Fresh`: `down -v`, images rebuilt, reader login created by the cluster bootstrap). Runs on the same code until it passed; product fixes between runs are listed in section 5.

| Run | Images | `IndexedSearchQueryStackTests` | Outcome |
|---|---|---|---|
| 1 | — | not run | the test project did not compile (analyzer CA1822 in the new test) |
| 2 | fresh | 8/19 | product defects found (section 5) and test defects |
| 3 | fresh, final product code | 17/19 | two test defects (boolean text form, a CHECK on session expiry) |
| 4 | same images as run 3 | **19/19** | test-only changes since run 3 |

The 19 tests (one theory with two cases): synthetic shadow differential (exit-gate part, N = 600); IX-08/09; IX-10/11/36; IX-12/30; plan-shape (IX-68); IX-20/78; IX-79/80; IX-81/22; IX-23/43; IX-24 (and D-IX2-7 rule 1); IX-44/34 (posting value changed; posting deleted); IX-47/48; IX-49/50; IX-55; IX-82; business-copy damage as the legitimate difference (IX-31/32 under IX-D3); reader-login privileges (§5.1); IX-13 report.

**Focused regression of the changed areas** on the run-3 images (policy C-131 §6), each class alone with `-Class`:

| Class | Result | Why it was run |
|---|---|---|
| `SearchIndexFoundationTests` (IX-1) | **9/9** | derivation version 2, entry columns, grants |
| `SearchIndexBuilderStackTests` (IX-2) | **11/11** | `IsIndexedAtHead` version rule, re-derivation, S-C header comparison |
| `SpiceDbBehaviourContractTests` | **7/7** | strict bulk checks used by the query path |
| `ProjectionIntegrityRollbackTests` (S1) | **14/14** | the S1 quarantine transaction now fires the snapshot trigger |
| `SessionRevocationTests` | **7/7** | the session trigger |
| `FieldSearchApprovalTests` | **5/5** | the approval trigger |
| `SearchCatalogueTests` | **12/12** | the V1 path the shadow compares against |

**Not run:** Stage B (all seven groups and the browser suite: R-20); `FieldSearchExecutionTests` (it contains the 480-record benchmark, which may not be run; the V1 search path itself is unchanged except for the new comparison call, which is off unless `FieldSearch:Engine=Shadow`); Stage A SEC-P (no Stage A requirement changed; the last result stands, 13/13 with the validator PASS).

### 3.3 Measurements (local; not PERF-01)

| Measurement | Value | Note |
|---|---|---|
| Synthetic shadow differential | **600 searches, 600 Match, 0 unexplained differences** | 25 predicate sets (every type and operator, AND combinations, LIKE metacharacters, Unicode and ß, a value nowhere) × 4 sorts × 3 status/archive filters × 2 callers (owner; partial reader). `artifacts/ix-3/shadow-differential.json` |
| IX-13 indexed search, caller scope 1 record, organization 24 records | p50 70 ms, p95 98 ms (20 searches) | Report only; local Docker; the plan-shape test is the timing control. `artifacts/ix-3/timing-report.json` |

Not measured: latency at 50,000 records, page-invalidation rate (R-26), seal-verification cost per page against Managed HSM (local ES256 only; R-01).

## 4. Security and integrity invariants (C-132 §6)

| Invariant | How IX-3 preserves it | Evidence |
|---|---|---|
| Current Authority authorization | Population, readability, the predicate permission (`query_fields`) and edit flags are SpiceDB decisions at one S1 pin; the pin is verified (S1 `CompleteAsync`) before anything is stored or released, and every page decides again | Architecture `EveryDecisionPathPinsAndCompletes` (IndexedSearchService added: 2 pins, 2 completes); IX-10/11/36; IX-20/78 |
| Pinned SpiceDB snapshots | Strict bulk checks (a per-item error fails the call) at the pin; no decision outside a pinned snapshot | `CheckBulkStrictAsync` only; IX-82 |
| S1 organization quarantine | Q1 order: gate and S1 first (423) for search and page, before any snapshot lookup; search-side failures never touch the S1 state; an S1 quarantine deletes the organization's snapshots in its own transaction | IX-23/43; IX-44 (S1 flag unchanged after an index quarantine) |
| Record-level quarantine | A quarantined record in scope fails the whole search with `RECORD_QUARANTINED`; a quarantined page record fails the page; such a record never appears | IX-24 (D-IX2-7 rule 1, R-18) |
| Signed index integrity | I-2 for every match; the ES256 seal of every released page record verified with the pinned key; any finding quarantines the search index only | Unit binding tests; IX-34/44 (posting changed, posting deleted) |
| Signature ≠ freshness or completeness | Freshness is checked separately (entry operation = head operation, projection revision = content revision, form state); the seal is never taken as proof of the current approval state or of omission-free counts | D-IX3-7; OR-1 limitation recorded by a unit test |
| Current accepted revisions | Entries must be at the head's operation and of derivation version 2; snapshots are bound to the authorization, content and form revisions | I-1; IX-22, IX-81 |
| Classification and protection | Only Active approvals (Public/Internal, Standard) are searchable; encrypted fields get the V1 denial; an encrypted value on a predicate field fails the search | IX-12/30 (encrypted `case_note`); D-IX3-8 |
| No Confidential, encrypted or ciphertext-derived content | Derivation unchanged; no new value is indexed; the snapshot stores ids only; the digest covers a nonce held by the business API alone | IX-49/50 (no value in snapshot rows); unit digest test |
| Fail closed | Every inconsistency fails the whole search with no partial result; a timeout is `SEARCH_VERIFICATION_LIMIT` | IX-82 (never a partial page); I-1 and I-2 findings |
| Tenant isolation | Organization bound in the reader session (RLS) and in every predicate; sessions bound to the identity; another organization's caller gets the denial | IX-12 (foreign organization, stolen session) |
| No unauthorized counts or pagination disclosure | Counts are of authorized matches only; limit refusals depend on caller-owned quantities; the scope count is the caller's own | IX-08/09 |
| Session-bound stored results | Session required; revoked, expired or idle sessions refuse pages; revocation deletes the session's snapshots | IX-79/80 |
| Reconciliation and freshness | The read-only §17.10 deadline check before every search; suspended or quarantined projections refuse search and delete snapshots | D-IX3-9; IX-44 |
| Authority checks never bypassed for performance | No cache of decisions; no value-index plans (plan-shape test); per-match I-2 and per-page seals always run | Architecture IX-56; plan-shape test |

## 5. Failures, limitations and deviations

**Product defects found by the Docker tests and fixed (none remains open):**
1. **Row locking was refused.** `SELECT … FOR UPDATE` needs UPDATE privilege on some column, and result snapshots were insert/delete-only for the runtime role, so the purge job and the page's final lock failed (`permission denied`, 503 to the caller). Fixed by D-IX3-2: a column-limited `UPDATE (last_page_served_on)` grant (additive migration `AuthoritySearchIndexIx3PageServed`); the page's final transaction updates that column, which takes the row lock.
2. **The plan-shape test found a value-index plan.** With the comparison inside the per-record probe, PostgreSQL used `ix_search_string_postings_value` (still per record, but the design prohibits the value index). Fixed by D-IX3-13: the probe is a fenced subquery that reads only the record's postings; the comparison is outside the fence. The test now passes for every template.
3. **An authorization refusal was masked by not-ready.** With one condition's coverage Building after a withdrawal, the Authority answered `SEARCH_INDEX_NOT_READY` before checking a later condition's approval; V1 answers `SEARCH_FIELD_NOT_ALLOWED`. Fixed: approvals and operators of every condition are checked first, coverage second.
4. **A shadow classification was missing.** V1's verification quarantines a record whose business copy is damaged, so the indexed engine (run afterwards) correctly refuses with `RECORD_QUARANTINED`; this was classified as a Difference. Fixed: classified as the documented legitimate difference (same root cause, IX-D3).

**Test defects fixed:** a partial-text predicate shorter than 3 characters; the Reader queried on a field it cannot read; archive and withdrawal request bodies; PostgreSQL boolean text; a session-expiry update violating a CHECK; reader-privilege assertions that assumed role settings apply under `SET ROLE`.

**Limitations:**
1. **The Stage B search shards were not run** (C-131), so the exit gate's shard part is open (PD-1, register R-21).
2. **IX-54 is partial** (D-IX3-16): one Authority instance in the stack.
3. **IX-46 (Q4 escalation)** is inherited, not re-tested: the search uses the same S1 verifier whose mismatch handling the S1 suite proves; no IX-3 test injects a SpiceDB mismatch during a search.
4. **IX-07 (approval change racing a search)** is covered by the locked re-check of coverages and approvals before the snapshot is stored and by IX-81; no deterministic race injection.
5. **I-2 is unkeyed** (OR-1): matches beyond the displayed page are bound only by I-2; the count can include false matches under AD-1 (design §18.1.4). Freshness against AD-1/AD-3 still needs signed external checkpoints (R-08).
6. **Shadow covers only session-authenticated searches**; bearer clients are skipped (D-IX3-5).
7. **IX-13 is a small local report**, not a timing-independence proof; PERF-01 measures at scale.
8. **The reader credential is held by the same Authority process** as the runtime credential (UF-5, FD-6 deferred).

**Deviations from the design text:** D-IX3-2 (served-on update instead of `SELECT … FOR UPDATE`, same serialization), D-IX3-3 (triggers instead of application deletes; same transactions), D-IX3-4 (sort in process with the V1 comparator), D-IX3-16, D-IX3-17. None weakens a security property.

## 6. Traceable register of outstanding requirements

R-01 to R-19 from the IX-2 report are carried; their status is unchanged except R-18.

| # | Requirement | Source | Status | Assigned to |
|---|---|---|---|---|
| R-01 | Azure Managed HSM ES256 seal provider (and the HMAC provider) | D-IX1-13; C-126 | Not implemented; fail closed | **IX-7**; blocks production |
| R-02 | 20 ms p95 incremental commit latency | IX-1 gate; C-128 | **FAIL**, carried | **IX-7** ST-PERF-1..7 |
| R-03 | Cross-language cryptographic test vectors | IX-1 §4.2 | .NET-only | Independent cryptographic review, before production |
| R-04 | W3 and W4 integration coverage; IX-73 beyond W1 | IX-1 §4.2 | Unit-tested only; the IX-3 guard relies on INV-R at every site | **Owner decision** |
| R-05 | pgAudit and Azure diagnostic log inspection (IX-28) | IX-1 §2.5 | Local only | **IX-7** |
| R-06 | 480-record verified-search limitation (P-S1-1) | IX-D10 | Open known failure | **IX-4** disposition |
| R-07 | Intermittent `RecordCreationIdempotencyTests` | IX-1 §2.4 | Not run on IX-3 code | **Owner decision** |
| R-08 | Signed external checkpoints | C-126; §18.1.4 | Not implemented | **Before any real-data use** |
| R-09 | Independent third-party security and cryptographic review | FD-8; C-123 | Not started | External; before production |
| R-10 | OBS-IX-1 and R6 | C-124 | Unauthorized | **Owner decision** |
| R-11 | IX-19 full (restore, G14 replay) | §12.2 | Simulated only | **G14**, **IX-7** |
| R-12 | Step-up on `SearchProjectionRebuild` | §12.2 | Not available | **PB-08** |
| R-15 | Builder and deep-sweep pacing; population and attestation cost at 100K | D-IX2-8 | Unmeasured | **IX-7** |
| R-16 | Dashboard health (coverage, sweeps, attestation, rebuild, snapshots) | §6.5 | Not built | **Owner decision** |
| R-17 | Builder client certificates outside Development/Testing | D-IX2-14 | Not exercised | **IX-7** |
| R-18 | Query-path tests | §16 | **Mostly closed by IX-3** (section 3.2); open: IX-46 and IX-07 (inherited, section 5), IX-54 (R-23), IX-33 (R-24) | — |
| R-19 | Stage B regression on the IX-2 code | C-131 | Not run | Next owner-authorized release run |
| R-20 | Stage B regression on the IX-3 code (all seven groups, browser suite) | C-131 | **Not run** | Next owner-authorized release run |
| R-21 | Exit-gate part "zero unexplained shadow differences across the Stage B search shards" | §16; C-131 | Not run; synthetic part done (section 3.2) | **PD-1**; next authorized release run with `FieldSearch:Engine=Shadow` |
| R-22 | Value of N (synthetic searches) | R1 §G.3 | Unset in the design; measured in this run | **PD-2** |
| R-23 | IX-54 with two Authority instances | §17.9.3 | Partial (D-IX3-16) | **IX-4** or **IX-7** (second Authority instance in the stack) |
| R-24 | IX-33 (Tier 2 counts and limits) | §16 | Not applicable while Tier 2 is unauthorized | **IX-5** (PD-5) |
| R-25 | Builder Worker escalation of persistent unexpected failures | C-132 §4 | Proposed (section 7) | **PD-4** |
| R-26 | Page-invalidation rate under continuous edits (R1 §E.5 UX risk; IX-D5 "measured before production") | IX-D5 | Unmeasured | **IX-7** |
| R-27 | PB-14 copy review of the indexed-result wording (§6.6) | §6.6 | No UI in IX-3 | **IX-4** |
| R-28 | One unexplained intermittent contract-suite failure during a concurrent image build (name not captured) | Section 3.1 | Three later runs 533/533 | Watch in the next runs; investigate if it recurs |
| R-29 | `FieldSearchExecutionTests` on the IX-3 code (contains the 480-record benchmark) | Section 3.2 | Not run | Next owner-authorized release run |

## 7. Builder Worker review (C-132 §4)

**Finding.** The runner classifies failures as transient, refused credential or unexpected. A refused credential stops the builder (fail closed). Transient and unexpected failures are retried forever with exponential backoff capped at 5 minutes, each logged once per attempt (Warning for transient, Error for unexpected). Nothing escalates a failure that persists. In addition, an unexpected or transient failure in one organization's form ends that pass for every later organization in the same round (only 404, 409 and 423 are isolated per form or organization).

**Security assessment.** No fail-open path: a failed pass completes nothing (only the Authority's attestation sets Complete), writes no verification timestamp, and the Authority's own §17.10 deadlines suspend indexed search independently when the deep sweep does not complete. **No security-critical correction is required before IX-3.** The gap is operational: a persistent defect is visible only as repeated log lines, and one organization's persistent failure can hold back the backfill and deep sweep of others until their deadlines suspend them.

**Proposed minimal safeguard (not implemented; PD-4):**
1. Count consecutive failures per pass kind and record when the run of failures began.
2. When consecutive *unexpected* failures reach 5, or any failure run lasts 30 minutes, emit one Critical event (new id 9315, "persistent builder failure", with pass, failure kind, exception type, error code, count and duration; no content), repeated at most hourly while it persists; emit Information 9316 on the first success after an escalation.
3. Keep retrying at the capped backoff; do not stop the builder (stopping would not improve security and would only delay recovery).
4. Optional, also minimal: isolate unexpected failures per organization within a round (log, continue with the next organization), so one organization cannot hold back others.

No change to the credential-refusal rule, the attestation rule or any Authority-side check.

## 8. Recommendation

IX-3 is ready for **conditional owner acceptance**:
- every IX-3 deliverable is implemented within the approved design: Tier 1 `indexed-query` and `page`, I-1, I-2, per-page ES256 seal verification, result snapshots with the §17.3/§18.4 lifecycle, the plan-shape test and the business `Shadow` engine;
- the focused suites pass on the final code: unit 435/435, contract 533/533, architecture 94/94, integrity 60/60, the IX-3 Docker class 19/19 on fresh images, and seven regression classes;
- the synthetic part of the exit gate shows 0 unexplained differences over 600 searches.

Conditions to record with the acceptance:
1. PD-1: the Stage B search-shard part of the exit gate is carried to the next owner-authorized release run (R-21);
2. PD-2: N = 600 for IX-3;
3. PD-3: confirm the snapshot deletion inside S1 quarantine transactions;
4. PD-4: the Worker escalation safeguard (approve, or defer);
5. PD-5: IX-33 to IX-5;
6. IX-54, IX-46 and IX-07 partial as described; the register items stay carried to their stages.

No production readiness is implied. IX-4 and later remain unauthorized.

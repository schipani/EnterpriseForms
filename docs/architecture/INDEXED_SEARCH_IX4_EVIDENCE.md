# Indexed search (Option C): IX-3 follow-ups and IX-4 implementation evidence report (C-133)

- **Status:** the IX-3 follow-ups (`e0e320c`) and IX-4 (`e0a36ed`) are committed (signed, local) on branch `ix-1/authority-search-index`, on top of IX-3 (`cb48ba7`) and the ruleset follow-up (`6428710`); this report is the third commit. Returned for owner acceptance. **One open acceptance blocker: the plan-shape finding (§6.1; design §22.4; PD-9).** **Not accepted, not independently reviewed, not pushed, not merged, not deployed.** Stage B intentionally not run (C-131).
- **Authorization:** C-133 (owner, 2026-11-08, message headed "C-132"): conditional acceptance of IX-3; decisions PD-1 to PD-5; authorization of **IX-4** within the approved design. C-134 (owner, 2026-11-08): completion instructions; PD-6 approved with conditions, PD-7 approved, PD-8 provisionally approved; the 50,000-record seeding not to be started. IX-5 to IX-7, Tier 2, OBS-IX-1, R6, G14 and M4-2 unauthorized.
- **Data:** synthetic only. The `Indexed` engine is refused at startup outside Development/Testing; the persistent development stack is unchanged (`VerifiedScan`); the security-test stack defaults to `Indexed`.
- **Kind:** implementation evidence for an owner-led review. Not an independent assessment; no attestation is created or implied.
- **Design records:** design §21.2 (PD-1 to PD-5 and their implementation) and §22 (IX-4 scope, decisions D-IX4-1 to D-IX4-9, owner decisions PD-6 to PD-8, P-S1-1 disposition).

## 1. IX-3 follow-ups

### 1.1 PD-1, PD-2 and PD-5

- **PD-1:** recorded as approved; the Stage B search-shard requirement stays open for the next owner-authorized release validation (R-21). Not marked passed.
- **PD-2:** N = 600 recorded as the IX-3 development acceptance threshold only; not a statistical guarantee, not a release threshold. Coverage limitations (browser-session searches only; synthetic data) unchanged.
- **PD-5:** dependency confirmed (design §21.2): IX-33 tests the Tier 2 access-posting candidate narrowing and per-result SpiceDB decisions, which exist only in Tier 2. Its Tier 1 counterpart (counts and refusals that depend only on the caller's own records) is IX-08/IX-09 and IX-29, executed in IX-3 and in this report (§3, §4). IX-33 is assigned to IX-5, requirement and assertions unchanged, **not completed** (R-24).

### 1.2 PD-3: S1 quarantine and snapshot invalidation

**S1 analysis (before any change).** The quarantine of a confirmed projection mismatch is recorded on the locked organization row and committed in one transaction, which (since IX-3, FD-2 X2) also deletes the organization's stored search results through the `trg_search_result_snapshots_organization_state` trigger. If that deletion fails:
1. the whole transaction rolls back: the organization is not quarantined and its stored results remain;
2. the exception propagates; the deciding request is refused (Authority 503 `AUTHORITY_NOT_READY`; the decision never completed, so nothing is released);
3. the mismatch is persistent state (SpiceDB against AuthorityDb), so every later decision for the organization (search, page, V1 catalogue, any permission check) observes it at its pin and attempts the quarantine again; the periodic check (S2) does the same;
4. the same holds for the projector's own quarantine writers (outbox gap, journal gap, divergence) and the archive processor: their loops retry and the organization gate stays closed while the projection or archive is behind.

So S1 stays fail closed after a failed quarantine transaction; **no security design issue**. The gap was observability: the failure was visible only as an unclassified 503 and a loop error.

**Change (no S1 semantics changed).** `ProjectionIntegrityVerifier.CommitQuarantineAsync` commits a recorded quarantine; on failure it raises the Critical alert `PROJECTION_INTEGRITY_QUARANTINE_NOT_COMMITTED` (organization id, reason, phase, exception type; no subject, record or permission identifiers) and the metric outcome `quarantine_not_committed`, then rethrows the original exception unchanged. The decision path (lock-confirm) and the startup transition use it.

**Tests.**

| Required by C-133 §4 | Test | Result |
|---|---|---|
| 1. A successful S1 quarantine invalidates all snapshots | `IX23IX43AnOrganizationQuarantineRefusesSearchAndPagesWith423` (IX-3); the retry step of the PD-3 Docker test (snapshots 1 → 0 when the quarantine commits) | **Pass** (§3.2) |
| 2. A deletion failure prevents reported success | PD-3 Docker test: runtime `DELETE` on the snapshot table revoked; the page's S1 check confirms the mismatch; the request fails (5xx, no records); organization not quarantined and the snapshot kept (rolled back) | **Pass** (§3.2) |
| 3. A failed or unresolved quarantine exposes nothing through search or paging | Same test: the page, a new indexed search and the V1 business search are all refused with no record ids | **Pass** (§3.2) |
| 4. Current S1 state checked before results are served | Q1 order (D-IX3-12); the PD-3 test's page fails at the S1 step; after the retry the page answers 423 | **Pass** (§3.2) |
| 5. A failed attempt is observable and retried or escalated | Critical alert in the Authority log; unit `AQuarantineThatCannotBeCommittedIsRefusedAlertedAndRetriedByTheNextDecision`; the Docker retry step commits the quarantine (423, reason `PROJECTION_MARKER_AHEAD`) | **Pass** (§3.2) |
| 6. No partial success | Rolled-back state asserted by SQL after each refused request | **Pass** (§3.2) |

The mismatch is an AuthorityDb state naming a foreign journal history at the same authorization revision (a restore from a foreign backup), so the page passes the gate and its snapshot-revision check and reaches its S1 pin. (A first version moved the authorization revision back; the page was then refused by its revision check before the pin — still fail closed, but not the path under test.)

### 1.3 PD-4: builder escalation

`SearchIndexBuilderEscalation` (one per pass kind, in `SearchIndexBuilderRunner`):
- a failure run starts at the first failure after a successful pass and ends only at the next pass that completes without an exception;
- **Critical event 9315** `IX-BUILDER-PERSISTENT-FAILURE` when the run holds 5 unexpected failures (transient failures in between do not reset the count) or has lasted 30 minutes, whichever first; repeated at most once per hour while the same run continues;
- **Information event 9316** `IX-BUILDER-RECOVERED` at the first successful pass after an escalation;
- unchanged: the transient and unexpected backoff (2 s doubling to 5 min), the credential refusal (Critical 9314, builder stops), cancellation and graceful shutdown, and the rule that a failed pass completes nothing (only the Authority's attestation sets Complete);
- diagnostics: pass name, trigger, counts, duration, failure kind, exception type name and Authority error code only.

Tests (contract project, `SearchIndexBuilderRunnerTests`): five unexpected failures escalate once an hour until a success resets the run; 30 minutes of transient failures escalate; transient failures do not reset the unexpected count; a success without an escalation is silent; the runner escalates, keeps retrying with the unchanged backoff sequence and reports the recovery; a refused credential still stops without an escalation event. Result: 25/25 in the contract suite (569/569 overall). On the real Worker, `SearchIndexBuilderStackTests` 11/11 (no escalation event during normal passes). Log flooding: by construction at most one 9315 event per pass kind per hour while a failure run continues; the per-failure Warning/Error lines are unchanged and already bounded by the capped backoff (at most one per 5 minutes per pass kind once capped).

### 1.4 PD-4: per-organization isolation, assessed and not included

Isolating one organization's failure inside a backfill round or deep-sweep pass (log and continue with the next organization) is simple in itself, but the pass would then complete and **reset the escalation**, so one organization's persistent failure would never escalate — the opposite of PD-4's purpose. Doing it safely needs per-organization failure accounting in the pass result and in the escalation (and its tests). That is more than a minimal change, so it is **not included**; it is recorded as a separate operational improvement (R-31). The Authority's own §17.10 deadlines still suspend the index of an organization whose sweeps do not complete, so the gap is availability, not security.

### 1.5 R-28 and R-29

- **R-28 (kept open):** no evidence was found to classify the historical failure. Every `stage-b.ps1` run and every `test.ps1` .NET step now writes an xUnit TRX report (failing test names, messages and stack traces) under `artifacts/test-runs/<run id>/`; `stage-b.ps1` also writes `run.json` (selection, exit, elapsed, `-RetryOf`) and the authority, api and worker logs on failure. The scripts never retry. Every run in this report has its run id (§3). No regression cycle was started to chase the historical failure.
- **R-29:** the 480-record benchmark of `FieldSearchExecutionTests` carries `[Trait("Category", "Benchmark")]`; `stage-b.ps1 -Class FieldSearchExecutionTests -ExcludeBenchmarks` runs the functional tests alone. The benchmark is unchanged and still runs in its Stage B group (the switch is refused for group and release runs). Result of the functional run: 13/13 in 132 s (run `20261108-065846-a93014d4`; the TRX lists 13 tests and no benchmark). The P-S1-1 benchmark of `SearchCatalogueTests` was given the same trait during IX-4 (see §6.3); `SearchCatalogueTests -ExcludeBenchmarks` ran 11/11. The script fix was verified on every focused run of this report: each run produced its run id and TRX report (one defect of the first version, below, is fixed).

## 2. IX-4 scope confirmed before implementation (C-133 §10)

| Step | Result |
|---|---|
| 1. Approved IX-4 specification | Design §16 IX-4 row; R1 §E.1, §E.4, §E.5, §G.1, §G.2, §L IX-4; §6.2, §6.6, §8.3, §12.3, §17.2, §17.3; IX-D3, IX-D8, IX-D10 |
| 2. Deliverables and acceptance criteria | `Indexed` engine for Advanced Field Search (Development/Testing); fallback; UI wording and contract field; Stage B; local benchmarks to 50K. Exit gate: all IX tests through 37; owner functional and copy acceptance; P-S1-1 disposition recorded |
| 3. Dependencies on IX-1 to IX-3 | Inline derivation and entry seals (IX-1); builder attestation and coverage lifecycle (IX-2); Authority `indexed-query` and `page`, result snapshots, I-1, I-2, page seals and the Shadow engine (IX-3) |
| 4. Invariants | §5 |
| 5. Unresolved decisions | At the start: none for security architecture; implementation decisions D-IX4-1 to D-IX4-10 (design §22.1); PD-6 to PD-8 (decided by C-134). **Found during testing: PD-9** — the §8.2 plan-shape guarantee needs an index-schema decision (§6.1) |
| 6. Nothing beyond IX-4 | No Tier 2, no access postings, no My Records change, no Azure provider, no V1 removal, no ENC-SR text change, no change to the persistent development stack |

### 2.1 Files

**Business (new):** `Application/Features/Search/FieldSearch/FieldSearchIndexedEngine.cs` (engine, cursor, error mapping, `FieldSearchResultBases`, `IFieldSearchEngineObserver`); `Api/Features/Search/SearchTestingSession.cs`.
**Business (changed):** `FieldSearchHandler.cs` (engine branch, fallback, `ResultBasis` on V1 responses; V1 path otherwise unchanged); `FieldSearchLimits.cs` (`Indexed`, environment guard); `FieldSearchShadowComparer.cs` (compares under `Indexed` when forced; `INTEGRITY_QUARANTINE` mapping corrected to V1's reason); `SearchEndpoints.cs`, `SearchTestingEndpoints.cs`, `FieldSearchShadowLogging.cs` (event 9333), `Program.cs`; `Contracts/Features/FieldSearchContracts.cs` (`ResultBasis`).
**Contracts:** `contracts/openapi/enterprise-forms-v1.json` (`resultBasis`, summary); `packages/api-client` regenerated.
**Frontend:** `features/search/components/resultBasis.ts` (+ test); `searchFailure.ts` (+ tests); `RecordsPage.tsx`; `e2e/advanced-field-search.spec.ts` (IX-37).
**Infrastructure:** `compose.security-test.yaml` (`FieldSearch:Engine=Indexed` default, `FIELD_SEARCH_ENGINE`).
**Tests:** `IndexedFieldSearchEngineTests` (contract); `IndexedFieldSearchEngineStackTests`, `IndexedSearchLocalBenchmarkTests` (Docker, group `search-execution`); updated pins in `PhaseC7FieldSearchTests` and `FieldSearchShadowComparerTests`.

## 3. Tests executed

All builds Release; AC power checked before every Docker run and the benchmark. **Stage B was not run** (C-131): no group, no `-All`, no full browser suite, no 480-record benchmark.

### 3.1 Unit, contract, architecture, integrity and frontend tests (final code)

| Suite | Result | New or changed |
|---|---|---|
| `AuthorizationAuthority.Tests` | **436/436** | PD-3 `AQuarantineThatCannotBeCommittedIsRefusedAlertedAndRetriedByTheNextDecision` |
| `EnterpriseForms.Api.ContractTests` | **569/569** | `IndexedFieldSearchEngineTests` (31: result basis and no "verified" rows; pages through the Authority with one nonce; cursor bound to caller and session, tampering refused; fallback for the three index states and the V1-limit rule; final refusals never fall back; one retry on a changed snapshot; pages never fall back; sessionless V1 routing; engine guard; dependency pin; **PD-6 boundaries**: a forbidden field refused before any engine with or without a session; a sessionless V1 refusal never retried through the index; an indexed page never downgraded to V1 without a session or with the engine switched off); escalation tests (5); updated pins (`PhaseC7FieldSearchTests`, `FieldSearchShadowComparerTests`: `Indexed` is now a valid engine) |
| `EnterpriseForms.ArchitectureTests` | **94/94** | — |
| `EnterpriseForms.Integrity.Tests` | **60/60** | — |
| Frontend lint, typecheck, Vitest | **clean, clean, 455/455** | `resultBasis.test.ts` (IX-37), `searchFailure.test.ts` (index states, encrypted predicate value, result cap) |

TRX reports: `artifacts/test-runs/ix4-final-*` (not committed).

### 3.2 Docker integration tests (security-test stack, images rebuilt from the final product code)

| Class | Result | Run id | Notes |
|---|---|---|---|
| `IndexedSearchQueryStackTests` (IX-3 class + PD-3) | **19/20** | `20261108-064816-a0c96303` | PD-3 passes; the synthetic shadow differential passes again under the `Indexed` engine (600 searches, 600 Match, 0 unexplained differences); **the plan-shape test fails** (§6.1, PD-9) |
| `IndexedFieldSearchEngineStackTests` (IX-4) | **5/5** | `20261108-065704-c0aabc72` | Engine answers as accepted content and matches V1's ordered ids over all pages; Building coverage → V1 fallback (logged); index quarantine → V1 fallback; S1 quarantine refused identically by both engines (423 at the capability decision), no fallback; IX-31/32; revocation between pages (409) |
| `FieldSearchExecutionTests -ExcludeBenchmarks` | **13/13** | `20261108-065846-a93014d4` | R-29 |
| `ProjectionIntegrityRollbackTests` | **14/14** | `20261108-070059-042b4f7d` | S1 (verifier changed) |
| `SearchIndexBuilderStackTests` | **11/11** | `20261108-070455-2bf4320d` | Worker runner changed |
| `SearchIndexFoundationTests` | **9/9** | `20261108-071048-594b28db` | — |
| `FieldSearchApprovalTests` | **5/5** | `20261108-071508-9cf0f32a` | — |
| `SessionRevocationTests` | **7/7** | `20261108-071551-76910aa0` | Session binding |
| `SearchCatalogueTests -ExcludeBenchmarks` | **11/11** | `20261108-071647-4aa086a4` | P-S1-1 benchmark excluded |

Earlier runs of this work (all with run ids in `artifacts/test-runs/`): the first IX-4 class run failed 2/5 on two wrong test expectations (an approval change returns *all* the form's coverages to Building; an S1 quarantine is refused at the capability decision with 423 before either engine runs) — tests corrected, product unchanged; the first PD-3 run failed at its last step because the simulation moved the authorization revision, so the page was refused by its revision check before its S1 pin — the simulation was changed to a foreign journal history at the same revision (§1.2).

### 3.3 Playwright (focused specs, real stack via Vite on port 5173 against the security-test API; port checked free first)

| Spec | Result | Notes |
|---|---|---|
| `advanced-field-search` | **7/7** | Includes the new IX-37 test (indexed result never labelled verified, with and without matches) |
| `field-search-execution` | **6/6** | Real searches through browser sessions; all 8 executed searches were journaled with engine `Indexed`, 0 fallbacks |

The first attempt failed at sign-in because the seeded persona organization had been S1-quarantined by `ProjectionIntegrityRollbackTests` (its SpiceDB restores leave other organizations inconsistent; known gotcha). The `enterpriseforms-sectest` project was recreated with clean volumes and both specs passed.

### 3.4 IX test traceability (§16 IX-4 gate: "all IX tests through 37")

| Tests | Where executed | Status |
|---|---|---|
| IX-01 to 07, 14 to 17, 28, 34, 35, 42 | IX-1 (`INDEXED_SEARCH_IX1_EVIDENCE.md`); IX-34 also IX-3 | Pass (IX-07 race injection inherited, R-18; IX-28 local only, R-05) |
| IX-18, 19, 25 to 27 | IX-2 | Pass (IX-19 simulated, R-11) |
| IX-08 to 13, 20 to 24, 30, 36 | IX-3 class (re-run here, 19/20) | Pass, except IX-68 plan shape (§6.1) which underpins IX-13's timing premise |
| IX-29 | Contract mapping; caller-owned limit checks (IX-3); real-stack limits partially (§6.4) | **Partial** |
| IX-31, 32 | `IndexedFieldSearchEngineStackTests` (business copy damage: returned from accepted content, quarantined on open, later searches refused); the sweep detection part is IX-2 (S-C deep sweep) | Pass |
| IX-33 | — | **Assigned to IX-5** (PD-5), not completed |
| IX-37 | Contract tests, Vitest, Playwright, real-stack engine test | Pass |
| IX-43 to 56, 78 to 82 | IX-3 class (IX-54 partial, R-23) | Pass, except as noted |

### 3.5 Not run

- **Stage B** (all groups and the full browser suite): not executed (C-131, PD-7; R-30).
- **The 480-record benchmarks** of `FieldSearchExecutionTests` and `SearchCatalogueTests`: not run in IX-4 (but see §6.3 for IX-3).
- **The 10,000- and 50,000-record benchmark steps** (§4).
- **SEC-P (Stage A):** no Stage A requirement changed; last result 13/13 stands.

Power: AC was checked before every Docker block; part of the regression block (from the IX-3 class to about `ProjectionIntegrityRollbackTests`) ran on battery (67 % to 65 %) until AC returned; all of it passed except the plan-shape test, which is deterministic. The benchmark ran on AC.

## 4. Local benchmark to 50K (report only)

**Run:** `20261108-073507-6be7dfa4` (21 min), commit `e0e320c` for the product code (the benchmark source was then fixed and committed in `e0a36ed`), clean `enterpriseforms-sectest` volumes, AC power. Report file: `artifacts/ix-4/benchmark.json` (not committed). Phases measured separately (C-134 §6); nothing written to AuthorityDb directly; no index entry fabricated.

| Phase | Measured |
|---|---|
| Ingestion (public API, concurrency 4) | 1,000 records in **944 s** (≈ 1.1 records/s). Concurrent creations conflict on the organization's authorization revision (`STALE_AUTHORIZATION_REVISION`, retried as a new command); the first attempt at concurrency 12 failed on this (§6) |
| Index building (approval change → builder rebuild → attestation) | 1,000 records × 3 coverages Complete in **42 s** |
| 10,000-record step | **Not started:** projected ingestion 142 min > 30 min budget (budget guard) |
| 50,000-record step and the discover-ceiling check | **Deferred** (C-134); requirement outstanding (R-34) |

Query execution at 1,000 records (20 runs per case, page size 25). Percentiles are end-to-end through the business API **over all 20 runs** as recorded by the first report format; the outcome of each run is reconstructed from the Authority's `FieldSearchExecuted` journal (108 searches COMPLETED for the organization, Authority-side elapsed p50 399 ms, max 3,741 ms):

| Case | Matches | p50 (ms) | p95 (ms, all runs) |
|---|---|---|---|
| Owner, selective (1 match) | 1 | 434 | 10,021 |
| Owner, 1 % (`exhibit_count = 7`) | 10 | 465 | 10,028 |
| Owner, 10 % (`region_bucket = 3`) | 100 | 465 | 10,033 |
| Page 2 of the 10 % search | 100 | 96 | 137 |
| Owner, 100 % (`exhibit_count >= 0`) | 1,000 | 644 | 10,028 |
| Owner, AND (prefix + 10 %) | — | 483 | 10,032 (last run 503 `AUTHORITY_UNAVAILABLE`) |
| Share-only partial reader (50 records) | 50 | 148 | 243 |

**Reading:** the ~10 s p95 values are **failed runs**, not slow answers: roughly 13 of about 120 owner and reader searches did not complete and were refused with 503 at the business API's 10 s Authority timeout, after SpiceDB `ReadRelationships` calls were cancelled at the 5 s SpiceDB timeout (Authority log). No partial result was returned (fail closed). The first report format recorded only the last run's status, which hid them; the benchmark now counts every run's outcome and fails on any availability failure (committed in `e0a36ed`; not re-run). Recorded as R-33. The security-test stack sets `SpiceDb:LookupPageSize=20`, so a 1,000-record caller costs about 100 lookup round trips per search; this was not varied.

**Limitations:** one laptop, Docker Desktop; Vault Transit HMAC and local ES256 instead of Managed HSM; one Authority instance; uniform synthetic data; owner and one 50-record reader only; no concurrency or commit load; not a PB-09 measurement. PERF-01 on Azure staging decides (IX-7).

## 5. Security and integrity invariants (C-133 §6, §10)

| Invariant | How IX-4 preserves it | Evidence |
|---|---|---|
| Current Authority authorization | The business never decides population, readability or matches; every search and page is one Authority decision at an S1 pin (IX-3), re-authorized per page | Engine contract tests; `AnAuthorizationChangeBetweenPagesRefusesTheNextPage` |
| S1 organization quarantine | Refused before either engine runs (capability decision, 423); the indexed engine's 423 is final; the fallback never runs for it (Q2) | `ASearchIndexQuarantineFallsBackToV1ButAnOrganizationQuarantineNeverDoes`; contract `QuarantineLimitAndAuthorizationRefusalsNeverFallBack` |
| Record quarantine | Final; same reason as V1; a record whose business copy fails on open is quarantined and every later search with it in scope fails | IX-31/32 Docker test |
| Signed index integrity; signature ≠ freshness or completeness | Unchanged from IX-3 (I-1, I-2, page seals; freshness checked separately). An index-integrity failure quarantines the index and the user is answered by the complete V1 path or refused | Quarantine fallback Docker test |
| Classification and protection | Unchanged: only approved Public/Internal fields; an encrypted value on a predicate field refuses (`PREDICATE_VALUE_ENCRYPTED`) | Contract mapping test |
| No Confidential or ciphertext-derived content | No new indexed content; the cursor holds the caller's own query, encrypted, in request bodies only | D-IX4-5 |
| Fail closed; no mixed or partial results | Fallback only for index states and only through the complete V1 path; pages never fall back; a page of another result is refused | Contract and Docker tests |
| Tenant isolation, session binding | Cursor bound to organization, user and session; the Authority binds the snapshot to the session | `ACursorIsBoundToTheCallerAndTheSessionAndAnAlteredOneIsRefused`; Docker session check |
| No unauthorized counts or pagination disclosure | Counts and refusals from the Authority, caller-owned only; at the 50,000 ceiling the owner's refusal is `discoverable` while the reader's 50-record scope still answers | Benchmark ceiling step |
| Not presented as verified | `resultBasis`, `projectionVerified=false`, §6.2 statement, no "verified" wording (IX-37) | Contract, Vitest, Playwright, Docker |
| Authority checks never bypassed for performance | No business cache of results or decisions; the benchmark measures the full path | Benchmark |

## 6. Failures, limitations and deviations

### 6.1 Open blocker: plan-shape guarantee (PD-9)

The IX-3 plan-shape test (IX-68) passed on fresh images in IX-3 and fails on the longer-lived stack: PostgreSQL 18 chooses a **B-tree skip scan** of `ix_search_string_postings_value` (organization, field, value, record) for the per-record probe, skipping the value column, instead of the record probe index. The probe stays per record (no unauthorized result), but its cost then grows with the organization-wide number of distinct values, the dependency §8.2 prohibits. PostgreSQL 18 has no setting to disable skip scans. A query-level change (non-indexable field condition) was tried, produced a two-column skip scan of the same index, and was **reverted**. The test is unchanged and failing. Options and recommendation: design §22.4 (drop or redefine the value indexes; both change the approved index set). Register R-32.

### 6.2 Defects found and fixed during IX-4

1. **R-28 script defect:** the first version created the evidence directory before `dotnet test`, which then took the existing directory after `--` as its project ("Specifying a directory for 'dotnet test' should be via '--project'"); the directory is now created after the run (the same message also appears when the project does not compile).
2. **Worker analyzer (CA1873):** the escalation logging passed computed arguments; now passes the `TimeSpan` and enum directly.
3. **Benchmark:** a stale-revision refusal was retried with the same idempotency key, so the API replayed the refusal (failed at record 8); a refused command creates nothing, so the retry now uses a new key. The report hid failed runs (§4); fixed.
4. **Test expectations (no product change):** coverage reset scope; S1 refusal point; PD-3 simulation (§3.2).

### 6.3 Correction to the IX-3 report

The IX-3 report (§3.2) listed `SearchCatalogueTests` **12/12** among the focused regression classes and also said that no 480-record benchmark was run. `SearchCatalogueTests` contains the P-S1-1 benchmark `BenchmarkTheVerifiedScopeAt50And150And480Records` (one of its 12 tests), so **that 480-record benchmark did run during IX-3**, contrary to the C-132 instruction. It is now categorized as a Benchmark and excluded from focused runs (§1.5); it was not run in IX-4.

### 6.4 Limitations

1. Stage B not run (R-30); the IX-4 gate's "Stage B" item is deferred (PD-7).
2. 10K and 50K benchmark steps not run (R-34); local numbers only (R-02, PERF-01).
3. Availability failures in the local benchmark (R-33), not investigated.
4. IX-29 (limits and timeouts) is partial on the real stack: the `results` cap, the `discoverable` ceiling and the `items` limit were not triggered (they need ≥ 10,000 matches, 50,000 discoverable records or 20,000 share-only items); the mapping of every limit refusal is contract-tested and the Authority's limit checks use caller-owned counts only (IX-3).
5. IX-54 (two Authority instances) still partial (R-23).
6. The indexed cursor carries the caller's own predicate values, encrypted (D-IX4-5).
7. French copy drafted, not reviewed (R-27).

### 6.5 Deviations

D-IX4-2 (sessionless callers → V1; approved as PD-6), D-IX4-10 (indexed cursor refused with the engine off), D-IX4-9 (benchmark scope per C-134). None weakens a security property.

## 7. Traceable register of outstanding requirements

R-01 to R-29 are carried from the IX-3 report. Changes: R-22 closed for IX-3 development acceptance only (PD-2); R-06 disposition recorded (design §22.3, still open); R-25 implemented (PD-4); R-27 now has copy to review; R-29 functional run executed (benchmark still not run).

| # | Requirement | Status | Assigned to |
|---|---|---|---|
| R-01 | Azure Managed HSM ES256 seal and HMAC providers | Not implemented; fail closed | **IX-7**; blocks production |
| R-02 | 20 ms p95 incremental commit latency | **FAIL**, carried | **IX-7** ST-PERF-1..7 |
| R-03 | Cross-language cryptographic test vectors | .NET only | Independent cryptographic review |
| R-04 | W3/W4 integration coverage; IX-73 beyond W1 (remaining revision and diagnostic coverage) | Unit only | **Owner decision** |
| R-05 | pgAudit and Azure diagnostic log inspection (IX-28) | Local only | **IX-7** |
| R-06 | P-S1-1 480-record V1 limitation | **Disposition recorded** (design §22.3): open as a V1 limitation, neither fixed nor waived | V1 fix (O-3/O-4) or V1 retirement after V2 acceptance |
| R-07 | Intermittent `RecordCreationIdempotencyTests` | Not run | **Owner decision** |
| R-08 | Signed external checkpoints (a signature is not freshness or completeness) | Not implemented | **Before any real-data use** |
| R-09 | Independent third-party security and cryptographic review | Not started | External; before production |
| R-10 | OBS-IX-1 and R6 | Unauthorized | **Owner decision** |
| R-11 | IX-19 full (restore, G14 replay) | Simulated only | **G14**, **IX-7** |
| R-12 | Step-up on `SearchProjectionRebuild` | Not available | **PB-08** |
| R-15 | Builder and deep-sweep pacing; population and attestation cost at 100K | Unmeasured (local rebuild of 1,000 records: 42 s) | **IX-7** |
| R-16 | Dashboard health of the index | Not built | **Owner decision** |
| R-17 | Builder client certificates outside Development/Testing | Not exercised | **IX-7** |
| R-18 | Query-path tests | Open: IX-46, IX-07 (inherited), IX-54 (R-23), IX-33 (R-24), IX-29 partial (§6.4) | — |
| R-19, R-20 | Stage B on the IX-2 and IX-3 code | Not run | Next owner-authorized release run |
| R-21 | Stage B search shards (IX-3 exit gate) | Not run; synthetic part accepted for development (PD-1) | Next owner-authorized release run |
| R-22 | N for synthetic searches | **Closed for IX-3 development acceptance only** (PD-2: N = 600); production threshold needs separate approval | — |
| R-23 | IX-54 with two Authority instances (multi-instance snapshot invalidation) | Partial | **IX-7** (or a second Authority instance in the stack) |
| R-24 | IX-33 (Tier 2 counts and limits) | Assigned to IX-5 (PD-5); **not completed** | **IX-5** |
| R-25 | Builder escalation | **Implemented** (PD-4) | — |
| R-26 | Page-invalidation rate under continuous edits | Unmeasured | **IX-7** |
| R-27 | PB-14 copy review (English and French) | Copy implemented in English; French drafts §8; **PB-14 not complete** | Owner and PB-14 review |
| R-28 | Unexplained intermittent contract failure | **Open**; every run now keeps TRX evidence and its run id; no recurrence in IX-4 runs | Watch |
| R-29 | `FieldSearchExecutionTests` functional tests on new code | **Executed** 13/13 without the benchmark; the 480-record benchmark itself not run | Benchmark: next authorized performance or release run |
| R-30 | Stage B on the IX-4 code (all groups, browser suite, `Engine=Indexed`) | **Not executed** (PD-7) | Next owner-authorized release run |
| R-31 | Per-organization failure isolation in the builder | Deferred (§1.4) | Operational improvement, separate approval |
| R-32 | Plan-shape guarantee on PostgreSQL 18 (§6.1) | **Open blocker** | **PD-9** |
| R-33 | Availability failures (SpiceDB call cancellations, business 10 s timeout) in the local benchmark (§4) | Not investigated | **IX-7** or owner decision |
| R-34 | Local benchmarks at 10,000 and 50,000 records, and the discover-ceiling refusal | **Not run** (budget guard; C-134 deferral); not passed, not waived | Separately authorized performance validation |
| R-35 | P-S1-1 benchmark ran during IX-3 contrary to C-132 (§6.3) | Recorded; prevented by the Benchmark trait | — |

## 8. French drafts for the PB-14 copy review (not approved; not in the application)

| English (as implemented) | French draft |
|---|---|
| Results reflect the content the Authorization Authority last accepted for each record, at one Authority snapshot. Each returned record's searched values were checked against that accepted content. The business database copy is not checked by search; it is fully verified when you open a record. | Les résultats reflètent le contenu que l'Autorité d'autorisation a accepté en dernier pour chaque dossier, à un même instantané de l'Autorité. Les valeurs recherchées de chaque dossier retourné ont été vérifiées par rapport à ce contenu accepté. La copie de la base de données métier n'est pas vérifiée par la recherche; elle est entièrement vérifiée lorsque vous ouvrez un dossier. |
| The search was complete for the records you can access, evaluated on the content the Authorization Authority last accepted for each record. | La recherche a porté sur tous les dossiers auxquels vous avez accès, évalués selon le contenu que l'Autorité d'autorisation a accepté en dernier pour chacun. |
| The search index could not answer this search, so every record in scope was verified instead. | L'index de recherche ne pouvait pas répondre à cette recherche; chaque dossier visé a donc été vérifié à la place. |
| The search index is not ready — The search index for this form is still being prepared, and this search covers too many records to check one by one, so no results are shown — this does not mean nothing matched. Try again later or narrow the search. | L'index de recherche n'est pas prêt — L'index de recherche de ce formulaire est encore en préparation et cette recherche couvre trop de dossiers pour les vérifier un par un; aucun résultat n'est donc affiché, ce qui ne signifie pas qu'aucun dossier ne correspond. Réessayez plus tard ou précisez la recherche. |
| Search is temporarily unavailable — The search index failed an integrity check and must be rebuilt by an administrator, so no results are shown — this does not mean nothing matched. A narrower search may still work. | La recherche est temporairement indisponible — L'index de recherche a échoué à un contrôle d'intégrité et doit être reconstruit par un administrateur; aucun résultat n'est donc affiché, ce qui ne signifie pas qu'aucun dossier ne correspond. Une recherche plus précise peut encore fonctionner. |
| Search is paused — The search index is paused until its scheduled integrity checks complete, so no results are shown — this does not mean nothing matched. A narrower search may still work; otherwise try again later. | La recherche est suspendue — L'index de recherche est suspendu jusqu'à la fin de ses contrôles d'intégrité planifiés; aucun résultat n'est donc affiché, ce qui ne signifie pas qu'aucun dossier ne correspond. Une recherche plus précise peut encore fonctionner; sinon, réessayez plus tard. |
| A searched field holds protected values — A record in this form holds an encrypted value in a field you searched, so this search cannot give a complete answer and no results are shown — this does not mean nothing matched. | Un champ recherché contient des valeurs protégées — Un dossier de ce formulaire contient une valeur chiffrée dans un champ recherché; cette recherche ne peut donc pas donner de réponse complète et aucun résultat n'est affiché, ce qui ne signifie pas qu'aucun dossier ne correspond. |
| Too many matching records — More records match than one search can return, so no results are shown. Narrow the search: choose a status or add another condition. | Trop de dossiers correspondants — Plus de dossiers correspondent qu'une recherche ne peut en retourner; aucun résultat n'est donc affiché. Précisez la recherche : choisissez un statut ou ajoutez une condition. |

## 9. Recommendation

IX-4 is **not ready for unconditional acceptance**. One blocker:

- **PD-9 (R-32):** the §8.2 plan-shape guarantee fails on PostgreSQL 18 because of B-tree skip scans of the value indexes. It needs an owner decision on the index set (recommended: drop the value indexes, design §22.4). Until then the Tier 1 timing claim of §8.2 (R-IX-1 closed for Tier 1) does not hold on this stack.

Otherwise the IX-4 scope is implemented and verified with focused tests:
- the `Indexed` engine, the IX-D8 fallback, the session-bound cursor and `resultBasis`, with indexed results never labelled verified;
- PD-6 implemented with its boundary tests; PD-7 recorded; PD-8 verified (focused Docker, contract and Playwright tests);
- the IX-3 follow-ups: PD-3 verified by fault injection; PD-4 escalation implemented; R-29 executed; R-28 evidence in place.

Recommended owner actions:
1. decide PD-9;
2. accept IX-4 conditionally once PD-9 is resolved and the plan-shape test passes on a long-lived stack;
3. keep R-30, R-33 and R-34 open for release and performance validation.

No production readiness is implied. IX-5 was not started. Stage B was not executed.

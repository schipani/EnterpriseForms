# Indexed search (Option C): IX-3 follow-ups and IX-4 implementation evidence report (C-133)

- **Status:** the IX-3 follow-ups (`e0e320c`) and IX-4 (`e0a36ed`) are committed (signed, local) on branch `ix-1/authority-search-index`, on top of IX-3 (`cb48ba7`) and the ruleset follow-up (`6428710`); this report is the third commit. Returned for owner acceptance. **One open acceptance blocker: the plan-shape finding (§6.1; design §22.4; PD-9).** **C-135 update: PD-9 remediated and verified (§10); IX-4 returned for conditional acceptance.** **C-136:** bounded seal sweep (§11). **C-137:** IX-4 conditionally accepted at `37ba4f5`; R-39 scheduling verified, R-37 measured, IX-5 stopped at its readiness step (§12). **Not accepted, not independently reviewed, not pushed, not merged, not deployed.** Stage B intentionally not run (C-131).
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
| R-24 | IX-33 (Tier 2 counts and limits) | Assigned to IX-5 (PD-5); **not completed**: IX-5 stopped at the readiness step (C-137, R-40) | **IX-5** |
| R-25 | Builder escalation | **Implemented** (PD-4) | — |
| R-26 | Page-invalidation rate under continuous edits | Unmeasured | **IX-7** |
| R-27 | PB-14 copy review (English and French) | Copy implemented in English; French drafts §8; **PB-14 not complete** | Owner and PB-14 review |
| R-28 | Unexplained intermittent contract failure | **Open**; every run now keeps TRX evidence and its run id; no recurrence in IX-4 runs | Watch |
| R-29 | `FieldSearchExecutionTests` functional tests on new code | **Executed** 13/13 without the benchmark; the 480-record benchmark itself not run | Benchmark: next authorized performance or release run |
| R-30 | Stage B on the IX-4 code (all groups, browser suite, `Engine=Indexed`) | **Not executed** (PD-7) | Next owner-authorized release run |
| R-31 | Per-organization failure isolation in the builder | Deferred (§1.4) | Operational improvement, separate approval |
| R-32 | Plan-shape guarantee on PostgreSQL 18 (§6.1) | **Remediated (C-135, §10):** value-keyed posting indexes dropped; plan-shape test passes on existing and fresh databases; owner acceptance pending | Owner acceptance |
| R-33 | Availability failures (business 10 s timeout) in the local benchmark (§4) | **Mitigated, not closed (C-137 owner decision).** Bounded seal transactions (C-136, §11): longest S-B lock hold 0.69–0.76 s; search max 2.1 s; no timeout in the bounded runs, so no lower rate demonstrated. C-137 §12.4: other organization-lock holders now measured for record creation (waits p50 ≈ 360 ms at 1,000 records under the 10 s test sweep) | **IX-7**: search and save latency with sweeps, builder and other lock holders active; timeout rates on representative data |
| R-34 | Local benchmarks at 10,000 and 50,000 records, and the discover-ceiling refusal | **Not run** (budget guard; C-134 deferral); not passed, not waived | Separately authorized performance validation |
| R-35 | P-S1-1 benchmark ran during IX-3 contrary to C-132 (§6.3) | Recorded; prevented by the Benchmark trait | — |
| R-36 | Seal-pass lock hold also applies in production (default 2,000 entries per pass every 15 minutes) | **Fixed in code (C-136), staging validation outstanding (C-137):** transactions of `SealCheckTransactionSize` (default 50, retained) | **IX-7** measurement on Azure |
| R-37 | Ingestion serialization per organization (authorization-revision CAS, organization gate until Effective, lock waits, graph load per command) | **Open; measured (C-137, §12.4):** serial p50 327 ms at 100 records, 1,845 ms at 1,000 (≈ 0.9 s lock waits; graph load 7 ms); concurrency 4 gives no gain. A/B comparison and revised recommendation (A for bulk; B deferred as a scaling item) in `RECORD_REGISTRATION_THROUGHPUT_R37_RECOMMENDATION.md` §7; nothing implemented. 5,000 records not run (R-41). **C-138:** revised findings recorded (organization-lock contention dominates at ≈ 1,000 records on the development stack; graph loading not the primary bottleneck at that size; bulk registration the preferred future candidate for bulk ingestion; relationship-scoped graph loading a later scaling option); neither implemented; also blocks 100K/250K dataset creation in practical time (annex C, R-43) | **Owner decision** |
| R-38 | Tier 2 index design must not reintroduce a value-keyed index on a record-probed table (PostgreSQL 18 skip scan) | **Reviewed (C-137):** the §9.2 access-posting indexes are value-keyed and the table is also probed by record, including the caller-facing Evaluable-parity check; options A/B/C (`INDEXED_SEARCH_IX5_READINESS_ASSESSMENT.md` §5); recommendation C measured first, then B, not A; no index created. **C-138 (IX5-D2): evaluation approved, not implementation;** C versus B assessed in `INDEXED_SEARCH_TIER2_INDEX_ARCHITECTURE_ASSESSMENT.md` (structural variant C-S; A recorded as rejected; GIN finding UF-15); no selection. **C-139 (owner direction, owner-led): Option B preferred for independent review** with separate access and value structures; C the baseline and fallback; A rejected. Annex A §6–§15: B1 (canonical values + intersection) rejected for timing (value-first and mixed-index plans, PD-9 class); B2 (principal-leading access-scoped values) preferred under SAFE-1 to SAFE-11; no index created; no selection | Independent review, then owner selection |
| R-39 | The bounded seal pass verifies ≈ 17 % fewer entries per second; a pass longer than the interval makes passes run back to back | **Accepted development tradeoff, risk open (C-137).** Verified: no overlapping passes within one instance (`SweepSchedulingTests`); deadlines re-evaluated at query time, so late scheduling never extends acceptance; observed ≈ 90 % lock duty cycle for a 1,040-entry organization at the 10 s test interval (§12.2); no lease across Authority instances (duplicated work, correctness unchanged) | **IX-7** sweep throughput, scheduling and lease decision |
| R-40 | IX-5 (Tier 2) implementation | **Not started beyond the readiness assessment (C-137):** the approved specification (IX-D13, R-B, §17.1, FD-3) requires an independent review of §17.1 and a separate approval before implementation; FD-8: no review has occurred. **C-138: IX5-D1 review-first approved; no exception for a dark implementation.** Review package prepared (`INDEXED_SEARCH_TIER2_REVIEW_PACKAGE.md`, annexes A to E); **not submitted; no review has occurred** | Independent review (FD-8, external), then separate owner approval |
| R-41 | R-37 measurement at 5,000 records | **Not run** (≈ 1.5–2 h of seeding, over the 30-minute limit; would mostly measure R-39 at the test interval) | Owner approval, with the sweep configuration for the run |
| R-42 | Per-organization sweep lease across Authority instances | Not present; duplicated sweep work with more than one instance (design §23.2). Applies to the Tier 2 verifier too (package FS-18) | **IX-7** |
| R-43 | 100K/250K benchmark dataset prerequisites (IX-5 exit gate) | **Designed, not seeded (C-138, annex C):** creation through the trusted path estimated at ≈ 30–60 h (100K) and ≈ 120–200 h (250K) serially without R-37 changes; Vault dev mode (in-memory) prevents a reusable snapshot, so a persistent-Vault benchmark overlay is needed; ≈ 40–60 GB (100K) and ≈ 100–150 GB (250K) disk estimated from measured 58 KB/record; P0 1,000-record pilot proposed | Owner approvals (pilot, overlay, sweep setting, R-37 options, multi-day runs) |
| R-44 | Design-author findings for the Tier 2 reviewer (UF-14 to UF-21): affected-set table does not match the command catalogue; GIN not bounded by principal; option B value-copy integrity; `LookupSubjects` without limit, cursor or bulk; fence starvation and S-D grace; Vault dev mode; minimum-safe-version fencing for Tier 2; unspecified Tier 2 disabled state | **Open; recorded in the review package §9 (C-138)**; not independent findings; no remediation implemented. **C-139:** proposed resolutions recorded for UF-14 (per-command matrix, INV-T2-15), UF-15 (no GIN, `contains` strategy), UF-16 (INV-T2-14, M2-V) and UF-18 (fence rules F-1/F-2); **none closed or waived** | Independent review, then owner decisions |
| R-45 | C-139 design-author findings (UF-22 to UF-27): planner statistics shared across principals and organizations; multi-path field-readability parity; correction of option B's approval-verification cost for the split design; **implemented Tier 1** share-only check set and `items` limit depend on unreadable items (UF-25); value-row amplification and fan-out cap; FD-5 boundary for B2 writes in Tier 1 writer transactions | **Open (C-139); C-140:** UF-28 added (C-path overflow channel; D-5 overflow withdrawn); UF-25 reproduced and moved to R-46; recorded in the review package §9; not independent findings; no code changed (UF-25 concerns implemented Tier 1 and needs a reviewer and owner decision) | Independent review, then owner decisions |
| R-46 | **UF-25, Tier 1:** the share-only check set and the `items` limit (R1 §E.3) depended on the presence and number of items the caller cannot read; results did not | **Corrected (C-141, U-1b), not independently reviewed:** candidates from the caller's own grants, accepted per-field item counts (derivation version 3), limit on readable candidates, Q4 on a SpiceDB denial. RQ-1 to RQ-8 pass (evidence §16): checks 1 → 1 and 200 → 200 with hidden content where C-140 showed 1 → 4 and 200 → 422. Not closed: residuals R-47 to R-50 | Independent review (TQ-24); owner review of the C-141 evidence |
| R-47 | C-141 residual: an inflated stored count is undetectable at query time (record omitted until S-A/S-B quarantine); a coordinated posting-delete-and-count-lower passes I-2 until the keyed page seal or S-B (existing OR-1 limitation) | **Assessed (C-142, UF-25 document section 9.1):** no tampered count releases a partly readable record (exhaustive unit matrix; stack); a raised count only omits, and is detected by S-A (1 h) and S-B (24 h). That is the approved delayed-detection class (OR-1, section 17.10, G06). No correction made; a seal-bound count is an option needing owner approval | Owner: accept delayed detection, or approve a seal-format change |
| R-48 | C-141 residual: a forged version 2 entry was re-derived as a legacy entry instead of quarantining the index | **Corrected (C-142, `aaf8eef`):** authenticated boundary in the append-only, hash-chained journal (`attesting_entry_derivation_version` in Complete attestations). An older entry in such a form is quarantined at query time, by S-A and by the builder, and never upgraded. Genuine legacy forms are still upgraded. Unit and stack tests pass. Rollback-and-return after a version 3 attestation now needs a journaled rebuild | Independent review |
| R-49 | C-141 migration rebuilds the seven record probe indexes non-concurrently (blocks posting writes while it runs); not measured at 100K/250K | **Open** | Before any production deployment: maintenance window or concurrent-build plan, measured |
| R-50 | Existing business-API defect seen in C-141 testing: after an expired delegated assertion (401), the audit insert violates `ck_audit_events_outcome` and the request surfaces as 503 `STORE_UNAVAILABLE` | **Open, not caused by C-141, not fixed** (out of scope) | Owner to schedule |
| R-51 | `IX79IX80SessionEndAndSessionTimeoutsRefuseThePage` (line 650) failed intermittently | **Corrected (C-142, `aaf8eef`), test defect:** the snapshot-existence check raced the 5 s purge job after the session change had committed. The change and the check now run in one transaction, with one changed session asserted; the page refusal is unchanged. Repetitions: UF-25 document section 9.5 | None beyond the repetition evidence |
| R-52 | **New (C-142):** the builder journals every attestation and population attempt for a form that can never complete (for example a form with a quarantined record): about 18 attestations a minute on this stack before C-141, about 110 a minute after the C-141 upgrade returned such forms to Building. Each event needs archiving, so new security commands waited for the archive (`AUTHORITY_NOT_READY`, `AwaitingArchive`), which failed 4 setup steps in one repetition series | **Open:** pre-existing builder behaviour, amplified by the C-141 upgrade policy (it demotes forms that cannot re-attest). Not fixed (outside C-142 scope) | Owner: rate-limit or suppress repeated incomplete attestations, and/or skip the upgrade demotion for forms that cannot re-attest; decide before any deployment |

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

## 9. Recommendation (C-134; superseded by §10.6)

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

## 10. C-135: PD-9 remediation, R-33 and ingestion diagnostics

### 10.1 PD-9 change

- **Indexes removed (8):** `ix_search_string_postings_value`, `ix_search_integer_postings_value`, `ix_search_decimal_postings_value`, `ix_search_date_postings_value`, `ix_search_date_time_postings_value`, `ix_search_boolean_postings_value`, `ix_search_option_postings_value` (B-tree, organization_id, field_definition_id, value, form_record_id) and `ix_search_string_postings_trigram` (GIN on normalized_string_value).
- **Dependency verification:** design §22.5 — no approved Tier 1 query, maintenance path, constraint, integrity or authorization check uses them; Tier 2 does not expect them. No dependency found, so no alternatives were needed.
- **Migration:** `20261108133232_AuthoritySearchIndexIx4DropValueIndexes` (forward-only, `DROP INDEX IF EXISTS`; `Down` refuses). Model snapshot updated; earlier migrations unchanged; no data changed.
- **Code:** model configuration (value indexes removed, documented as forbidden); Testing-only `analyze` option on the candidate-plan endpoint (`EXPLAIN (ANALYZE, BUFFERS)`); no product query changed (the probe template is the IX-3 template, unchanged).
- **Not done (by instruction):** no replacement index, no global planner setting, no hint, no change to the plan-shape assertions, classification rules, PD-3, PD-4, PD-6, PD-7 or PD-8.

### 10.2 PD-9 verification (PostgreSQL 18.0)

| Step | Database | Result | Run id |
|---|---|---|---|
| Unchanged plan-shape test **before** the migration | existing (long-lived) | **Fails** (`ix_search_string_postings_value`, skip scan) — reproduces the finding | direct run, 27 s |
| Migration applied | existing | `ef_migrations_history` latest `20261108133232_AuthoritySearchIndexIx4DropValueIndexes`; posting indexes 21 (3 × 7), value or trigram 0 | — |
| `IndexedSearchQueryStackTests` (unchanged plan-shape test, new PD-9 test, PD-3, shadow differential and the rest) | existing | **21/21** | `20261108-083828-47dc879f` |
| `IndexedFieldSearchEngineStackTests` | existing | **5/5** | `20261108-084901-666148db` |
| Migration applied | **fresh** (`down -v`) | same history entry; value or trigram 0; 21 | — |
| `IndexedSearchQueryStackTests` | fresh | **21/21**; shadow differential 600/600, 0 unexplained differences | `20261108-090221-dd321893` |
| `IndexedFieldSearchEngineStackTests` | fresh | **5/5** | `20261108-091331-499ca115` |
| Authority unit / architecture / contract / integrity | — | **436/436, 95/95, 569/569, 60/60** (architecture adds `NoPostingIndexIsKeyedOnAValueColumn`) | `artifacts/test-runs/pd9-*` |

**Plans** (`artifacts/ix-4/pd9-plans.json`, both databases): 74 `EXPLAIN (ANALYZE, BUFFERS)` plans each — 9 predicate shapes (string equals, prefix, contains; integer equals, between; skewed date; option is; boolean; a three-condition AND) × 2 scopes (40 and 5 records) × 4 statistics scenarios (as analyzed; `n_distinct = 2`; `n_distinct = -1`; statistics target 1, each followed by `ANALYZE`), plus the indicator. Data: unique references, two exhibit counts, a 90 % skewed date, Boolean and option values. In every plan: node types only Limit, Nested Loop, Function Scan (the scope), Subquery Scan, Result, Index Scan; indexes only `…_postings_record` and `uq_search_record_entry_record`; every index condition includes `form_record_id`. No skip scan of any value index is possible: none exists. Indicator (5-record scope, shared buffers before and after 120 out-of-scope records with distinct values): existing database 35 → 30, fresh database 20 → 30 (B-tree growth from a near-empty table); an indicator, not a proof.

**Residual (stated, C-135 §3):** the guarantee is now structural for value access; the remaining record-keyed probe still relies on the IX-3 per-statement removal of hash, merge, bitmap and sequential plans, which is part of the approved §8.2 design. A future index added to a posting table would be caught by the architecture test (model) and the PD-9 structural check (database).

### 10.3 R-33: search timeouts (bounded diagnostics)

| Probe | Workload | Result |
|---|---|---|
| Small organization (`IndexedSearchTimeoutDiagnosticsTests`, run `20261108-085147-9fc4c5df`) | 170 records; 40 searches right after ingestion, 40 after 60 s quiet | 80/80 answered; p50 ≈ 200 ms, max 2.4 s |
| Existing 1,000-record benchmark organization (two runs, no seeding) | 40 sequential searches each | **2/40 failed in each run** (503 `AUTHORITY_UNAVAILABLE` at ≈ 10 s); answered p50 584 and 619 ms; searches queued behind a failure 3.6 to 7.1 s |
| Lock holder sampling (`pg_stat_activity`, `pg_blocking_pids`, every 0.3 s) | during the second probe | one Authority transaction **idle in transaction / active for up to 15.7 s**, reading entries and postings and updating `last_seal_verified_on` (the S-B seal pass), holding the organization row; waiting sessions on `organization_authorization_states … FOR UPDATE` (Lock:transactionid, tuple) |

**Answers to C-135 §4:**
- *What consumes the 10 s:* waiting for the organization lock in the search's final transaction while the seal pass holds it.
- *Why SpiceDB calls reach a timeout:* they do not time out on their own; the business API's 10 s timeout cancels the Authority request and its in-flight SpiceDB call is reported as Cancelled.
- *Enumeration, batching, pooling:* not the cause in these probes (Authority-side elapsed of completed searches p50 ≈ 400 ms; `SpiceDb:LookupPageSize=20` in the security-test stack adds round trips but no timeout); database contention is (organization row lock).
- *Concurrent load:* not required (sequential searches, no ingestion).
- *Latency figures:* the original benchmark's p95 included failed runs (§4); the current reports separate answered and all runs.
- *Retries:* the engine retries only on a changed snapshot or not-ready pin, never on a timeout, so no amplification from search; ingestion retries do amplify load (below).
- *Remediation recommendation:* design §22.6 (bounded seal-pass transactions); no timeout change; not implemented.

### 10.4 Ingestion

Preserved: 944 s for 1,000 records (§4). Diagnostic (170 records): 20 records serially at 2.48/s (p50 367 ms); 150 at concurrency 4 at 2.41/s (p50 1,555 ms; p50 7 attempts; 124 `STALE_AUTHORIZATION_REVISION`, 812 `AUTHORITY_NOT_READY`). Likely bottleneck: the per-organization authorization revision (every creation is a revision-bound security command, then gated on projection and archive), plus the whole-graph read per command (cost grows with organization size) and the seal-pass lock waits. Next step: design §22.6 (not implemented).

### 10.5 Not run

Stage B; the 480-record benchmarks; the 10,000 and 50,000-record benchmarks; no other long benchmark (the diagnostics took 162 s and about 60 s per probe).

### 10.6 Recommendation

IX-4 is **ready for conditional owner acceptance**: the PD-9 blocker is remediated structurally and verified on PostgreSQL 18 on an existing and a fresh database, with the plan-shape test unchanged and passing. Conditions to record: R-33/R-36 (seal-pass lock hold; remediation proposal awaiting approval), R-37 (ingestion serialization), R-30 and R-34 (Stage B and the 50K benchmark, deferred), R-27 (PB-14), and the carried production items (Managed HSM, signed external checkpoints, independent review, OBS-IX-1/R6, G14, the 20 ms p95 gate FAIL, cross-language vectors). No production readiness implied; IX-5 not started; Stage B not executed.

## 11. C-136: bounded seal-sweep transactions (R-33/R-36)

### 11.1 Change

Design §22.7: the S-B pass runs in transactions of `SealCheckTransactionSize` entries (default 50), each under the organization lock and committed separately; entries already attempted in the pass are not re-selected; projection state and approvals are re-read per transaction; cancellation is checked between transactions; per-entry timestamps remain the only unit of completion. Event 9305 logs the S-A and S-B lock hold. Files: `SearchIndexSweepService.cs`, `SearchIndexOptions.cs`, `ISearchIndexStore.cs`, `SearchIndexStore.cs` (selection with exclusion), `SearchIndexComposition.cs` (validation, logging), `compose.security-test.yaml` (`IX4_SEAL_TRANSACTION_SIZE`), tests below. No migration.

### 11.2 Focused tests (C-136 §4)

| # | Requirement | Test | Result |
|---|---|---|---|
| 1 | Search during active sweeps | Measurement (§11.3): 40 searches per configuration while passes ran; IX-3 and IX-4 classes on the bounded sweep | 120/120 answered; classes pass |
| 2 | Saves during active sweeps | Measurement: 1,000 seeding saves and 10 saves per configuration while passes ran | All succeeded |
| 3 | Record mutation between batches | Unit `ARecordCommittedBetweenTransactionsIsVerifiedAgainstItsNewHead` | Pass |
| 4 | Organization quarantine during a sweep | S1 is independent of S-B (§17.2 Q3): the pass neither reads nor changes it; unit `AFailInALaterTransactionQuarantinesTheIndex…` asserts S1 untouched; `ProjectionIntegrityRollbackTests` were not affected by this change (not re-run) | Pass (unit) |
| 5 | Record (and index) quarantine during a sweep | Unit `AQuarantineBetweenTransactionsEndsThePassAsInconclusiveWithoutVerifyingTheRest`; record-quarantine handling inside an entry is the unchanged `VerifyEntryAsync`, covered by IX-1/IX-2 classes (re-run: 9/9, 11/11) | Pass |
| 6 | Cancellation and restart | Units `AnInterruptionBetweenTransactionsAdvancesOnlyTheCommittedEntriesAndTheNextPassResumes`, `ACancellationRequestedBetweenTransactionsStopsBeforeTheNextOne`; real restarts: the Authority was recreated three times during the measurement while passes ran | Pass |
| 7 | Failed batch and retry | Unit `AFailInALaterTransactionQuarantinesTheIndexAndAdvancesOnlyTheEntriesVerifiedBefore`; interruption test (a transaction that throws writes nothing; the next pass resumes) | Pass |
| 8 | Incomplete sweep crossing a deadline | Unit `ADeadlineIsEvaluatedFromTheOldestUnverifiedEntryOfAnInterruptedPass` (S-B overdue, index suspended) | Pass |
| 9 | Completion across transactions | Units `APassVerifiesEveryEntryOnceInBoundedTransactions` (7 entries, 3 transactions, each entry once), `AnInconclusiveEntryIsNotSelectedAgainInTheSamePassAndKeepsItsTimestamp` | Pass |
| 10 | Indexed-query invariants | `IndexedSearchQueryStackTests` 21/21 (plan shape, PD-9, PD-3, shadow 600/600), `IndexedFieldSearchEngineStackTests` 5/5 | Pass |

Suites on the final code: Authority 444/444 (8 new), architecture 95/95, integrity 60/60, contract 569/569 (`artifacts/test-runs/c136-*`). Docker on rebuilt images: `SearchIndexFoundationTests` 9/9 (`20261108-093920-07ab43d9`), `SearchIndexBuilderStackTests` 11/11 (`20261108-094228-688f1d3f`), `IndexedSearchQueryStackTests` 21/21 (`20261108-094710-83ffa7dd`), `IndexedFieldSearchEngineStackTests` 5/5 (`20261108-095635-87dff64e`).

### 11.3 Bounded measurement (`SealSweepLockMeasurementTests`, run `20261108-095801-76a077b9`, 759 s, AC power)

The 1,000-record dataset of the IX-4 benchmark was not available (removed by the C-135 fresh-database verification), so one synthetic organization of 1,000 records was created serially: **506 s, 1.98 records/s, save p50 363 ms, p95 1,841 ms**. Then, for each transaction size, the Authority alone was recreated with it and measured over at least two sweep cycles (sweep every 10 s in this stack). Lock hold is the duration of each locked S-B transaction for this organization (event 9305).

| S-B transaction size | Passes | Transactions per pass | Longest locked transaction (ms) | Locked time per pass (ms) | Entries/s | Searches (40) | Search p50 / p95 / max (ms) | Saves (10) p50 / p95 (ms) |
|---|---|---|---|---|---|---|---|---|
| 2,000 (former single transaction) | 3 | 1 | 9,564 / 10,277 (p50 / max) | 9,564–10,277 | 102.8 | 40 answered, 0 timeouts | 462 / 2,023 / **9,843** | 387 / 1,150 |
| 200 | 3 | 6 | 2,213 / 2,551 | 10,332–10,352 | 99.8 | 40 answered, 0 timeouts | 488 / 2,200 / 2,504 | 527 / 4,254 |
| **50 (default)** | 2 | 21 | **685 / 762** | 11,027–12,563 | 84.8 | 40 answered, 0 timeouts | 504 / 1,198 / **2,135** | 838 / 2,863 |

S-A (cheap pass) held the lock 20–44 ms in every configuration.

**Comparison with the C-135 baseline** (≈ 15.5 s sampled lock hold; 2 of 40 searches failed, run on the earlier 1,000-record organization):
- **Lock contention is bounded:** the longest organization-lock hold of the seal sweep fell by a factor of about 13 (≈ 10 s → < 0.8 s on the same data), below the business API's 10 s timeout by an order of magnitude; the worst search latency during sweeps fell from 9.8 s to 2.1 s.
- **Timeout rate not demonstrated:** this time no search timed out in any configuration, the single-transaction baseline included (its worst search, 9.8 s, came within 0.2 s of the timeout). 40 searches per configuration cannot show a change in a ≈ 5 % failure rate. R-33 is therefore reported as **mitigated, not closed**.
- **Costs:** about 17 % lower verification throughput and a longer pass; save latency during sweeps is noisy at 10 saves per configuration and does not show a consistent effect (saves also wait on projection and archive, R-37).
- **Remaining sources of search latency** (max 2.1 s at size 50): not attributed in this run; other organization-lock holders (commits, the builder, deadline evaluation) were not sampled.

### 11.4 R-37

Not implemented. Recommendation: `docs/architecture/RECORD_REGISTRATION_THROUGHPUT_R37_RECOMMENDATION.md` (measure the split first; then option B — restrict the command planner's graph read — and option A — a bounded batched registration command for bulk ingestion; never relax the Effective wait).

### 11.5 Status

- R-33: mitigated (bounded lock hold measured); open for owner review and staging confirmation.
- R-36: remediated in code; production sizing at IX-7.
- R-37: open (recommendation only). R-39 new (throughput cost, back-to-back passes in the security-test stack).
- Not run: Stage B, the 480-record benchmarks, the 10K and 50K benchmarks.
- IX-5 not started.


## 12. C-137: IX-4 conditional closure, R-39 scheduling, R-37 measurement, IX-5 readiness

### 12.1 Owner decision recorded

IX-4 conditionally accepted (`37ba4f5`); R-33 mitigated, not closed; R-36 fixed in code, staging validation outstanding; R-37 measurement only; R-39 an accepted development tradeoff, risk open; IX-5 within its approved specification (decision log C-137; design §23). Owner-led; not an independent assessment.

### 12.2 R-39: no overlapping passes

`SweepSchedulingTests.APassLongerThanTheIntervalIsFollowedBackToBackAndNeverOverlapsTheNextPass` runs the real `SearchIndexSweepLoop` against the in-memory store with a 1 s interval and a seal pass of ≈ 1.35 s: at most one seal selection was in progress at any time, and at least two complete passes ran (passes back to back). Analysis (design §23.2): one sequential loop per Authority instance; queries re-evaluate deadlines themselves, so delayed scheduling never extends acceptance; across instances there is no lease (duplicated work, correctness unchanged); continuous execution in the security-test stack observed directly after the measurement below: for the 1,040-entry organization, passes ended ≈ 11.5 s apart and each held the lock for 10.4–13.9 s in total (21 transactions, longest 0.67–1.34 s), a lock **duty cycle of about 90 %**.

### 12.3 R-37: instrumentation

Diagnostics off unless `Diagnostics:OperationTiming` (Development/Testing only; startup refusal elsewhere, tested in both hosts): business event 7310 (record-creation phases, status polls, outcome), Authority events 9306 (security command: organization-lock wait, graph load and edge count, plan, commit, outcome) and 9307 (content prepare and commit: lock wait, duration, outcome). Archive and projection waits come from AuthorityDb timestamps (`command_created_on` → `journal_archived_on` → `command_effective_on`). No payload, value, actor or record identifier is logged. Tests: `OperationTimingTests` (Authority: committed, replayed and refused outcomes reported, revision unchanged by observing; registration only in Development/Testing) and `OperationTimingSeamTests` (business).

### 12.4 R-37: bounded measurement (`RecordRegistrationThroughputMeasurementTests`, run `20261108-175237-3e14cb8b`, 857 s, AC power)

Fresh security-test stack, images rebuilt from the working tree on `37ba4f5`; one synthetic organization, one approved Internal search field (each content commit also derives and seals an index entry); seal sweeps active (10 s interval, transaction size 50). 1,040 records created through the public API. Each window: 40 creations serially, then 40 at concurrency 4. Report: `artifacts/r-37/record-registration.json`.

| Window | Records/s | Client p50 / p95 (ms) | Attempts mean (max) | Refusals |
|---|---|---|---|---|
| 60→100, serial | 2.62 | 330 / 472 | 1 (1) | none |
| 100→140, concurrency 4 | 2.57 | 1,330 / 3,134 | 7.8 (19) | 252 `AUTHORITY_NOT_READY`, 20 `STALE_AUTHORIZATION_REVISION` |
| 960→1,000, serial | 0.65 | 1,847 / 2,798 | 1 (1) | none |
| 1,000→1,040, concurrency 4 | 0.74 | 5,100 / 7,641 | 16.7 (33) | 580 `AUTHORITY_NOT_READY`, 47 `STALE_AUTHORIZATION_REVISION` |

**Phase split, serial windows (p50 ms; business phases per creation, Authority phases per call):**

| Phase | ≈ 100 records | ≈ 1,000 records |
|---|---|---|
| Form load, validation, fingerprint, business row, head projection | 14 | 14 |
| Permission check (create) | 16 | 17 |
| Permission check returning the authorization revision | 15 | 16 |
| `RegisterRecord` submit (round trip) | 14 | 387 |
| — of which organization-lock wait | 1.5 | 364 |
| — graph load (edges) | 1.4 (165) | 7.2 (1,965) |
| — plan / commit | 2.1 / 4.7 | 3.3 / 7.6 |
| Effective wait seen by the business API (status polls p50) | 164 (2) | 513 (4) |
| — Authority: created → archived | 123 | 46 |
| — Authority: archived → projected and Effective | 24 | 438 |
| Content head read | 19 | 21 |
| Content prepare + commit (business view) | 72 | 709 |
| — prepare lock wait / commit lock wait | 1.6 / 1.5 | 172 / 359 |
| **Total** | **327** | **1,845** |

**Findings:**
1. **At 1,000 records the time is organization-lock waiting, not graph loading.** About 0.9 s of the 1.85 s median is spent waiting for the lock in the security command, content prepare and content commit, plus the projection step's own wait (archived → Effective grew from 24 to 438 ms). The sweep's ≈ 90 % lock duty cycle at this size (§12.2) explains a mean wait of about half a 0.7 s transaction. The graph load is linear in edges (≈ 4 µs per edge: 1.4 ms at 165 edges, 7–10 ms at about 2,000) and is ≈ 0.5 % of the creation time at 1,000 records.
2. **The duty cycle is an artefact of the security-test sweep interval (10 s)**, not of production (900 s; at most `SealCheckBatchSize` = 2,000 entries per pass, ≈ 24 s locked per 900 s, about 3 %). The measurement therefore over-states lock contention relative to production settings and was not repeated with a longer interval (C-137 §2: no interval change for benchmarks).
3. **Small-organization floor (≈ 330 ms):** about half is the Effective wait — the archive step (≈ 120 ms, including the work loop's 250 ms idle poll) and projection (≈ 25 ms), observed by the business API through status polls with backoff (60, 90, 135 ms…) — then the content prepare and commit (≈ 70 ms) and three Authority round trips for decisions and the head (≈ 50 ms).
4. **Concurrency adds no throughput** (2.62 → 2.57 and 0.65 → 0.74 records/s): while one registration is not yet Effective, the organization gate refuses other protected operations (`AUTHORITY_NOT_READY`, the majority of refusals, including content prepares and commits), and concurrent registrations collide on the revision compare-and-swap (`STALE_AUTHORIZATION_REVISION`). This is the intended fail-closed serialization (CROSS_SYSTEM_CONSISTENCY_PROTOCOL), not a defect.

**Not run:** 5,000 records. Seeding the extra ≈ 4,000 records at ≈ 0.5–0.65 records/s would take about 1.5–2 hours, over the 30-minute limit (C-137 §4); at that size the security-test sweep would hold the lock continuously, so the run would mostly measure R-39. Needs owner approval, preferably with a decision on the sweep configuration for the run.

### 12.5 Focused tests (C-137 §6; final code)

| Suite | Result |
|---|---|
| Authority unit (`AuthorizationAuthority.Tests`), including `SweepSchedulingTests` and `OperationTimingTests` | 452/452 |
| Architecture | 95/95 |
| Integrity | 60/60 |
| Contract (`EnterpriseForms.Api.ContractTests`), including `OperationTimingSeamTests` | 575/575 |
| Docker, rebuilt images: `SearchIndexFoundationTests` | 9/9 (`20261108-180949-723db388`) |
| `SearchIndexBuilderStackTests` | 11/11 (`20261108-181336-eb46ad1b`) |
| `IndexedSearchQueryStackTests` (plan shape, PD-9, PD-3, shadow comparison) | 21/21 (`20261108-181908-da3ef439`) |
| `IndexedFieldSearchEngineStackTests` | 5/5 (`20261108-182812-ea2ae5e1`) |
| SEC-P01..P12 Stage A suite (the security-command and content paths were instrumented) | 13/13 (the evidence validator was not run) |
| R-37 measurement (Benchmark) | 1/1 (`20261108-175237-3e14cb8b`) |

**Not run:** Stage B (release-only, C-131; R-30), the 480-record benchmarks, the 10K/50K benchmarks (R-34), the 5,000-record R-37 window (R-41), Playwright (no UI change), any Tier 2 test (none exists; R-24, R-40).

### 12.6 Security invariants

The diagnostics observe and never decide: no decision, ordering, transaction boundary, revision check or Effective gate changed (the business handler's split submit and wait is the body of `SubmitAndAwaitAsync`); the observers are absent unless configured and refused outside Development/Testing; nothing confidential is logged. The SEC-P suite and the IX Docker classes pass unchanged on the instrumented code. No Tier 2 table, index or query path exists.

### 12.7 Status

- IX-4: conditionally accepted (C-137).
- R-33 open (mitigated); R-36 staging outstanding; R-37 open (measured; recommendation revised); R-38 reviewed, owner decision IX5-D2; R-39 open (accepted tradeoff; scheduling verified); R-40 to R-42 new.
- IX-5: readiness assessment only (`INDEXED_SEARCH_IX5_READINESS_ASSESSMENT.md`); awaiting IX5-D1. IX-6 and IX-7 not started.

## 13. C-138: IX-5 readiness acceptance and independent review preparation

- **Owner decision (C-138, owner-led):** C-137 report and readiness assessment accepted; `1708c0c` and `1868e16` accepted; IX-5 not implemented and not accepted. IX5-D1 review-first (no dark-implementation exception); IX5-D2 to IX5-D6 approved as evaluation or design work only (decision log C-138).
- **Delivered (documentation only):** `INDEXED_SEARCH_TIER2_REVIEW_PACKAGE.md` (threat model, invariants INV-T2-01 to 11, PostgreSQL 18 timing risks, SpiceDB requirements SC-1 to 7, findings UF-14 to UF-21, failure scenarios FS-01 to 20, rollback, stage gates, TQ-1 to TQ-12, evidence E-1 to E-12) and annexes A (C versus B), B (14-day sampling proposal), C (100K/250K dataset design), D (7-day M2 assessment, M2-E/M2-S split option) and E (`LookupSubjects` interface and cost model). Basis facts checked by the design author: the vendored SpiceDB v1.56.2 `LookupSubjects` has no limit, no cursor and no bulk form and may repeat subjects; the command catalogue at `1868e16` (UF-14); storage of the security-test stack (1,637 records: AuthorityDb 59.4 MB, BusinessDb 19.6 MB, SpiceDB 16.8 MB).
- **Not done:** no Tier 2 code, schema, test or client; no dataset seeded; no benchmark; no test run (documentation only); not submitted to a reviewer; no review has occurred. Stage B not executed; IX-6 and IX-7 not started.
- **Register:** R-37, R-38, R-40 and R-42 updated; R-43 and R-44 added; nothing closed or waived.

## 14. C-139: IX-5 architecture direction and review finalization

- **Owner direction (C-139, owner-led; not an independent review):** Option B preferred for independent review, with separate access and searchable-value structures; Option C the comparison baseline and possible fallback; Option A rejected unless separately approved. An architecture direction, not implementation approval (decision log C-139).
- **Delivered (documentation only):**
  * annex A §6–§15: the access index; the command-impact matrix over every contract kind (`UpdateShareScope` declared but unimplemented; no record-deletion command); B1 rejected for timing; B2 with duplication rules D-1 to D-6, the multi-path parity rule and conditions SAFE-1 to SAFE-11; the sharing correctness matrix (14 cases); fence rules F-1 to F-3; the scaling model at 100K, 250K and 1M per organization and across organizations; the final comparison;
  * review package: threats T2-16 to T2-20, invariants INV-T2-12 to 15, findings UF-22 to UF-27 and the status of UF-14 to UF-21, failure scenarios FS-21 to FS-25, questions TQ-13 to TQ-20, evidence E-13 to E-15;
  * annexes B (S-23 to S-28, value-row controls), C (fan-out dimensions; 1M not seeded), D (M2-V) and E (approval-cost correction, F definitions).
- **Basis checked by the design author** (not independent): `SecurityCommandKind` and the `CommandPlanner*.cs` dispatch and edge writes; `authority.schema.zed`; the Tier 1 share-only check set (`IndexedSearchService`, `SearchQueryReader.ReadPredicateItemsAsync`); posting derivation inside the content commit (`ContentRevisionCoordinator`, W1).
- **Not done:**
  * no Tier 2 code, tables, migrations or `LookupSubjects` client;
  * no dataset seeded; no benchmark or test run (documentation only);
  * not submitted to a reviewer, and no independent review has occurred;
  * Stage B not executed; IX-6 and IX-7 not started;
  * no push, PR, merge or deploy.
- **Register:** R-38 and R-44 updated; R-45 added; nothing closed or waived. Every scaling figure in annex A §13 is an estimate or extrapolation unless stated as measured.

## 15. C-140: B2 for review; UF-25 investigation

- **Owner decisions (C-140, owner-led; not an independent review, not IX-5 implementation approval):** B2 preferred for the independent review; C baseline and fallback; six proposed decisions recorded in the review package §14A, none waiving a gate (decision log C-140).
- **UF-25 (implemented Tier 1) reproduced**, focused class `IndexedSearchShareOnlyItemChannelTests` (C-131 focused development class; not a Stage B run):
  * runs `20261109-054439-3d97fe06` (1/1) and `20261109-054736-a619042e` (2/2), on the security-test stack at `22732d8` plus the test-only compose variable;
  * the Authority was recreated with `MaximumShareOnlyItems = 2` for the deterministic test and restored to 20,000 (verified).
- **Results, identical for direct, team and overlapping shares:**
  * the check set grew from 1 to 4 items while the caller's readable items stayed at 1;
  * the same query by the same caller went from 200 to 422 `SEARCH_VERIFICATION_LIMIT` (`items`);
  * with the default limit, results were unchanged;
  * the parity rule's result-level effect was observed (an added unshared selection removes a partly shared record).
- **Informational timing** (Benchmark trait, single run, **on battery**): p50 119.6 → 162.1 ms and minimum 100.2 → 132.5 ms for 1,001 versus 1 checked items, with identical results.
- **Correction U-1** proposed with RQ-1 to RQ-8 (`INDEXED_SEARCH_UF25_SHARE_ONLY_ITEM_CHANNEL.md`); **not implemented** (it changes approved R1 §E.3 behaviour).
- **Not done:** no Tier 2 code, tables or migrations; no product code change; no large seeding or benchmarks; Stage B not run; no IX-6/IX-7; not submitted to a reviewer; no push, PR, merge or deploy.
- **Register:** R-45 updated; R-46 added (UF-25 Tier 1 channel); nothing closed or waived.

## 16. C-141: UF-25 correction (U-1b) implemented

- **Owner authorization (C-141):** correct UF-25 with U-1b; preserve search semantics; no Tier 2; focused tests only; Stage B not run; no large benchmarks.
- **Implementation and analysis:** `INDEXED_SEARCH_UF25_SHARE_ONLY_ITEM_CHANNEL.md` §8 (pre-implementation review of the count, changes, RQ-1 to RQ-8, measurements, residuals).
- **Runs (focused, local Docker, synthetic; not Stage B):**
  * unit `c141-unit-20261109-191737` (461/461);
  * architecture `c141-arch-20261109-192932` (97/97);
  * integrity (60/60) and API contract (575/575);
  * stack `20261109-222430-39ae5268` (`IndexedSearchShareOnlyItemChannelTests`, 5/5, Benchmark trait excluded);
  * existing Tier 1 stack classes as listed in the UF-25 document §8.4.

  The first stack run, `20261109-192842-d7c0bf70`, was 3/5: one wrong expectation, and one failure caused by host sleep 00:32–03:19Z (R-50 observed afterwards).
- **Results:**
  * hidden statuses in three share-only records leave the probe at 1 check and the search at 200 (C-140: 1 → 4 items checked, 200 → 422), for direct, team and overlapping paths;
  * the limit refuses on readable candidates only;
  * lowered, raised and missing counts, version 2 entries and a SpiceDB denial all fail closed;
  * the partially shared multi-select semantics are unchanged.
- **Measurements (AC power, informational):**
  * item checks with 1,000 hidden items: 1,001 (before) → 1 (after);
  * p50 163.0 → 111.6 ms in the hidden-items phase;
  * commit latency indexed p50/p95 40.88/54.20 → 37.86/51.90 ms, database commands 16 → 16;
  * **R-02 remains FAIL.**
- **Existing classes:** engine 5/5, foundation 9/9, builder 11/11, execution 13/13; query 20/21 twice (R-51).
- **Register:** R-46 corrected (not closed); R-47 to R-51 added; nothing waived.

## 17. C-142: R-47, R-48, R-51, snapshot lifecycle, FD-5-ALT

- **R-47:** assessed, no change (UF-25 document §9.1).
- **R-48:** corrected with a journal-anchored derivation boundary (§9.2).
- **R-51:** corrected as a test defect (§9.3).
- **Snapshot lifecycle:** single completion restored, strict fitness test (§9.4).
- **FD-5-ALT:** specified for the review (package §14A.2); owner checklist §14B.
- **Code commit:** `aaf8eef`.
- **Runs (focused; not Stage B):**
  * unit `c142-unit-20261110-100000` (465/465);
  * architecture (97/97);
  * `IndexedSearchShareOnlyItemChannelTests` 5/5 twice (`20261110-100304-98f9a3f5`; fresh stack `20261110-103241-ec02cc35`);
  * IX79/IX80: 16 of 16 assertion executions passed, plus 4 setup failures caused by R-52.
- **Register:** R-47 assessed; R-48 and R-51 corrected; R-52 added; nothing closed or waived. **R-02 remains FAIL.**

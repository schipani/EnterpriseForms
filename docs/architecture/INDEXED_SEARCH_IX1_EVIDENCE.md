# Indexed search (Option C): IX-1 implementation evidence report (C-126)

- **Status:** IX-1 implemented on local branch `ix-1/authority-search-index`.
  * C-127: the owner **conditionally accepted** IX-1 for functional and security progress and **withheld final acceptance** until the 20 ms p95 commit-latency gate is met.
  * The remediation the owner authorized is in section 6, and the gate is **still not met** (section 6.4).
  * C-128: the owner **approved the completed round-trip optimization**, directed that no further speculative local optimization be made, kept the 20 ms p95 requirement unchanged and recorded that the local evidence does not establish it. The gate status stays **FAIL**.
  * Returned for owner acceptance with a recommendation for conditional acceptance (section 6.9). **IX-1 is not accepted.** Not independently reviewed, not merged, not pushed.
- **Authorization:** C-126 (owner): ES256 seals (C-125 option 4b) and IX-1 implementation. IX-2 to IX-7 are not authorized; Tier 2 is design-only; OBS-IX-1 remediation and R6 are separate; signed external checkpoints remain mandatory before real-data use; M4-1 open; M4-2, G14 and RCF-1 unauthorized.
- **Data:** synthetic only. The index runs in **Dark** mode (no query path) and only on the Testing stack. Every other environment keeps the default **Disabled** mode, which creates no index rows.
- **Kind:** implementation evidence for an owner-led review. Not an independent assessment.

## 1. What IX-1 contains

| Area | Implemented | Where |
|---|---|---|
| Seal format | `DynamicForms/SearchEntrySeal/v1` encoder; ES256 verification; pinned-key import (P-256 SPKI only) | `src/shared/EnterpriseForms.Integrity/Search/`; spec `docs/dynamic-forms/search-entry-seal-format.md` |
| Test vectors | 3 message vectors, 9 signature vectors (positive and negative), 7 encoding-failure vectors, synthetic test key | `trust_authority/search-entry-seal-v1.test-vectors.json` |
| Normalization | `SearchValueNormalization/v1` (NFC, trim, invariant upper case) with parity tests against the C7 evaluator semantics | `SearchValueNormalizationV1.cs` |
| Schema | `authority_search`: projection states, coverages, entries, seven typed posting tables, anomalies, form states; composite foreign keys from postings and anomalies to their entry and operation; check constraints; value indexes; shared trigram GIN | migration `*_AuthoritySearchIndexIx1`; `SearchIndexConfigurations.cs` |
| Roles and grants | Runtime: `SELECT, INSERT, DELETE` on derived tables, column-limited `UPDATE` of the three S-B timestamp columns, `SELECT, INSERT, UPDATE` on state tables, no DDL. `authority_search_reader`: NOLOGIN, SELECT-only, `default_transaction_read_only = on`. Logging settings (`log_parameter_max_length = 0`, `…_on_error = 0`, `log_error_verbosity = terse`, `log_statement = none`) on both roles | `AuthorityRuntimeGrants.SearchIndexStatements` |
| RLS | `ENABLE` and `FORCE` on the 9 value-bearing tables; policy on the transaction-local organization context | migration |
| Derivation | `SearchPostingDerivation/v1` inside `ContentService.CommitAsync`, on the success path only, from the bytes the commit verified; ES256 seal through the pin-checking gate | `SearchPostingDerivation.cs`, `SearchIndexWriter.cs` |
| ES256 provider port | `ISearchSealSigner`: local (tests), Vault Transit `ecdsa-p256` (Docker), Managed HSM fails closed (not implemented) | `SearchSealSigners.cs`, `SearchIndexComposition.cs` |
| Pinning | Public keys only from configuration; the signer is used only after its public key equals the pin; each signature is verified locally against the pin before it is stored | `SearchSealVerifier.cs` |
| INV-R | `ContentIntegrityRevision` is init-only; the only advance is `AdvanceContentIntegrityRevision(action)`, called only by `ContentRevisionCoordinator` (W1 to W4); deferred constraint trigger `trg_content_revision_search_synchronization` | `ContentRevisionCoordinator.cs`, migration |
| Coverage lifecycle | Approval start → Building coverage; approval leaving Active → coverage Withdrawn and the whole form's index purged, in the security command's transaction; other `ApplyPlan` callers refuse approval changes | `SearchIndexWriter`, `SecurityCommandService` |
| Sweeps | S-A (coverage maintenance, INV-R reconciliation, set checks), S-B (re-derivation from stored source bytes, typed-column and manifest-leaf comparison, local ES256 verification), deadline evaluation (K1 to K9) in a Dark-only background loop | `SearchIndexSweepService.cs`, `SearchIndexSweepLoop` |
| Testing-only additions | Crash fault `CrashAfterSearchIndexDerivation`; `IX1-COMMIT-TIMING` log line (identifiers only); Vault import of the synthetic seal key | `ContentService.cs`, `ContentEndpoints.cs`, `compose.security-test.yaml` |

Implementation decisions taken within C-126: design §19 (D-IX1-1 to D-IX1-14).

## 2. Tests executed

All runs on this workstation (Windows 11, Docker Desktop), 2026-11-06, against the uncommitted working tree that this report's commit records. Synthetic data only.

### 2.1 Unit, vector and architecture tests

| Suite | Result | IX-1 content |
|---|---|---|
| `AuthorizationAuthority.Tests` (all) | **380/380 passed** | `SearchIndexTests` 34 cases: derivation (W1), re-commit, no coverage, type mismatch, seal outage, pin mismatch, unknown key, abort paths, coverage lifecycle, Disabled mode, W2-W4 synchronization, sweeps (S-A, S-B), 6 tamper cases, quarantine stickiness, deadlines K1-K9 |
| `EnterpriseForms.Integrity.Tests` (all) | **60/60 passed** | `SearchEntrySealV1VectorTests` 26 cases: 3 message vectors, 9 signature vectors (positive and negative), 7 encoding failures, order independence, pin thumbprint, pin import refusals, normalization parity |
| `EnterpriseForms.ArchitectureTests` (all) | **89/89 passed** | `SearchIndexBoundaryTests` 9: INV-R single writer, the four call sites, index writes only on the success path, verifier reads no database key, signer only through the gate, search schema confined to the feature, naming, RLS and trigger in the migration, no runtime UPDATE of derived rows |
| Contract tests | **507/507 passed** | Unchanged contracts |
| Platform console | 23/23 passed (earlier run in this session; no later IX-1 change touches it) | - |

### 2.2 Integration tests on the Testing stack (`SearchIndexFoundationTests`: real AuthorityDb, Vault Transit ES256, Business API)

| Test | Result | Covers |
|---|---|---|
| `CommitsDeriveSealedEntriesThatTheSweepVerifiesWithThePinnedKey` | Passed | IX-02 to IX-05, IX-14; IX-63 (W1) |
| `AnAuthorityDomainPostingRewriteQuarantinesOnlyTheIndex` | Passed | IX-34 (seal side), IX-16 |
| `GrantsAndForcedRlsConfineBothRoles` | Passed | IX-01, IX-02, IX-42 |
| `TheDatabaseRefusesAnUnsynchronizedContentRevisionAtCommit` | Passed | IX-74, IX-35 (revised) |
| `ACrashAfterDerivationRollsTheIndexBackWithTheHead` | Passed | IX-64, IX-66, IX-69 |
| `AVerifiedReadQuarantineKeepsTheProjectionSynchronized` | Passed | IX-63 (W2) |
| `WithdrawalPurgesTheFormIndexInTheCommandTransaction` | Passed | IX-06 |
| `ConcurrentCommitsKeepTheIndexAndRevisionsSynchronized` | Passed | concurrency (12 parallel edits) |
| `MeasureCommitLatencyWithAndWithoutTheIndex` | Passed (records only; see section 3.3) | commit-latency gate |

### 2.3 Coverage of the IX-1 test list

| Test id | Status | Where |
|---|---|---|
| IX-01, IX-02 organization isolation | Executed: RLS through the runtime and reader roles; reads without a binding return zero rows | integration `GrantsAndForcedRls...` |
| IX-03 to IX-05 encrypted and unapproved exclusion | Executed: an encrypted leaf on an approved field produces an anomaly and no posting; unapproved and repeating-group fields are not indexed. A forged *business* classification has no input path: derivation reads only Authority approvals and the verified leaf protection | unit `CommitDerives...`, integration `CommitsDerive...` |
| IX-06 withdrawal purge | Executed | unit and integration |
| IX-07 strengthening race with a concurrent search | **Not applicable in IX-1**: there is no query path (Dark mode). Moves to IX-3 | - |
| IX-14 business DBA cannot reach the index | Executed for IX-1 scope: the index is in AuthorityDb only, and business roles have no AuthorityDb grant (privilege inventory) | integration, inventory |
| IX-15 business-supplied postings impossible | By construction and architecture test: postings are derived only inside `ContentService.CommitAsync`, from bytes the Authority verified; no API accepts postings | architecture `IndexWritesHappenOnlyOnTheSuccessfulCommitPath` |
| IX-16, IX-17 Authority-side tampering and partial restore | Executed: 6 tamper cases (typed value, seal, widened operators, record status, deleted posting, leaf digest); residue and revision mismatch found by S-A | unit theory, integration |
| IX-28 log scan | Executed: 0 matches for the synthetic values in the Authority, AuthorityDb and Vault logs. pgAudit and Azure diagnostics not covered (section 2.5) | - |
| IX-34, IX-35, IX-42 | Executed | see section 2.2 |
| IX-61 abort paths write nothing | Executed for **3 of the 4** paths (expired, revision conflict, permission revoked). The record-locked path (contributors only) has no dedicated test; the architecture test covers it | unit |
| IX-63 W1 to W4 | W1 and W2 in integration tests; W2 to W4 in a unit test (`EventsWithoutIndexChange...`). **W3 and W4 are not integration-tested** | - |
| IX-64, IX-66, IX-69 | Executed | crash test |
| IX-65 seal failure | Executed | unit `AnUnavailableSealProvider...` |
| IX-67 backfill race | **Deferred** (D-IX1-14): backfill does not exist until IX-2 | - |
| IX-69 to IX-71 deadline clock rules | Executed | unit |
| IX-72 repeated W4 | Executed (25 events) | unit |
| IX-73 fault at each revision site | **W1 only** (`CrashAfterSearchIndexDerivation`) | - |
| IX-74, IX-75, IX-76 | Executed | integration, architecture, unit |
| IX-77 staged-byte mismatch | Executed | unit `StagedBytesThatDiffer...` |

### 2.4 Regression

Clean stack (`down -v`), then the Stage A gate:

| Run | Result |
|---|---|
| SEC-P01 to SEC-P12 (`--minimum-expected-tests 13`) | **13/13 passed** |
| Evidence validator (`validate_security_evidence.py`) | PASS (manifest structure and digests only; not a security attestation) |

Stage B, all seven groups. The session ended partway through the `-All -Fresh` run, after four groups. The remaining three groups ran on a fresh stack, and two failures were re-run:

| Group | Result | Note |
|---|---|---|
| forms-documents | 38/38 | |
| authorization-security-onboarding | 93/93 | |
| records-lifecycle | 61/62 on the first run; the failed class passed 16/16 on re-run | `RecordCreationIdempotencyTests.ADifferentRequestWithTheSameKey...` got 202 instead of the 503 from its armed business-side fault after a 19-minute stall. It passed on re-run. IX-1 does not touch the business-side fault path. Recorded as intermittent |
| administration-membership | 55/55 | |
| search-catalogue | 16/17 | the verified-scope benchmark at 480 records; see below |
| search-execution | **23/23** | includes `SearchIndexFoundationTests` and the C7 field-search tests |
| protection-reconciliation | 22/22 | |

**Benchmark at 480 records (`SearchCatalogueTests.BenchmarkTheVerifiedScopeAt50And150And480Records`).** This verified search has a 15 s limit (`FieldSearchLimits.ApprovedMaximumExecutionSeconds`). On this workstation it runs with 2-5% headroom whichever build is used. Six runs on the same stack:

| Authority image | Index mode | Result | 480 records p50 / max |
|---|---|---|---|
| IX-1 | Dark (sweeps every 10 s) | failed (time limit) | - |
| IX-1 | Dark (sweeps every 10 s) | failed (time limit) | - |
| IX-1 | Disabled | failed (time limit) | - |
| Baseline `28a6e44`, no IX-1 code | - | passed | 13.60 s / 14.46 s |
| IX-1 | Disabled | passed | 13.45 s / 14.23 s |
| Baseline `28a6e44` | - | passed | 13.24 s / 14.71 s |

At 50 and 150 records the IX-1 and baseline medians differ by less than 7%, in both directions.

Conclusion: no measurable change to verified search with the IX-1 Authority in Disabled mode. The benchmark is marginal on this workstation and fails whenever the stack is loaded, which includes Dark-mode sweeps across the accumulated indexed test organizations. The Dark-mode sweep load is a Testing-stack effect. With more organizations it would compete with verified searches for Authority database time; it should be measured at IX-7 (staging).

### 2.5 IX-28 log scan

Executed on the Testing stack after all search groups had run, including the IX-1 integration tests. The scan looked for every synthetic value those tests submit (for example `Ref-0 Alpha`, `Tamper One`, `Parallel 3`, `Latency 7`):
- Authority container log (428,227 lines): **0 matches**;
- AuthorityDb log: **0 matches**;
- Vault log: **0 matches**.
The only `authority_search` lines in the AuthorityDb log are the `STATEMENT` echoes of the grants test's intentionally refused probes. They carry identifiers and no values.
Not covered by this scan: pgAudit (not enabled on the local stack), Azure diagnostic settings and telemetry.

## 3. Measurements (actual, this workstation; not Azure, not Managed HSM)

All values below were measured on this workstation; none come from Azure or Managed HSM. Estimates are labelled as estimates.

### 3.1 ES256 signing latency (measured)

| Measurement | p50 | p95 | p99 | Samples |
|---|---|---|---|---|
| Vault Transit 1.20.4 dev, `ecdsa-p256`, sequential HTTP from the host (`scripts/measurement/ix1/vault_sign_bench.py`) | 4.69 ms | 6.53 ms | 9.36 ms | 500 |
| Sign stage inside a real commit, Authority container to Vault container, **including** local verification against the pin (3 runs) | 3.24-3.60 ms | 4.13-5.35 ms | - | 3 x 35 |

Managed HSM signing latency was **not measured**. Vault in dev mode is not an HSM.

### 3.2 Local verification throughput (measured)

Release build, .NET 10, 16 logical processors, a 20-posting message of 4,947 bytes (`scripts/measurement/ix1/sealbench`):
- ES256 verification on one thread: **2,586 per second** (about 387 microseconds each);
- ES256 verification on 16 processors: **29,251 per second**;
- seal message encoding: **52 microseconds**.

Derived from these figures (not measured): verifying 10,000 matches at query time would take about 3.9 s on one core, or about 0.34 s across 16 cores. This matters for the IX-3 design of per-match verification. It does not affect IX-1, which has no query path.

### 3.3 Additional commit latency (measured)

Source: the Authority-side `ContentService.CommitAsync` duration, read from the Testing-only `IX1-COMMIT-TIMING` log.

Method:
- 40 records per form, created one after another;
- the first 5 per form excluded, leaving 35 samples;
- "Indexed" = a form with three search approvals;
- "Plain" = an identical form in an organization without approvals.

| Run | Sweeps | Indexed p50 / p95 | Plain p50 / p95 | Delta p50 | Delta p95 |
|---|---|---|---|---|---|
| repeat-1 | every 10 s | 80.8 / 107.4 | 57.4 / 69.7 | 23.4 | **37.7** |
| repeat-2 | every 10 s | 83.8 / 101.0 | 60.4 / 108.6 | 23.4 | -7.6 |
| nosweep-1 | off | 85.4 / 108.0 | 60.5 / 90.1 | 24.9 | 17.9 |
| nosweep-2 | off | 86.6 / 115.3 | 59.3 / 83.2 | 27.3 | **32.2** |
| nosweep-3 | off | 79.6 / 122.6 | 62.1 / 89.3 | 17.5 | **33.3** |
| stages-1 | off | 85.6 / 126.9 | 61.6 / 94.9 | 24.0 | **32.1** |
| stages-2 | off | 83.7 / 112.2 | 61.1 / 79.6 | 22.6 | **32.6** |
| stages-3 | off | 82.9 / 102.1 | 61.0 / 90.1 | 21.9 | 12.0 |

All values are milliseconds. Two earlier runs, taken around the store optimization (batched delete, form-state upsert), were not kept as artifacts and did not repeat:
- one gave p50 25.7 and p95 23.2;
- one after the optimization gave p50 5.3 and p95 19.65.

**Result: the IX-1 gate "commit p95 delta <= 20 ms (local)" is not met.**
- It failed in 5 of the 8 runs.
- The p50 delta is 17.5 to 27.3 ms in every run.
- With 35 samples, p95 is the second-highest sample, so it is noisy; one run even has a negative delta. The p50 delta (about 22 ms) is the more reliable figure.
- Turning sweeps off did not remove the delta, so sweep lock contention is not its cause.

### 3.4 Where the time goes (measured, three runs)

Source: the Testing-only `IX1-DERIVE-TIMING` log (D-IX1-15). All values are milliseconds.

| Stage | p50 | p95 | What it includes |
|---|---|---|---|
| Reads | 7.35-7.97 | 10.2-12.1 | four Authority database round trips: organization binding, record registration, coverages, approvals |
| Derive | 0.17-0.30 | 0.28-0.56 | posting derivation and message encoding |
| Sign | 3.24-3.60 | 4.13-5.35 | Vault ES256 call and local verification |
| Writes | 5.63-5.69 | 9.6-11.3 | index delete and insert, form-state upsert |

The four stages add up to about 17 ms at p50. The remaining 5 ms or so of the delta is outside them:
- the projection-state read in the coordinator;
- the extra flush (D-IX1-8);
- the deferred INV-R trigger;
- committing more rows.

**The ES256 seal is not the main cost**: it accounts for about 15% of the delta. Database round trips account for about 75%.

Possible reductions. These are estimates only: not implemented and not measured. None of them changes the C-126 architecture.
- Read coverages and approvals in one round trip, and bind the organization in the same command: about 3-5 ms.
- Fold the form-state upsert into the index replacement command: about 1-2 ms.
- Make the D-IX1-8 flush conditional on an armed fault: about 1-3 ms.
- Total: about 5-10 ms.

### 3.5 Estimates that IX-1 does not verify

- **C-125's 98.8% reduction in HSM operations is not verified.** IX-1 shows by construction that sweeps verify locally and make no signer calls, but no test counts provider calls, and nothing ran against Managed HSM or at scale.
- **C-125's Azure cost figures remain estimates.**
- **Commit latency in Azure staging is unknown**, because local Docker round trips differ from Azure PostgreSQL round trips.

## 4. Failures, limitations and deviations

### 4.1 Failures

1. **The commit-latency gate failed locally** (section 3.3). **It still fails after the C-127 remediation; see section 6.4.**
   - The p95 delta exceeded 20 ms in 5 of the 8 runs, and the p50 delta is about 22 ms.
   - The main cost is database round trips, not the seal (section 3.4).
   - Owner decision needed: accept the result, ask for the section 3.4 reductions within IX-1, or treat it as a blocker.

### 4.2 Limitations

2. **No Azure Managed HSM ES256 provider** (D-IX1-13). The `AzureManagedHsm` seal provider fails closed. Production signing latency, throttling and key custody are not demonstrated.
3. **Vault in dev mode is not an HSM.** The Docker seal key is imported from a synthetic, published test key.
4. **No cross-language vectors.** The seal vectors are produced and checked by the .NET implementation only.
5. **Revision sites W3 and W4 are unit-tested only**, not integration-tested. IX-73 fault injection covers only W1.
6. **The record-locked abort path has no dedicated test.** Only the architecture test covers it.
7. **Tests that need later stages are not run.** IX-07 and the query-path tests (IX-08 to 13, 20 to 24, 33 and 36) need IX-3. IX-67 (backfill race) needs IX-2.
8. **Freshness is not bound by the seal.** Detecting a replayed older entry together with a rolled-back head needs signed external checkpoints. They are not in IX-1, and they remain mandatory before real-data use.
9. **Latency figures come from a single workstation** and a small sample: 35 per run.
10. **The Stage B verified-scope benchmark is marginal on this workstation** (2-5% headroom against 15 s) for both IX-1 and the baseline. Dark-mode sweeps on a stack with many indexed organizations can push it over (section 2.4).
11. **`RecordCreationIdempotencyTests` failed once and passed on re-run.** It is intermittent and outside IX-1 code.
12. **The IX-28 log scan did not cover pgAudit, Azure diagnostics or telemetry.**

### 4.3 Deviations from the design text

All are recorded in design section 19; none is a security deviation.

| Decision | Deviation |
|---|---|
| D-IX1-3 | A withdrawal purges the whole form's index, not one field's postings |
| D-IX1-4 | No dedicated NOLOGIN owner role; the reader role is NOLOGIN |
| D-IX1-5 | Forced RLS on the value-bearing tables only |
| D-IX1-8 | One extra flush per commit |
| D-IX1-10 | Sweeps hold the organization lock for a batch |
| D-IX1-11 | S-C is part of IX-2 |
| D-IX1-15 | Testing-only stage timing |

Testing-only additions: the fault point `CrashAfterSearchIndexDerivation` and the log lines `IX1-COMMIT-TIMING` (event 9300) and `IX1-DERIVE-TIMING` (event 9301). The log lines contain identifiers and durations only.

No deviation from the C-126 architecture was needed, and no material security issue was found.

## 5. IX-1 exit checklist

| C-126 exit requirement | Status |
|---|---|
| Authority-owned PostgreSQL index foundation | Done (section 1) |
| Atomic integration with successful content commits | Done; crash, abort and concurrency tests pass (section 2) |
| ES256 provider abstraction and crypto test vectors | Done for local and Vault; **Managed HSM not implemented** (fails closed) |
| Public-key pinning and verification | Done; mismatch and unknown-key tests pass |
| INV-R | Done; architecture, trigger and synchronization tests pass |
| Failed-commit, rollback, concurrency and backfill-race tests | Done except the **backfill race (IX-2)**; gaps in section 2.3 |
| Architecture tests | 9 IX-1 tests; 89/89 overall |
| Synthetic data only; index not enabled for user searches | Yes: Dark mode on the Testing stack, Disabled elsewhere, no query path |
| Signing latency, verification throughput and commit latency measured | Yes (section 3), with measured values separate from estimates |
| Positive and negative vectors verified | Yes (26 cases) |
| Atomicity and revision synchronization demonstrated | Yes (section 2.2) |
| Existing search behaviour unchanged | Yes, for the Disabled mode that every non-Testing environment uses. The C7 field-search tests passed (search-execution 23/23). The search-catalogue tests passed except the marginal 480-record benchmark, whose timings match the baseline (section 2.4). Dark mode adds sweep load on the Testing stack (section 2.4) |
| Failures, limitations and deviations documented | Section 4 |
| **Commit p95 delta <= 20 ms (local)** | **Not met**: section 3.3 before the remediation, section 6.4 after it |

IX-1 is returned for owner acceptance. IX-2 to IX-7 have not started.

## 6. Performance-gate remediation (C-127)

C-127 authorized a narrowly scoped remediation: reduce the AuthorityDb round trips IX-1 adds, without changing the architecture, transaction boundaries, INV-R, ES256 signing, canonical encoding or key pinning. The 20 ms threshold is unchanged and no run is excluded.

### 6.1 Code changes

| Change | Files | Effect |
|---|---|---|
| The derivation reuses the record's registered form, which `CommitAsync` has already read from the Authority registry for the protection barrier. If the commit did not read it (the reprotection path), the derivation reads it as before | `ContentService.cs`, `SearchIndexWriter.cs` (`CommitIndexInput.RegisteredFormDefinitionId`) | one read fewer |
| Coverages and approvals are read with one query: Active approvals of the form that have a coverage other than Withdrawn. If the scope holds unsaved coverage or approval changes for the form (never the case in a content commit), the store falls back to the previous two reads so the result is identical | `ISearchIndexStore.GetCoveredActiveApprovalsAsync`, `SearchIndexStore.cs` | two reads become one |
| `ResetRecordIndexAsync` is one command: the transaction-local organization binding (RLS), the nine deletes, and the form-state increment as a data-modifying CTE. On the derivation path the increment is always applied; without approvals it is applied only if an entry was deleted, which is the previous behaviour | `SearchIndexStore.cs`, `SearchIndexWriter.cs` | binding, delete and form-state upsert: three commands become one |
| The new entry and postings are queued (`AddRecordIndex`) and written by the commit's final `SaveChanges`, in the same transaction. The extra flush (D-IX1-8) now runs only when the Testing crash point `CrashAfterSearchIndexDerivation` is armed, so the crash test still proves rollback with the rows already on the server | `ContentService.cs`, `IFaultInjector.IsArmed`, `ProcessCrashFaultInjector` | one flush fewer per commit, **including commits of organizations without an index** |
| Testing-only measurement: an EF command interceptor plus a counter for raw commands, active only in the Testing environment. The `IX1-COMMIT-TIMING` line now carries `db_commands`, and the latency report adds p99 and command counts | `AuthorityCommandCounter.cs`, `AuthorityInfrastructureRegistration.cs`, `ContentEndpoints.cs`, `SearchIndexFoundationTests.cs` | measured round-trip counts |
| New unit tests: `ARecordLockedAtCommitWritesNothingToTheIndex` (dedicated record-locked abort path, IX-61) and `ACommitUsesOneResetCommandAndTheRegisteredFormFromTheCommit` | `SearchIndexTests.cs`, `InMemorySearchIndexStore.cs` | coverage |

Unchanged:
- the seal message, canonical encoding, signing gate, pins and key purpose;
- the INV-R coordinator, the domain method and the database trigger;
- transaction boundaries: every write stays inside the commit's single transaction;
- the order "verification, then head, then derivation, then final commit".

The record-locked test simulates an operation whose prepare-time check was bypassed:
- the owner prepares the edit;
- the stored operation is then given the share-editor classification and re-bound to a contributor who holds a valid share.

Normal calls cannot reach that check, because the prepare-time check and the head compare-and-swap stop them first.

### 6.2 Database round trips (measured)

These are AuthorityDb commands per content commit, counted by the Testing-only counter. Every commit in every run had the same count (minimum = maximum).

| | Indexed form | Plain form | Added by the index |
|---|---|---|---|
| Before (`47f6905` plus the counter) | 21 | 15 | 6 |
| After | 16 | 14 | 2 |

The five commands removed, by code reading:
- the registry read;
- one of the coverage and approval reads;
- the binding;
- the separate form-state upsert;
- the flush.

The flush also ran for plain commits. The original IX-1 change therefore added a round trip to commits of organizations without an index, and the remediation removes it.

The two commands the index still adds are the combined approvals read and the reset command. Both sets also contain the projection-state read, which the plain path performs too.

### 6.3 Measurements (actual, local Docker, same synthetic workload)

**Methodology** (identical for all three sets; the benchmark code was not changed between "before" and "after" except to report command counts and p99):
- Test: `SearchIndexFoundationTests.MeasureCommitLatencyWithAndWithoutTheIndex`, run alone (`--filter-method`), eight times per set, one after another, by `ix1_latency_set.ps1`.
- Each set starts from a freshly created `enterpriseforms-sectest` stack (`down -v`, then `up --build`), with the default Testing configuration: Dark mode, S-A and S-B sweeps every 10 s.
- Per run, the test creates two new synthetic organizations: one whose form has an Active searchability approval (indexed) and one without (plain). It then creates **40 records on the indexed form, and only after that 40 records on the plain form**, sequentially, through the Business API. The two series are therefore **not interleaved**.
- Measured value: the Authority-side duration of `ContentService.CommitAsync`, read from the Testing-only `IX1-COMMIT-TIMING` log line. It covers the content commit transaction, including derivation, signing and the final write. It excludes HTTP, the Business API and prepare.
- The first 5 records per form are excluded as warm-up, leaving **35 samples per form per run**: 280 per form per set, and 560 per form across the 16 post-remediation runs.
- Percentiles use nearest rank (`sorted[floor(p * n)]`). With 35 samples, p50 is the 18th value, p95 the 34th (second highest) and p99 the 35th (the highest). The delta is indexed minus plain for each percentile, per run. A delta can be negative, and a delta p99 compares two single maxima.
- Stage timings (reads, derive, sign, writes) come from the Testing-only `IX1-DERIVE-TIMING` line and cover only the index derivation.
- The report's `stage_note` text still names the D-IX1-8 flush. Since this remediation, the flush runs only when the crash point is armed, which these runs never do. The note text is out of date; the measured values are not affected.
- CPU: `docker stats` of the `authority` and `authority-db` containers, about one sample every 2 s, by a parallel sampler (`ix1_cpu_sampler.ps1`). The value reported is the mean of the samples in each run's time window.
- Raw per-commit samples were not kept: the test reads them from the container log, and the stack is recreated for each set. Every per-run report (`commit-latency-<n>.json`) and run log is kept under `artifacts/ix-1/remediation/{before,after,after-2}`.

**Environment:**
- Workstation: AMD Ryzen 7 PRO 4750U (8 cores, 16 threads), 31.2 GB RAM, Windows 11 Pro.
- Docker Desktop engine 29.1.3, Linux VM with 16 CPUs and 15.2 GiB.
- `postgres:18.0` for AuthorityDb and BusinessDb; `hashicorp/vault:1.20.4` dev mode, Transit `ecdsa-p256`; .NET SDK 10.0.201; Release builds.
- Containers of an unrelated project (SQL Server, Redis, Azurite, Service Bus emulator) ran on the same Docker engine during all three sets; they had been up since about 20:45 local time. No other test or benchmark was started during any set, but background load was not controlled.
- Times: the three sets ran back to back on 2026-11-07 between 04:51 and 05:33 UTC (23:51 to 00:33 Eastern): before 04:51 to 05:04, after set 1 05:07 to 05:20, after set 2 05:20 to 05:33.
- **Power: all three sets ran on battery.** The Windows power log shows the charger disconnected at 04:45 UTC and reconnected at 10:41 UTC; the battery reached the critical level (2%) at 06:56 UTC and the laptop slept. Battery power management (CPU frequency limits) was therefore in effect for every set and was not controlled, and the remaining charge fell during the sets. The sets are comparable with each other (same conditions, consecutive), but their absolute values are battery-mode values, and power management is a plausible source of part of the run-to-run variation. No set was repeated on AC power; C-128 asks for no further lengthy benchmark.

**Before** (`47f6905` plus the Testing-only counter):

| Run | Indexed p50 / p95 / p99 | Plain p50 / p95 / p99 | Delta p50 / p95 / p99 | Sign p50 / p95 / p99 |
|---|---|---|---|---|
| 1 | 49.4 / 65.4 / 81.1 | 39.7 / 64.1 / 66.8 | 9.7 / 1.3 / 14.3 | 2.0 / 3.1 / 3.1 |
| 2 | 51.4 / 98.0 / 108.6 | 38.1 / 73.6 / 98.9 | 13.2 / **24.4** / 9.7 | 2.2 / 7.7 / 8.1 |
| 3 | 53.2 / 88.8 / 116.5 | 38.5 / 58.6 / 74.6 | 14.8 / **30.2** / 42.0 | 2.2 / 3.5 / 3.6 |
| 4 | 52.0 / 81.6 / 97.4 | 38.9 / 63.5 / 79.8 | 13.0 / 18.1 / 17.7 | 2.1 / 3.7 / 4.2 |
| 5 | 52.5 / 82.1 / 108.6 | 37.8 / 79.7 / 79.9 | 14.7 / 2.5 / 28.7 | 2.1 / 3.3 / 4.6 |
| 6 | 54.5 / 100.9 / 115.8 | 37.7 / 53.1 / 61.6 | 16.9 / **47.8** / 54.2 | 2.1 / 4.2 / 4.9 |
| 7 | 50.8 / 91.2 / 92.5 | 38.2 / 58.8 / 64.6 | 12.6 / **32.4** / 27.9 | 2.0 / 3.3 / 3.8 |
| 8 | 56.8 / 81.0 / 86.8 | 39.0 / 58.6 / 65.3 | 17.8 / **22.4** / 21.5 | 2.2 / 3.4 / 3.7 |

Before: p95 delta at or under 20 ms in **3 of 8** runs; median p95 delta 23.4; p50 delta 9.7 to 17.8.

**After, set 1:**

| Run | Indexed p50 / p95 / p99 | Plain p50 / p95 / p99 | Delta p50 / p95 / p99 | Sign p50 / p95 / p99 |
|---|---|---|---|---|
| 1 | 46.3 / 73.8 / 381.3 | 38.4 / 73.5 / 76.3 | 7.9 / 0.3 / 305.0 | 2.2 / 2.9 / 3.3 |
| 2 | 48.1 / 67.1 / 69.1 | 35.4 / 58.1 / 61.7 | 12.7 / 8.9 / 7.4 | 2.1 / 3.2 / 3.7 |
| 3 | 45.9 / 81.4 / 93.6 | 36.1 / 62.3 / 69.9 | 9.9 / 19.1 / 23.6 | 2.1 / 3.5 / 5.0 |
| 4 | 47.6 / 78.0 / 87.0 | 35.7 / 59.5 / 100.3 | 11.9 / 18.5 / -13.3 | 2.3 / 3.4 / 4.1 |
| 5 | 48.2 / 74.4 / 81.2 | 35.3 / 54.8 / 56.2 | 12.9 / 19.6 / 25.0 | 2.2 / 3.5 / 5.7 |
| 6 | 45.9 / 81.1 / 85.2 | 36.9 / 68.7 / 70.8 | 9.0 / 12.4 / 14.3 | 2.1 / 3.0 / 3.1 |
| 7 | 47.0 / 78.0 / 104.9 | 36.5 / 52.5 / 58.9 | 10.5 / **25.5** / 46.0 | 2.2 / 3.5 / 3.8 |
| 8 | 45.4 / 64.6 / 110.3 | 34.7 / 52.2 / 97.3 | 10.7 / 12.4 / 13.1 | 2.1 / 3.2 / 5.5 |

After set 1: at or under 20 ms in **7 of 8** runs; median p95 delta 15.4. This set has no CPU samples: the sampler did not start because its arguments were split at spaces in the path. A second set was therefore run.

**After, set 2** (same code, fresh stack, CPU sampler working):

| Run | Indexed p50 / p95 / p99 | Plain p50 / p95 / p99 | Delta p50 / p95 / p99 | Sign p50 / p95 / p99 | CPU % Authority / AuthorityDb |
|---|---|---|---|---|---|
| 1 | 46.4 / 75.9 / 77.9 | 35.4 / 53.5 / 53.6 | 11.1 / **22.4** / 24.3 | 2.1 / 5.6 / 6.9 | 54.9 / 12.9 |
| 2 | 49.8 / 103.9 / 104.2 | 37.6 / 60.8 / 77.9 | 12.3 / **43.1** / 26.3 | 2.2 / 3.8 / 4.3 | 45.0 / 16.3 |
| 3 | 48.4 / 87.6 / 576.5 | 33.4 / 57.5 / 80.7 | 15.0 / **30.1** / 495.7 | 2.3 / 3.6 / 5.9 | 48.5 / 18.9 |
| 4 | 44.7 / 73.8 / 80.0 | 36.2 / 59.7 / 64.7 | 8.4 / 14.1 / 15.3 | 2.2 / 3.8 / 4.2 | 53.6 / 22.9 |
| 5 | 45.0 / 84.0 / 87.4 | 35.6 / 91.0 / 98.3 | 9.4 / -7.1 / -10.9 | 2.2 / 3.0 / 3.5 | 53.1 / 23.7 |
| 6 | 45.9 / 105.1 / 514.4 | 34.1 / 79.4 / 160.7 | 11.8 / **25.7** / 353.7 | 2.1 / 2.9 / 3.9 | 60.3 / 27.8 |
| 7 | 43.5 / 87.7 / 117.6 | 34.0 / 63.0 / 63.0 | 9.4 / **24.7** / 54.5 | 2.1 / 3.7 / 6.4 | 61.4 / 27.9 |
| 8 | 45.3 / 78.9 / 83.8 | 34.5 / 46.7 / 56.2 | 10.8 / **32.2** / 27.6 | 2.1 / 3.9 / 9.3 | 68.0 / 32.6 |

After set 2: at or under 20 ms in **2 of 8** runs; median p95 delta 25.2.

**Summary:**

| | Before | After (both sets, 16 runs) |
|---|---|---|
| AuthorityDb commands added by the index | 6 | **2** |
| Delta p50, range of run medians | 9.7 to 17.8 (median of runs about 14) | 7.9 to 15.0 (median of runs about 10.8) |
| Runs with p95 delta at or under 20 ms | 3 of 8 | 9 of 16 (set 1: 7 of 8; set 2: 2 of 8) |
| Index reads stage, p50 | 4.7 to 5.1 | 2.0 to 2.4 |
| Index writes stage, p50 | 3.2 to 3.8 | 2.5 to 3.1, now only the reset command (the rows go out with the final write) |
| ES256 sign stage, p50 | 2.0 to 2.2 | 2.1 to 2.3 (unchanged) |
| Total indexed commit p50 | 49.4 to 56.8 | 43.5 to 49.8 |
| Total plain commit p50 | 37.7 to 39.7 | 33.4 to 38.4 (the removed flush applied to plain commits too) |

**Run-to-run variation** (computed from the run-level values above; the raw samples were not kept, so no sample-level interval can be computed):

| | Runs | Mean | Std. dev. | Median | Min | Max | Bootstrap 95% interval of the run mean |
|---|---|---|---|---|---|---|---|
| Delta p95, before | 8 | 22.4 | 15.4 | 23.4 | 1.3 | 47.8 | 12.3 to 32.4 |
| Delta p95, after (16 runs) | 16 | 18.9 | 12.3 | 19.4 | -7.1 | 43.1 | 12.8 to 24.5 |
| Delta p50, after (16 runs) | 16 | 10.9 | 1.9 | 10.8 | 7.9 | 15.0 | 10.0 to 11.7 |

What this shows:
- The p50 delta is stable (standard deviation 1.9 ms).
- The p95 delta is not (standard deviation 12.3 ms, range 50 ms). The upper end of its interval, 24.5 ms, lies above the 20 ms requirement, so these data **do not establish** that the requirement is met.
- The bootstrap uses 20,000 resamples of the run-level values (seed 1). It describes the spread of the run-level p95, not the p95 of commits.

**CPU** (docker stats, about one sample every 2 s; mean per run):

| | Authority | AuthorityDb |
|---|---|---|
| Before, runs 3 to 8 (the sampler started after run 2) | 50 to 68% | 20 to 33% |
| After set 2 | 45 to 68% | 13 to 33% |

There is no material change. In both sets CPU rises from run to run because the 10 s sweeps visit the indexed test organizations, which accumulate with each run.

**Signing:** the in-commit ES256 stage has a p50 of 2.0 to 2.3 ms in all three sets; the remediation does not touch it. The figures in this section are lower than in section 3.1 (3.2 to 3.6 ms there). Section 3 and section 6 were measured on different days under different, unrecorded power conditions, so their absolute values should not be compared.

### 6.4 Gate conclusion

**FAIL.** The 20 ms p95 incremental commit-latency gate is still not met.
- After the remediation, 9 of 16 runs met it: 7 of 8 in one set and 2 of 8 in the other.
- The remediation reliably reduced the cost the index adds: 6 commands became 2, and the median delta p50 fell from about 14 ms to about 10.8 ms.
- The per-run p95 of 35 samples swings between -7 and +43 ms for the same code on the same stack, so the gate as measured cannot be passed reliably.
- All sets ran on battery power (section 6.3). The AC-power rerun in section 6.7 shows battery power management slowing a verified search by a factor of 2.5. The latency figures are therefore not representative of an unthrottled host, and the variance may partly come from power management. This does not change the conclusion: the requirement is not established.
- No run was excluded and the threshold is unchanged.

**C-128 owner decision:**
- The round-trip optimization is approved.
- No further speculative local optimization will be made.
- The 20 ms p95 requirement is unchanged.
- The local evidence does not establish that the requirement is met, so the gate status stays **FAIL**.
- Pooling the existing samples is **not** accepted as a substitute for a pass.

Why further local reductions would be speculative, and have not been made:
- The two remaining added commands are the approvals read and the index reset.
- Removing either would mean caching approvals across transactions, or merging the index write into a different statement.
- Signing (about 2 ms) is fixed by the C-126 architecture.

### 6.5 Proposed statistically defensible benchmark (not executed)

This proposal fixes how the requirement would be measured. The 20 ms p95 target stays. It has not been run; C-128 asks for no further lengthy benchmark unless the evidence package needs it, and it does not.

Weaknesses of the current benchmark that the proposal removes:
1. The indexed series runs before the plain series, so time-dependent load (the 10 s sweeps, accumulating test organizations, the database cache) falls unevenly on the two.
2. Each run yields 35 samples per form. A p95 of 35 samples rests on 2 observations, so its run-to-run spread (standard deviation about 12 ms) is wider than the effect being measured.
3. The raw samples are discarded, so no sample-level interval can be computed afterwards.
4. Background load and power state on the host are not controlled: the C-127 sets ran on battery (section 6.3).

Proposal:
- **Interleaved, paired design.** Each iteration creates one record on the indexed form and one on the plain form. The order inside each pair is randomized with a recorded seed, so both series see the same load at the same time.
- **Equivalent conditions.** The forms have the same fields and values, and each organization has the same record count, so table sizes stay equal. The sweep cadence is the production default, recorded. A warm-up of at least 50 pairs is excluded and reported. The host runs on AC power with a fixed power plan and sleep disabled, runs no other containers or tests, and records its power state, host CPU, memory and container CPU for the whole run.
- **Sample size.** At least 2,000 pairs per run (an order-statistic interval for p95 needs the 95th percentile to be supported by about 100 tail observations), across at least 5 independent runs on freshly created stacks.
- **Primary statistic.** The p95 of each series over the pooled pairs of a run, and its difference. Report the 95% confidence interval of the p95 difference by a stratified bootstrap, resampling pairs within each run, with at least 10,000 resamples.
- **Run-to-run variation.** Report each run's p50, p95 and p99 per series and their difference, with the mean, standard deviation and range across runs.
- **Pass criterion.** The upper bound of the 95% confidence interval of the pooled p95 difference is at or under 20 ms, **and** no single run has a p95-difference interval lying entirely above 20 ms. A point estimate under 20 ms with an interval reaching above 20 ms is **not a pass**; it is inconclusive and is reported as such.
- **Retained evidence.** All raw per-commit samples (record, series, pair, order, elapsed time, command count, stage times, timestamp) as CSV next to the report, the seed, the stack configuration and the commit hash.
- **No exclusions** beyond the predeclared warm-up. Outliers stay in, and the largest ten samples per series are listed.

The design needs only test-side changes (interleaving, a larger count, CSV output). It changes nothing in the Authority.

### 6.6 Mandatory IX-7 staging performance gate (recorded requirement)

This gate is mandatory. **A failure blocks production readiness of the indexed search.** It applies the method of section 6.5 in Azure staging:

| # | Requirement |
|---|---|
| ST-PERF-1 | Measure with the production signer: Azure Managed HSM, EC-HSM P-256 key, ES256, through the Managed HSM provider (D-IX1-13, not yet implemented) |
| ST-PERF-2 | Measure actual PostgreSQL transaction latency on the staging AuthorityDb (Azure Database for PostgreSQL), not only the Authority-side duration: commit duration and lock waits from server statistics |
| ST-PERF-3 | Compare indexed and non-indexed commits with the interleaved, paired design of section 6.5 |
| ST-PERF-4 | Report p50, p95 and p99 of each series and their differences, with confidence intervals and run-to-run variation |
| ST-PERF-5 | Validate the 20 ms incremental p95 requirement with the pass criterion of section 6.5 |
| ST-PERF-6 | Measure concurrent commit and search workloads: commits of several organizations at once, with the S-A and S-B sweeps active, and the IX search load of the PB-09 targets once the query path exists |
| ST-PERF-7 | Report the Managed HSM signing throughput measured (operations per second, latency, throttling) and the cost assumptions: pool pricing, operations per month at the stated workload, and the 330/s P-256 sign figure from C-125 checked against measurement |

### 6.7 Regression verification (after the remediation)

All results are from the remediated code (the working tree of this commit), Release builds, on the `enterpriseforms-sectest` Testing stack. Logs: `artifacts/ix-1/remediation/{unit-*,regression,secp-results}`.

**Unit, architecture and contract tests:**

| Suite | Result |
|---|---|
| Authority (`AuthorizationAuthority.Tests`), including the two new tests | **382/382** |
| Architecture (`EnterpriseForms.ArchitectureTests`) | **89/89** |
| Integrity (`EnterpriseForms.Integrity.Tests`), including the seal vectors | **60/60** |
| Contract | **507/507** |
| Platform console | **23/23** |
| Solution build, Release | 0 errors |

**Stage A gate** (clean stack, `down -v`): SEC-P01 to SEC-P12, `--minimum-expected-tests 13`: **13/13 passed**. Evidence validator: PASS. It checks manifest structure and digests only and is not a security attestation.

**Stage B, all seven groups** (`stage-b.ps1 -All -Fresh`, one run, 5 h 48 min):

| Group | Result | Note |
|---|---|---|
| forms-documents | **38/38** | |
| authorization-security-onboarding | **93/93** | |
| records-lifecycle | **62/62** | the intermittent `RecordCreationIdempotencyTests` failure did not recur |
| administration-membership | **55/55** | |
| search-catalogue | **16/17** | `SearchCatalogueTests.BenchmarkTheVerifiedScopeAt50And150And480Records` failed at 480 records with `SEARCH_VERIFICATION_LIMIT` (`limit_kind: time`), the pre-existing problem. 50 records p50 3.53 s, 150 records p50 10.27 s |
| search-execution | **23/23** | includes all 9 `SearchIndexFoundationTests` and the C7 field-search tests |
| protection-reconciliation | **22/22** | |

Conditions of this run, found afterwards in the Windows power log:
- The laptop ran on battery from 04:45 UTC, before the regression started at 05:35 UTC.
- At 06:56 UTC, during the search-catalogue group, the battery reached the critical level and the laptop slept until 10:41 UTC.
- The group's remaining tests continued after resume and passed. The 3 h 45 min sleep is why that group took 4 h 11 min.
- The benchmark failure happened before the sleep, on battery.

**Rerun of the failed class** on AC power, on a freshly created stack (`stage-b.ps1 -Class SearchCatalogueTests -Fresh`): **12/12 passed**, including the benchmark. Results:

| Records | Runs | Server p50 | Server max |
|---|---|---|---|
| 50 | 15 | 1.44 s | 1.72 s |
| 150 | 12 | 4.34 s | 5.62 s |
| 480 | 8 | 14.46 s | 14.93 s |

At 480 records the headroom against 15 s is 0.5%; this benchmark stays marginal on this workstation. On battery, the same search at 50 records took 2.5 times as long. Battery power management therefore slows these workloads materially. The same applies to the latency sets of section 6.3, which all ran on battery. The second group class, `FieldSearchApprovalTests`, passed in the original run and was not rerun.

**Properties the directive names, and the tests that verify them (all passed):**

| Property | Tests |
|---|---|
| Successful-commit atomicity (content, head, index and revision together) | `CommitDerivesASealedEntryForApprovedTopLevelValuesOnly`, `ARecommitReplacesTheRecordsEntryAtTheNewHead` (unit); `CommitsDeriveSealedEntriesThatTheSweepVerifiesWithThePinnedKey` (integration) |
| Failed and rolled-back commits | `ACrashAfterDerivationRollsTheIndexBackWithTheHead` (integration; uses the armed-only flush), `ATypeThatDoesNotMatchTheApprovalAbortsTheCommit`, `AnUnavailableSealProviderRefusesTheCommitAndWritesNoIndexRow`, `AProviderKeyThatIsNotThePinnedKeyRefusesSigning` |
| Revision consistency | `TheDatabaseRefusesAnUnsynchronizedContentRevisionAtCommit` (integration, INV-R trigger), `WithoutCoverageNothingIsIndexedAndTheRevisionStillAdvances`, `EventsWithoutIndexChangeKeepTheProjectionAtTheContentRevision`, `ARevisionMismatchRequiresReconciliationAndNeverQuarantines` |
| Concurrent writes | `ConcurrentCommitsKeepTheIndexAndRevisionsSynchronized` (integration), `ARevisionConflictLeavesTheFirstCommitsEntryUntouched` |
| Record-locked abort | **new** `ARecordLockedAtCommitWritesNothingToTheIndex` (unit, IX-61) |
| No index entries for rejected commits | `AnExpiredOperationWritesNothingToTheIndex`, `APermissionRevokedBeforeCommitWritesNothingToTheIndex`, `StagedBytesThatDifferFromTheCandidateWriteNothingToTheIndex`, the record-locked test above, and the rollback tests |
| One reset command and the registered form from the commit | **new** `ACommitUsesOneResetCommandAndTheRegisteredFormFromTheCommit` |
| No change to existing search execution | search-execution 23/23 and search-catalogue (16/17, then 12/12 on rerun); no file of the search execution path is changed by the remediation |

The record-locked test cannot reach its check through normal calls, because the prepare-time check and the head compare-and-swap stop the case first. It therefore modifies the stored operation directly (section 6.1). It is a unit test with the in-memory index store; there is no integration test of this path.

### 6.8 Exceptions that remain

These are not IX-1 acceptance requirements; they are deferred to later stages or outside IX-1:

| Item | Deferred to |
|---|---|
| Azure Managed HSM ES256 provider (D-IX1-13) | IX-7 / production |
| IX-7 staging performance gate ST-PERF-1 to ST-PERF-7 (section 6.6) | IX-7; blocks production readiness |
| Backfill-race test IX-67 (D-IX1-14) | IX-2 |
| Cross-language seal vectors | independent cryptographic review |
| pgAudit and Azure diagnostic log inspection (IX-28 partial) | staging |

These are known exceptions, carried forward, not resolved:

| Item | Status |
|---|---|
| Intermittent `RecordCreationIdempotencyTests` failure (business-side fault path) | one failure, then a pass on re-run in section 2.4; see section 6.7 for this run |
| Pre-existing 480-record verified-search performance problem (2 to 5% headroom against 15 s, baseline too) | not caused by IX-1; see section 6.7 for this run |
| W3 and W4 unit-tested only; IX-73 covers only W1 | coverage limitation |

This IX-1 acceptance requirement is **not satisfied**:

| Item | Status |
|---|---|
| Commit p95 delta at or under 20 ms (local) | **FAIL** (section 6.4) |

Everything recorded as deferred is still deferred; nothing is marked complete.

### 6.9 Closure recommendation (for the owner; not an acceptance)

**Recommendation: conditional acceptance of IX-1**, on these terms:
1. **Functional and security scope:** accepted as delivered, on the regression results of section 6.7.
2. **Performance:** the 20 ms p95 local gate is recorded as **FAIL, carried forward**. It is not waived and not converted to a pass.
3. **Closure condition:** the requirement is closed only by the IX-7 staging gate (section 6.6, ST-PERF-1 to ST-PERF-7), using the method of section 6.5. A staging failure blocks production readiness.
4. **Before IX-7:** the section 6.5 benchmark may be run locally, if the owner wants an earlier signal. Its result would be informative and would not close the gate.
5. **Unchanged:**
   - The deferred items of section 6.8 stay deferred.
   - IX-2 to IX-7 stay unauthorized.
   - Signed external checkpoints stay mandatory before real-data use.

Acceptance is the owner's decision; this document does not record it.

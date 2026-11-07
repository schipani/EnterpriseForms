# Indexed search (Option C): IX-1 implementation evidence report (C-126)

- **Status:** IX-1 implemented on local branch `ix-1/authority-search-index`, **returned for owner acceptance. Not accepted, not independently reviewed, not merged, not pushed.**
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

1. **The commit-latency gate failed locally** (section 3.3).
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
| **Commit p95 delta <= 20 ms (local)** | **Not met** (section 3.3) |

IX-1 is returned for owner acceptance. IX-2 to IX-7 have not started.

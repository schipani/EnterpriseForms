# C-149 evidence: R-55 diagnosis and remediation, R-02, P-2, P-4, P-6 and IX-5b regression coverage

- **Status (2026-11-11):** implemented and measured for Development/Testing under GA-IX5-1. Owner-led; **not independently reviewed.** Tier 2 still serves no search; IX-5c is not started.
- **Labelling (workflow rule 5):**
  * "code fact" means checked by inspection;
  * "tested" means executed test evidence with a run id;
  * "measured" means a local Docker measurement. Measurements are indicative only, not staging and not PERF-01.
- **Environment for every timing:**
  * host on AC power, checked before each run; sleep suppressed for the session by a keep-awake process (no power setting changed);
  * Docker Desktop with 16 CPUs and 16 GB;
  * the security-test stack;
  * containers of another local project (`crown-portal-*`) were also running and were not stopped, so the host was not otherwise idle.
- **Raw output:** `artifacts/ix-5/r55/`, `artifacts/ix-5/r02/` and `artifacts/ix-5/p4-sharing-workload.json`. Not versioned.
- **Code commit:** `d5945f0` (signed, local).

## 1. R-55: diagnosis

### 1.1 What the C-148 maintenance loop did (code fact)

At C-148, `SearchAccessMaintenanceLoop` visited every organization on every pass, with a 250 ms pause between passes in the security-test stack. For each organization it called `CatchUpAsync`, which opens three read-committed transactions even when nothing changed (authorization, content, coverage). Each transaction:
- binds the RLS context;
- reads the checkpoint with `SELECT … FOR UPDATE` (a row lock that writes the tuple's lock information);
- reads the organization state;
- for the coverage step, also reads the coverage set.

An idle pass over N organizations therefore costs 3N transactions, and passes run back to back.

### 1.2 Idle measurement, maintenance enabled and disabled

Method:
- AuthorityDb `pg_stat_database` and `pg_stat_wal` deltas, plus `docker stats` CPU, over about 72 s with no workload (`idle-measure.sh`).
- Tier 2 disabled means the same image recreated with `SearchIndex:Access:Mode=Off` (compose variable `IX5_ACCESS_MODE`, test stack only). In that mode the loop is not registered; nothing else changes.
- Every security control is unchanged.

| Build | Stack | Orgs | Tier 2 | AuthorityDb tx/min | tx per org per min | WAL records/s | AuthorityDb CPU % | Authority CPU % | File |
|---|---|---|---|---|---|---|---|---|---|
| C-148 | long-lived | 134 | Dark | 33,183 | 247.6 | 995 | 79.7 | 146.8 | `idle-c148-dark-ac.json` |
| C-148 | long-lived | 134 | Off | 4,370 | 32.6 | 541 | 42.8 | 90.7 | `idle-c148-off-ac.json` |
| C-148 | seeded | 132 | Dark | 32,211 | 244.0 | 1,015 | 83.1 | 151.7 | `idle-c148-seeded-dark.json` |
| C-148 | seeded | 132 | Off | 2,864 | 21.7 | 558 | 41.3 | 82.9 | `idle-c148-seeded-off.json` |
| **C-149** | seeded | 132 | **Dark** | **4,333** | **32.8** | **571** | **41.9** | **83.6** | `idle-c149-seeded-dark.json` |
| C-148 | fresh | 1 | Dark | 2,907 | n/a | 32 | 6.3 | 17.5 | `idle-c148-fresh-dark.json` |

All rows were measured on AC. The first two rows were also measured on battery, with the same picture (31,761 and 4,029 tx/min).

The "seeded" stack is a fresh stack plus 130 empty synthetic organizations (`SyntheticOrganizationSeedTests`), so idle cost can be compared at the organization count of the long-lived stack.

- **Measured:** the C-148 maintainer was 88–91 % of all idle AuthorityDb transactions (about 29,000 of 32,000 per minute at 132 to 134 organizations). It used about 40 points of AuthorityDb CPU and 60 to 70 points of Authority CPU, and it nearly doubled WAL records.
- **Measured:** its cost is linear in the number of organizations, about 220 transactions per organization per minute.
- **Not measured as contention:** client connections stayed at 4 to 6, with 1.2 to 1.6 active on average. The pool was not exhausted. The loop never takes the organization lock (code fact): its row lock is on the Tier 2 checkpoint only.

### 1.3 P-6 (seal sweep, 1,000 records), maintenance enabled and disabled

Test: `SealSweepLockMeasurementTests` (unchanged), run against the C-136 baseline from IX-4 evidence §11.3 (`20261108-095801-76a077b9`).

| Build | Stack | Tier 2 | Run | Size 2,000: longest lock (ms) / entries/s / search 503s | Size 200: longest (ms) / entries/s | **Size 50 (default): longest p50/max (ms) / entries/s** | Search timeouts at 50 and 200 | Seeding rec/s, save p50/p95 (ms) |
|---|---|---|---|---|---|---|---|---|
| C-136 | fresh (C-135) | none | `20261108-095801-76a077b9` | 9,564 / 102.8 / 0 | 2,213 / 99.8 | **685 / 762 / 84.8** | 0 | 1.98, 363 / 1,841 |
| C-148 | long-lived (134) | Dark | `20261110-230303-53b8e1cc` | 12,702 / 75.6 / 3 | 3,122 / 68.6 | 1,075 / 1,494 / 57.8 | 0 | 1.61, 384 / 2,216 |
| C-148 | long-lived (134) | **Off** | `20261111-055018-f04e2a45` | 14,108 / 69.2 / 1 | 3,302 / 67.2 | 1,261 / 1,261 / 60.0 | 0 | 1.88, 401 / 1,093 |
| C-148 | fresh (1) | Dark | `20261111-060821-a187f341` | 11,066 / 87.7 / 17 | 2,459 / 88.9 | 776 / 1,232 / 78.2 | 0 | **0.99**, 414 / 3,245 |
| **C-149** | seeded (132) | **Dark** | `20261111-070035-d562d6b4` | 11,106 / 89.9 / 2 | 1,907 / 104.1 | **729 / 776 / 87.4** | 0 | 2.11, 366 / 1,430 |
| C-149 | seeded (132) | Off | `20261111-071337-0fcd3a2f` | 10,933 / 91.5 / 0 | 2,809 / 84.0 | 979 / 979 / 74.5 | 0 | 2.28, 368 / 556 |

Each configuration has 1 to 22 sweep passes, so the per-configuration figures carry substantial run-to-run noise.

The fresh C-148 run lasted 25 min 55 s, above the 20-minute P-6 bound. The longer seeding and the 10 s search timeouts lengthened the window. The other runs lasted 11 to 14 minutes.

### 1.4 Root cause (what the evidence supports, and what it does not)

1. **The C-148 maintainer caused large, avoidable idle database work: confirmed** (§1.2). It ran three transactions with a row lock per organization per pass, continuously, whether or not anything had changed.
2. **That loop was not the dominant cause of the P-6 seal-verification regression: shown.**
   * On the same long-lived stack, disabling Tier 2 did not recover seal throughput (60.0 against 57.8 entries/s at size 50; longest lock 1,261 against 1,075 ms).
   * On fresh and seeded stacks, both builds return to the C-136 range:
     - C-148 Dark on the fresh stack: 78.2 entries/s;
     - C-149 Dark on the seeded stack: 87.4 entries/s, longest lock 729/776 ms against the baseline's 685/762 ms.
   * The S-B seal path is unchanged in code since C-136 (code fact: the only commits touching `SearchIndexSweepService` since `37ba4f5` are C-141 to C-143, all in the cheap pass).
   * **The regression therefore depends on the state of the long-lived stack, not on the Tier 2 code.**
3. **Not isolated:** which property of the long-lived stack slowed seal verification. Candidates:
   * the other organizations' indexed data and their continuous 10 s sweeps;
   * database-side effects of hours of the old loop (row-lock tuple churn, autovacuum and transaction-id consumption at about 530 transactions per second).

   The second candidate would persist after the Authority alone is recreated, which matches the long-lived Off row. That stack was removed (`down -v`) by the fresh-stack run in this session, so the two cannot now be separated. Reproducing them needs several hours of the C-148 loop on a fresh stack; that was not done.
4. **Search timeouts are explained.**
   * They occur only at the non-default size 2,000. Every size-2,000 configuration held the lock longer than the 10 s business timeout (10.9 to 14.1 s in this session, against 9.6 to 10.3 s at C-136), which is the R-33 mechanism.
   * Their number grows with the number of passes inside the window (22 passes in the fresh run, hence 17 timeouts).
   * At the default size 50, and at 200, no run had a timeout.
5. **Not explained:** the fresh-stack C-148 seeding at 0.99 records/s against 1.6 to 2.3 elsewhere. It is not reproduced on the seeded stack; it is recorded, not attributed.

## 2. R-55: remediation (code fact; tested)

**Change-aware work discovery** (`SearchAccessMaintenanceSchedule`, `SearchAccessProjectionService.ProbeAsync` and `MaintainAsync`, `SearchAccessMaintenanceLoop`):

| Element | Behaviour |
|---|---|
| Discovery | One statement per pass reads every organization's committed authorization, content-integrity and audit journal revisions (`IAuthorityStore.GetOrganizationChangeMarkersAsync`; no row lock, no scope) |
| Skip | An organization whose markers equal those it was last settled at opens no transaction |
| Catch-up | A moved marker runs the unchanged steps at once (authorization, content, coverage; one transaction per event; compare-and-set; `FOR UPDATE` fencing) |
| Probe | On the first pass after start and every `SearchIndex:Access:ReconciliationIntervalSeconds` (default 60; security-test stack 15), every organization is probed in one read-only snapshot transaction with no lock. *Settled* only if the checkpoint is Maintaining at the current protocol, at the authorization and content heads (revision and digest), and its coverage digest equals the live set. *Quarantined* is skipped. Anything else goes to the locked steps, which verify and quarantine; the probe never decides an integrity failure |
| Settling | An organization is settled at the markers read *before* the pass, and only after an outcome that leaves nothing to do (Idle, Quarantined, Fenced). Contended, Advanced (event budget reached), NotBuilt, UpgradeRequired and exceptions leave it due on the next pass |

**Why this is safe:**
- **Readiness is unchanged.** It is computed from the live state on every read (`IsReady`); the loop only affects availability. A delayed catch-up fails closed: a revoked grant's rows are never servable.
- **No work can be lost across restarts.** The memo records completed work, never pending work. It starts empty after a restart, so every organization is probed.
- **Every relevant change moves a marker:**
  * a security command moves the authorization revision;
  * a content commit moves the content revision;
  * Tier 1 attestations, reconciliations and rebuilds, and Tier 2 rebuilds and quarantines, move the audit revision.
- **Changes that move no marker are bounded by the reconciliation interval.** A coverage withdrawn by the Tier 1 cheap sweep is journaled nowhere (code fact). Out-of-band checkpoint damage is the other case.
- **Ordering, atomicity, fencing, isolation and the bounded event budget are unchanged.**

**Not done:** no process-local notification, no `LISTEN/NOTIFY` and no arbitrary slowdown. The 250 ms pass interval is unchanged.

**Measured result:**

| Measure | C-148 | C-149 |
|---|---|---|
| Idle transactions (132 organizations) | 32,211/min | 4,333/min (Off: 2,864) |
| CPU (AuthorityDb / Authority) | 83 % / 152 % | 42 % / 84 %, the Off level |
| Tier 2 checkpoint reads per organization per minute | hundreds | 4.17 (stack test, 139 organizations, 15 s reconciliation; bound 6) |
| Remaining Tier 2 cost | | about 11 transactions per organization per minute (probes plus marker reads); about 1 at the code default of 60 s |

## 3. Tests (C-149 code `d5945f0`, rebuilt images)

| Suite | Result | Run |
|---|---|---|
| Authority unit (6 maintenance, 2 counter new) | **517/517** (final rerun after the benchmarks) | `artifacts/ix-5/r55/c149-unit-full-final.txt` |
| Architecture | 104/104 | `artifacts/ix-5/r55/c149-arch.txt` |
| `SearchAccessValueProjectionStackTests` (IX-5b, 5 existing + 5 C-149) | **10/10** | `20261111-064642-f9be61d8` |
| `SearchAccessProjectionStackTests` (IX-5a) | 10/10 | `20261111-065028-eb7b0ab5` |
| `SearchAccessItemIdentityTests` (TQ-6) | 2/2 | `20261111-065208-9c826a1e` |
| `SpiceDbBehaviourContractTests` (client now built on a call invoker) | 7/7 | `20261111-065231-d8df02d3` |
| `IndexedSearchShareOnlyItemChannelTests` (functional) | 5/5 | `20261111-065246-4933cf2a` |
| `SearchIndexFoundationTests` (functional) | 9/9 | `20261111-065527-ed6177f8` |
| `ContributorWriteTests` | 7/7 | `20261111-065909-4cca0c11` |

The maintenance unit tests (`SearchAccessMaintenanceTests`) cover:
- no transaction for an unchanged organization over 20 passes;
- immediate catch-up of a revocation and of a content commit;
- a coverage change that moves no marker stays not Ready until the next reconciliation;
- a restart loses no committed work;
- a damaged checkpoint is never settled by the probe and is quarantined by the steps;
- a quarantined organization is then skipped until a rebuild;
- a missing checkpoint is built and then settled.

Business contract tests were not run: no business code changed. Stage B was not run (C-131).

**Observation O-1 (test stack, not C-149):** recreating the stack with `up --build` restarts the Authority migration job while the Authority is already running. The job re-applies the runtime grants, and a Tier 1 sweep pass that ran in that window was refused (`permission denied for table record_content_heads`, 64 ms before the job finished). The loop logged it and continued. This is a deployment-ordering property of the local stack.

## 4. IX-5b regression coverage (tested, `20261111-064642-f9be61d8`)

| Test | What it proves |
|---|---|
| `C149ASearchableFieldWithdrawalAndATier1RebuildCloseReadinessUntilConsumed` | A withdrawal closes readiness with no content event, and the withdrawn field's rows are never servable and are removed once consumed. A Tier 1 rebuild (coverages Building, Tier 1 Rebuilding) is never Ready until re-attested; afterwards the rows equal those before |
| `C149DecimalDateAndDateTimeValuesAreProjectedExactlyAndUnindexableTypesNeverEnterARow` | Decimal (1234.50, −0.0001, 99.9999), Date and DateTime (offsets normalized to UTC) rows equal Tier 1's values exactly (verification CLEAN), with exactly one typed column filled. A Guid field's approval is refused, so it has no coverage and no row. **There is no time-only field type** (the data types are String, Integer, Decimal, Date, DateTime, Guid, Boolean, Option and File). File and Guid are not indexable (no operators) |
| `C149ARevocationRacingValueProjectionIsNeverServedFromTheRevokedRows` | Content edits and revocations interleaved with the loop and concurrent catch-ups. No observation between two Ready statuses at the same checkpoint revision contained a revoked reader's row; the final state is verified and evaluates empty for the reader |
| `C149AMaintainerRestartMidCatchUpLosesNoEventAndEndsExactlyConsistent` | A partial catch-up (3 of 10 events), then an Authority restart. The restarted loop, with an empty memo, finishes the backlog with no catch-up call, and the result is verified with the correct values |
| `C149AnIdleMaintainerOpensNoTransactionForUnchangedOrganizations` | 4.17 checkpoint reads per organization per minute (bound 6 at the 15 s interval) |

**Unsupported value types are not silently misindexed (code fact and tested):**
- Tier 1 derives postings only for the seven indexable types (`SearchPostingDerivationV1.EntityKindByValueType`).
- Tier 2 copies only Tier 1 postings, and its table constraint requires exactly the one typed column of the row's type.
- Approvals of Guid and File fields are refused, because they have no operators.

## 5. P-2: SpiceDB API-call amplification (measured)

Counter: gRPC client interceptor, measurement-only, attributed by Authority route; during the P-4 window (§6). Background baseline: 120 s idle window with 186 organizations (`artifacts/ix-5/p2-spicedb-idle-background.json`, run `20261111-074013-1c0a11f6`).

| Authority operation (route) | Requests | SpiceDB calls per request |
|---|---|---|
| Security command (`POST /internal/v1/security-commands`): shares, revocations, team member add/remove | 301 | **0 in the request.** 1 `WriteRelationships` each, in the background projector (301 in total) |
| Authorization check (`POST /internal/v1/authorization/check`) | 84 | 1 `CheckBulkPermissions` (1 item) + 2 `ReadRelationships` (pin and completion marker reads) |
| Batch check (`POST /internal/v1/authorization/check-batch`) | 72 | 1 `CheckBulkPermissions` (2 items on average) + 2 `ReadRelationships` |
| Content revision read (`GET /internal/v1/content-revisions/…`) | 145 | 1 `CheckBulkPermissions` + 2 `ReadRelationships` |
| Field-search capabilities (`POST /internal/v1/field-search/capabilities`) | 142 | 83 requests reached SpiceDB, each 1 `CheckBulkPermissions` (3 items on average) + 2 `ReadRelationships`; the other 59 were refused before it |
| Tier 1 indexed query (`POST /internal/v1/search/indexed-query/`) | 102 | 1.5 `CheckBulkPermissions` (14.9 items), **7.9 `LookupResources`**, 1.5 `ReadRelationships` on average |
| Readiness probe (`GET /internal/v1/health/ready`) | 57 | 1 `ReadSchema` |
| Background loops | | 16.4 `ReadRelationships`/s during P-4, against **17.0/s idle**. Permission changes add no measurable background reads |

The `LookupResources` count per Tier 1 query is an amplification point to examine at IX-5c, where Tier 2 is meant to replace record enumeration. This table is not a capacity figure. Neither staging nor multi-instance SpiceDB was measured.

## 6. P-4: bounded sharing workload (measured, tested; `20261111-073224-03937271`, 6 min 15 s)

**Setup:**
- 40 users; overlapping teams of 5, 10 and 20 members at the start;
- 3 forms of 30 records each, one of them blocked (a record forged by the business DBA before approval, so its coverages stay Building);
- 300 s at one command per second, while indexed searches run every 2 s;
- C-149 code; Tier 2 Dark; SpiceDB counting on.

| Measure | Result |
|---|---|
| Commands | 301: 80 direct shares, 65 team shares, 84 revocations, 33 member additions, 39 removals |
| Command latency p50 / p95 / max (ms) | direct share 358 / 392 / 618; team share 239 / 406 / 576; revoke 339 / 386 / 1,203; member add 523 / 975 / 1,499; member remove 539 / 1,071 / 1,166 |
| Tier 2 Ready again after a command (loop only) | p50 7.6, p95 199, p99 521, max 887 ms; 0 waits over 20 s |
| Journal events per minute | authorization 60.1 (security commands); audit 20.4 (`FieldSearchExecuted`) |
| Archive | at most 1 pending entry, oldest 0.2 s |
| Tier 1 searches (142) | 61 answered (p50 179, p95 224 ms); **61 × `503 AUTHORITY_NOT_READY`** and **20 × `409 SEARCH_SNAPSHOT_CHANGED`**, all retryable fail-closed refusals, none a timeout |
| Correctness | Verification CLEAN, journal replay COMPLETE. 80 evaluations (40 users × 2 searchable forms) equal the test's model exactly: direct or team share including the field; overlaps; revocations; membership changes; the share-only item rule. Blocked form: 0 value rows, coverages `Building,Building` |

**Finding R-56 (new, Tier 1, not caused by C-149; diagnosed in §8):** under continuous permission changes at one per second, 57 % of Tier 1 searches were refused: retryably and fail closed, but refused. §8 shows this figure is inflated, because P-4's search and command schedules were phase-locked. With random arrivals about 27–30 % of first attempts are refused at that rate. Tier 1 refuses while the organization's SpiceDB projection has not caught up (S1 gate), and a snapshot changes when authorization moves. This is designed behaviour, but its availability under permission churn has not been assessed before. It matters for IX-5c, where Tier 2 readiness has the same dependency (Ready again in under 200 ms at p95 here).

## 7. R-02: paired commit-latency benchmark (diagnostic, capped; **R-02 stays FAIL**)

### 7.1 What was run

**Test:** `SearchIndexFoundationTests.MeasurePairedInterleavedCommitLatencyWithAndWithoutTheIndex`, the IX-1 evidence §6.5 design:
- each iteration creates one record on the indexed form and one on an identical plain form;
- the order inside each pair is randomized with a recorded seed;
- the first 50 pairs are a warm-up, excluded and reported; nothing else is excluded;
- the measured value is the Authority-side `ContentService.CommitAsync` duration and command count (Testing-only log line);
- the raw CSV is kept per run.

**Analysis:** `scripts/measurement/ix5/p5_paired_pooled.py`, using nearest-rank percentiles and a 10,000-resample bootstrap. The pooled interval resamples pairs within each run (stratified).

**History of the run set:**
- **Started at the agreed size, then stopped.** The first run used the agreed §6.5 size (2,000 pairs, a fresh stack, production sweep cadence of 900 s). At about 1.2 record creations per second, one run would have taken about 60 minutes. On the owner's instruction (C-149 continuation) it was stopped after 1,529 timed commits.
- **Its data was preserved.** The pairs were reconstructed from the Authority log in creation order (`p5_reconstruct_partial.py`; each pair verified to hold one indexed and one plain record).
- **Five capped diagnostic runs followed.** These are a protocol deviation and are not the acceptance protocol:
  * up to 900 pairs;
  * a 27-minute creation cap per run, so each run stays under 30 minutes;
  * on the existing stack, not freshly recreated (each run uses new organizations, so per-organization tables start empty);
  * sweep cadence 900 s; Tier 2 Dark; C-149 images; AC power checked before each run.
- **Not met:** the "freshly created stacks" condition; the host was not idle (other containers running).

### 7.2 Results

The table reports two statistics:
- **Δp95**, the p95 of the indexed series minus the p95 of the plain series. This is the gate statistic.
- **Paired difference**, the percentiles of (indexed − plain) computed within each pair. This is a different, secondary statistic.

| Run | Pairs | Capped | Indexed p50 / p95 / p99 (ms) | Plain p50 / p95 / p99 (ms) | **Δp95 (ms)** | 95 % interval of Δp95 | Paired difference p50 / p95 / p99 (ms) | Duration | Test run |
|---|---|---|---|---|---|---|---|---|---|
| Stopped full-size run (fresh stack) | 714 (of 2,000) | stopped | 58.8 / 96.6 / 112.1 | 46.5 / 71.0 / 93.9 | **25.5** | 18.1 – 31.5 | – | ≈ 23 min until stopped | `20261111-074415-34efc628` (log reconstruction) |
| Capped 1 | 900 | no | 50.3 / 80.3 / 98.2 | 38.8 / 58.6 / 83.2 | **21.6** | 16.4 – 24.9 | 11.4 / 38.1 / 59.4 | 27.3 min | `20261111-081028-a110259e` |
| Capped 2 | 873 | yes (partial) | 52.8 / 84.4 / 107.0 | 41.2 / 64.1 / 83.4 | **20.3** | 14.7 – 25.4 | 11.2 / 40.2 / 63.0 | 27.3 min | `20261111-083748-52c11bf5` |
| Capped 3 | 895 | yes (partial) | 48.5 / 70.7 / 95.0 | 37.3 / 57.6 / 77.8 | **13.0** | 9.2 – 18.3 | 10.9 / 32.0 / 55.5 | 27.3 min | `20261111-090506-7f4f7b07` |
| Capped 4 | 889 | yes (partial) | 48.8 / 73.4 / 94.9 | 37.7 / 59.3 / 80.3 | **14.2** | 9.4 – 19.5 | 10.7 / 33.8 / 52.7 | 27.5 min | `20261111-093224-81f4bd39` |
| Capped 5 | 867 | yes (partial) | 49.7 / 76.0 / 95.0 | 37.7 / 59.3 / 76.1 | **16.7** | 11.7 – 22.5 | 10.7 / 34.1 / 59.4 | 27.5 min | `20261111-095953-a972a83d` |
| **Capped, pooled (5 runs)** | 4,424 | | 49.8 / 77.4 / 98.7 | 38.5 / 60.3 / 81.7 | **17.1** | **15.3 – 19.3** (stratified) | 10.9 / 35.7 / 58.7 | | `artifacts/ix-5/r02/p5-capped-pooled.json` |

- Run-to-run Δp95: mean 17.2, standard deviation 3.7, range 13.0 to 21.6 ms.
- No run's interval lies entirely above 20 ms.
- Commands: indexed 17, plain 15, in every commit.
- The largest single sample was an indexed commit of 1,324 ms; no outliers were removed.

### 7.3 Database observations during the capped runs

Sources:
- `authority_db_wait_sampler.sh`, sampling `pg_stat_activity` about once per second (verified before the first capped run);
- `pg_stat_database` and `pg_stat_wal` deltas;
- `docker stats` every 10 s.

| Measure | Value (range over the 5 runs) |
|---|---|
| Client connections | mean 3.4–3.8, max 5 (no pool contention) |
| Active backends | mean 0.11–0.16, max 3 |
| Idle-in-transaction connections | mean 0.35–0.42, max 2 |
| Active-backend samples by wait | CPU (no wait) 78–83 %; **`Lock:transactionid` 15–21 %**; `Lock:tuple` 1–3 %; `Client:ClientRead` 0–2 % |
| Transactions, WAL | about 100 transactions/s, 300–320 WAL records/s, 122–150 MB WAL per run |
| Buffer reads | 0 to 14,349 blocks per run against about 8,000–10,000 hits/s (the working set is in memory) |
| CPU (mean / max %) | AuthorityDb 25–26 / 68–102; Authority 41–43 / 117–131; business API 13–16 / 44–56 |
| Block I/O | 0 B (the security-test databases are on tmpfs) |

The only non-CPU wait of note is a row-lock wait (`Lock:transactionid`: a backend waiting for another transaction's row lock). The sampler does not record query text, so which transactions contend is not attributed.

### 7.4 Dominant source of the remaining overhead (measured)

Stage breakdown of the indexed commit's derivation over the 4,424 capped pairs (Testing-only `IX1-DERIVE-TIMING`):

| Stage | p50 | p95 | p99 | Mean |
|---|---|---|---|---|
| Reads | 5.35 | **10.29** | 13.84 | 5.60 |
| Derivation | 0.11 | 0.20 | 0.30 | 0.13 |
| Seal signing (Vault Transit ES256) | 1.74 | 2.51 | 3.15 | 1.80 |
| Writes | 2.07 | 3.65 | 5.45 | 2.27 |
| Total of the stages | 9.36 | 15.50 | | 9.81 |

All values are in milliseconds.

The reads stage is the dominant cost and the main tail contributor (57 % of the mean).

**Code fact:** the stage is one SQL statement (`GetCoveredActiveApprovalsAsync`). It is preceded by two `ChangeTracker.Entries<T>()` calls, which with automatic change detection scan every entity the commit's context tracks.

**Not measured:** the split between those two scans and the SQL. Neither the scans nor the statement is attributed as the cause, and 5.35 ms is well above a local round trip.

**Smallest proposed next step (not implemented; owner decision):**
1. A Testing-only timing split of the reads stage (change detection against SQL) and an `EXPLAIN (ANALYZE, BUFFERS)` of the statement under the commit's RLS binding.
2. Only then, the corresponding correction:
   * either one explicit `DetectChanges` with the two lookups run without re-detection;
   * or a plan or index correction of the statement.

Both are performance-only and leave the authorization and integrity checks unchanged.

**Gate decision.**
- The pooled capped result numerically meets the §6.5 interval criterion: upper bound 19.3 ms; no run interval entirely above 20 ms.
- It was obtained under a deviating protocol. The single run with the full-size setup (fresh stack, 714 pairs) gave Δp95 = 25.5 ms (18.1–31.5).
- **R-02 therefore stays FAIL, as the owner directed. The 20 ms threshold is unchanged, and nothing in this section closes it.** The closure path stays the IX-7 staging gate ST-PERF-1..7.

## 8. R-56: Tier 1 search refusals under permission churn (diagnosed)

**Test:** `SearchAvailabilityUnderPermissionChurnTests` (Benchmark). One organization:
- form A: 40 records, the count field approved, the Reader a field searcher, 20 records initially shared to the Reader;
- form B: 20 records.

Five phases of 120 s each: no changes; a share toggle on form A every 10 s, every 5 s, every 1 s; and every 1 s on form B only. Throughout:
- the Reader searches form A through the business API every second;
- the gate is polled every 50 ms.

Each search records its first-attempt outcome and then a bounded test-client retry (250 ms apart, at most 4 more attempts). The business API's own single immediate retry is unchanged and is inside each attempt. Two runs:
- **aligned:** search slots phase-locked with the changes;
- **random:** a uniform random offset within each second.

### 8.1 Results

| Phase | Authorization revisions/min | Gate closed (fraction; samples AwaitingArchive / AwaitingProjection) | Closed interval p50 / p95 (ms) | **First attempt answered, random arrivals** | First attempt answered, aligned | With bounded retry (random): answered; latency p50 / p95 (ms) |
|---|---|---|---|---|---|---|
| No changes | 0 | 0 | – | **100 %** | 100 % | 100 %; 117 / 170 |
| Every 10 s | 6.5 | 1.9–2.3 % (34–41 / 3) | 247 / 312 | **99.2 %** | 89.3 % | 100 %; 114 / 151 |
| Every 5 s | 11.9 | 3.0–5.0 % (54–85 / 4–10) | 251 / 317 | **96.7 %** | 80.2 % | 100 %; 110 / 192 |
| Every 1 s | 60.1 | 17.7–23.7 % (287–411 / 41–50) | 248 / 365 | **72.7 %** | 2.5 % | 100 %; 133 / 498 |
| Every 1 s, form B only | 60.1 | 21.1–25.9 % (369–445 / 32–46) | 249 / 498 | **70.2 %** | 3.3 % | 99.2 % (1 of 121 still refused after 5 attempts); 143 / 503 |

- Runs: aligned `20261111-103018-e550e5a9`, random `20261111-104337-e20083af`; `artifacts/ix-5/r56-search-availability*.json`.
- Answered search latency (one attempt) stays at p50 110–143 ms, p95 148–205 ms in every phase.
- **Refusal breakdown (random arrivals, first attempts):**
  * every 1 s: 22 × `503 AUTHORITY_NOT_READY`, 11 × `409 SEARCH_SNAPSHOT_CHANGED`;
  * every 5 s: 3 and 1;
  * every 10 s: 1 and 0.
- **Correctness:** 605 (aligned) and 604 (random) answered searches were checked against the toggles' commit windows. **0 violations:** no definitely unshared record was returned, and no definitely shared record was missing.

### 8.2 Root cause (code facts and measurements)

1. **The gate is organization-wide by design (code fact).** `OrganizationGate` closes protected Allow for the whole organization from the commit of a security command until:
   * its authorization-journal entry has its archive receipt (`AwaitingArchive`, mode `RequiredBeforeEffective`), then
   * SpiceDB has applied the revision (`AwaitingProjection`).

   **Measured:** changes to a form the searcher never queries refuse as often as changes to the searched form (70.2 % against 72.7 % answered). Invalidation is organization-wide, not record-scoped.
2. **The closed window is about 250 ms per change, mostly waiting for the archive.**
   * p50 247–251 ms in every phase; p95 312–498 ms.
   * About 88–92 % of closed samples are `AwaitingArchive`.
   * **Code fact:** the single `AuthorityWorkLoop` that archives and then projects sleeps 250 ms when it found no work. The measured p50 equals that sleep, so the window is dominated by the loop's polling cadence, not by the archive or SpiceDB work itself.
   * At one change per second, the gate is closed 18–26 % of the time.
3. **Two separate refusal codes come from the same window.**
   * `503 AUTHORITY_NOT_READY` comes from the business capability check when the gate is closed.
   * `409 SEARCH_SNAPSHOT_CHANGED` comes from the indexed search itself:
     - either the Authority refused it as not ready (the business maps `AUTHORITY_NOT_READY` from the query to 409, for V1 parity),
     - or the authorization revision moved between the search's pin, its reader snapshot and its final check under the organization lock (`IndexedSearchService`; the search spans about 110–140 ms).
4. **The business retry does not help.** The business engine retries once, immediately, with no pause (`FieldSearchIndexedEngine`). The retry lands in the same ~250 ms closed window. A 250 ms client pause answered every search except one.
5. **Why P-4 measured 57 % refusals: test timing.** P-4 issued searches every 2 s and commands every 1 s, both started together, so searches arrived just after commands. The aligned R-56 run reproduces this: 2.5 % answered at one change per second. With random arrivals the first-attempt refusal rate at one change per second is about 27–30 %, which equals the closed-gate fraction plus the chance of a revision change during a search.
6. **Search snapshot duration:**
   * the result snapshot lives 300 s (configuration) for paging;
   * the first-page execution window during which a revision change refuses the search is the search itself, about 110–140 ms at p50;
   * paging re-validates each page against the snapshot's authorization revision (code fact).

### 8.3 Proposed corrections (for owner approval; none implemented)

| Id | Proposal | Effect | Security sensitivity |
|---|---|---|---|
| R56-A | Replace the business engine's single immediate retry with a bounded backoff on `AUTHORITY_NOT_READY` and `SEARCH_SNAPSHOT_CHANGED` (for example 150, 300 and 450 ms, inside the existing request budget and admission slot) | Measured analogue: about 100 % answered, p95 about 0.5 s at one change per second | **Low:** business-side waiting only; every Authority check and refusal is unchanged; still fails closed when the window does not reopen |
| R56-B | Wake `AuthorityWorkLoop` immediately after a security-command commit (an in-process signal), keeping the 250 ms poll as the durable fallback | Expected to cut the closed window from about 250 ms to the archive + projection time. Estimate, not measured | **Low to moderate:** no gate rule changes; the ordering of archive before projection and the fail-closed gate are kept; a lost signal falls back to today's poll |
| R56-C | Record- or form-scoped invalidation instead of organization-wide revision equality | Would remove refusals caused by unrelated changes | **High:** changes the S1 and cross-system consistency semantics (CONS, ADR-PB-002/003 boundary). **Not recommended** without an independent design review |

Recommendation: R56-A first (smallest; no Authority change), then R56-B with a focused stack test; no R56-C.

## 9. Final status and blockers before IX-5c

**R-55 (owner acceptance, C-149 continuation):**
- **Accepted:** the committed idle-maintenance optimization, as reported.
- **Recorded:** the old loop caused about 29,000 avoidable AuthorityDb transactions per minute across 132 organizations.
- **Not conclusively attributed:** the original long-lived-stack seal slowdown. Not every R-55 symptom has a proven root cause (§1.4 items 3 and 5).
- **No regression found:** the fixed implementation is kept; focused IX-5a and IX-5b tests pass.

**Remaining blockers before IX-5c:**
1. **R-02** FAIL (closure only at IX-7 staging).
2. **R-56:** owner decision on R56-A and R56-B. Tier 2 readiness has the same organization-wide closing pattern (P-4: Ready again within 199 ms at p95), so IX-5c serving availability under churn inherits it.
3. **IX-5c questions already open:**
   * TQ-23 timing leakage during fallback;
   * the external journal archive and checkpoint requirements;
   * safe live-serving readiness;
   * large-organization scaling (R-43);
   * controlled operational recovery.
4. **Not run or not attributed:**
   * the Lock:transactionid row-lock source (§7.3);
   * the reads-stage split (§7.4);
   * a reproduction of the long-lived-stack slowdown (§1.4).
5. **Process requirements:** an independent review (none has occurred); Stage B not run (C-131); production and staging requirements unchanged.

No IX-5c, no Tier 2 serving, no IX-6 or IX-7, no 100K/250K dataset, no push, PR, merge or deployment.

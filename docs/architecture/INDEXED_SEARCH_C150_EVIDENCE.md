# C-150 evidence: R56-A and R56-B, R-02 read stage, IX-5c shadow preparation

- **Status (2026-11-11):** implemented and tested for Development/Testing under GA-IX5-1. Owner-led; **not independently reviewed.** Tier 2 serves no search; the IX-5c shadow comparison is off by default and has no production enablement.
- **Owner decisions applied (C-150):**
  * C-149 accepted (`d5945f0`, `9e246ea`, `a52e9b1`);
  * R-55 accepted, with the historical long-lived-stack seal slowdown still documented as not attributed;
  * R-02 stays FAIL;
  * R56-A and R56-B authorized, R56-C deferred;
  * IX-5c shadow preparation only.
- **Labelling (workflow rule 5):** "code fact" means checked by inspection; "tested" means executed test evidence with a run id; "measured" means a local Docker measurement (indicative, not staging, not PERF-01).
- **Power (stated per measurement, checked at the start of each run):**
  * the R-56 wake-up-on run started on AC (50 %);
  * the host fell back to battery before the wake-up-off run (60 %) and stayed on battery for both R-02 read-stage runs (48 % and 38 %).

  Battery timings are higher across the board. The on/off comparison is therefore not an AC/AC pair, and its absolute latencies are not comparable. The gate-closed figures are reported because they differ by a factor of about four, far beyond the battery effect on latencies (about 1.3× at the no-change phase).
- **Environment:**
  * Docker Desktop, 16 CPUs, 16 GB, the security-test stack, recreated fresh (`down -v`, `up --build`) at the start of this work;
  * a keep-awake process for the session (no power setting changed);
  * containers of another local project were also running.
- **Raw output (not versioned):**
  * `artifacts/ix-5/r56-search-availability-random-arrival-wakeup-{on,off}.json`;
  * `artifacts/ix-5/r02-read-stage-{before,after}.json`;
  * test-run evidence under `artifacts/test-runs/<run id>`.

## 1. R56-A: bounded search retry (business API)

### 1.1 Implementation (code fact)

- **New:** `FieldSearchRetryPolicy` (business Application layer).
- **What is retried:** an Authority refusal that is explicitly retryable *and* is either 503 `AUTHORITY_NOT_READY` or 409 `SEARCH_SNAPSHOT_CHANGED`. Code, status and the retryable flag must all match.
- **Never retried:** authorization refusals (`SEARCH_FIELD_NOT_ALLOWED`, `SEARCH_SESSION_INVALID`), integrity and quarantine refusals (`INTEGRITY_QUARANTINE`, `RECORD_QUARANTINED`), limits, capacity (`SEARCH_CAPACITY_EXCEEDED`) and `AUTHORITY_UNAVAILABLE`. Each case is a unit test.
- **Where:**
  * the capability check (`GetFieldSearchCapabilitiesAsync`), which a closed gate also refuses with 503 (C-149 §8.2 item 3);
  * the indexed search call;
  * the V1 verified-scan catalogue (its immediate single retry is replaced; its half-budget rule is kept).
- **One budget per request:** the capability check and the search attempts share it.
- **Bounds (defaults; configuration `FieldSearch:Retry*`, validated at startup):**
  * at most 4 attempts in all (1..6);
  * delays 100, 200, 400 ms, each drawn uniformly from ½ to 1 of the step (equal jitter), so searches refused together do not retry together (largest step 1..2000 ms);
  * no retry starts once 2,000 ms have passed since the search started (0..5000 ms);
  * for V1, no retry starts after half of the 15 s execution budget, or if its wait would exceed what is left of that half;
  * the request's cancellation token ends the wait;
  * never into an instance without a free search slot (the existing rule).
- **Fresh snapshot:** every attempt is a complete new Authority call with a new nonce, so a new result snapshot. Nothing of a refused attempt is reused, and the Authority re-authorizes everything.
- **Error and audit semantics unchanged:** after the last allowed attempt the same refusal as before is returned (same code, status and `retryable` flag). V1 still records evidence of each failed attempt; the Authority journals each indexed attempt itself.
- **Observability:** one Information log line per retry (event 9334, `R56-RETRY`): the request's correlation id, stage, code, retry number and delay. No values, record ids or predicates.
- **Security-test compose:** `R56_RETRY_ATTEMPTS` (default 4).

### 1.2 Tests

- `FieldSearchRetryPolicyTests` (new, 17 cases): qualifying refusals, step doubling and the largest step, the jitter range and its spread over 200 concurrent budgets, the attempts, total-time and caller-deadline bounds, cancellation, startup validation.
- `IndexedFieldSearchEngineTests` (16 new cases, 1 changed):
  * retried with a new nonce each time, at most 4 calls;
  * a closed gate at the capability check is retried and shares the budget with the search;
  * other capability refusals are never retried;
  * only the explicitly retryable pair is retried;
  * the wait ends with the request.
- `PhaseC7FieldSearchExecutionTests`: the V1 path within the bounded budget.
- Dependency fitness tests: the handler, engine and policy have no business-store dependency.
- **Tested:** business contract suite 608/608.

## 2. R56-B: Authority work-loop wake-up

### 2.1 Implementation (code fact)

- **Signal:** `AuthorityWorkSignal` is a coalescing wake-up (a one-slot semaphore). A signal raised while the loop is busy ends its next wait at once, and any number of signals collapse into one pass. Counters by source, plus wake-ups against polls.
- **Commit side** (`AuthorityStore` scope):
  * a scope that appended an **authorization-stream** journal entry runs `SELECT pg_notify('authority_work', '')` inside its transaction, after `SaveChanges` and before the commit;
  * PostgreSQL delivers it to every listening session only if that transaction commits, never on rollback;
  * after the commit has returned, the scope signals its own instance's loop;
  * only authorization-stream entries signal, because only their archive receipt and projection keep the organization gate closed. Content and audit entries keep the poll.
- **Listener** (`AuthorityWorkNotificationListener`, one per Authority instance):
  * one dedicated, unpooled AuthorityDb connection (`application_name` `authority-work-listener`) runs `LISTEN authority_work`;
  * each notification is a wake-up;
  * on any connection failure it reconnects with backoff from 1 s up to 30 s;
  * each (re)connection signals once, because a notification may have been missed meanwhile;
  * the payload is empty: it names no organization, record or principal.
- **Work loop:** waits for a signal or its unchanged poll (250 ms idle, 50 ms after work), whichever comes first. The durable scan (`GetOrganizationsWithPendingWorkAsync`), the archive-before-projection order, the processors and the organization gate are unchanged.
- **A signal is only a hint.** It never says that work exists or has completed. A lost, duplicated or spurious signal changes only *when* the loop looks.
- **Multiple instances:** every instance listens, so any instance's commit wakes every loop. The existing fencing between instances (the SpiceDB marker compare-and-set) is unchanged.
- **Configuration:** `AuthorityWork:CommitWakeupEnabled` (default true). Off restores the poll alone (security-test compose `R56_COMMIT_WAKEUP`). Testing-only counters at `GET /internal/v1/testing/authority-work`.

### 2.2 Tests

- `AuthorityWorkSignalTests` (new, 6 tests): no lost wake-up, coalescing, poll fallback, prompt wake during a wait, 200 producers without a lost signal, cancellation.
- `C150SearchAvailabilityBoundaryTests` (architecture): `pg_notify` is inside the transaction; the local signal comes after the commit; the loop keeps its durable scan and its poll.
- **Tested** — `AuthorityWorkWakeupStackTests`, 4/4, run `20261111-165406-52318a4d`:
  * notification and commit counters move with 12 changes, the gate is Ready again a median well under 250 ms after the response, and a content edit sends no commit signal;
  * a terminated LISTEN session: the change still becomes effective through the poll, and the listener reconnects and resumes;
  * after an Authority restart the listener is back and changes still wake the loop;
  * the revocation race under the bounded retry (§3.3).

## 3. R-56 acceptance (measured)

**Test:** `SearchAvailabilityUnderPermissionChurnTests` (Benchmark), extended for C-150:
- randomized arrivals (`R56_ARRIVAL=random`);
- no test-client retry (the business API retries itself);
- each search carries its own correlation id, so the business API's retry lines attribute retries to searches;
- the gate is polled every 50 ms.

Phases are 120 s each, as in C-149 §8.

### 3.1 Results

**Wake-up on** (R56-A + R56-B), run `20261111-171103-9bb9f732`, started on AC:

| Phase | Changes | Gate closed (fraction of 50 ms samples) | Closed interval p50 / p95 (ms) | First attempt answered (no business retry) | Final answered | Business retries (NOT_READY / SNAPSHOT_CHANGED) | Max retries per search | Refusals returned | Latency p50 / p95 (ms) |
|---|---|---|---|---|---|---|---|---|---|
| None | 0 | 0 | – | 100 % | 100 % | 0 | 0 | 0 | 125 / 202 |
| Every 10 s | 13 | 0.4 % | 63 / 74 | 99.2 % | 100 % | 1 (0 / 1) | 1 | 0 | 118 / 190 |
| Every 5 s | 25 | 1.1 % | 62 / 66 | 98.3 % | 100 % | 2 (2 / 0) | 1 | 0 | 120 / 218 |
| Every 1 s | 121 | **4.9 %** | **62 / 72** | **81.0 %** | **100 %** | 24 (13 / 11) | 2 | 0 | 149 / 329 |
| Every 1 s, other form | 121 | 6.4 % | 63 / 86 | 79.3 % | 100 % | 27 (14 / 13) | 2 | 0 | 175 / 434 |

**Wake-up off** (R56-A only, the C-149 poll), run `20261111-172146-37145310`, **on battery**:

| Phase | Changes | Gate closed | Closed interval p50 / p95 (ms) | First attempt answered | Final answered | Business retries (NOT_READY / SNAPSHOT_CHANGED) | Max retries | Refusals returned | Latency p50 / p95 (ms) |
|---|---|---|---|---|---|---|---|---|---|
| None | 0 | 0 | – | 100 % | 100 % | 0 | 0 | 0 | 170 / 382 |
| Every 10 s | 13 | 2.1 % | 136 / 707 | 96.7 % | 100 % | 9 (7 / 2) | 3 | 0 | 155 / 384 |
| Every 5 s | 25 | 4.7 % | 203 / 434 | 95.9 % | 100 % | 13 (10 / 3) | 3 | 0 | 157 / 452 |
| Every 1 s | 121 | **19.1 %** | **193 / 326** | **65.0 %** | **99.2 %** | 79 (59 / 20) | 3 | 1 × 503 `AUTHORITY_NOT_READY` | 198 / 713 |
| Every 1 s, other form | 121 | 18.3 % | 191 / 336 | 62.8 % | 99.2 % | 77 (59 / 18) | 3 | 1 × 409 `SEARCH_SNAPSHOT_CHANGED` | 201 / 710 |

Columns:
- "First attempt answered" is a search answered without any business retry.
- "Final answered" is the response the user received.
- "Refusals returned" are refusals still returned after the bounded retry.
- Latency is the user-visible response time, retries included.

**C-149 reference** (immediate single retry, poll only, AC, random arrivals; C-149 §8.1), at one change per second:
- the gate was closed 17.7 % of the time, with a closed interval of 248 / 365 ms;
- 72.7 % of responses answered, with 22 × 503 and 11 × 409 returned to the user.

### 3.2 Gate-closed duration before and after R56-B

- **The closed interval shrinks from about 190–250 ms to about 62 ms at p50**, at every change rate. At one change per second, the closed fraction falls from about 18–19 % to about 5 %.
  * The 50 ms sampling cannot resolve intervals shorter than one sample. A 62 ms p50 means "closed in one or two consecutive samples", not a precise duration.
  * The archive receipt plus the SpiceDB projection now take tens of milliseconds after a commit, instead of waiting for the next 250 ms poll.
- **The remaining closed time is the actual archive and projection work,** still mostly `AwaitingArchive` (60 of 94 closed samples at one change per second). The gate is unchanged and still organization-wide (R56-C deferred): changes to another form close it equally (6.4 % against 4.9 %).
- **Not a useful metric** (the reason is now known): the time from a share command's response to the gate being Ready was 7–9 ms at p50 with the wake-up on *and* off. The business share endpoint returns only once the command is effective, so the closed window lies before the actor's own response. It affects other users' searches, which is what the closed fraction measures.

### 3.3 Correctness and the revocation race

- **R-56 benchmark:** 605 (on) and 600 (off) answered searches were checked against each toggle's commit window. **0 violations:** no definitely unshared record was returned, and no definitely shared record was missing.
- **Tested** — `AuthorityWorkWakeupStackTests.UnderBoundedRetryNoSearchDisclosesAStaleAuthorizationResult` (run `20261111-165406-52318a4d`): the Reader searches continuously while one record's share is toggled 16 times. No search that started after a revocation was confirmed returned the record, and none that ended before a share was requested returned it. Every refusal was one of the two transient codes or `SEARCH_VERIFICATION_UNAVAILABLE`.
- **Why a retry cannot disclose a stale result (code fact):**
  * every retry is a new Authority call that pins, checks and commits at the then-current revision;
  * a result snapshot is created only under the organization lock at equal revisions;
  * nothing of a refused attempt is reused.

### 3.4 What is and is not closed

- R56-A and R56-B are implemented and measured: at one change per second every search was answered, with at most 2 business retries.
- **R-56 is not closed by the retry alone.** The underlying refusal source still exists:
  * the organization-wide gate is closed about 5 % of the time at one change per second;
  * a search that spans a revision change is still refused, then retried;
  * 19 % of searches needed a retry at that rate.
- The availability gain is mostly R56-B's shorter window. The retry absorbs the rest.
- **Unmeasured:**
  * more than one change per second;
  * concurrent searchers (the retry holds its admission slot during the backoff, at most about 700 ms of waits);
  * more than one Authority instance (NOTIFY reaches them all; not exercised with two instances);
  * an AC/AC on/off pair.

## 4. R-02: read-stage investigation (measured; R-02 stays FAIL)

### 4.1 Instrumentation (code fact; Testing only)

- `SearchDerivationReadProbe` is active only while the indexed commit's reads stage runs, only when the stage-timing sink exists (Testing).
- It splits the stage into:
  * the registry lookup;
  * the change-tracker work;
  * the approvals statement (call to last row);
  * EF Core's own command-execution time for it (`CommandExecutedEventData.Duration`, from the Testing interceptor);
  * the number of commands, tracked entities and approval rows.
- Log event 9302 `IX1-READ-SPLIT` carries durations and counts only.
- **Bounded benchmark:** `IndexedCommitReadStageMeasurementTests` creates 300 indexed records one at a time, then runs the approvals statement 50 times under `EXPLAIN (ANALYZE, BUFFERS)` as `authority_runtime` (these two tables have no RLS).
- The five-run §6.5 benchmark was **not** repeated.

### 4.2 Findings

Run `20261111-173527-b7f63d63`, fresh stack, **on battery**, 300 commits:

| Component | p50 | p95 | p99 | Mean |
|---|---|---|---|---|
| **Reads stage (total)** | **2.63** | **5.16** | 7.01 | 2.94 |
| Registry lookup (the commit passes its registered form) | 0.002 | 0.003 | | |
| First change-tracker inspection (full automatic change detection) | 0.64 | 1.64 | 3.80 | 0.74 |
| Second change-tracker inspection (full detection again) | 0.55 | 1.36 | 3.30 | 0.64 |
| Approvals statement, call to last row | 1.43 | 2.53 | 3.23 | 1.54 |
| — of which EF command execution (send to first result) | 0.99 | 1.76 | 2.06 | 1.08 |
| — of which client read and materialization (with identity tracking) | 0.43 | 0.77 | 1.17 | 0.47 |
| Unattributed remainder | 0.02 | 0.04 | | |

All values are in milliseconds.

Other measurements:
- 1 command per stage.
- Tracked entities p50 319, p95 589.
- 1 approval row.
- The tracked-merge path was never taken in a content commit.
- **Server execution of the statement: p50 0.09 ms, p95 0.21 ms** (`EXPLAIN ANALYZE`), all shared-buffer hits; the planner reports about 1 ms of planning per execution.

| Item the owner asked to separate | Finding |
|---|---|
| Database round trips | **1** per stage (measured) |
| Queries executed | 1, the approvals statement with its coverage `EXISTS` (measured) |
| Query execution time | 0.09 ms server-side at p50 (measured) |
| Lock waits | None in this stage. A plain `SELECT` takes no row lock, and the measured remainder is 0.02 ms. The `Lock:transactionid` waits of C-149 §7.3 are elsewhere in the commit (not attributed) |
| Network and serialization overhead | About 0.9 ms of the 0.99 ms command time is round trip, protocol and **planning**. The statement is not prepared, so it is planned on every execution (planning time ≈ 1 ms in `EXPLAIN`). Client materialization adds 0.43 ms |
| Redundant data retrieval | One row; the full approval entity is needed by the derivation (frozen operators and options). The **redundant work is the second full change detection** (0.55 ms p50) |
| Waiting for integrity or authorization checks | None in this stage; the reads stage starts after the content verification (code fact) |

**On the C-149 figure:** C-149 measured this stage at 5.35 / 10.29 ms on a long-lived stack (AC). The fresh-stack figure here is 2.63 / 5.16 ms, on battery. Stack state, not power, is the plausible difference. Like R-55's slowdown, this is **not attributed**.

### 4.3 Correction (implemented; performance only)

- **Change (`SearchIndexStore.GetCoveredActiveApprovalsAsync`):** one explicit `ChangeTracker.DetectChanges()`, then both inspections read the detected entry states with automatic detection switched off (restored in a `finally`).
- **Semantics are identical (code fact):** previously each `Entries<T>()` call ran full detection. Nothing changes between the two calls, so the second detection could never find anything the first had not. The merge path, the SQL and the returned entities are unchanged.
- **Run `20261111-173956-9a99662f`**, same stack, back to back, **on battery**:

| Reads stage | Before | After |
|---|---|---|
| p50 | 2.63 ms | **2.04 ms** |
| p95 | 5.16 ms | **3.73 ms** |
| p99 | 7.01 ms | 6.33 ms |
| Second inspection p50 / p95 | 0.55 / 1.36 ms | **0.04 / 0.14 ms** |
| Approvals statement p50 | 1.43 ms | 1.37 ms |

- **Tested:**
  * Authority unit 531/531 and architecture 107/107 on the changed code;
  * regression stack classes on the rebuilt image: `SearchIndexFoundationTests`, `FieldSearchApprovalTests`, `SearchIndexBuilderStackTests` and `FieldSearchExecutionTests` (benchmarks excluded). Run ids are in §7.
- **Not implemented (owner decision):** preparing the statement (Npgsql automatic preparation) would remove most of the ~1 ms planning. It is a connection-string setting that affects every AuthorityDb statement and was not assessed for side effects.
- **R-02 stays FAIL.** The 20 ms p95 requirement is unchanged. This bounded measurement is not the §6.5 paired protocol and closes nothing; the closure path remains IX-7 ST-PERF-1..7.

## 5. IX-5c shadow preparation

### 5.1 Implementation (code fact)

- **Tier 1 hook:** `IndexedSearchService` knows only `IIndexedSearchShadowSink` (a new interface in the Tier 1 feature).
  * After the search's result snapshot and its `FieldSearchExecuted` evidence have committed, it offers an in-memory observation: organization, form, user, prepared conditions and approvals, metadata filters, the complete matched record set, and the snapshot revisions (authorization, content integrity, form content).
  * The offer is synchronous, non-blocking and inside a catch-all: Tier 1's response, status and decisions cannot change.
  * With the comparison disabled, no sink is registered and nothing happens.
- **Queue:** `SearchAccessShadowComparer` holds a bounded in-memory channel (64 by default). A full queue drops the observation and counts it. One background worker compares one observation at a time, each within its own time budget (5,000 ms by default) and the host's stop token. Observations are lost on restart; they are evidence, not state.
- **Equivalent snapshot only** (`SearchAccessProjectionService.CompareShadowAsync`): one read-only REPEATABLE READ snapshot in which all of these hold:
  * the authorization revision, the content-integrity revision and the form's Tier 1 content revision equal the Tier 1 result's;
  * the organization gate is Ready (archive receipt and S1 projection);
  * the Tier 2 checkpoint is Ready (exact A-3 equality).

  Otherwise the result is `NotComparable` with a reason (`SNAPSHOT_MOVED`, `ORGANIZATION_GATE_CLOSED`, `TIER2_NOT_READY`, `ORGANIZATION_NOT_FOUND`). A revocation or edit after the search is never a mismatch and never re-evaluated at another snapshot.
- **Tier 2 evaluation (no SpiceDB):**
  * P is the user and the user's teams (canonical membership in the same snapshot);
  * the candidates are the records of P's Tier 2 record grants (effective owner or share recipient);
  * the value rows are P's, for the conditions' fields, under Tier 1's predicate approvals;
  * a share-only record must read every accepted item of each condition's field (the UF-25 rule);
  * each condition is decided with Tier 1's own predicate semantics (`IndexedSearchPredicates.Holds`), so the comparison tests the projection, not a second predicate implementation.
- **Metadata:** Tier 2 has no record header. A Tier 2 candidate that Tier 1 did not return is checked against Tier 1's verified header facts (status, parent). One that Tier 1's metadata filter excludes is counted as explained, not as a difference.
- **Organization isolation:**
  * both RLS contexts are bound to the organization;
  * Tier 1 facts are read only through two narrow reads on the Tier 2 store port (the existing IX-5b pattern: `GetTier1FormContentRevisionAsync`, `GetTier1RecordHeadersAsync`);
  * the Tier 1 search code stays the only user of the Tier 1 schema from its feature (the existing fitness test passes).
- **Mismatch recording:**
  * a counter, a Warning log line (event 5320) and a `SEARCH_ACCESS_SHADOW_MISMATCH` alert;
  * counts only: Tier 1 count, Tier 2 count, only in Tier 1, only in Tier 2, number of conditions. Never record ids, values or predicates;
  * nothing is quarantined, repaired or served.
- **Disabled by default:** `SearchIndex:Access:ShadowComparisonEnabled` is false. It requires `Mode=Dark` and is refused at startup outside Development and Testing (security-test compose `IX5C_SHADOW`, default false). No production enablement exists.
- **No serving:**
  * the live search and page endpoints still serve Tier 1 only;
  * a fitness test checks that no serving path references shadow results;
  * the Testing endpoint `GET/POST /internal/v1/testing/search-access/shadow[/reset]` reports counters and the last 200 results (counts and codes) and answers 404 when disabled.

### 5.2 Tests

- **Unit** (`SearchAccessProjectionTests`, 8 new):
  * a Match on equal answers (owner, direct share, team, empty);
  * Mismatch in both directions with counts-only alerts;
  * NotComparable for every non-equivalent snapshot (each revision, gate behind, quarantine, Tier 2 behind, unknown organization);
  * a revocation after the search is NotComparable and not evaluated;
  * the share-only item rule and the metadata filter;
  * the bounded queue never blocks;
  * the time budget gives Cancelled, and host cancellation propagates;
  * off by default.
- **Architecture** (`C150SearchAvailabilityBoundaryTests`):
  * the offer comes after the commit, only through the sink, inside a catch-all and never awaited;
  * no serving path reads shadow results;
  * the Tier 2 allowlist is extended by the two new files only.
- **Tested** — `SearchAccessShadowStackTests`, 2/2, run `20261111-165525-9ba0beeb`:
  1. off by default (404);
  2. with the comparison on (Authority recreated, then restored):
     * identical Tier 1 status codes and result ids for 8 fixed searches (Clerk and Reader) with the comparison off and on;
     * every stable comparison a Match;
     * share and revoke churn (12 toggles under continuous search): 0 Mismatch, and no revoked record returned after its revocation;
     * a Tier 2 value row deleted out of band: Tier 1 still returns the record, and the comparison reports one Mismatch with `only_in_tier1 = 1` and no record id in its output;
     * a second organization's comparison stays a Match.

## 6. New findings

| Id | Finding | Kind | Status |
|---|---|---|---|
| F-150-1 | The gate-closed window lies before the actor's own response (the share endpoint returns when effective); it is other users' searches that are refused | Measured, §3.2 | Recorded |
| F-150-2 | The approvals statement of every indexed commit is planned on every execution (≈ 1 ms planning; 0.09 ms execution). Statement preparation would remove most of it | Measured, §4.2 | Proposal, owner decision |
| F-150-3 | The reads stage was about twice as slow on the long-lived C-149 stack as on a fresh stack | Measured, not attributed | Recorded with R-55's slowdown |
| F-150-4 | `pg_notify` takes a database-wide notification-queue lock at commit, so security-command commits that notify serialize briefly across organizations. Unmeasured above one change per second | Code fact (PostgreSQL) | Measure before raising command rates |
| F-150-5 | The business retry keeps its admission slot during the backoff (at most about 700 ms). Capacity under concurrent searchers is unmeasured | Code fact | Measure before production limits |
| F-150-6 | A shadow comparison reads the organization's canonical edges and all its record grants, then the candidates' value rows. Cost grows with organization size (R-43) | Code fact | IX-5c scaling item |
| F-150-7 | `search_field_coverages` was read by a sequential scan (107 rows on this stack). Unverified at scale | Measured plan | Check at the 100K/250K datasets |
| F-150-8 | On a loaded security-test stack (64 indexed organizations, two with 300 records), one cheap-sweep cycle exceeds the 60 s deadline of two builder stack tests. They pass on a fresh stack. The sweep visits organizations sequentially, so detection latency grows with organization count and size | Tested, §7 | Test-timing and scaling item (with R-33 and R-43); run those classes on a fresh stack |

No new security finding: no change touches a decision, a gate rule, a projection, an integrity check or what any endpoint returns.

## 7. Tests executed (summary)

- Authority unit 531/531.
- Business contract 608/608.
- Architecture 107/107.
- **Stack, on the rebuilt fresh stack:**

| Class | Result | Run id |
|---|---|---|
| R56-B `AuthorityWorkWakeupStackTests` | 4/4 | `20261111-165406-52318a4d` |
| IX-5c `SearchAccessShadowStackTests` | 2/2 | `20261111-165525-9ba0beeb` |
| IX-5a `SearchAccessProjectionStackTests` | 10/10 | `20261111-165608-87e082c1` |
| IX-5b `SearchAccessValueProjectionStackTests` | 10/10 | `20261111-165722-eed144bd` |
| `IndexedFieldSearchEngineStackTests` | 5/5 | `20261111-170036-cb746e3b` |
| `IndexedSearchQueryStackTests` | 21/21 | `20261111-170159-c1ab2cc3` |

- **After the R-02 correction** (Authority image rebuilt):

| Class | Result | Run id |
|---|---|---|
| `SearchIndexFoundationTests` (benchmarks excluded) | 9/9 | `20261111-174217-cb75d3fa` |
| `FieldSearchApprovalTests` | 5/5 | `20261111-174820-e64d3013` |
| `FieldSearchExecutionTests` (V1 bounded retry) | 13/13 | `20261111-180056-dcd68d33` |
| `SearchIndexBuilderStackTests`, loaded stack | 10/12 | `20261111-174921-69211f32` |
| `SearchIndexBuilderStackTests`, fresh stack (`-Fresh -NoBuild`, same images) | **12/12** | `20261111-180535-42517efa` |

- **The two loaded-stack failures:** `IX19AnEntryLossQuarantinesTheIndexAndOnlyARebuildRestoresIt` and `TheWorkerBackfillsExistingRecordsAndTheAuthorityAttestsComplete`.
  * Both wait at most 60 s for the Tier 1 cheap sweep (S-A) to reach their organization.
  * The stack then held 64 indexed organizations, including the two 300-record R-02 measurement organizations, whose S-B seal passes took up to 9.7 s each. One sweep cycle exceeded the tests' 60 s deadline (F-150-8).
  * The same images pass 12/12 on a fresh stack.
  * The corrected method is not on the sweep path (code fact). The failures are attributed to stack load and test timing, not to C-150.
- Measurements (Benchmark): R-56 on `20261111-171103-9bb9f732`, off `20261111-172146-37145310`; R-02 before `20261111-173527-b7f63d63`, after `20261111-173956-9a99662f`. An earlier R-02 attempt, `20261111-173246-fd24ac69`, failed in the test harness: 50 `EXPLAIN` statements exceeded the Windows command-line length. The statements were split into five calls; no measurement was lost.
- Stage B was not run (C-131).

## 8. Remaining blockers to enabling Tier 2 searches

1. **Owner approval of Tier 2 serving:** separate, and not requested here.
2. **IX-5c proof obligation (acceptance record item 1):**
   * the shadow comparison with zero unexplained differences over a sustained, realistic workload (only focused tests so far);
   * the M1 sweep;
   * the 14-day sampling and the M2 split, still to be decided with IX-5c evidence.
3. **R-02 FAIL:** a production-readiness blocker; closure only at IX-7 staging.
4. **Open IX-5c questions:**
   * TQ-23 timing leakage during fallback;
   * whether serving must require the archive receipt (the shadow compares only with the gate Ready, which includes it);
   * safe live-serving readiness;
   * large-organization scaling (R-43, F-150-6);
   * controlled operational recovery.
5. **R-56 residual:** the organization-wide gate (R56-C deferred). Tier 2 serving inherits the same closing pattern.
6. **Not run:** the 100K/250K datasets; P-4 at its full size; more than one Authority instance; staging.
7. **Process:** no independent review has occurred; Stage B not run; production and staging requirements unchanged.

## 9. Restrictions observed

- No live Tier 2 serving.
- No R56-C.
- No IX-6 or IX-7.
- No Stage B.
- No 100K/250K dataset.
- No production or staging deployment.
- No push, pull request or merge.
- Signed local commits only.

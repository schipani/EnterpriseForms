# E3-M2f-3G — Capability-Refusal Evidence: PostgreSQL Verification Record

**Status (2026-11-17):**

| Item | State |
|---|---|
| Increment | E3-M2f-3G, the disposable PostgreSQL 18 verification gate of `E3_M2F3_CONTRACT_CLOSURE.md` §10 (PD-15) for the E3-M2f-3b recorder (`E3_M2F3B_IMPLEMENTATION_RECORD.md`) on the accepted E3-M2f-3i index (`E3_M2F3I_IMPLEMENTATION_RECORD.md`) |
| Authorization | Owner, 2026-11-17: E3-M2f-3b conditionally accepted (D-3b-1, D-3b-2, D-3b-3 approved subject to verification and accurate documentation); E3-M2f-3G authorized on disposable PostgreSQL 18 and disposable supporting containers only. Not authorized: commit, push, merge, deploy, persistent databases, Stage B, Azure, runtime privilege changes, migrations, infrastructure, persistent Envelope v2 acceptance, G14 recovery, other refusal classes, summary events, any expansion of the T2 write footprint, any bound-organization scenario |
| Baseline | Branch `e3/m1-envelope-v2-core`, HEAD `3009878f55dc4430cc90ef5092764ed01abeff80`, good SSH ED25519 signature; 39 working-tree entries, exactly the uncommitted E3-M2f-3a, -3i and -3b work, which is preserved |
| Product code | **Unchanged.** No security-contract defect was found; no product file was edited except temporarily by the mutation driver (every mutation restored byte-for-byte and verified by SHA-256) |
| Result | **Three runs of record on fresh disposable PostgreSQL 18.0 + Vault: run-1 68/68; run-2 67/68 (M-03: one of 420 burst requests failed inside a host with a Windows socket-buffer exhaustion before reaching the capability check; harness correction HC-10; safety unaffected); run-3 68/68. **Two passing runs of record: run-1 and run-3**.** Mutations: **7 of 7 PostgreSQL mutations DETECTED; MZ-22 SURVIVED as an equivalent mutant (re-reviewed); all restored; restored baseline green**. Regressions: Authority 1204/1208 (4 skipped), architecture 214/214, contract 940/952 in four of five full runs (one unidentified failure in the first; TS-M2f-01 open) |
| Review | Owner-led. Not an independent security review and not a certification. Disposable-container planner and timing results do not establish Azure PostgreSQL behaviour or performance |
| Final closure 2026-11-17 | Owner, 2026-11-17: E3-M2f-3a, -3i, -3b and -3G **accepted for Development/Testing within their verified scope**; final closure documentation, validation and one controlled local SSH-signed commit authorized. Not authorized: push, merge, deployment, Azure, persistent database change, Stage B. Reconciled verification matrix: §12. Committed locally with the E3-M2f-3 closure; not pushed or merged |

---

## 1. What was verified, and how

**Gate.** `scripts/dev/e3-m2f3g-evidence-pg-gate.ps1` and the harness `tools/experiments/e3-m2f3g-evidence-gate/HarnessG3` (README there). One fresh `postgres:18.0` container (tmpfs, loopback port, random name and credentials, autovacuum off, statements logged with database and application name, lock waits logged) and one fresh `hashicorp/vault:1.20.4` dev container (the host's Vault Transit signer, the repository's `authority-transit` policy, a token with only that policy) per run; both removed with their anonymous volumes; container and volume lists verified unchanged. Every database is created by the run and admitted by nonce comment and cluster system identifier (the unchanged E3-M2e-2 `Isolation.cs`).

**What is real.**

| Element | How |
|---|---|
| PostgreSQL 18.0 | One disposable cluster per run; one database copy per scenario group |
| The Authority host | This tree's Release build as a separate process (`dotnet AuthorizationAuthority.Api.dll`, Development, settings file removed, every setting explicit): **deployment floor `D = 2`**, Release B floor 3, `Content:EnvelopeV2AcceptanceEnabled = false`, Vault Transit signer, in-memory archive, SpiceDB unreachable. Endpoint filter, recorder, retry service and logs unchanged |
| Business clients | This tree's `AuthorityHttpClient`, `ProtectionVerifierHttpClient`, `SearchIndexBuilderHttpClient` in-process (capability header removed on the wire = a level-1 client); the **real historical builds 11e15ee and 0be5f52** (`git archive`, unmodified Release builds) through their own clients via the E3-M2f-2G `BusinessAgent` |
| T2 | The real `CapabilityRefusalEvidenceRecorder` over the real `AuthorityStore`, **approved runtime identity** (`authority_runtime`, NOINHERIT, no elevated attribute), this build's versioned application name. Organization-row lock, `clock_timestamp()`, the catalogue check, the eight-row lookup, the journal insert, the audit-head update, the digest chain, commit/rollback and ambiguous commits are all executed by PostgreSQL |
| Commands | `--migrate`, `--apply-content-acceptance-floor` and the accepted E3-M2f-3i `--provision-capability-refusal-evidence-index` as separate processes with the migration identity (only where required) |
| Archive | The unchanged `ArchiveProcessor` with the production `InMemorySecurityEventArchive` (and `UnavailableSecurityEventArchive` for the outage) |
| Uniqueness proof | An independent read-only SQL oracle (`Oracle.cs`, index and bitmap scans disabled, so never through IX-B) over the organization's **whole** audit stream after every applicable scenario: ≤ 1 entry per (key, bucket), ≤ 7 per bucket, closed approved bodies, non-decreasing evidence times along revisions (I-1), digest = SHA-256 of the stored bytes, chain links, contiguous revisions, audit head = newest entry. The recorder's own classification is never the proof |

**Controlled test setup (disposable copies only).** Organizations and verifier census snapshots seeded as the services write them (no data-key binding anywhere: Z-03); a 2,000,000-entry unrelated audit history for one organization (plus 500,000 over four others); earlier-hour, future-dated, duplicate-key and malformed evidence appended through the real `AuditJournal`; one stored body altered in place (superuser, replica role); server-side faults on one copy each (`NOT VALID` CHECK on the journal or the organization table, a deferred constraint trigger failing COMMIT, a non-recorder audit row occupying the next revision); competing sessions holding the organization row, the journal table (`LOCK TABLE … ACCESS EXCLUSIVE`) or IX-B (an open `REINDEX INDEX`); an interrupted concurrent build for an INVALID IX-B; `auto_explain` and `track_functions = all` as database settings of T2 copies. In-process test pieces: a shifted recorder clock (caches, window, backoff only; budget and database times real), an outcome observer, EF interceptors that observe, pause, terminate or fail at one exact statement or commit (never altering SQL, parameters or results), a test MAC for in-process level-2 writers.

**Statement traces.** The migration gives the runtime role `log_statement = 'none'` (product setting in `AuthorityRuntimeGrants`; not changed). T2's planned statements are therefore taken from `auto_explain` (query text in completion order) and the EF-issued commands from an observing interceptor; the two `SET LOCAL` bounds are additionally proven by their server-side effects (`canceling statement due to lock timeout` at ~1 s, `… statement timeout` at ~0.5 s).

---

## 2. Runs of record

| Run | Started (UTC) | Groups | Checks | Failed | Demonstrations (outside totals) | Not executed | Containers removed / volumes / deadlocks / leaks | Duration |
|---|---|---|---|---|---|---|---|---|
| run-1 | 13:13 | all (S, H, M, T, F, Q, L, W, G, B, Z) | **68** | **0** | 2 (as expected) | 1 | yes / 0 new / 0 / 0 | 2,808 s (incl. the wait for the 14:00 boundary) |
| run-2 | 14:14 | all | 68 | **1 (M-03)** | 2 (as expected) | 1 | yes / 0 new / 0 / 0 | 2,718 s |
| run-3 | 15:03 | all | **68** | **0** | 2 (as expected) | 1 | yes / 0 new / 0 / 0 | 3,418 s |

Each run used a fresh container, fresh databases, fresh credentials and a fresh nonce; no database was reused between runs.

**Run-2 failure (M-03), analysed.** In the ten-organization burst on three real hosts, 419 of 420 requests returned the 409; one returned an unhandled
server error. The host's log shows why: opening its own PostgreSQL connection failed with `SocketException 10055` ("the system lacked sufficient
buffer space"), so that request never reached the organization lock or the capability check (T1 did not run; no refusal occurred, so no evidence was
due). Every organization still held exactly its seven entries and the oracle held. Cause: Windows socket-resource exhaustion on the test machine,
aggravated by the harness creating a new HTTP stack (a new TCP connection) per request. **Not a product defect**; the refusal path did not run.
Harness correction HC-10 (one pooled HTTP stack, at most 64 connections per host) was made and a **third run of record** executed on fresh containers.
Run-2 is kept as a run of record with its failure (`docs/evidence/e3-m2f3g/run-2`); it is not re-labelled. Evidence: `docs/evidence/e3-m2f3g/run-1`, `run-2`, `run-3` (scrubbed: no credential, nonce or local path). Exploratory runs before the runs of record (harness debugging, §8) are **not** runs of record and are not reported as such.

---

## 3. Results by closure test (run-1 values; run-2 and run-3 in their evidence)

### 3.1 The seven routes through the real host (§10.3)

| Check | Closure | Result (run-1) |
|---|---|---|
| H-01 | M2F3-07..14, -17 on PostgreSQL | 7 × `409 CLIENT_ENVELOPE_CAPABILITY_INSUFFICIENT`, retryable false; exactly the seven approved entry points once each; every evidence database time inside the harness's own database-clock window; organization fingerprint (everything except the audit head and the evidence) unchanged by the seven refused T1 transactions and their T2s; oracle holds |
| H-02 | M2F3-05 | 14 further refusals, evidence stays 7 |
| H-03 | CAP-S3/S6 on the wire | `1`, `02`, two instances, `two`, `1,2`, absent: all refused, one `verify` entry. A value padded with HTTP whitespace arrives trimmed as `2` (admitted) — a diagnostic, as in E3-M2f-2G GH-06 |
| H-04 | level 2 not recorded | the current header `2` passes the capability check on all seven routes (later stages answer); evidence 0 |
| H-05 | **M2F3-26** | the real **11e15ee** and **0be5f52** builds, their own clients, no header sent: 7 × 409 each, 7 distinct entries each, fingerprint unchanged, oracle holds |
| H-06 | M2F3-31 (delivery) | the unchanged `ArchiveProcessor` anchors all 22 host-recorded entries (digests verified by the archive), no quarantine. *Observation:* the host's own work loop archives only once SpiceDB reports its schema ready; SpiceDB is unreachable in this run, so that loop delivered nothing (as in E3-M2e-2/M2f-2G) |
| H-07 | restart | a fresh host process re-checks durably: 7 × 409, nothing appended |
| H-08 | **M2F3-34** (host) | journal table locked by another session: the client still gets its 409 in 536 ms; T2 is cancelled at its statement bound; retry scheduled (log **9253** in the host output); host killed before the retry: no evidence, nothing half-written; after restart the next refusal records exactly one entry |
| H-09 | **M2F3-83** rolling deployment | database without IX-B: 7 × 409, evidence 0, log 9253; the E3-M2f-3i command creates IX-B while the host runs; the scheduled retries then record exactly the seven keys; further refusals add nothing; oracle holds throughout |
| M-01/M-02 | **M2F3-27** | 2 real hosts × 56 and 3 real hosts × 84 concurrent refusals of one organization: exactly 7 entries each; max refusal latency 1.9 s / 1.2 s |
| M-03 | multi-organization | 10 organizations × 3 hosts × 2 passes (420 refusals): every organization exactly 7; fleet bound holds |

### 3.2 The T2 itself (§10.3, §10.4)

| Check | Closure | Result (run-1) |
|---|---|---|
| T-01 | **M2F3-54, M2F3-71**, M2F3-28 (fence) | Server: organization lock → `clock_timestamp()` → catalogue check → eight-row lookup, then exactly one journal INSERT and one organization UPDATE (EF orders the batch UPDATE first) and nothing else. EF: `SET LOCAL lock_timeout` → lock → `SET LOCAL statement_timeout` → one SaveChanges batch → one commit. UPDATE columns exactly `audit_journal_revision, latest_audit_journal_digest, state_modified_on`. No `transaction_timestamp/now/current_timestamp/statement_timestamp`; no content-operation, floor or ledger statement |
| T-02 | **M2F3-56** | audit 0 → 1; authorization, projection, content-integrity revisions unchanged (so snapshot validity, which compares them, is unaffected); no quarantine; fingerprint and other organizations unchanged; the search-access maintenance schedule plans exactly one `CatchUp`, for this organization |
| T-03 | M2F3-54 (triggers) | only `authority.organization_integrity_quarantine_is_permanent` ran, once |
| T-04/T-05 | M2F3-72, M2F3-05 | oracle holds; a second process finds the key (no INSERT, no UPDATE, no SaveChanges, no commit) |
| T-06 | **M2F3-52** | pre-filter capacity 1: the evicted key is found durably; a 105-call flood made 101 lookups and exactly 18 inserts; 21 entries for 3 organizations |
| T-07 | D-3b-3 | 40 concurrent refusals of one key: one T2 (1 lookup, 1 insert), 39 `SuppressedInFlight` |
| T-08 | **M2F3-27/-51/-72** | R = 10 restarts × 6 instances × 7 keys (420 T2 attempts) in one bucket: exactly 7 `Committed`, 413 `AlreadyRecorded`, no error; T2 p50 236 ms, p95 519 ms, max 705 ms under that contention |
| T-09 | **M2F3-36** | organization row held: T2 gives up at the server lock timeout (55P03, 1,032 ms), appends nothing, is retried and records once the row is free. Whole pipeline (T1 refusal, row taken at T2's first statement): 409 in 1,136 ms, evidence 0 |
| T-10 | M2F3-63 (catalogue) | journal table held: the catalogue check is cancelled by the 500 ms statement timeout (533 ms total), Deferred, nothing appended |
| T-11 | **M2F3-63** (lookup) | IX-B held by an open `REINDEX`: the catalogue check passes, the **lookup** is cancelled by the statement timeout (536 ms), Deferred; after release the retry records exactly once |
| T-12 | **M2F3-31** (outage) | archive unavailable: delivery fails and stays Pending while a further refusal is still recorded; afterwards both are Anchored |

### 3.3 Faults, unknown outcomes, clock and bodies (§10.4)

| Check | Closure | Result (run-1) |
|---|---|---|
| F-01 / F-02 | M2F3-64 | journal INSERT / organization UPDATE fails on the server inside T2's SaveChanges: **Ambiguous** (the commit had been requested; D-3b-4/D-6), the whole T2 rolled back (evidence 0, revisions unchanged), retry records exactly one |
| F-03 | commit failure | a deferred constraint trigger fails COMMIT on the server: Ambiguous, nothing durable, retry records one |
| F-04 | **M2F3-35, -48, -50** | COMMIT succeeded, outcome lost (exception after COMMIT returned): entry durable; the same process's retry and a restarted process both reconcile (`AlreadyRecorded`); never a second entry. Technique for TQ-11: a post-commit **transaction** interceptor; it did not disturb Npgsql |
| F-05 | **M2F3-49** | T2's session terminated just before COMMIT: Ambiguous, nothing durable, retry records one |
| F-06 | **M2F3-34** (in-process) | a refused T1 (level 1, D = 2) commits nothing (fingerprint and audit head unchanged); crash before T2: no evidence; next refusal records one |
| F-07 | **M2F3-65** | newest entry dated 4 s after `t_db`: Deferred `CLOCK_REGRESSION`, nothing appended; after `t_db` passed it the retry records one |
| F-08 | invalid bodies | (a) an unapproved key: Inconclusive `UNVERIFIABLE_BODY`, then `SuppressedInconclusive`; (b) a body altered after commit: Inconclusive; nothing appended; the oracle independently sees the invalid body and the digest mismatch |
| F-09 | MZ-22 re-review | eight current-hour entries necessarily contain a duplicate key: Inconclusive `BOUND_INCONSISTENT`, nothing appended (§6) |
| F-10 | **M2F3-64** (unique violation) | a non-recorder audit row already at the next revision: T2's insert violates the unique stream revision; Ambiguous, nothing appended, audit head unchanged (evidence lost, never duplicated) |

### 3.4 Quota and retries

| Check | Closure | Result (run-1) |
|---|---|---|
| Q-01 | D-7, liveness | attempt limit 2 (test value): the third key is `QuotaSuppressed` with **no T2 at all** (two lookups in the trace) and is not recorded while the window lasts; after the window it is recorded. **First-refusal liveness is not unconditional under the quota** |
| Q-02 | retry capacity | capacity 2, persistent server fault: two keys pending (Ambiguous, 9253), the third `Abandoned/RETRY_CAPACITY` (9254); nothing appended |
| Q-03 | exhaustion | the pending keys are retried with backoff (24 attempts, still nothing appended) and abandoned after one hour (`ABANDONED`, 9254); oracle holds |
| Q-04 | **M2F3-37** (D-4) | a retry pending in a process that restarts is lost; the next refusal after the fault cleared records exactly one |

### 3.5 Mature-organization liveness and plans (M2F3-30, M2F3-70)

Organization with **2,000,000** unrelated audit entries (2,500,000 journal entries in the database), plans captured by `auto_explain` **inside the real T2**:

| Check | Result (run-1) |
|---|---|
| L-01 (no statistics) | first refusal **Committed on the first attempt** in 56 ms; `Index Scan Backward using ix_journal_capability_refusal_evidence` under `Limit`, 0 rows, **2 shared buffers**, no filter removals (no unrelated audit entry read); **T2 lock hold 28 ms** (TQ-14) |
| L-02 / L-03 | repeat: 1 row, AlreadyRecorded; six further keys Committed first attempt; lookup over 7 entries (≤ 8 heap rows), 3 buffers |
| L-04 (after ANALYZE) | same plan |
| L-05 | generic prepared plan (`force_generic_plan`, runtime identity, parameterized organization): still the backward IX-B scan under Limit |
| L-06 | later hour: 7 entries in each of the three previous hours; the current hour's first refusal is recorded; the lookup reads exactly 8 rows |
| L-07 / L-08 | IX-B held → lookup cancelled at 500 ms; journal table held → catalogue check cancelled, no lookup ran; Deferred, nothing appended |
| L-09 / L-10 / L-11 | IX-B **missing**, **mismatched** (same name, no predicate), **invalid** (interrupted concurrent build of the approved definition): one catalogue check, **no lookup**, nothing appended, Deferred `INDEX_UNAVAILABLE`, a repeat is `SuppressedPending`; oracle holds |
| L-12 | after the manual drop, the accepted command rebuilds IX-B on the mature table; the next refusal is recorded first attempt (0 rows, 1 buffer) |

### 3.6 Writers, floors, restore (M2F3-28, -55, -57, -84)

| Check | Result (run-1) |
|---|---|
| W-01 (A-1) | floors 1 → 2 → 3 applied by the migration identity while T2s ran continuously on four organizations: every floor step exit 0, no T2 failed, 28 entries, fleet oracle holds |
| W-02 | 15 s of eight level-2 v1 writers (Release B floor 3, D = 2) next to level-1 refusals and T2s on the same organizations: **518 writes, all OK**; 69 refusals, all the 409; no unexpected T2 outcome; 28 entries; no deadlock |
| G-01..G-03 (M2F3-55, **-57**) | live evidence anchored; the restore copy (taken before it) records a post-restore entry at the same revision; T2 does not quarantine; the unchanged archive processor finds the collision and quarantines **that** organization (`ARCHIVE_BODY_MISMATCH`); an organization whose post-restore entry does not collide is anchored normally; one without post-restore evidence is untouched; the archived pre-restore entry is intact; the post-restore entry is retained; the live database is unchanged |
| G-04 | `EXPECTED_GAP_RESTORE_EVIDENCE` (demonstration): the restored database lost the live evidence; it is detected only when a post-restore append collides; no recovery mechanism exists (BD-1, BD-2, G14 recovery stay open) |
| G-05 (**M2F3-84**) | restore copy from before IX-B: recording stops (Deferred `INDEX_UNAVAILABLE`, nothing appended, nothing to collide, no quarantine); re-running the accepted command adds exactly IX-B and nothing else; the next post-restore entry collides and the organization is quarantined exactly as in M2F3-57 |

### 3.7 A real hour boundary (M2F3-69, M2F3-29)

| Check | Result (run-1; database-clock skew −1 ms) |
|---|---|
| B-01 (**M2F3-69**) | T2 A sent its first command (`SET LOCAL lock_timeout`) at 13:59:59.433, T2 B at 14:00:00.132; B locked and committed first (`commit`), A second (`verify`); both entries in the 14:00 bucket, buckets non-decreasing along revisions; oracle (I-1) holds |
| B-01d | `COUNTERFACTUAL_TRANSACTION_TIMESTAMP` (demonstration, plain SQL, no journal write): C's `transaction_timestamp()` 13:59:59.028 (bucket 13:00) while it locked **after** E (14:00:00.205, bucket 14:00): a `transaction_timestamp()` bucket would have decreased along the lock order; `clock_timestamp()` read after C's lock gave 14:00:00.209 (bucket 14:00) |
| B-02 | later hour: the seven keys recorded in the closing hour are each recorded again, first attempt, in the new hour (14 entries, 7 per bucket) |
| B-03 (**M2F3-29**) | 25 organizations × 7 keys refused by new recorder processes every 3 s from 45 s before to 40 s after the boundary: **exactly 175 entries in each bucket** (= 7 × 25), every organization ≤ 7 per bucket, 9,450 attempts, no failure; level-2 backfill requests on the same organizations: p50/p95/max 7/19/74 ms during the flood (baseline 9/33/872 ms) |

Only two buckets were crossed per run ("several buckets" in M2F3-29 is limited to the boundary each run waited for).

Run-3 values for the key measurements: L-01 `Committed in 54 ms; backward IX-B True; Limit True; scan rows 0; shared buffers 2; rows removed by filter False; scan [-…`; T-08 `AlreadyRecordedx413,Committedx7; T2 p50 246 ms, p95 498 ms, max 773 ms; evidence 7; max per (key, bucket) 1; m`; B-01 `skew -1 ms; A first command 15:59:59.478 (lock bound True); B first command 16:00:00.134; B Committed; A Committed; rows [1|commit|2026-11-17T16:00:00…`; B-03 ` outcomes AlreadyRecordedx9100,Committedx350; level-2 p50/p95/max baseline 8/25/868 ms, during 7/14/63 ms (322 requests)`.

### 3.8 Sentinels

Z-01: the runtime identity's privileges, memberships and role attributes are unchanged (2,379 entries, identical digest). Z-02: no database of the run holds an acceptance policy with acceptance requested. Z-03: no data-key binding exists anywhere.

---

## 4. Failure, retry and quota: log classification (D-3b-2)

| Situation (check) | Outcome / reason | Host log class |
|---|---|---|
| Lock wait, statement timeout, catalogue failure, journal-table lock (T-09/-10/-11, L-07/-08, H-08) | Deferred / Error (server 55P03 or 57014) | 9253 (retry scheduled; observed in the real host output in H-08) |
| IX-B absent, invalid or mismatched (L-09..-11, G-05, H-09) | Deferred / IndexUnavailable | 9253 (observed in the real host in H-09) |
| Clock regression (F-07) | Deferred / ClockRegression | 9253 |
| Server insert/update/commit failure, unknown outcome, unique violation (F-01..F-05, F-10) | Ambiguous | 9253 |
| Unverifiable body, duplicate key in the bucket (F-08, F-09) | Inconclusive | 9254 |
| Attempt limit (Q-01), retry capacity (Q-02), abandoned after one hour (Q-03) | QuotaSuppressed / Abandoned | 9254 |

The closure text of M2F3-62 and M2F3-65 names 9254 and a negative cache for the index-unavailable and clock-regression cases. Under the approved D-3b-2 classification they are transient: a bounded retry is scheduled and 9253 is logged; a repeat refusal is `SuppressedPending`. The gate verified that implemented behaviour; the closure text is annotated accordingly. 9256 (retry-loop failure) did not occur.

---

## 5. Mutations on PostgreSQL

Driver: `scripts/dev/e3-m2f3g-mutations.ps1` (evidence `docs/evidence/e3-m2f3g/mutations/`). For each mutation: the anchor verified to occur exactly once, ONE edit applied to the product source, the gate run on fresh containers on the groups that exercise it (`-BigRows 200000`), the exact original bytes restored and verified by SHA-256. The restored tree was then run once more on the union of the groups.

| Id | Mutation (product source) | Groups | Result | Failing checks |
|---|---|---|---|---|
| PM-01 | `t_db` from `transaction_timestamp()` instead of `clock_timestamp()` after the lock (M2F3-71) | T | **DETECTED** | T-01 (forbidden function, shape), T-05, T-06, T-08 (under contention a transaction started before another's commit sees a newer entry: clock regression, appends deferred) |
| PM-02 | no T2 lock-wait bound (`lock_timeout = 0`) | T | **DETECTED** | T-09 (no server lock timeout; only the budget ends the wait) |
| PM-03 | no T2 statement bound (`statement_timeout = 0`) | T | **DETECTED** | T-10, T-11 (the catalogue check and the lookup are no longer cancelled at 500 ms) |
| PM-04 | lookup without the event-kind predicate (IX-B no longer matches; unrelated audit entries read) | L | **DETECTED** | L-01..L-07, L-12 (plan not IX-B; first refusal not recorded on a mature organization) |
| PM-05 | catalogue check bypassed (IX-B always "usable") | L | **DETECTED** | L-09..L-12 (lookups and appends without a usable IX-B) |
| PM-06 | T2 reads the organization row without the lock (untracked) | T | **DETECTED** | T-01..T-09, T-11, T-12 |
| PM-07 | the durable lookup decision ignored (always append) | T | **DETECTED** | T-05, T-06, T-08 (duplicates seen by the oracle) |
| MZ-22 | E3-M2f-3b MZ-22 re-run on PostgreSQL (LK-5 disabled) | F | **SURVIVED (equivalent)** | none (§6) |

**8 mutations: 7 DETECTED, 1 SURVIVED (MZ-22, equivalent), 0 INCONCLUSIVE; every file restored byte-identical; restored baseline (groups F, L, T): 41 checks, 0 failed.** The E3-M2f-3b in-process mutation matrix (33 mutations, `docs/evidence/e3-m2f3b/`) is unchanged and not re-run.

---

## 6. MZ-22 re-review on the PostgreSQL path

MZ-22 disables LK-5 ("eight rows, none older than the bucket ⇒ inconclusive"). It stays an **equivalent mutant**: on the PostgreSQL path the lookup returns the newest ≤ 8 evidence rows of the organization; LK-4/LK-6 confine the rows that matter to the current bucket; with seven approved keys, eight current-bucket rows must repeat a key (LK-7 ⇒ `BOUND_INCONSISTENT`), and any row with another key fails verification first (`UNVERIFIABLE_BODY`). F-09 seeded exactly that state on PostgreSQL and observed `BOUND_INCONSISTENT`; the mutation run of MZ-22 (§5) changed no outcome. It remains recorded as SURVIVED (equivalent), not as detected. The original E3-M2f-3b mutation evidence (`docs/evidence/e3-m2f3b/`) is unchanged.

---

## 7. Regressions

Suites after runs of record 1 and 2 and the mutations (run-3 waited on its hour boundary meanwhile, idle), Debug builds of this working tree, run while no gate stage was active:

| Suite | Result |
|---|---|
| Authority (full) | **1208 total, 1204 passed, 0 failed, 4 skipped** (unchanged from E3-M2f-3b) |
| Architecture (full) | **214/214** (unchanged) |
| Contract (full; includes `EnvelopeCapabilityEnforcementHostTests` 13 and `CapabilityRefusalEvidenceHostTests` 4) | Five full runs. **Run 1: 952 total, 939 passed, 1 failed, 12 skipped; the failing test's name was not captured by the output filter and the failure did not recur.** Runs 2–5: 952 total, **940 passed, 0 failed**, 12 skipped each |
| TS-M2f-01 (`PhaseC7FieldSearchExecutionTests`) in isolation | 5 × 12/12 |

**TS-M2f-01 stays open.** Its earlier failures are documented (E3-M2f-3b record §8); the unnamed failure of contract run 1 is consistent with it but **not attributed** to it (unidentified). The full contract suite is therefore **not** reported as unconditionally green. Not run: Stage B (not authorized), integrity and platform-console suites (no code change).

---

## 8. Harness corrections during exploratory runs (not product changes)

Five exploratory runs preceded the runs of record; none is a run of record. Corrections HC-1..HC-9 were made before run-1, HC-10 between run-2 and run-3. None changes approved product behaviour or the security contract:

| # | Finding | Correction |
|---|---|---|
| HC-1 | The runtime role carries `log_statement = 'none'` (product, `AuthorityRuntimeGrants`), so no T2 statement reached the server log | Statement shape from `auto_explain` (database setting of the disposable copy) plus an observing EF interceptor; `SET LOCAL` bounds proven by their server-side cancellations |
| HC-2 | The host's work loop archives only after SpiceDB reports ready (unreachable here) | Delivery verified with the unchanged `ArchiveProcessor` on the host database; the host loop's idleness recorded as an observation |
| HC-3 | A padded header (`" 2"`) arrives trimmed (`2`) | Removed from the level-1 variants; logged as a diagnostic (as E3-M2f-2G) |
| HC-4 | Harness self-deadlock: counting evidence while the harness itself held IX-B in an open `REINDEX` | Count after the release, before the retry |
| HC-5 | Windows ephemeral-port exhaustion (SocketException 10048) from one unpooled admin connection per query | Admin connections pooled |
| HC-6 | Fleet bound summed every organization of the database; floor query reports the current floor only; EF issues `SET LOCAL statement_timeout` and orders the batch UPDATE→INSERT | Expectations corrected to the tested organization set and the observed, contract-conformant facts |
| HC-7 | `docker rm -f` left the Vault image's anonymous volumes (six volumes from three exploratory runs, identified by creation time and removed) | `docker rm -f -v` |
| HC-8 | B-01 compared harness time with database time | Database-clock skew measured; scheduling and comparisons in database time |
| HC-9 | The merged harness binaries kept the Authority's IdentityModel 8.19.2 while the business clients reference 8.23 | The harness merge keeps the higher third-party version (the host and commands run from their own unmerged copies) |
| HC-10 | **Made after run of record 2** (§2): one new HTTP stack per in-process request contributed to socket-buffer exhaustion (10055) inside a host | One pooled HTTP stack shared by all in-process requests (64 connections per host); requests still concurrent |
| HC-11 | Edit scripts wrote CRLF line endings into the harness sources, scripts and documents (the repository normalizes to LF) | Normalized to LF after run-3; run-3 recorded its harness-source digests over the CRLF working copies, so `run-3/harness-sources-lf-sha256.txt` lists the LF digests and confirms identical content for every file |

---

## 9. Not executed, and why

| Item | Reason |
|---|---|
| M2F3-28 binding-inserting Prepares | Needs a bound organization and an Envelope v2 write: outside this authorization (separate owner approval, as OPEN-M2f-4) |
| M2F3-33 restore below the first binding (`EXPECTED_GAP_BD1_EVIDENCE`) | Needs a binding: outside this authorization |
| The host's own archive loop delivering evidence | Requires SpiceDB (not part of this gate); the same processor was verified directly (H-06) |
| More than two buckets in M2F3-29 | Each run crosses one real boundary |
| TQ-15 (when Npgsql sends `BEGIN`) | Not observable for the runtime role (no statement log). M2F3-69's ordering holds regardless of when `BEGIN` is sent, because `t_db` is read after the lock (B-01) |
| Azure PostgreSQL plans, timing, `clock_timestamp()` under failover (TQ-16, TQ-18 Azure, TQ-19 Azure, TQ-23 Azure) | Out of scope; not inferred from this container |

---

## 10. Technical questions and risks (updates)

| Item | Update |
|---|---|
| R-3b-1 (store SQL never run on PostgreSQL) | **Closed for Development/Testing**: executed in all three runs of record |
| TQ-1 | PostgreSQL behaviour verified: 1 s lock bound (55P03 at ~1.03 s), 500 ms statement bound (57014 at ~0.53 s) at both the catalogue check and the lookup, 2 s budget never reached |
| TQ-3 | PostgreSQL 18.0: backward IX-B scan under Limit, with and without statistics, and as a generic prepared plan; Azure open |
| TQ-10 | One CatchUp per audit revision (T-02); load at the bound not measured beyond B-03 |
| TQ-11 | Resolved for this gate: a post-commit **transaction** interceptor (not a command interceptor) produces a committed-but-unknown outcome without breaking Npgsql; the rolled-back case uses backend termination before COMMIT |
| TQ-14 / R-M2f3-13 | Lock hold 28 ms on the 2M-entry organization (L-01); T2 p95 519 ms under 6-instance contention on one organization (T-08); level-2 latency unaffected by the boundary flood (B-03) |
| TQ-15 | Open (see §9) |
| R-M2f3-16 | Not observed: the planner chose IX-B in every captured case; Azure open |
| New observation O-3G-1 | The host's work loop (archive delivery) depends on SpiceDB readiness; evidence stays Pending while SpiceDB is unavailable. Existing behaviour, unchanged; relevant to D-3 delivery expectations |
| New observation O-3G-2 | A non-recorder audit row at the next revision (privileged or buggy writer) makes every later T2 of that organization Ambiguous until resolved: evidence is lost, never duplicated (F-10); covered by R-M2f3-6/-15 |

Carried forward unchanged: OD-IX-2, BD-1, BD-2, G14 recovery, F-E2, R-M2f-9 (owner acceptance of -3i, -3b and -3G given on 2026-11-17: closed for Development/Testing only, see closure §14), TQ-16, TQ-18/19/21/23 (Azure), TS-M2f-01.

---

## 11. Recommendation

- **E3-M2f-3G: recommend acceptance for Development/Testing**, on two passing runs of record (run-1 and run-3) with run-2's single failure analysed as an environment socket exhaustion before the capability check (not a product defect). The T2 SQL, its bounds, the database clock, the catalogue check, the eight-row lookup, the write footprint, the digest chain, commit/rollback and ambiguous outcomes were executed on PostgreSQL 18 and the seven-event bound held under every fault, concurrency, restart, eviction, quota, index and boundary scenario, as proven by the independent oracle. The PostgreSQL mutations that remove a bound, the database clock, the row lock, the catalogue check, the lookup predicate or the durable decision were all detected.
- **D-3b-1, D-3b-2, D-3b-3**: verified as implemented: the refusal context reaches the recorder only through the refused request (all seven routes, real host and real historical builds); the log classification is as documented in §4 (closure text of M2F3-62/-65 annotated); in-flight coalescing produces one T2 for 40 concurrent refusals (T-07).
- **Not covered and still requiring separate decisions:** bound-organization scenarios (M2F3-28 binding inserts, M2F3-33) need separate owner authorization (as OPEN-M2f-4); OD-IX-2 (persistent rollout); Azure PostgreSQL behaviour (TQ-16, TQ-18, TQ-19, TQ-23); BD-1, BD-2 and G14 recovery; TS-M2f-01; an independent review (V2-IND-01).
- **Persistent use remains blocked** (OD-IX-2, `E3_M2F_CONTRACT_CLOSURE.md` §11.5, PD-14 monitoring routes).

## 12. Final closure: reconciled verification matrix (2026-11-17)

Owner decision, 2026-11-17: E3-M2f-3a, -3i, -3b and -3G **accepted for Development/Testing within their verified scope**; final closure documentation, validation and one controlled local SSH-signed commit authorized. Not authorized: push, merge, deployment, Azure, persistent database change, Stage B. This section reconciles the planned tests of `E3_M2F3_CONTRACT_CLOSURE.md` §10 with what E3-M2f-3i, E3-M2f-3b and E3-M2f-3G actually executed. It adds no test and changes no result.

**Status vocabulary.** *Planned*: listed in closure §10. *Passed*: executed and every assertion held. *Passed, reduced scope*: executed and passed, but not in the full form planned (the reduction is stated). *Inconclusive*: executed, but the result cannot be called green as planned. *Not executed as specified*: no dedicated test exists; any indirect coverage is named and is **not** counted as a pass. *Not executed*: not run. *Failed*: an executed check that failed. No planned closure test failed. The only failed check in a run of record was 3G's M-03 in run-2 (§2), and it passed in run-1 and run-3.

### 12.1 Closure tests (§10)

| Section | Planned | Passed | Passed, reduced scope | Inconclusive | Not executed as specified | Not executed | Failed |
|---|---|---|---|---|---|---|---|
| §10.1 Unit (M2F3-01..07, -67) | 8 | 8 | 0 | 0 | 0 | 0 | 0 |
| §10.2 In-process (M2F3-08..14 as 7, -15..-25, -68) | 19 | 17 | 0 | 0 | 2 (-22, -25) | 0 | 0 |
| §10.3 Disposable PostgreSQL (M2F3-26..31, -33, -69, -70) | 9 | 5 | 3 (-28, -29, -31) | 0 | 0 | 1 (-33) | 0 |
| §10.4 Fault injection (M2F3-34..37, -48..52, -62..66) | 14 | 14 (2 with the annotated D-3b-2 log class) | 0 | 0 | 0 | 0 | 0 |
| §10.5 Security and architecture (M2F3-38..47, -54..61, -71, -72; M2F3-42 is the mutation driver, §12.2) | 20 | 17 | 1 (-42) | 1 (-44) | 1 (-43) | 0 | 0 |
| §10.7 Index (M2F3-73..84) | 12 | 11 | 1 (-78) | 0 | 0 | 0 | 0 |
| **Total** | **82** | **72** | **5** | **1** | **3** | **1** | **0** |

Per test, where the status is not a plain pass:

| Test | Status | Where and why |
|---|---|---|
| M2F3-22 (requests rejected before the floor produce no evidence) | Not executed as specified | No dedicated test. Indirect: M2F3-46 (other exceptions pass through unrecorded, 3b host test), the architecture test that only the capability refusal carries a refusal context, and M2F3-21. Not counted as a pass |
| M2F3-25 (no summary event is ever written) | Not executed as specified | No dedicated test. Indirect: M2F3-38 (sole appender of `EnvelopeCapabilityRefused`, architecture), M2F3-54 (T2 inserts exactly one journal row and updates exactly three organization columns, T-01) and the oracle's closed-body check. Not counted as a pass |
| M2F3-28 | Passed, reduced scope | Release B writers (W-02), floor activation A-1 (W-01) and the fence (T-01) executed; **binding-inserting Prepares not executed** (bound organization outside the authorization; OPEN-M2f-4) |
| M2F3-29 | Passed, reduced scope | One real hour boundary per run (two buckets), not "several buckets" (B-03) |
| M2F3-31 | Passed, reduced scope | Delivery and outage verified with the unchanged `ArchiveProcessor` run directly (H-06, T-12); the host's own work loop was not exercised because it waits for SpiceDB (O-3G-1) |
| M2F3-33 | Not executed | Restore below the first binding needs a bound organization (outside the authorization) |
| M2F3-62, M2F3-65 | Passed | The closure text names 9254 and a negative cache; under the approved D-3b-2 both are Deferred, retried and logged 9253. The gate verified the approved behaviour (L-09..L-11, H-09, F-07); closure §10.4 carries the annotation |
| M2F3-42 | Passed, reduced scope | Mutation driver executed (§12.2), but not over every MZ identifier and with one survivor |
| M2F3-43 (delete or alter a delivered evidence row ⇒ chain or archive verification detects it) | Not executed as specified | No dedicated test. Related, not equivalent: the archive processor detected a post-restore collision (G-01..G-03), and the harness oracle (not product code) detected an altered body's digest mismatch (F-08b). Not counted as a pass |
| M2F3-44 (existing suites green, TS-M2f-01 the recorded open failure) | Inconclusive | Authority and architecture suites green. Contract suite: 3G run 1 had **one unidentified failure** (name not captured, did not recur in four further runs), and 3b's final full run failed TS-M2f-01. Neither is attributed to E3-M2f-3; the contract suite is not called green (§7) |
| M2F3-47 | Passed | In-process token test and architecture check; a real client disconnect is not producible in the test server (3b record §10) |
| M2F3-78 | Passed, reduced scope | T2-8 runtime refusal executed on PostgreSQL for absent, mismatched and INVALID IX-B (L-09..L-11, H-09, G-05); the `indisready = false` and column-order variants were classified by 3i unit tests and PostgreSQL I-11/I-13/I-16, not by a T2 run |

### 12.2 Mutations (§10.6, PD-15)

| Source | Planned / executed | DETECTED | SURVIVED | INCONCLUSIVE | Not executed |
|---|---|---|---|---|---|
| Closure MZ-01..MZ-36 (3b driver, in-process) | 36 planned; 28 executed (MZ-28 as 28a and 28b) | 27 | 1 (MZ-22, equivalent) | 0 | 8: MZ-02, -03, -09, -13, -24 (3b record §9: not expressible locally or covered by the same mechanism; guarded by ordering and architecture tests), MZ-34, -35, -36 (index command; no mutation driver was run in E3-M2f-3i) |
| 3b additions MZ-37..MZ-40 | 4 | 4 | 0 | 0 | 0 |
| 3G PostgreSQL mutations PM-01..PM-07 | 7 | 7 | 0 | 0 | 0 |
| MZ-22 re-run on PostgreSQL | 1 | 0 | 1 (equivalent, §6) | 0 | 0 |

Every mutated file was restored byte-identical (SHA-256); the restored 3G baseline passed 41/41 and the 3b restored baseline passed all declared classes. MZ-22 is recorded as SURVIVED, not reclassified. PM-01 (`transaction_timestamp()`), PM-05 (catalogue check bypassed) and PM-06 (no row lock) exercise on PostgreSQL the mechanisms of MZ-25, MZ-31/MZ-35 and MZ-24 respectively; they are recorded as PM results and **not** counted as executions of MZ-24 or MZ-35.

### 12.3 Runs of record and suites

| Item | Planned | Executed | Passed | Failed | Skipped | Other |
|---|---|---|---|---|---|---|
| 3i PostgreSQL gate | 2 runs of record | 2 | 40/40 each | 0 | 0 | demonstration D-1 outside totals |
| 3G PostgreSQL gate | 2 independent runs of record | 3 (run-1, run-2, run-3) | run-1 68/68, run-3 68/68 | run-2: 1 (M-03, environment, §2) | 0 | 2 demonstrations per run outside totals (G-04, B-01d); 1 not-executed item per run (M2F3-28 binding inserts) |
| 3b focused classes | 4 classes | 4 | 32/32, 28/28, 4/4, 20/20 | 0 | 0 | |
| Authority suite | full | full | 1204 of 1208 | 0 | 4 (real-Vault classes) | |
| Architecture suite | full | full | 214/214 | 0 | 0 | |
| Contract suite (3G) | full | 5 full runs | runs 2–5: 940 of 952 | run 1: 1 (unidentified) | 12 each (real-Vault classes) | TS-M2f-01 isolated 5 × 12/12; open |
| Stage B | — | not run (not authorized) | | | | |

### 12.4 PD-15 gate as written versus as accepted

PD-15 asks that §10.1, §10.2 and §10.5 be green, that §10.3 and §10.4 be green in two independent runs of record, and that every MZ mutation be DETECTED. As executed, the gate is **not met as written**:
- M2F3-22, -25 and -43 have no dedicated test. These three gaps were identified during this final reconciliation and had not been reported before.
- M2F3-44 is inconclusive.
- M2F3-28 ran in reduced form and M2F3-33 did not run.
- Eight MZ identifiers were not executed, and MZ-22 survived as an equivalent mutant.

The owner's acceptance is of the increments **within their verified scope**. The items above are outside that scope and stay open as **FU-M2f3-01** (dedicated tests for M2F3-22, -25 and -43), **FU-M2f3-02** (mutations MZ-02, -03, -09, -13, -24, -34, -35, -36, or a recorded decision that they are not expressible), together with the bound-organization scenarios, TS-M2f-01 and the unidentified contract failure. No product or security-contract change was made to close them.

## 13. Not claimed

No Azure, production or persistent-environment behaviour; no Azure PostgreSQL plan or performance; no recovery mechanism (BD-1, BD-2, G14 recovery open); no bound-organization behaviour; no Envelope v2 acceptance; no independent review; no closure of OD-IX-2, RE-1, R-M2f-9 or any production gate.

# E3-M2f-3b — Capability-Refusal Evidence: Implementation Record

**Status (2026-11-17):**

| Item | State |
|---|---|
| Increment | E3-M2f-3b, as specified in `E3_M2F3_CONTRACT_CLOSURE.md` (governing: §3–§9, §10, §12) on the accepted E3-M2f-3i index (`E3_M2F3I_IMPLEMENTATION_RECORD.md`) |
| Authorization | Owner, 2026-11-17: E3-M2f-3i accepted for Development/Testing; E3-M2f-3b authorized for implementation and focused verification only. Not authorized: E3-M2f-3G, E3-M2f-3c, persistent deployment or database change, migrations, tables, columns, indexes, privileges, infrastructure, commit, push, merge, Azure, Stage B, Envelope v2 acceptance |
| Baseline | Branch `e3/m1-envelope-v2-core`, HEAD `3009878f55dc4430cc90ef5092764ed01abeff80`, good SSH ED25519 signature; the working tree held exactly the uncommitted E3-M2f-3a and E3-M2f-3i work, which is preserved |
| Result | Implemented for **Development/Testing**, verified in-process (focused, host-pipeline, architecture and mutation tests; regression suites). **The new store SQL has not been executed on PostgreSQL** (that is E3-M2f-3G). **Uncommitted; owner review pending** |
| Deviations | D-3b-1 (refusal context carrier) and D-3b-2..D-3b-5 (clarifications), §2; owner acceptance requested |
| Review | Owner-led. Not an independent security review and not a certification |
| Update 2026-11-17 | Owner: conditionally accepted; D-3b-1, D-3b-2 and D-3b-3 approved subject to verification and accurate documentation. **E3-M2f-3G executed** (`E3_M2F3G_VERIFICATION_RECORD.md`): the store SQL ran on PostgreSQL 18 in the runs of record (run-1 and run-3 passing); R-3b-1 is closed for Development/Testing; no product change was needed |
| Final closure 2026-11-17 | Owner, 2026-11-17: E3-M2f-3a, -3i, -3b and -3G **accepted for Development/Testing within their verified scope**; final closure documentation, validation and one controlled local SSH-signed commit authorized. Not authorized: push, merge, deployment, Azure, persistent database change, Stage B. D-3b-1, D-3b-2 and D-3b-3 approved and verified (`E3_M2F3G_VERIFICATION_RECORD.md` §11); R-3b-2 is therefore accepted by the owner. Reconciled test and mutation accounting, including the closure tests without a dedicated test (M2F3-22, -25) and the unexecuted mutations: `E3_M2F3G_VERIFICATION_RECORD.md` §12 |

---

## 1. Scope delivered

| Item | Where |
|---|---|
| Evidence vocabulary (stream, kind, schema version, refusal code, seven entry points, workload enumeration, `WorkloadOf`, `BucketOf`), refusal context, closed body record | `src/authority/AuthorizationAuthority.Application/Features/Content/CapabilityRefusalEvidence.cs` (from E3-M2f-3i, extended) |
| Lookup decision LK-1..LK-9 and row verification | `…/Features/Content/CapabilityRefusalEvidenceLookup.cs` (new) |
| The recorder (T2, pre-filter, negative cache, in-flight coalescing, attempt limit, bounded retry, outcomes) | `…/Features/Content/CapabilityRefusalEvidenceRecorder.cs` (new) |
| Refusal context attached to the unchanged refusal | `…/Features/Content/EnvelopeCapabilityFloor.cs` (modified, one statement) |
| Evidence store port (four methods) and row record | `src/authority/AuthorizationAuthority.Application/Common/Ports/IAuthorityStore.cs` (modified) |
| PostgreSQL implementation of the port | `src/authority/AuthorizationAuthority.Infrastructure/Persistence/AuthorityStore.cs` (modified) |
| T2 SQL constants (`SET LOCAL lock_timeout = '1s'`, `SET LOCAL statement_timeout = '500ms'`, `SELECT pg_catalog.clock_timestamp()`) | `src/authority/AuthorizationAuthority.Infrastructure/Persistence/CapabilityRefusalEvidenceIndex.cs` (from E3-M2f-3i, extended) |
| Endpoint filter, endpoint metadata, observer (logs 9253/9254, counter), retry service (log 9256), composition | `src/authority/AuthorizationAuthority.Api/Composition/CapabilityRefusalEvidenceComposition.cs` (new) |
| Filter on exactly the seven routes | `…/Api/Features/Content/ContentEndpoints.cs`, `…/Catalogue/CatalogueEndpoints.cs`, `…/SearchIndex/SearchIndexBuilderEndpoints.cs` (modified) |
| Registration | `src/authority/AuthorizationAuthority.Api/Program.cs` (modified) |
| CAP-E1 text (CC-2) and the regenerated internal OpenAPI (seven response descriptions; no path, parameter, schema or status change) | `…/Api/Composition/EnvelopeCapabilityComposition.cs`, `contracts/openapi/authorization-authority-internal-v1.json` (modified) |
| Tests | `tests/authority/AuthorizationAuthority.Tests/CapabilityRefusalEvidenceWorld.cs`, `…LookupTests.cs`, `…RecorderTests.cs` (new); `tests/features/EnterpriseForms.Api.ContractTests/CapabilityRefusalEvidenceHostTests.cs` (new); `tests/architecture/EnterpriseForms.ArchitectureTests/CapabilityRefusalEvidenceRecorderBoundaryTests.cs` (new); interface stubs in `ArchiveProcessorTests.cs`, `CommandPlannerTests.cs`, `InMemoryAuthorityStore.cs`; three helpers of `EnvelopeCapabilityEnforcementHostTests.cs` widened from `private` to `internal` (no assertion change) |
| Evidence | `docs/evidence/e3-m2f3b/` (mutation results) |
| Runbook | `E3_M2B1_CARV_OPERATIONS.md` §12 (appended) |

**No migration, table, column, index, trigger, grant, privilege, configuration key, endpoint, permission or infrastructure was added.**

## 2. Conformance and deviations

| ID | Item | Contract | Implemented | Status |
|---|---|---|---|---|
| **D-3b-1** | How the refusal reaches the filter | §6.1 item 1: a sealed `EnvelopeCapabilityRefusal : AuthorityException` | `AuthorityException` is itself **sealed**, and about 55 existing assertions use `Assert.ThrowsAsync<AuthorityException>`, which requires the exact type (M2F3-44 forbids assertion changes). The unchanged exception carries a `CapabilityRefusalContext(OrganizationId, ClientCapabilityLevel)` in `Exception.Data`, attached only by `EnvelopeCapabilityFloor` (architecture test) and recognised only for the code `CLIENT_ENVELOPE_CAPABILITY_INSUFFICIENT`. It is never serialized: the exception handler writes only code, correlation, retryable and detail. The wire response is byte-identical (M2F3-07). The same context is attached whichever floor refused (CAP-E2) | **Owner acceptance requested** |
| D-3b-2 | Log classification | §12.2 listed `INDEX_UNAVAILABLE`, `CLOCK_REGRESSION`, `TIMEOUT`, `ERROR` under 9254 | These transient reasons schedule a retry, so they are reported as `Deferred` with **9253**. 9254 is reserved for "this refusal will not be recorded": persistent inconclusive reasons (`BOUND_INCONSISTENT`, `UNVERIFIABLE_BODY`, `TIME_ORDER_VIOLATION`), `ATTEMPT_LIMIT`, `ABANDONED`, `RETRY_CAPACITY` (new reason name for a full retry set) and an unapproved key. A transient reason that is finally given up appears as 9254 `ABANDONED`. 9256 reports a retry-loop failure | Clarification; owner acceptance requested |
| D-3b-3 | In-flight coalescing | §5.7 (not listed) | Added: while one T2 for a key runs in this process, further refusals of that key are `SuppressedInFlight` and consume no attempt. Without it, a burst of one key could exhaust the 256-attempt limit and starve other keys (found in review, test `ABurstOfOneKeyCostsOneAttemptSoOtherKeysStayRecordable`). Load control only; never a reason to append | Addition; owner acceptance requested |
| D-3b-4 | Ambiguous commit | D-6, TQ-12 | Every exception raised once the commit was requested (including disposal after it) is `Ambiguous`; the retry runs the full locked lookup, which reconciles against the durable journal before any append (no entry-id point read needed) | As specified |
| D-3b-5 | Budgets | §5.7, TQ-1 | `SET LOCAL lock_timeout = '1s'` before the organization lock; `SET LOCAL statement_timeout = '500ms'` after it (covers the clock read, the index check, the lookup and the commit's statements); a 2 s cancellation budget for the whole T2, linked to host shutdown | TQ-1 resolved (unexecuted on PostgreSQL) |
| — | Refusal handling for persistent inconclusive reasons | §5.7 | Negatively cached until the end of the authoritative hour (`H + 1 h − t_db` on the local monotonic clock); not retried | As specified |
| — | Attempt limit | §5.7 | 256 T2 attempts (synchronous and retries) per process per fixed window of one hour of monotonic time; a suppressed key gets one 9254 per window and is negatively cached until the window ends; it is **not** queued | As specified (§5 below) |

## 3. T1/T2 transaction boundary (evidence)

| Property | How it is established | Evidence |
|---|---|---|
| T1 unchanged | No change to the seven entry points, the floor decision, its ordering, the Release B fence or the organization lock; the only floor change attaches `Exception.Data` | Existing Authority and host suites green without assertion changes (§8) |
| T1 disposed before T2 | `await using` in the throwing method (source analysis, closure §6.1) and, executed, the host event log: for each of the seven routes T1's `Dispose` precedes T2's `Begin` | `CapabilityRefusalEvidenceHostTests.EveryCapabilityRouteRecordsOneEntryOnlyAfterTheRefusedTransactionWasDisposed` |
| T1 writes nothing | T1 calls neither `AddJournalEntry` nor `CommitAsync` | Same test |
| 409 unchanged | Byte-identical response (same correlation header) whether evidence is recorded, suppressed, deferred (index unusable) or failing (evidence port throws) | `TheRefusalIsByteIdenticalWhetherEvidenceIsRecordedSuppressedDeferredOrFailing`; MZ-04, MZ-05 detected |
| Exact T2 footprint | One scope with exactly `BeginScope, SetCapabilityEvidenceLockTimeout, LockOrganization, StartCapabilityEvidenceLookup, IsCapabilityRefusalEvidenceIndexUsable, GetNewestCapabilityRefusalEvidence, AddJournalEntry, Commit, Dispose`; the organization row differs in exactly `AuditJournalRevision`, `LatestAuditJournalDigest`, `StateModifiedOn`; one audit entry, `Pending` | `AFirstRefusalCommitsOneEntryWithTheExactFootprint`; MZ-15, MZ-16, MZ-17, MZ-21 detected |
| No ledger, fence or quarantine | Never called in T2 (fake records every call; architecture test over the recorder source) | `TheRecorderNeverTouchesTheLedgerOrAnythingElse`; architecture tests; MZ-21 detected |
| Archive delivery | The committed entry is delivered by the unchanged archive processor (`Pending` → `Anchored`) | `TheCommittedEntryIsDeliveredByTheUnchangedArchiveProcessor` |
| Request independence | The filter passes the host lifetime, never `RequestAborted` | Architecture test; MZ-06 detected |

Real PostgreSQL behaviour of T2 (row lock, `SET LOCAL`, `clock_timestamp()` after the lock, the IXP-6 query through EF's transaction, trigger footprint) is **not executed** here; E3-M2f-3G.

## 4. Deduplication and the seven-event bound (evidence)

The bound rests only on the durable, locked lookup (closure §5.4); in-process state can only skip attempts.

| Case | Evidence |
|---|---|
| Decision table LK-3..LK-9 (no rows, fewer than eight, boundary row, eight in the hour, duplicate key, regression, order violation, eight kinds of unverifiable row, other event kinds) | `CapabilityRefusalEvidenceLookupTests` (32); MZ-22, MZ-23, MZ-27, MZ-28a/b, MZ-30 |
| Repeat in the hour | `ARepeatInTheBucketCostsNoTransaction`; host repeat check; MZ-10 |
| Restart | `ARestartedProcessFindsTheEntryAndAppendsNothing` |
| Eviction | `EvictionOnlyAddsAttemptsNeverEntries` (pre-filter capacity 1) |
| Concurrent instances | `ConcurrentInstancesNeverExceedOnePerKeyOrSevenPerBucket` (3 recorders × 3 organizations × 7 keys × 4, under a real row lock in the double) |
| Hour boundary | `TheNextHourStartsANewBucket`, `AnEntryJustBeforeTheHourDoesNotBlockTheNextHour`, `TheBucketIsTheUtcHour` |
| Clock regression or failover | `AClockRegressionDefersUntilTheDatabaseTimeCatchesUp`; MZ-27 |
| Ambiguous commits (committed, not committed, restart after) | three tests; MZ-14 |
| Index missing | `AnUnusableIndexDefersAndRecordsOnceItIsUsable`; MZ-31 |
| Timeout, database errors (five failure points) | `ALookupThatExceedsTheBudgetAppendsNothing`, `ADatabaseErrorBeforeTheCommitAppendsNothing` |
| Unverifiable or inconsistent evidence | two tests; MZ-28a, MZ-29 |
| Organization isolation | `OrganizationsAreIsolated` |
| Append without decision | MZ-12 detected |

## 5. Quota and first-refusal liveness

- **Attempt limit.** 256 T2 attempts per process per hour. Each key needs one attempt per hour to be recorded; retries also consume attempts. With in-flight coalescing (D-3b-3), concurrent identical refusals cost one attempt.
- **When it binds.** On one instance, a flood of every key of more than about 36 organizations within one hour (7 × 36 = 252), plus any retries. Beyond that, the remaining keys of that hour are `QuotaSuppressed`: one 9254 `ATTEMPT_LIMIT` per key per hour, negatively cached until the window ends, **not** queued.
- **Consequence for first refusals.** A quota-suppressed first refusal is recorded only if the same key is refused again after the window resets, or another instance records it. Otherwise it leaves only the operational signal. The safety bound is unaffected. At the PB-09 envelope (25 organizations, 175 keys) the limit does not bind.
- **Other liveness losses** (closure §5.6): no index (`INDEX_UNAVAILABLE`, retried); AuthorityDb clock behind the newest entry (`CLOCK_REGRESSION`, retried until it passes); an unverifiable entry among the newest eight (persistent, Critical investigation); timeouts and errors (retried up to 1 h, then `ABANDONED`); a full retry set (`RETRY_CAPACITY`); process crash before commit (lost, D-5).

## 6. Outcomes distinguished

`Committed` (archive delivery then pending until the archive processor anchors it: delivery completed is visible as `archive_delivery_state = 'Anchored'`), `AlreadyRecorded` (suppressed duplicate found by T2), `SuppressedRecorded` / `SuppressedInconclusive` / `SuppressedPending` / `SuppressedInFlight` (no T2), `Deferred` (retry scheduled), `Ambiguous` (commit unknown; retry reconciles), `Inconclusive` (persistent), `QuotaSuppressed`, `Abandoned`, `NotAttempted`. Reported to the observer (counter `capability_refusal_evidence`, tags outcome, reason and entry point; logs per D-3b-2), never to the caller.

## 7. Focused tests

| Class | Project | Result | Closure tests covered |
|---|---|---|---|
| `CapabilityRefusalEvidenceLookupTests` | Authority | **32/32** | M2F3-01, -02, -03, -04, -15 (context per floor), -59, -60, -67 |
| `CapabilityRefusalEvidenceRecorderTests` | Authority | **28/28** | M2F3-05, -06, -16 (T1 untouched by T2), -18, -19, -20, -21 (unapproved key), -24, -27 (in-process), -37, -40, -47 (token), -48, -49, -50, -51, -52, -54 (in-process), -55, -56 (in-process), -58, -61, -62, -63, -64, -65, -66, -68, -72 (in-process), archive delivery, burst coalescing |
| `CapabilityRefusalEvidenceHostTests` | Contract (real Authority host) | **4/4** | M2F3-07, -08..-14, -17, -21, -45, -46 |
| `CapabilityRefusalEvidenceRecorderBoundaryTests` | Architecture | **20/20** | M2F3-38, -39, -40, -41, -45 (source), -47 (source), -61, -71, D-3b-1 attaching site |
| E3-M2f-3i classes (`CapabilityRefusalEvidenceIndexTests`, `…IndexBoundaryTests`) | Authority, Architecture | 40/40, 21/21 | unchanged |

**Not executed here (E3-M2f-3G, real PostgreSQL):** M2F3-26, -28, -29, -30, -31, -33, -34, -35, -36, -57, -69, -70, -83, -84, and the PostgreSQL forms of -27, -54, -56, -72.

## 8. Regression

| Suite | Before E3-M2f-3b | Final run (after the last code change) |
|---|---|---|
| Authority (full) | 1148 total, 1144 passed, 4 skipped | **1208 total, 1204 passed, 0 failed, 4 skipped** (+60 new) |
| Architecture (full) | 194/194 | **214/214** (+20 new) |
| Contract (full) | 948 total, 935 passed, 1 failed (TS-M2f-01), 12 skipped (E3-M2f-2 record) | **952 total, 939 passed, 1 failed, 12 skipped** (+4 new). The failure is **TS-M2f-01** (`PhaseC7FieldSearchExecutionTests.AChangedSnapshotIsRetriedWithinTheBoundedBudgetAndAPendingCreationNeverBlocks`, the tracked timing test). Its class in isolation five times: **12/12 each**. In an earlier full run of this increment (before the coalescing change) it passed: 952 total, 940 passed, 0 failed, 12 skipped |
| E3-M2f-2 host tests (`EnvelopeCapabilityEnforcementHostTests`) | 13/13 | **13/13**, no assertion change |

Not run: Stage B (not authorized), integrity and platform-console suites (untouched code), the E3-M2f-3i PostgreSQL gate (index command unchanged).

## 9. Mutations

Driver: `docs/evidence/e3-m2f3b/mutations.json` (scratchpad driver; one mutation at a time, affected projects rebuilt `--no-incremental`, declared classes run, exact original bytes restored and verified by SHA-256, then the restored baseline rebuilt and every declared class re-run).

| Run | Mutations | DETECTED | SURVIVED | INCONCLUSIVE | Restored | Restored baseline |
|---|---|---|---|---|---|---|
| **Final** (after in-flight coalescing) | **33** | **32** | **1 (MZ-22)** | 0 | all byte-identical | builds green; all declared classes pass |
| Superseded (before coalescing; `mutations-superseded-before-coalescing.json`) | 32 | 31 | 1 (MZ-22) | 0 | all | green |

Detected: MZ-01, -04, -05, -06, -07, -08, -10, -11, -12, -14, -15, -16, -17, -18, -19, -20, -21, -23, -25, -26, -27, -28a, -28b, -29, -30, -31, -32, -33, and new MZ-37 (floor no longer attaches the context), MZ-38 (never abandon), MZ-39 (unbounded retry set), MZ-40 (no coalescing).

**MZ-22 SURVIVED (equivalent mutant, R-3b-4):** disabling "eight rows, none older than the hour ⇒ inconclusive" changes no outcome, because LK-6/LK-4 confine all rows to the current hour and, with seven approved keys, eight such rows always contain a duplicate key, which LK-7 rejects as `BOUND_INCONSISTENT`. It is recorded as SURVIVED, not reclassified.

**Not executed:** MZ-02 and MZ-03 (recording inside T1 or before the handler returns: not expressible as a local edit without restructuring the entry points; guarded by the host ordering test and the architecture tests), MZ-09 (recording on admission: structurally impossible, the context exists only on the refusal exception; the filter's catch-only shape is pinned), MZ-13 (covered by MZ-12, the same mechanism), MZ-24 (lookup before the lock: guarded by the architecture order test), MZ-34..MZ-36 (E3-M2f-3i command; outside this increment).

## 10. Failures, skips and inconclusive checks

| Item | Classification |
|---|---|
| TS-M2f-01 failed in the final full contract run; passed in the earlier full run and 5/5 in isolation | Known timing test, unchanged, open (not attributed to E3-M2f-3b; not fixed) |
| 4 Authority skips, 12 contract skips | The real-Vault classes, as before |
| MZ-22 survived | Equivalent mutant (§9) |
| MZ-02, -03, -09, -13, -24 not executed | §9 |
| PostgreSQL execution of the new store SQL | **Inconclusive by design**: not run in this increment (E3-M2f-3G) |
| Request-abort behaviour under a real client disconnect | Not testable in the test server; covered by the architecture check and the recorder token test |

## 11. Risks and technical questions (updates)

| ID | Item | Status |
|---|---|---|
| TQ-1 | T2 lock and budget mechanism | Implemented (D-3b-5); **PostgreSQL behaviour unexecuted** (E3-M2f-3G) |
| TQ-7 | In-memory emulation | Resolved: `CapabilityRefusalEvidenceWorld` (transactional, row-locked) plus `InMemoryAuthorityStore` port methods |
| TQ-9 | Retry service lifetime, OpenAPI-generation guard | Implemented; shutdown loses pending retries (D-4) |
| TQ-12 | Commit-phase classification | Resolved: every commit-phase exception is `Ambiguous` (D-3b-4) |
| TQ-17 | Recorder obtains its state only from `LockOrganizationAsync` | Architecture order test (`TheEvidenceTransactionFollowsTheSpecifiedOrder`) |
| R-3b-1 | **The new store SQL (`SET LOCAL`, `clock_timestamp()`, IXP-6 and the index check through EF's open transaction) has not run on PostgreSQL** | Closed for Development/Testing (2026-11-17, E3-M2f-3G, `E3_M2F3G_VERIFICATION_RECORD.md`); Azure open |
| R-3b-2 | D-3b-1: the refusal context lives in mutable `Exception.Data`; only Authority code handles the exception between the floor and the filter | Accepted pending owner decision; architecture test pins the single attaching site |
| R-3b-3 | Quota-suppressed first refusals may never be recorded (§5) | Accepted (closure R-M2f3-14) |
| R-3b-4 | MZ-22 survives as an equivalent mutant: with seven keys, eight rows in one hour always contain a duplicate, which LK-7 rejects, so LK-5's "no older row" branch is not independently observable. It becomes observable if the key space grows (PD-11 extension) | Recorded; keep LK-5 explicit |
| R-3b-5 | TS-M2f-01 (timing-sensitive contract test) failed in the final full run, passed in an earlier one and 5/5 in isolation; it is not fixed and stays open | Unchanged |

Carried forward unchanged: OD-IX-2, E3-M2f-3G, BD-1, BD-2, G14 recovery, F-E2, R-A1-1..5, R-M2f-1..10, R-M2f3-1..19 (closure §11.3), R-3i-1..5, UQ-4, R-CARV-12 for other classes, FU-M2E2-MUT-01, V2-IND-01, INV-1/INV-2, OA-FE1-2, PB-08, PB-11, PB-12, Azure verification.

## 12. Recommendation

- **E3-M2f-3b:** accept for Development/Testing, subject to an owner decision on deviations D-3b-1 (refusal context in `Exception.Data`), D-3b-2 (log classification) and D-3b-3 (in-flight coalescing). Everything specified for 3b is implemented, and the in-process proof of T1/T2 separation, the footprint, deduplication, the bound and the failure paths is executed.
- **E3-M2f-3G:** recommend authorizing it next. It is the first execution of the T2 SQL on PostgreSQL 18 (`SET LOCAL` bounds, `clock_timestamp()` after the row lock, IXP-6 and the index check inside EF's transaction, the real trigger footprint), concurrency under the real row lock, legacy agents on all seven routes, restore-triggered G14 detection, the liveness case on a mature journal, and two runs of record. A refusal without bindings can be produced with the deployment floor `D = 2`, so the gate need not enable Envelope v2 acceptance; any bound-organization scenario would need the same separate authorization as OPEN-M2f-4.
- **Persistent use:** remains blocked (OD-IX-2, E3-M2f-3G, `E3_M2F_CONTRACT_CLOSURE.md` §11.5, PD-14 monitoring routes).

## 13. Not claimed

- No PostgreSQL execution of the new T2 SQL; no E3-M2f-3G result; no Azure, staging or production evidence.
- No persistent database change; no evidence recorded in any persistent environment.
- No exactly-once, at-least-once (before commit) or per-request durability; no restore-independent evidence; no BD-1 or G14 recovery.
- No independent review, certification or production readiness.

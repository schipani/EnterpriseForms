# E3-M2f-3 FU-M2f3-01 / FU-M2f3-02 — PD-15 Verification Remediation Record

**Status (2026-11-17):**

| Item | State |
|---|---|
| Increment | FU-M2f3-01 (dedicated tests for M2F3-22, M2F3-25, M2F3-43) and FU-M2f3-02 (mutations MZ-02, -03, -09, -13, -24, -34, -35, -36), the follow-ups opened by `E3_M2F3G_VERIFICATION_RECORD.md` §12.4 and `E3_M2F3_CONTRACT_CLOSURE.md` §14 |
| Authorization | Owner, 2026-11-17: test-only increment. Not authorized: production code, security-contract change, persistent databases, Azure, Stage B, migrations, privilege grants, deployment configuration, data-key bindings, Envelope v2 acceptance, commit, push, merge, deploy; OD-IX-2, BD-1, BD-2 and G14 recovery are out of scope |
| Baseline | Branch `e3/m1-envelope-v2-core`, HEAD `b50264ed6ceee6ad4a7e145c59f0a2cca3cedc1e`, good SSH ED25519 signature, clean working tree |
| Product code | **Unchanged** (`git diff HEAD -- src` is empty). Every mutation ran in a separate scratch clone; each mutated file was restored byte-identically (SHA-256 verified) and its recorded original hash equals the working-tree file |
| Result | M2F3-22 **passed** (dedicated). M2F3-25 **passed** (dedicated). M2F3-43 **failed as written**: the dedicated test shows that deletion of a delivered evidence row, and alterations that are not re-sent or lie outside the newest eight, are not detected by any product mechanism. FU-M2f3-02: MZ-02, -13, -24, -34, -35, -36 DETECTED; **MZ-09 SURVIVED** (variant MZ-09a, not equivalent); MZ-03 not executable as written (its closest expressible variant was detected and is not counted). **PD-15 is not met** |
| Review | Owner-led. Not an independent security review and not a certification |
| Final closure 2026-11-17 (Option A) | Owner: E3-M2f-3 refusal-evidence implementation workstream closed and accepted for Development/Testing; FU-M2f3-01/02/03 results accepted (MZ-09 detected, M2F3-21 passed, HC-12 accepted for both gates, MZ-22 accepted as equivalent, MZ-03 not executable as written); **M2F3-43 stays FAILED** and moves to SEC-BL-01 (`SECURITY_BACKLOG.md`) with R-FU-1; Design D preserved as a candidate only; **PD-15 remains OPEN** (`E3_M2F3_CONTRACT_CLOSURE.md` §14.1). Committed locally |
| Update 2026-11-17 (follow-up) | Owner reviewed FU-M2f3-01/02; PD-15 OPEN. Authorized and executed (§10): **FU-M2f3-03** (success-path test for M2F3-21: passed; **MZ-09a now DETECTED**; M2F3-21 → Passed); the documentation-only M2F3-43 design investigation (`E3_M2F3_DELIVERED_ENTRY_VERIFICATION_DESIGN.md`); owner dispositions for MZ-09, MZ-22 and MZ-03; HC-12 retained and the same correction applied to the E3-M2e-2 gate. No product code changed; uncommitted; owner review pending. **PD-15 remains not met** (M2F3-43) |

---

## 1. Method

- **Files added (test only):** `tests/features/EnterpriseForms.Api.ContractTests/CapabilityRefusalEvidencePreFloorHostTests.cs` (M2F3-22), `tests/authority/AuthorizationAuthority.Tests/CapabilityRefusalEvidenceNoSummaryTests.cs` (M2F3-25), `tests/authority/AuthorizationAuthority.Tests/CapabilityRefusalEvidenceRemovalTests.cs` (M2F3-43); the mutation driver `scripts/dev/e3-m2f3-fu-mutations.py`; evidence `docs/evidence/e3-m2f3-fu/`.
- **Harness correction HC-12 (verification tooling, not product):** the committed E3-M2f-3G gate refused to start at `b50264e`. Its preflight greps every tracked `*.json` for `EnvelopeV2AcceptanceEnabled` (to prove no configuration enables Envelope v2 acceptance) and now matched the check descriptions inside the twelve 3G evidence files committed in `b50264e`. `scripts/dev/e3-m2f3g-evidence-pg-gate.ps1` now excludes `docs/evidence/**` from that grep; with the exclusion no tracked configuration file matches. The first MZ-35 attempt hit this defect before the gate started (kept as `docs/evidence/e3-m2f3-fu/superseded/MZ-35-first-attempt`) and MZ-35 was re-run in full. `scripts/dev/e3-m2e2-acceptance-pg-gate.ps1` contains the same preflight and will refuse to start for the same reason; it was not run or changed in this increment.
- **Mutation isolation:** a `git clone --local --no-hardlinks` of the repository at `b50264e` in a temporary scratch directory, plus the three new test files and HC-12. Mutations were applied only there, so an interrupted run could never leave a product file mutated in the repository. Driver: one mutation at a time; anchors verified to occur exactly once; affected projects rebuilt `--no-incremental` (Release for the PostgreSQL gates); declared classes run with `--filter-class`; DETECTED when a declared check fails, SURVIVED when all pass, INCONCLUSIVE on a build failure or a missing gate report; original bytes restored in `finally`, SHA-256 verified, file touched.
- **Disposable PostgreSQL:** the accepted gates `e3-m2f3i-index-pg-gate.ps1` and `e3-m2f3g-evidence-pg-gate.ps1` (`-BigRows 200000`; the 3G gate with the two historical business builds prepared once from `git archive`). Every run used fresh `postgres:18.0` (and, for 3G, `hashicorp/vault:1.20.4`) containers: all removed, no new volume, no deadlock line, container list unchanged. No data-key binding was inserted; Envelope v2 acceptance stayed off (3G hosts run with acceptance disabled and deployment floor `D = 2`). No persistent database, the developer stack or the security-test stack was touched. Stage B was not run.
- **Test status vocabulary** as in `E3_M2F3G_VERIFICATION_RECORD.md` §12, plus *Failed*: a dedicated test executed and the requirement as written does not hold.

---

## 2. FU-M2f3-01 — dedicated tests

### 2.1 M2F3-22 — "Requests rejected before the floor produce no evidence" (§10.2)

| Aspect | Content |
|---|---|
| Requirement | A request on one of the seven capability routes that is rejected **before** `EnvelopeCapabilityFloor.RequireAsync` runs (CAP-O ordering: authentication, request reading, organization lock, Release B fence, census snapshot) must produce no capability-refusal evidence: no T2, no journal entry, no recorder state |
| Indirect coverage before | M2F3-46 host test (another error passes through unrecorded), the architecture test that only the floor attaches the refusal context, M2F3-21. None replayed a level-1 request that the floor **would** refuse and rejected it earlier |
| Dedicated test | `CapabilityRefusalEvidencePreFloorHostTests.RequestsRejectedBeforeTheFloorProduceNoEvidenceOnAnyOfTheSevenRoutes`: the real Authority host pipeline (authentication, endpoints, the evidence filter, exception mapping) with a scripted, transactional store and **capturing observers on both the floor and the recorder**. The seven requests captured from the real E3-M2f-1 clients are replayed **without a capability header** (level 1) for a **bound** organization, each with exactly one change that rejects it before the floor (table below) |
| Premise asserted | The variant's own status; never `CLIENT_ENVELOPE_CAPABILITY_INSUFFICIENT`; the floor observer saw **no** evaluation; no scope asked for the binding existence |
| Assertion | No scope called `SetCapabilityEvidenceLockTimeoutAsync` (no T2) or `AddJournalEntry`; the recorder observer saw nothing; the recorder holds no attempt, cache or pending entry (`AttemptsInWindow`, `CachedCount`, `PendingCount` all 0); the journal is empty |
| Sensitivity control | Afterwards the seven **unmodified** requests are refused by the floor (7 × `Refused` at level 1) and recorded (7 × `Committed`, one entry per entry point) |
| Result | **Passed**, 45 rejected cases (7 routes × 6 variants + 2 fence + 1 census), 5 focused runs out of 5 plus the three full contract runs (§4) |
| Violation demonstrations | V22-1 (any `AuthorityException` treated as a capability refusal) and V22-2 (the filter records every rejected level-1 request, organization from the request arguments): **both DETECTED by the new test only**; the existing `CapabilityRefusalEvidenceHostTests` stayed green under both (`docs/evidence/e3-m2f3-fu/mutations/V22-1`, `V22-2`) |
| Status | **Passed (dedicated)** |

| Variant | Applies to | Observed status (pinned) |
|---|---|---|
| no credential | 7 routes | 401 |
| forged credential (signature changed) | 7 | 401 |
| another workload's credential | 7 | 401 |
| unreadable JSON body | 7 | 422 `INVALID_COMMAND` (body read before the handler; the filter is not reached) |
| unregistered organization (lock returns nothing) | 7 | 404 |
| store unavailable at the organization lock | 7 | 503 |
| Release B fence refuses | Prepare, Commit | 503 |
| unknown census snapshot | protection census | 404 |

Limits: in-process host with a scripted store (closure §10.2 is the in-process layer); the rate limiter (429) was not exercised; not run on PostgreSQL.

### 2.2 M2F3-25 — "No summary event is ever written" (§10.2)

| Aspect | Content |
|---|---|
| Requirement | PD-07 (suppressed-repeat accounting is metrics only; summary events deferred), §9.1 ("No summary event"), §3.3 (the only permitted T2 write is one `EnvelopeCapabilityRefused` entry), §12.3 (summary events out of scope) |
| Indirect coverage before | M2F3-38 (sole appender, architecture), M2F3-54 (T2 footprint), the 3G oracle's closed-body check. None drove the suppression, window and hour-rollover paths and checked the journal after each |
| Dedicated test | `CapabilityRefusalEvidenceNoSummaryTests.NoOutcomeRetryWindowOrHourRolloverEverWritesAnythingButOneEvidenceEntryPerKeyAndBucket` on the transactional AuthorityDb double. A ledger diffs the journal and **every** organization row around **each** recorder step and takes the step's outcomes from the observer. The run drives every outcome the recorder reports: Committed; 50 × SuppressedRecorded; AlreadyRecorded (second instance); a 30-request burst (SuppressedInFlight); Deferred, 20 × SuppressedPending, a retry that still defers, the retry that records; Ambiguous not committed + its retry; Ambiguous committed + its reconciling retry; Inconclusive + 10 × SuppressedInconclusive; attempt limit (2 Committed, 2 QuotaSuppressed, repeats); Abandoned by retry capacity and by age (70 one-minute retry passes); NotAttempted; then a **new authoritative hour and a new attempt window** with every key refused 15 times on three instances; then archive delivery |
| Assertions | Per step: at most one entry, and only when an outcome requested a commit; every entry is `audit` / `EnvelopeCapabilityRefused`, verifies, belongs to the refused key and the current bucket and has exactly the §9.2 members (so no count or aggregate can travel); only the refused organization's audit revision moves, by the entries added; authorization, content, projection revisions and quarantine unchanged. Whole run: one entry per (key, bucket); exactly 7 entries in the first hour and 7 in the new hour (14); the journal holds nothing else (the only other row is the seeded fixture); at least 111 suppressed outcomes reached the observer (the metrics port) |
| Result | **Passed**, 6 runs out of 6 (plus the full Authority suite run, §4) |
| Violation demonstrations | V25-1 (a summary event committed when T2 finds the key already recorded) and V25-2 (a summary event appended with each evidence entry): **both DETECTED** by the new test (and by existing recorder tests) |
| Status | **Passed (dedicated)** |

### 2.3 M2F3-43 — "delete or alter a delivered evidence row ⇒ chain / archive verification detects it" (§10.5)

| Aspect | Content |
|---|---|
| Requirement | After an evidence entry was delivered (`Anchored`), deleting or altering its AuthorityDb row is detected by chain or archive verification |
| Product verification that exists | (1) `ArchiveProcessor` checks a **pending** row's body against its digest before delivery (`JOURNAL_BODY_DIGEST_MISMATCH`) and quarantines on an archive conflict (`ARCHIVE_BODY_MISMATCH`); it reads only pending rows. (2) The archive is create-only and readable (`ReadAsync`). (3) The recorder's lookup verifies each of the organization's **newest eight** evidence rows (own digest, closed body, order, clock); it does **not** verify the chain link to the successor. No product component re-reads a delivered audit row, compares the audit journal with the archive, or walks the audit chain (archive reads exist only for the platform journal) |
| Dedicated test | `CapabilityRefusalEvidenceRemovalTests` (7 tests) on the transactional double with the unchanged `ArchiveProcessor` and the in-memory archive; a privileged writer is emulated by editing delivered rows directly |
| Detected (4 tests, trait `M2F3-43 = Detected`) | An altered row (digest kept) made pending again → `JOURNAL_BODY_DIGEST_MISMATCH`, organization quarantined. A consistently re-digested forgery made pending again → archive conflict, `ARCHIVE_BODY_MISMATCH`, quarantined. An altered row (digest kept) among the newest eight → the next lookup is Inconclusive and appends nothing (the archive processor notices nothing). The archive keeps every delivered body byte-identically after deletion or alteration, and refuses an overwrite |
| **Not detected (3 expected-gap demonstrations, trait `M2F3-43 = ExpectedGap`)** | **Deletion** of a delivered row: no check notices; the recorder records the same key again in the same bucket at a new revision; the archive then holds two entries for one (key, bucket) and nothing compares them. A **consistently re-digested forgery that is not re-sent**: accepted by the lookup (the forged key counts as recorded, the original key is recorded again); no quarantine. An **alteration outside the newest eight**: never read, never detected |
| Result | 7 of 7 executed as designed, 6 runs out of 6 (plus the full Authority suite run). The three gap tests assert the observed non-detection so that a future verifier makes them fail; they are demonstrations, **not passes**, and are outside every pass total (the Authority suite runner counts them as passed: see §4) |
| Violation demonstrations | V43-1 (pre-delivery digest check removed) and V43-2 (archive conflict treated as delivered): **both DETECTED** by the new test (and by existing `ArchiveProcessorTests`) |
| Status | **Failed as written.** Detection holds only for re-sent alterations and for in-window alterations with an unchanged digest. Deletion and the other two cases are undetected |
| Dependency (stop and report) | Closing M2F3-43 needs either (a) a product verifier for delivered entries (an audit-chain walk or an archive-versus-journal reconciliation: a production change, not authorized here; related to G14 recovery and R-M2f3-6), or (b) an owner decision amending M2F3-43 to the detectable scope and accepting the residual risk (a security-contract change). Neither is made here |

New risk for the register: **R-FU-1**: a privileged AuthorityDb writer can delete or consistently forge a *delivered* evidence row without product detection; the archive preserves the delivered original, but nothing reports the divergence. A forged row can also suppress recording of the forged key for the rest of its bucket (at most one hour). Within the privileged-actor class A4 (G08 residual); R-M2f3-6 names only the undelivered tail.

---

## 3. FU-M2f3-02 — mutation reconciliation

Declared test classes per mutation and full results: `docs/evidence/e3-m2f3-fu/mutations/<id>/result.json` (failing tests and checks named). Every file restored byte-identically.

| Id | Invariant protected (closure) | Expressible? | Executed variant(s) | Result | Detected by |
|---|---|---|---|---|---|
| **MZ-02** record inside T1 | T1 writes nothing (CAP-O6, RE-4, M2F3-16/-17) | Yes: the floor holds T1's scope and the locked organization | **02a** floor stages the evidence entry in T1 (rolled back); **02b** stages and commits it in T1 | **DETECTED** (both) | M2F3-17 host test (`EveryCapabilityRouteRecordsOneEntryOnlyAfterTheRefusedTransactionWasDisposed`: T1 must not stage or commit); E3-M2f-2 host tests; architecture `OnlyTheRecorderAppendsTheEvidenceKind`; floor unit test |
| **MZ-03** record before `next` returns | T2 begins only after T1 is disposed (T2-1/T2-2, M2F3-17) | **Not as written.** The refusal context exists only on the exception that leaves `next`, and `await using` disposes T1 before that exception leaves the entry point; the filter cannot record before `next` returns without information it does not have | **03a** (closest expressible violation of the same invariant): the Verify entry point lets the refusal leave **before** T1 is disposed (disposal deferred 2 s), so the filter records while T1 is still open | **Not executable as written**; 03a **DETECTED** (not counted for MZ-03) | M2F3-17 host test ("verify: T2 began only after T1 was disposed") |
| **MZ-09** record on admit | Admitted traffic produces no evidence (M2F3-21) | Yes (3b called it structurally impossible; it is expressible by synthesizing the context from the request arguments) | **09a** after an admitted request **completes**, the filter records (organization from the arguments); **09b** every exception of an admitted request is recorded | **SURVIVED** (09a); 09b DETECTED | 09a: **no** declared test, and neither the full Authority suite (1216; 1212 passed, 4 skipped) nor the full contract suite (953; 941 passed, 12 skipped) detects it. 09b: M2F3-21 host test and architecture `TheRecorderIsReachedOnlyThroughTheFilterAndItsRetryService` |
| **MZ-13** append after an ambiguous commit without a lookup | D-6, L-C: the retry decides by the durable lookup (M2F3-48, -72) | Yes | the retry of an Ambiguous key ignores the lookup decision | **DETECTED** | M2F3-48 recorder test (`AnAmbiguousCommitThatTookEffectIsReconciledWithoutASecondEntry`); the new M2F3-25 test |
| **MZ-24** look up before the organization lock | A2 / M2F3-61, the bound (-27, -72) | Yes | clock, index check and lookup moved before `LockOrganizationAsync` | **DETECTED** | Architecture order test (M2F3-61); recorder footprint and concurrent-instances tests (M2F3-27/-72 in-process, duplicates); 3G gate group T: T-01 and T-05 (statement order), T-06, T-08 (the independent oracle finds 33 entries in one bucket and up to 6 per key: the seven-event bound broken), T-09 |
| **MZ-34** index command re-applies grants or runs in a transaction | M2F3-80, -81 | Yes | **34a** `AuthorityRuntimeGrants.Apply` after the build; **34b** the build inside a transaction block | **DETECTED** (both) | 34a: architecture forbidden-reference test; 3i gate I-19 (privileges changed), I-09 (trace) and eight more. 34b: architecture tests (`BeginTransaction`, own statement); 3i gate I-01 (exit 3, SQLSTATE 25001 "CREATE INDEX CONCURRENTLY cannot run inside a transaction block") and sixteen more |
| **MZ-35** an INVALID index treated as present | M2F3-62, -79 | Yes (shared classification of the command and T2-8) | `indisvalid` dropped from the usability condition | **DETECTED** | Unit `AnApprovedButUnusableIndexWithoutAVisibleBuildIsInvalid`; 3i gate I-11, I-16, I-17 (M2F3-79), I-15, I-21; 3G gate L-11 (M2F3-62: an INVALID IX-B must stop the lookup), L-12 |
| **MZ-36** predicate literal differs from the recorder constant | M2F3-77 | Yes | **36a** the CREATE statement's literal only; **36b** the shared pinned definition (CREATE and expected predicate) while the lookup keeps the constant | **DETECTED** (both) | M2F3-77 unit tests; 36a: 3i gate (24 checks, the index no longer matches); 36b: 3i plan checks I-25..I-29 and 3G L-01..L-06 (the lookup no longer uses IX-B on a mature journal) |
| MZ-22 | LK-5 | — | not re-run | **SURVIVED (equivalent)**, unchanged (`E3_M2F3G_VERIFICATION_RECORD.md` §6); no new evidence changes it | — |

**MZ-09a analysis.** Not equivalent: a successfully completed admitted request (level 2, or level 1 in an unbound organization) would append evidence through the unchanged recorder. It survives because no test drives an admitted request to a **successful** completion through the evidence filter: the M2F3-21 host test admits only requests that then fail at the next step (an exception path), and no host test replays admitted level-1 traffic of an unbound organization with an evidence check. Consequently M2F3-21 is reclassified from Passed to **Passed, reduced scope** (§5). A test that would detect MZ-09a is test-only work outside this authorization; proposed as **FU-M2f3-03**.

**Baselines.** Before the mutations, unmutated: declared classes 173/173 (Authority), 18/18 (host), 41/41 (architecture); 3i gate 40/40. After all mutations, restored: the same three class sets green; 3i gate 40/40; 3G gate groups T and L 31/31 (`docs/evidence/e3-m2f3-fu/baselines`).

---

## 4. Regression (real working tree, Debug, `--no-incremental` builds)

| Suite | Result |
|---|---|
| Authority (full) | **1216 total, 1212 passed, 0 failed, 4 skipped** (+8 new: 1 M2F3-25, 7 M2F3-43, of which **3 are expected-gap demonstrations counted as passed by the runner but not passes of M2F3-43**) |
| Architecture (full) | **214/214** (unchanged) |
| Contract (full), three runs | **953 total, 941 passed, 0 failed, 12 skipped** in each run (+1 new M2F3-22). TS-M2f-01 did not fail in these runs; it stays **open** (not fixed). The unidentified failure of 3G contract run 1 stays **unidentified**; three green runs do not explain it |
| New classes repeated (focused) | M2F3-25 and M2F3-43 classes 6 × 8/8; M2F3-22 5 × 1/1 |
| Not run | Stage B (not authorized); integrity and platform-console suites (untouched code) |

---

## 5. Updated PD-15 accounting (replaces `E3_M2F3G_VERIFICATION_RECORD.md` §12.1 / §12.2 counts; changes and reasons below)

### 5.1 Closure tests (82 planned)

*(Superseded 2026-11-17 by §10.5: M2F3-21 is now Passed and MZ-09 DETECTED. The tables below record the state at the end of FU-M2f3-01/02.)*

| Section | Planned | Passed | Passed, reduced scope | Failed | Inconclusive | Not executed | Missing dedicated coverage |
|---|---|---|---|---|---|---|---|
| §10.1 Unit | 8 | 8 | 0 | 0 | 0 | 0 | 0 |
| §10.2 In-process | 19 | 18 | 1 (-21) | 0 | 0 | 0 | 0 |
| §10.3 Disposable PostgreSQL | 9 | 5 | 3 (-28, -29, -31) | 0 | 0 | 1 (-33) | 0 |
| §10.4 Fault injection | 14 | 14 | 0 | 0 | 0 | 0 | 0 |
| §10.5 Security and architecture | 20 | 17 | 1 (-42) | 1 (-43) | 1 (-44) | 0 | 0 |
| §10.7 Index | 12 | 11 | 1 (-78) | 0 | 0 | 0 | 0 |
| **Total** | **82** | **73** | **6** | **1** | **1** | **1** | **0** |

Changes from §12.1 (72 / 5 / 0 / 1 / 1 / 3): M2F3-22 and M2F3-25 **Missing dedicated coverage → Passed** (§2.1, §2.2); M2F3-43 **Missing dedicated coverage → Failed** (§2.3); M2F3-21 **Passed → Passed, reduced scope** (MZ-09a survivor: admitted level-1 traffic and successful admitted completions are not exercised, §3); M2F3-42 stays reduced (now executed over every MZ identifier, but MZ-03 is not executable as written and MZ-09 and MZ-22 survive); M2F3-44 stays **Inconclusive** (this increment's three full contract runs were green, but TS-M2f-01 and the unidentified failure remain); the other reduced and not-executed tests are unchanged.

### 5.2 Mutations (closure MZ-01..MZ-36, counted per identifier)

| | Count | Identifiers |
|---|---|---|
| Planned | 36 | |
| DETECTED | **33** | 27 earlier (3b) + MZ-02, -13, -24, -34, -35, -36 |
| SURVIVED | **2** | MZ-22 (equivalent, unchanged); **MZ-09** (non-equivalent, variant 09a) |
| INCONCLUSIVE | 0 | |
| Not executable as written | **1** | MZ-03 (closest expressible variant 03a DETECTED, not counted) |
| Not executed | 0 | |

Also executed, outside the closure numbering: the 3b additions MZ-37..40 (4 DETECTED, unchanged), 3G PM-01..07 (7 DETECTED, unchanged), and the six violation demonstrations of §2 (V22-1, V22-2, V25-1, V25-2, V43-1, V43-2: 6 DETECTED by the new dedicated tests).

### 5.3 PD-15 as written

PD-15 requires §10.1, §10.2, §10.5 green; §10.3, §10.4, M2F3-83, -84 green in two runs of record; **every MZ mutation DETECTED**. It is **not met**:
1. M2F3-43 **failed** as written (§2.3); closing it needs a product verifier or a contract amendment (owner decision).
2. M2F3-21 passed only in reduced form, and **MZ-09 survives** (non-equivalent); a test for the admitted-success path is needed (FU-M2f3-03, test-only, not authorized here).
3. MZ-22 survives as an equivalent mutant; PD-15's "every MZ DETECTED" can be met only by an owner decision accepting the equivalence (or a key-space change that makes LK-5 observable, R-3b-4).
4. MZ-03 is not executable as written; an owner decision is needed whether the detected variant MZ-03a discharges it.
5. M2F3-44 inconclusive (TS-M2f-01 open; one unidentified contract failure).
6. Reduced or not executed: M2F3-28 (binding inserts), -29, -31, -42, -78, and -33 (bound organization, OPEN-M2f-4).

---

## 6. Remaining verification gaps

FU-M2f3-03 (proposed: a test driving admitted requests, level 2 and admitted level 1 of an unbound organization, to a successful completion through the filter, killing MZ-09a); M2F3-43 product detection of delivered-row deletion and forgery (or an owner amendment), R-FU-1; owner decisions on MZ-22 (equivalence) and MZ-03 (variant); TS-M2f-01 and the unidentified contract failure (kept separate, not attributed); M2F3-28 binding inserts and M2F3-33 (OPEN-M2f-4); M2F3-29 more than two buckets; M2F3-31 host archive loop (SpiceDB); M2F3-78 `indisready = false` and column-order variants on the T2 runtime path; the E3-M2e-2 gate preflight defect (HC-12 applies only to the 3G gate). Carried forward unchanged: OD-IX-2, BD-1, BD-2, G14 recovery, F-E2, TQ-15, TQ-16, TQ-18/19/21/23 (Azure), V2-IND-01 (independent review), PB-11, PB-12.

## 7. Specification validation

Manifest regenerated with `scripts/generate-spec-manifest.py`: 266 → 268 entries (this record and `scripts/dev/e3-m2f3-fu-mutations.py`; `docs/evidence/**` is excluded by design). `scripts/validate-specifications.py`: **BASELINE VALIDATION PASS** (normal) and **STRICT VALIDATION PASS** (`--strict`), 268 tracked, 14 approved ADRs, 75 owned pages.

## 8. Recommendation

- **Do not close PD-15.** M2F3-22 and M2F3-25 now pass as dedicated tests, but M2F3-43 fails as written, MZ-09 is a genuine survivor, and the owner decisions of §5.3 items 3 and 4 are open.
- Owner decisions requested: (1) M2F3-43: authorize a delivered-entry verifier (production change, likely with G14 recovery) **or** amend M2F3-43 to its detectable scope and accept R-FU-1; (2) authorize FU-M2f3-03 (test only); (3) accept or reject MZ-22 as equivalent and MZ-03a as discharging MZ-03; (4) accept HC-12, and whether to apply the same correction to the E3-M2e-2 gate.

## 9. Not claimed

No product or security-contract change; no persistent, Azure, staging or production evidence; no bound-organization behaviour; no Envelope v2 acceptance; no recovery mechanism; no independent review or certification; no closure of PD-15, OD-IX-2, TS-M2f-01 or any production gate.

---

## 10. Follow-up 2026-11-17: FU-M2f3-03, M2F3-43 design investigation, dispositions, HC-12

**Authorization (owner, 2026-11-17):** FU-M2f3-03 (test only); a documentation-only design investigation of M2F3-43 delivered-entry tamper detection; retention of HC-12 and the equivalent narrow correction to the E3-M2e-2 gate. Not authorized: product code, a delivered-entry verifier, any security-contract change (M2F3-43 is not amended or weakened), commit, push, merge, deploy, persistent databases, Azure, Stage B, Envelope v2 acceptance, G14 recovery, BD-1/BD-2 remediation. **Baseline** verified again: branch `e3/m1-envelope-v2-core`, HEAD `b50264e`, good SSH ED25519 signature, the uncommitted FU-M2f3-01/02 work intact (product diff empty).

### 10.1 FU-M2f3-03 — successful admitted requests (M2F3-21)

| Aspect | Content |
|---|---|
| Requirement | M2F3-21 (§10.2): "Level-2 traffic and admitted level-1 traffic produce no evidence", including requests that the check admits and that then **complete successfully** (the gap MZ-09a exposed) |
| Test | `CapabilityRefusalEvidenceAdmittedSuccessHostTests.SuccessfullyCompletedAdmittedRequestsProduceNoEvidenceTelemetryOrRetry` (contract tests). Real Authority host pipeline (authentication, endpoint, evidence filter, the real recorder, the real floor and evidence observers wrapped so their counters and logs still flow, a host-scoped log capture, a meter listener on the floor's evaluation counter filtered by organization) over a scripted transactional store. Requests: the real E3-M2f-1 builder-plane requests (deep-sweep verify, backfill) with their workload credentials |
| Traffic | Deployment floor 1: level 2 in a bound organization (no ledger read); **admitted level 1** in an unbound organization (one existence query); level 2 in an unbound organization. Deployment floor 2: level 2 in a bound organization. 8 admitted requests |
| Success paths | Deep-sweep verify: the stored entry holds another operation, so the **legitimate deep-check bookkeeping `ENTRY_CHANGED` is written and committed** in the request's own transaction; 200, result `Inconclusive / ENTRY_CHANGED`. Backfill: no covering approval; 200, result `NOT_COVERED`, nothing written. Only these two of the seven routes can be completed successfully without faking signed-content verification; the filter is the same class on all seven routes (M2F3-45), and admitted-then-failing requests on all seven routes stay covered by `AdmittedRequestsAndOtherErrorsAreNeverRecorded` |
| Assertions per request | Admitted: exactly one `admitted` floor evaluation at the request's level. Completed: 200 and the expected result. Legitimate behaviour unchanged: exactly one scope whose calls equal the pinned success path (no evidence call, no second transaction); the committed write is exactly the expected deep check (or none for backfill). No evidence: no `EnvelopeCapabilityRefused` entry, audit revision unchanged. No retry: recorder attempts, cache and pending set all 0, also 2.5 s later, and no evidence transaction appeared. No refusal telemetry: no evidence-observer outcome (so no `capability_refusal_evidence` counter), no 9250/9253/9254/9256 log |
| Sensitivity control | On the same host, the same requests without a declaration in the bound organization are refused: 2 evidence entries committed, 2 × log 9250; the evaluation counter shows exactly 8 `admitted` and 2 `refused` for the organization |
| Result | **Passed**, 6 of 6 focused runs, plus three full contract runs (§10.4) |
| Mutation | **MZ-09a DETECTED** by the new test (a second scope, an evidence T2, appears after the admitted success); the unmutated baseline passes; MZ-09b (exception path) is detected by the existing exception-path host test, not by this success-path test, as expected (`docs/evidence/e3-m2f3-fu/fu03`) |
| M2F3-21 disposition | **Reduced scope → Passed.** The written criterion (level-2 and admitted level-1 traffic produce no evidence) is now exercised for both levels, for successful completions and for admitted-then-failing requests, and MZ-09 is detected. Stated limit: successful completion is exercised on two of the seven routes (builder plane) in-process; not on PostgreSQL |

### 10.2 M2F3-43 — design investigation (documentation only)

`E3_M2F3_DELIVERED_ENTRY_VERIFICATION_DESIGN.md`. Current coverage of the seven owner cases: alteration before delivery detected only with the digest kept (re-digested: undetected and archived as the original); alteration followed by redelivery detected; undelivered-tail alteration or deletion partly (R-M2f3-6); **deletion of a delivered entry, re-digested alteration never resent, and alteration outside the newest eight: not detected**; AuthorityDb restore detected only when a post-restore append collides. Recommended: **design D**, scheduled archive-anchored chain verification (the digest of entry *k* commits to the whole chain; anchoring it in the archived bytes at *k* detects any change to delivered rows 1..k with O(N) sequential row reads and O(1) to O(log N) archive reads), checkpoints and findings create-only in an independent archive namespace, alert-only until the owner grants quarantine authority, no repair. Owner decisions OD-DV-1..7; dependencies on G14 recovery (restore classification) and archive independence; no hard dependency on BD-1/BD-2. **M2F3-43 stays Failed** until a verifier is authorized, implemented and verified; it is not amended.

### 10.3 Owner dispositions (Part C)

| Mutation | Disposition (owner, 2026-11-17) | State after this follow-up |
|---|---|---|
| **MZ-09** | Open until detected by FU-M2f3-03 | **DETECTED** (09a by FU-M2f3-03, 09b by the existing host test): closed |
| **MZ-22** | Accepted as an equivalent survivor under the existing seven-key contract; reassess if the key space changes (R-3b-4) | SURVIVED, equivalent, accepted |
| **MZ-03** | Original remains not executable as written; the executable variant MZ-03a is accepted as supplementary evidence, not counted as the original detected | Not executable as written; MZ-03a DETECTED (supplementary) |

Original mutation evidence is unchanged; the FU-M2f3-03 runs are added under `docs/evidence/e3-m2f3-fu/fu03`.

### 10.3a Harness correction HC-12 (Part D)

HC-12 is retained in `scripts/dev/e3-m2f3g-evidence-pg-gate.ps1`. The E3-M2e-2 gate (`scripts/dev/e3-m2e2-acceptance-pg-gate.ps1`) has the identical preflight; the same false positive was confirmed (all twelve tracked hits are committed 3G evidence under `docs/evidence/e3-m2f3g/`; with `docs/evidence/**` excluded nothing matches) and the identical one-pathspec exclusion applied. Only generated evidence under `docs/evidence/` is excluded; every `*.json`, `*.yaml`, `*.yml`, `*.env`, `*.bicep`, `*.bicepparam` elsewhere is still scanned, before and after each run. **Safety check** (`scripts/dev/e3-m2f3-hc12-preflight-check.ps1`, output `docs/evidence/e3-m2f3-fu/hc12/preflight-check.txt`): the HEAD and corrected functions of both gates, run against the repository and a throwaway clone with staged probe files (application settings, Bicep parameters, deployment YAML, root `.env`, documentation outside the evidence folder, tool settings, and an evidence report): **36/36**. Both versions report every configuration probe; only the uncorrected versions report the evidence probe and the committed 3G evidence. Both scripts parse without error. The E3-M2e-2 gate was not run end to end.

### 10.4 Regression (real working tree, `--no-incremental`)

| Suite | Result |
|---|---|
| Authority (full) | 1216 total, 1212 passed, 0 failed, 4 skipped (unchanged; 3 of the passes are the M2F3-43 expected-gap demonstrations, §4) |
| Architecture (full) | 214/214 |
| Contract (full), three runs | **954 total, 942 passed, 0 failed, 12 skipped** each (+1: FU-M2f3-03). TS-M2f-01 did not fail; it stays open. The unidentified 3G failure stays unidentified |
| Not run | Stage B; integrity and platform-console suites (untouched code); PostgreSQL gates (no product or gate-behaviour change beyond the preflight exclusion verified in §10.3a) |

### 10.5 PD-15 matrix (supersedes §5)

| Section | Planned | Passed | Passed, reduced scope | Failed | Inconclusive | Not executed | Missing dedicated coverage |
|---|---|---|---|---|---|---|---|
| §10.1 Unit | 8 | 8 | 0 | 0 | 0 | 0 | 0 |
| §10.2 In-process | 19 | **19** | **0** | 0 | 0 | 0 | 0 |
| §10.3 Disposable PostgreSQL | 9 | 5 | 3 (-28, -29, -31) | 0 | 0 | 1 (-33) | 0 |
| §10.4 Fault injection | 14 | 14 | 0 | 0 | 0 | 0 | 0 |
| §10.5 Security and architecture | 20 | 17 | 1 (-42) | 1 (-43) | 1 (-44) | 0 | 0 |
| §10.7 Index | 12 | 11 | 1 (-78) | 0 | 0 | 0 | 0 |
| **Total** | **82** | **74** | **5** | **1** | **1** | **1** | **0** |

Mutations per identifier (MZ-01..MZ-36): **DETECTED 34**; **SURVIVED 1** (MZ-22, equivalent, accepted by the owner); **not executable as written 1** (MZ-03; MZ-03a accepted as supplementary evidence, not counted); INCONCLUSIVE 0; not executed 0. M2F3-42 stays reduced because PD-15's "every MZ DETECTED" is met only through the two owner dispositions.

**PD-15 is not met:** M2F3-43 failed (design D not authorized or implemented); M2F3-44 inconclusive (TS-M2f-01, the unidentified contract failure); M2F3-28, -29, -31, -42, -78 reduced; M2F3-33 not executed (bound organization, OPEN-M2f-4); MZ-22 and MZ-03 rest on owner dispositions rather than detection.

### 10.6 Specification validation

Manifest regenerated: 268 → 270 entries (`E3_M2F3_DELIVERED_ENTRY_VERIFICATION_DESIGN.md`, `scripts/dev/e3-m2f3-hc12-preflight-check.ps1`; `docs/evidence/**` excluded by design). `scripts/validate-specifications.py`: **BASELINE VALIDATION PASS** and **STRICT VALIDATION PASS**, 270 tracked, 14 approved ADRs, 75 owned pages.

# SEC-BL-01 G08 / E-6 / E-7 — Implementation Record: Journal Immutability and Trusted Delivery Receipts

**Status (2026-11-18):**

| Item | State |
|---|---|
| Increment | G08 (journal immutability and privilege hardening) and the E-6/E-7 remediation (protected access and content release no longer rest on mutable AuthorityDb delivery fields). Contract amendment **A-7** (§27). Development/Testing only |
| Authorization | Owner decision **C-158** (2026-11-18): WP-5b accepted; CF-1 closed for Development/Testing (delivery-integrity scope); E-6/E-7 open and to be remediated; G08 authorized |
| Owner acceptance | **Accepted for Development/Testing** (C-159, 2026-11-18; contract A-8 A8-1). *(Updated 2026-11-19; the original status was "Pending".)* |
| Not done | Broader G14; SR-B, SR-D, SR-E; WP-8 production infrastructure; formal WP-7 acceptance; any persistent environment, persistent baseline or production infrastructure; CI workflow changes; Stage B (built, **not run**); commit, push, merge or deployment |
| Baseline | `main` at `a9b32de` with every uncommitted WP-0..WP-5b, G14-R1/R2 and C-156/C-157 change preserved. Contract §1–§20 unchanged (§A) |
| SEC-BL-01 / PD-15 / M2F3-43 / DD-10 / OI-DR-7 / CF-1 | OPEN / OPEN / FAILED (pending WP-7) / qualified Development/Testing resolution, production open / OPEN / closed for Development/Testing (C-158) |
| E-6 / E-7 | **Closed for Development/Testing against the J1 runtime database credential threat model** (C-159; A-8 A8-2, A8-3). J2, JP and J4 + J6 limitations retained (A8-4). Integrated verification: `SEC_BL_01_WP6_WP7_VERIFICATION_RECORD.md` (finding F-1 on the TR-6 cache) |
| Review | Owner-led; prepared by the implementing assistant; **not an independent review** |

Evidence tags: **[T]** executed test evidence in `docs/evidence/sec-bl-01-g08/`; **[S]** source inspection; **[I]** inferred. Source inspection is not test evidence.

---

## 1. Owner decisions recorded

C-158 (`PHASE_C_DECISION_LOG.md`): WP-5b accepted for Development/Testing; CF-1 closed for Development/Testing within its delivery-integrity scope; E-6 and E-7 remain open and require remediation; DD-10 keeps its qualified resolution; OI-DR-7 open; SEC-BL-01 and PD-15 open; M2F3-43 FAILED pending WP-7. G08 and E-6/E-7 remediation authorized with the package boundaries (§8 of the package; restated in §14 below).

## 2. Governing requirements (package §1 assessment)

| Area | Governing source | Assessment |
|---|---|---|
| Journal immutability | Contract §11 (M-6 design input), E-4/E-5 | Implementable as designed, with two clarifications made in A-7: the G14-R2 recovery hint needs a `VerificationFailed → Pending` exception to "terminal states final" (A-4 EX-2), and a plain `TRUNCATE` is refused by foreign keys before the trigger. Neither weakens §11 |
| Privilege separation | §11 constraints, F-E2, PG-01..PG-20 intent | Runtime/migration separation already existed (`--migrate` applies grants; runtime is not owner). G08 narrows the runtime UPDATE grant to columns and adds a read-only verifier role |
| Authorization receipt trust (E-6) | §3 E-6, §26.7 | The gate trusted the row state and the mutable enforcement mode. Remediation needs evidence outside AuthorityDb. The W2-S commitments (§24.7) and the pinned, create-only archive already provide it: **no new owner decision was needed** |
| Content receipt trust (E-7) | §3 E-7, SEC-P12 | Same evidence, plus a binding of the checkpoint to the head being released |
| AuthorityDb attacker capabilities | §2 J1/J2 | Unchanged. G08 constrains J1 only; J2 can bypass triggers and grants. Trusted receipts do not depend on G08 for J2: J2 still cannot write the archive or the verification store |
| Verifier read-only access | §11, WP-4 | Implemented as a dedicated `NOLOGIN` role plus optional login. Finding (§8, MV-G8-DB6): `default_transaction_read_only` is not a boundary; the grants are |

No material new contract decision was required. Rules are recorded in A-7 (§27): TR-1..TR-8, GT-1..GT-6, CR-1..CR-3, G8-1..G8-6, L-1..L-5.

## 3. Database immutability and privilege implementation (G08)

Migration `20261119021623_AuthorityJournalImmutabilityG08` (SQL in `JournalImmutabilitySql.cs`):

| Rule | Implementation [S], evidence [T] |
|---|---|
| G8-1 immutable fields | Row `BEFORE UPDATE OR DELETE` trigger `trg_security_journal_entry_guard` → `authority.security_journal_entry_guard()`: identity, organization, stream, revision, kind, command id, operation id, body, digest, previous digest, creation time compared with `IS DISTINCT FROM` |
| G8-2 transitions | `Pending → Pending` (no receipt fields), `Pending → Anchored` (reference, digest, archived-on all required), `Pending → VerificationFailed`; `Anchored` final; attempt count never decreases; `VerificationFailed → Pending` only with `DPLUS_RECOVERY_AUTHORIZED:…`, no receipt fields, same attempt count. All conditions NULL-safe (`coalesce`) after the attempt-1 defect (§7) |
| G8-3 no deletion | `DELETE` raises in the row trigger; statement `BEFORE TRUNCATE` trigger `trg_security_journal_entry_no_truncate` raises |
| G8-4 no INSERT trigger | M2F3-54 preserved |
| G8-5 grants | `AuthorityRuntimeGrants`: journal removed from the append-only blanket list; runtime gets `SELECT, INSERT` plus `UPDATE (archive_delivery_state, archive_attempt_count, archive_receipt_reference, archive_receipt_digest, journal_archived_on, last_archive_error_code)`; no DELETE/TRUNCATE/TRIGGER |
| G8-6 verifier | `authority_journal_verifier` `NOLOGIN`: `USAGE` on the schema, `SELECT` on `security_journal_entries` and `platform_journal_entries`, column `SELECT` on the organization head columns. Optional login (`Database:JournalVerifierLoginName` in `--migrate`) granted the role with `default_transaction_read_only = on` (defense in depth only) |
| Function hardening | plpgsql, SECURITY INVOKER, `SET search_path = pg_catalog, pg_temp`, owned by the migration identity, `PUBLIC` execute revoked; `SET LOCAL lock_timeout = '5s'` in the migration |

**DB enforcement versus privileged administrators.** The runtime credential (J1) cannot rewrite, renumber, reset, delete or truncate the journal [T]. An administrator without bypass is stopped by the triggers [T]; with `session_replication_role = replica` (or by disabling or dropping the triggers) it is not [T]. That is J2, and G08 makes no claim against it. Test helpers that previously tampered with journal rows as the administrator now state the J2 bypass explicitly (`AdminSqlAsync` prefix, Stage B tamper helpers).

## 4. E-6: organization gate

**Root cause [S, reproduced T].** `OrganizationGate` opened when `CountPendingArchiveAsync(authorization) == 0`, a count over the mutable `archive_delivery_state` column, and in `Asynchronous` enforcement mode (also a mutable column) it did not wait at all. J1 holds table-wide `UPDATE` on the journal (E-4), so it could set undelivered rows `Anchored` (and forge receipt reference, digest, timestamps and positions) to reopen the gate for an undelivered authorization change, including a revocation.

**Remediation [S].** GT-1..GT-6: the gate reads the latest authorization entry from the journal (`FindLatestJournalEntryAsync`, highest revision) and requires `IJournalReceiptVerifier.VerifyAsync(head).IsVerified`; the pending count is only a pre-check; the enforcement mode is ignored; the projection processor applies an entry only with a verified receipt. `TrustedJournalReceipts` implements TR-1..TR-8 (§6).

**Why the head suffices.** Under D+ an entry is archived only after its predecessor was archived and checked (DP-9), and W2-S commits the row's previous digest, so a verified head implies a delivered, contiguous prefix [S]. With G08, J1 cannot delete or renumber the head, and an inserted fake head has no receipt and closes the gate (fail closed) [T].

**Revocation freshness.** The head is read on every evaluation (one indexed query). A new head (for example a committed revocation) is never answered from cache: the cache key contains the row digest, and the gate stays `AwaitingArchive` until the new entry is delivered and its receipt verified [T: `G08ReceiptMeasurement` asserts `AwaitingArchive` for each of 20 new heads before delivery].

## 5. E-7: content release

**Root cause [S, reproduced T].** `ContentService` released a revision (workload path, `VerifyRecordCoreAsync`) when the checkpoint row was `Anchored`. J1 could forge that state.

**Remediation [S].** `ContentCheckpointReceipts.IsDeliveredAsync`: CR-1 binds the checkpoint to the head (content stream, `ContentHeadCommitted`, operation column and body, body organization, stream name and revision, record, committed revision, operation and manifest root); CR-2 requires a verified receipt; CR-3 otherwise `AwaitingArchive`. Row HMAC and manifest verification are unchanged and still follow. `ContentRevision.ArchiveState` remains an informational copy of the row state and is not a release decision (A-7 §27.3).

## 6. Trusted receipt architecture and trust boundaries

```
AuthorityDb row (claim only)                Outside AuthorityDb (proof)
  state=Anchored  ── pre-filter ──►          archive object at (org, stream, rev): SHA-256 == row digest      [create-only, J4]
  body ── SHA-256 == row digest              archive sentinel instance == pinned expected instance            [provisioning record / first checkpoint]
                                             W2-S commitment, highest stop generation, in BOTH stores,        [archive meta + verification store, J4/J6]
                                               byte-identical, naming org, stream, rev, generation, row id,
                                               row digest, previous digest, instance
  receipt ref/digest, archived-on, error code, attempt count, enforcement mode: never consulted (TR-8)
```

| Actor | Can it make an undelivered entry count as delivered? |
|---|---|
| J1 runtime DB credential | No [T]: forged state, receipt fields, timestamps, positions and mode are not inputs; immutable fields and transitions are enforced by G08 |
| J2 AuthorityDb administrator | No [T for forged fields]: J2 can bypass G08, but cannot write the archive or the verification store. J2 deleting the journal tail before delivery is detection-only (L-4) |
| J4 archive credential alone | No [T]: the commitment must also exist, identical, in the verification store |
| J6 verification-store credential alone | No [T]: the archive object and archive-side commitment are required |
| J4 + J6, or JP (runtime process with both credentials) | **Yes** — out of scope as before (L-3); store independence is a production requirement (PV-9, DD-10) |

Outages are `Unavailable` and never cached [T]. Entries without W2-S commitments fail closed (L-2). Caching is per process, keyed by (organization, stream, revision, row digest), bounded at 200 000 and cleared when full; a restart starts empty [T].

## 7. Before/after attack results

| Attack | Before (reproduced) | After |
|---|---|---|
| E-6: J1 marks undelivered authorization rows `Anchored` | Gate reopened [T in-process `KnownLimitE6…`; PG `G08ReproductionE6ForgedAnchoredStateReopensTheRealGate`] | Gate stays `AwaitingArchive` [T `E6ForgedDeliveryStateReceiptFieldsModeAndPositionNeverReopenTheGate`; PG `G08ForgedDeliveryStateByJ1OrJ2NeverReopensTheRealGate`] |
| E-6: forged receipt identifiers, digests, timestamps, positions, `Asynchronous` mode | Mode waived the wait [S]; forged fields accepted [I from the state check] | Not a receipt [T `ForgedAnchoredStateReceiptIdentifiersDigestsAndTimestampsAreNotAReceipt` ×3 streams; E-6 test] |
| E-7: J1 marks the checkpoint `Anchored` | Revision released [T `KnownLimitE7…`] | Not released [T `E7ForgedDeliveryStateAndReceiptFieldsNeverReleaseTheRevision`] |
| E-7: genuine receipt of another head | n/a (no binding) | Not released [T `E7AReceiptOfAnotherHeadNeverReleasesThisHead`] |
| J1 rewrites body/digest, resets delivery, renumbers, deletes | Succeeded [T PG `G08ReproductionJ1RewritesImmutableFieldsAndResetsDeliveryAndJ2Deletes`] | Refused [T `G08TheRuntimeCredentialCannotRewriteResetRenumberDeleteOrTruncateTheJournal`] |
| Administrator deletes / truncates | Succeeded [T same reproduction] | Refused without bypass; succeeds with `replica` (J2) [T `G08TheTriggersStopTheAdministratorUntilItBypassesThemWhichIsJ2`] |
| Evidence changed after receipt; stale cache | n/a | Not verified; cache misses on a changed digest [T] |
| Archive instance substitution; missing pin | n/a | Not a receipt [T] |
| Commitment: missing copy, contradictory copies, wrong fields, generation change | n/a | Not a receipt; the highest generation governs [T] |
| Cross-organization / cross-stream reuse | n/a | Not a receipt [T] |
| Archive outage; verification-store outage | n/a | `Unavailable`, not cached; gate closed / not released [T] |
| Restart | n/a | Receipts re-established from the stores only [T] |
| Revocation during delivery / recovery | n/a | New head closed until delivered (§4) [T]; recovery never fabricates a receipt [T `RecoveryNeverFabricatesAReceipt`]; authorization-stream stop keeps the gate closed (WP-5b PG-05, re-run in §9) [T] |
| Verifier identity writes | n/a | Refused by grants alone (read-only turned off) and by the read-only default [T, after the MV-G8-DB6 fix] |
| Legitimate transitions | n/a | All allowed; invalid ones refused [T `G08LegitimateDeliveryStateTransitionsWorkAndInvalidOnesAreRefused`] |

The pre-remediation in-process tests were replaced by the post-remediation tests named above; their passing output is preserved in `before/in-process.txt`, and the PostgreSQL reproduction (a temporary test file, removed after it ran) in `before/pg/console.txt`.

## 8. Mutation results

Driver `scripts/dev/g08-mutations.py` on a scratch clone (anchors verified unique, original bytes restored and SHA-256-checked after each mutation). In-process checks: `TrustedJournalReceiptTests`, `AuthorizationStreamGateDPlusTests`, `ContentStreamDPlusReadabilityTests`, `ArchiveProcessorTests` (41 tests). PostgreSQL checks: the gate script with `-MethodFilter '*G08*'` on a fresh PostgreSQL 18 + Azurite stack built from the mutated clone.

| Id | Mutation | Attempt 1 | Attempt 2 |
|---|---|---|---|
| BASELINE / BASELINE-PG / RESTORED | none | PASS 41/41 / PASS 5/5 / PASS 41/41 | BASELINE-PG PASS 5/5 |
| MV-G8-01 | gate ignores the head's receipt | DETECTED (E-6 test) | |
| MV-G8-02 | gate head from the mutable revision column | DETECTED (E-6 test) | |
| MV-G8-03 | content release trusts the row state | DETECTED (E-7 test) | |
| MV-G8-04 | checkpoint not bound to the head | DETECTED (E-7 other-head test) | |
| MV-G8-05 | archive object not compared with the row digest | DETECTED | |
| MV-G8-06 | sentinel instance not compared with the pin | DETECTED | |
| MV-G8-07 | W2-S commitment not checked | DETECTED (2 tests) | |
| MV-G8-08 | an `Anchored` row is a receipt | DETECTED (12 tests) | |
| MV-G8-09 | cache not keyed by the row digest | DETECTED | |
| MV-G8-10 | archive outage counts as delivered | DETECTED | |
| MV-G8-DB1 | trigger skips body/digest immutability | DETECTED (administrator without bypass; the runtime role is already stopped by the column grant) | |
| MV-G8-DB2 | `Anchored` not final | DETECTED | |
| MV-G8-DB3 | `VerificationFailed` changes freely | DETECTED | |
| MV-G8-DB4 | deletion not refused | DETECTED | |
| MV-G8-DB5 | runtime table-wide UPDATE | DETECTED | DETECTED |
| MV-G8-DB6 | verifier role granted INSERT/UPDATE | **SURVIVED** | **DETECTED** |

**MV-G8-DB6, attempt 1 (preserved).** The verifier test's write attempts ran with the login's `default_transaction_read_only = on`, which refused them before any privilege check, so the grants were never tested. That setting is a session default the login can turn off itself; it is not a security boundary. Fix (test only, no product change): every write is also attempted with `SET default_transaction_read_only = off` and must fail with `42501`, and `has_table_privilege` / `has_any_column_privilege` assert the role's privileges directly. Attempt 2: DETECTED; the baseline still passes. Results: `mutations/attempt-1-summary.json`, `mutations/attempt-2-summary.json`.

## 9. PostgreSQL 18 / Azurite results

| Run | Result |
|---|---|
| `pg-attempt-1/` (G08 subset, first run) | 2 failures, preserved: (1) a `VerificationFailed → Pending` update with a NULL error code was accepted, because `NULL LIKE …` is NULL and the trigger's `NOT (…)` did not raise — a trigger defect, fixed with `coalesce`; (2) the administrator's plain `TRUNCATE` was refused by foreign keys (`0A000`) before the trigger — the test now accepts `0A000` and exercises the trigger with `TRUNCATE … CASCADE` |
| `pg-attempt-2/` | G08 tests 5/5 |
| `pg-azurite-final-run-1/` (run of record, 20 000 rows) | 52 total, **50 passed, 2 skipped (the two opt-in measurements), 0 failed** [T]: every WP-4/WP-4A/WP-5a/G14-R2/WP-5b method plus the 5 G08 methods (with the strengthened verifier test). Ran partly concurrently with the in-process regression suites, so its timings (21 min 50 s) are not performance evidence |

## 10. SEC-P and full regression

| Suite | Result |
|---|---|
| Authority (in-process, Debug) | 1 803 total, **1 734 passed, 69 skipped** (PG/Azurite and opt-in measurement), **0 failed** [T `authority-final.txt`] |
| Architecture | **226/226** [T `architecture.txt`], including `ProtectedAccessAndReleaseRestOnTrustedReceiptsNotOnMutableDeliveryFields` and `JournalImmutabilityTriggersAndGrantsAreHardened` |
| Contract | 958 total, **946 passed, 12 skipped, 0 failed** [T `contract.txt`] |
| SEC-P01..SEC-P12 | **13/13** [T `sec-p-final/`] on a fresh disposable security-test stack built from these sources. Afterwards every journal entry was delivered: audit 16, authorization 60, content 25 `Anchored`; 0 of 16 organizations quarantined; both G08 triggers present (so the migration was applied; an added migration-history query failed on identifier quoting and is kept as run); `down -v` removed the stack. The `enterpriseforms/{authority,worker,api,dev-seed,platform-console}:local` tags were saved as `:pre-g08-saved` before the build and re-pointed afterwards; restored image IDs verified equal, temporary tags removed; `:dev` tags untouched |
| Stage B | Built only (tamper helpers updated for the J2 bypass); **not run** (no release authorization) |

## 11. Performance

Measured on real stores: disposable PostgreSQL 18.0 and two Azurite 3.9.0 instances, loopback, one process. The machine was otherwise idle. Synthetic data, so these are Development/Testing figures: Azure latency is unverified **[NV]**. Harness: `G08ReceiptMeasurement` (`measurement/g08-receipt-measurement.json`) and `WP5BDPlusDeliveryMeasurement` (`measurement/dplus-measurement-audit-authorization-content.json`, `measurement/trigger-ab/`). A first invocation passed two method patterns as one string; the runner rejected the filter, so that invocation measured nothing. Its re-run used a single pattern.

| Metric | Before (pre-G08 equivalent) | After | Notes |
|---|---|---|---|
| Authorization gate evaluation, open scope (200 samples) | p50 3.74 / p95 4.74 / p99 7.37 ms (pending count only) | **warm** p50 7.70 / p95 9.13 / p99 11.26 ms; **cold** p50 130.0 / p95 173.6 / p99 241.6 ms | Warm cost +3.96 ms p50: one indexed journal-head query; the cached receipt needs no store call. Cold is paid once per new head per process (or after a restart) |
| Store operations per cold receipt | 0 | **6**: archive `ReadEntry`, `ReadIdentity`, `ListMeta`; provisioning `ReadRecord`; checkpoints `Read`; store `List` | Bounded and constant; there is no per-request full-journal verification |
| New head (revocation) refresh, 20 heads | — | Before delivery: `AwaitingArchive` (asserted). First evaluation after delivery: p50 134.3 / p95 174.3 ms; second: p50 7.65 ms | Freshness: a new head is never served from cache |
| Concurrency, 16 workers × 25 evaluations | — | Warm: 601.8 evaluations/s, p50 10.4 / p95 20.4 / p99 26.4 ms. Cold (fresh verifier per worker): 235.1/s, p50 8.1 / p99 1 245.8 ms | The cold p99 comes from 16 simultaneous first misses: there is no single-flight, so each worker establishes the same receipt (R-G8-8) |
| Content release check (checkpoint lookup + decision; 200 samples) | p50 3.91 / p95 4.33 ms (row state) | **warm** p50 3.98 / p95 4.80 ms; **cold** p50 126.7 / p95 168.1 ms | CR-1 binding in-process: p50 0.006 ms. HMAC/manifest verification of the content is unchanged and not included |
| Cache | — | 22 receipts after the run (22 store establishments); limit 200 000 | |
| Delivery throughput (D+ drain, 3 streams) | WP-5b measurement-3: 18.7 / 23.5 / 13.6 entries/s (backlog 32/256/2048) | This session: 9.0 / 10.7 / 7.0 entries/s; store operations per entry **identical** (5.219 / 5.246 / 5.250) | The difference is the environment, not G08. Back-to-back A/B/A in this session (A = migration without triggers): A 8.8 / 10.6, B 9.2 / 10.6, A 9.4 / 10.7 entries/s (backlog 32/256); steady-state pass p50 A 475 / B 484 / A 481 ms. Trigger cost is within run-to-run noise |
| Delivery steady state | WP-5b: pass p50 475.2 ms, 39 ops/pass | p50 471.8–483.6 ms, 39 ops/pass | Unchanged |

No unrelated optimization or checkpoint-growth work was done (OI-DR-10 unchanged).

## 12. Remaining risks and production blockers

| # | Item |
|---|---|
| R-G8-1 | J2 is unconstrained by G08 (triggers and grants can be bypassed). J2 cannot forge receipts, but J2 deleting the journal tail before delivery is detection-only (D-R `BEHIND_ARCHIVE`/gaps) |
| R-G8-2 | J4 + J6 together, or a compromised runtime (JP), can fabricate receipts. Production requires independently administered stores (PV-9, DD-10 production open) |
| R-G8-3 | Entries without W2-S commitments fail closed. A store whose latest authorization or content checkpoint predates W2-S (for example the persistent Development stack) would deny protected access/release until a separately authorized action. The persistent stack was not touched |
| R-G8-4 | `default_transaction_read_only` on the verifier login is defense in depth only (§8) |
| R-G8-5 | The F-E2 grant re-application window (`--migrate`) applies to the new column grants unchanged |
| R-G8-6 | The platform journal is outside E-6/E-7 (L-5); `ContentRevision.ArchiveState` is informational |
| R-G8-7 | Azure PostgreSQL (`azure_pg_admin`), managed-server trigger semantics, real store independence and latency on Azure are unverified **[NV]** |
| R-G8-8 | Cold receipts have no single-flight: N concurrent first evaluations of one new head each reach the stores (6 operations each; cold p99 1.2 s at 16 workers). Correctness is unaffected; a production-latency concern only |
| Inherited | SEC-BL-01, PD-15, G14 (SR-B, SR-D, SR-E, SC-R3 production blocker), OI-DR-7, OI-DR-9, M2F3-43 (WP-7), WP-8, independent review |

## 13. Recommendation

**Recommendation: close E-6 and E-7 for Development/Testing, within the J1 scope**, if the owner accepts this evidence. The acceptance evidence supports it:

- the attacks were reproduced before remediation, on the in-process and PostgreSQL paths [T];
- the same attacks fail after it [T];
- every mutation that removes or bypasses a receipt, binding, trigger or grant protection is detected, the one survivor after a test fix [T];
- the PostgreSQL/Azurite gate, SEC-P01..SEC-P12 and the full Authority, architecture and contract suites pass [T];
- the performance cost is bounded and measured [T].

Conditions to record with any closure:

- **(a)** The scope is J1, J4 alone and J6 alone. J2 is detection-only for tail deletion; J4 + J6 together and JP are out of scope (L-3, L-4).
- **(b)** Entries without W2-S commitments fail closed (L-2). This needs an owner decision before the persistent Development stack runs this code.
- **(c)** Production needs independent stores, Azure verification and an independent review.

**G08:** accept for Development/Testing as a J1 hardening. It is **not** a control against J2.

**Unchanged:** SEC-BL-01 and PD-15 remain OPEN, M2F3-43 remains FAILED pending WP-7, and DD-10 and OI-DR-7 are unchanged.

## 14. Contract amendments, files and evidence

**Amendment:** A-7, contract §27 (TR-1..TR-8, GT-1..GT-6, CR-1..CR-3, G8-1..G8-6, L-1..L-5); status row updated. §1–§26 unchanged.

**Files changed (uncommitted):**
- Application: `Common/Ports/IJournalReceiptVerifier.cs` (new), `Common/Ports/IAuthorityStore.cs` (`FindLatestJournalEntryAsync`, `JournalReceipts`), `Common/OrganizationGate.cs`, `Features/Journal/TrustedJournalReceipts.cs` (new), `Features/Content/ContentCheckpointReceipts.cs` (new), `Features/Content/ContentService.cs` (two release sites), `Features/Projection/ProjectionProcessor.cs`.
- Infrastructure: `Persistence/JournalImmutabilitySql.cs` (new), migration `20261119021623_AuthorityJournalImmutabilityG08` (+ Designer; model snapshot unchanged), `Persistence/AuthorityRuntimeGrants.cs`, `Persistence/AuthorityStore.cs`, `AuthorityInfrastructureRegistration.cs`; Api `Composition/MigrateCommand.cs` (verifier login).
- Tests: `TrustedJournalReceiptTests.cs` (new), `AssumeDeliveredTestReceipts.cs` (new test double), `JournalVerificationPostgresIntegrationTests.G08.cs` and `.G08Measurement.cs` (new), updates to `AuthorizationStreamGateDPlusTests`, `ContentStreamDPlusReadabilityTests`, `InMemoryAuthorityStore`, `CapabilityRefusalEvidenceWorld`, `BindingExistenceQueryTests`, `ContentAcceptanceRuleGuardTests`, `DiscoveryCompletenessTests`, `ProtectionReconciliationTests`, the PG partials (`AdminSqlAsync` J2 prefix, `ReceiptStore`, real-gate helper), architecture `JournalVerificationBoundaryTests`, contract `CapabilityRefusalEvidenceAdmittedSuccessHostTests`, Stage B `SearchAccessProjectionStackTests` / `SearchAccessValueProjectionStackTests` (J2 prefix).
- Scripts: `scripts/dev/g08-mutations.py` (new); `sec-bl-01-wp4-pg-azurite-gate.ps1` (`-MethodFilter` accepts several patterns).
- Documentation: this record; contract A-7; decision log C-158; `SECURITY_BACKLOG.md` progress row; `SEC_BL_01_WP5B_IMPLEMENTATION_RECORD.md` cross-reference; `SHA256SUMS.txt` / `spec-files.json`.

**Evidence** (`docs/evidence/sec-bl-01-g08/`): `before/`, `pg-attempt-1/`, `pg-attempt-2/`, `pg-azurite-final-run-1/`, `mutations/`, `sec-p-final/`, `measurement/`, `authority-final.txt`, `architecture.txt`, `contract.txt`. Local paths scrubbed; synthetic data only; Development/Testing evidence, not production evidence.

## 15. Not claimed

- No closure of SEC-BL-01, PD-15, G14, OI-DR-7, OI-DR-9, E-6 or E-7 (E-6/E-7 closure is an owner decision on this record). M2F3-43 stays FAILED pending formal WP-7 verification; it is not reclassified.
- No claim that database privileges or triggers stop an AuthorityDb administrator (J2), or that they eliminate AuthorityDb write attacks; G08 narrows J1. Detection is not presented as prevention.
- No production enablement, production database hardening, store independence, Azure evidence or independent review. The persistent Development stack, its data and its baseline were not touched. Stage B was not run.

### A. Contract preservation

§1–§20 (file without the status row at line 15, up to "## 21.") hash to `3549e3ba…f8f2` before and after A-7.

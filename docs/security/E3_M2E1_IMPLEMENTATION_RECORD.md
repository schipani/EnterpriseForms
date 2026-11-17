# E3-M2e-1 — Dark Data-Key Ledger, CARV 3, Floor 3 and Default-Off Acceptance Switch: Implementation Record

**Status.** Implemented for **Development/Testing only** on `e3/m1-envelope-v2-core`, on top of the signed E3-M2d commit
`a1127a109b5596e65cd3ac7f66a4ae4b35df2eb7`, together with the E3-M2e contract-closure documents. **Owner-accepted for Development/Testing on
2026-11-16 (§14), with one signed local commit authorized; E3-M2e-2 is not authorized.** Owner-led work; not an independent review, not a
certification, not production-ready.

- **Dark:** Envelope v2 acceptance is **off** everywhere and was never enabled, in any test or gate run.
- **Floors:** floor 3 (and floors 1 and 2) were installed **only in disposable PostgreSQL containers**.
- **Untouched:** no persistent database, Release B configuration, Azure service or deployment. Stage B did not run.

Governing documents:
- `E3_M2E_CONTRACT_CLOSURE.md` (OD-M2e-01..16, AM-1..AM-12, ledger L-1..L-14; owner-approved);
- `E3_M2B_IMPLEMENTATION_CONTRACT.md`;
- `E3_M2_AUTHORITY_DATABASE_CONTRACT.md`;
- `E3_M2D_IMPLEMENTATION_RECORD.md`.

Evidence: `docs/evidence/e3-m2e1/`.

**Addendum (2026-11-16): F-E1 remediated by RB-CARV-1 amendment A-1** (contract §22.14; §13 below). Development/Testing only. The results in §9 are the pre-A-1 runs; §13 holds the A-1 runs. **Owner decisions OA-FE1-1..3 (2026-11-16): §14.** F-E2 is tracked separately in `F-E2_MIGRATE_GRANT_WINDOW_DEFECT.md` (open).

---

## 1. Summary

| Area | Delivered |
|---|---|
| Ledger | `authority.content_data_key_bindings`. Ordinary migration `20261116031706_AuthorityContentDataKeyBindings`, which installs **no floor** (AM-1). Global unique UUIDv7 identifier, CHECKs, RESTRICT FKs, P-1 binding guard, P-2 immutability (UPDATE/DELETE/TRUNCATE), runtime SELECT/INSERT only, guarded Down. No P-3 |
| Prepare | Unchanged M2d refusal of New v2 groups while acceptance is off. With acceptance on (never in this increment): DK-4 after authorization and before signing (OD-M2e-12), with L-13 first; DK-5 bindings inserted with the operation in the Prepare transaction |
| Commit | Replaces the M2d blanket v2 refusal (OA-M2d-1), in this order: DK-6 and P-4 from the verified staged bytes and the ledger (mismatch: abort and a minimal content-stream incident); then the switch re-check (New groups refused while off); then DK-8 |
| CARV | `CurrentVersion = 3`; `RecognizedFloors = [1, 2, 3]`; marker `carv-v3`. Exact P = V and C = P unchanged |
| Floor 3 | Floor-context migration `20261116040000_AuthorityContentAcceptanceRuleFloor3`. AM-1 preconditions include the ledger objects. Reinstatement SQL. Registry, fence, policy, command and readiness generalized |
| Switch | `Content:EnvelopeV2AcceptanceEnabled`, default off. Startup refusals outside Development/Testing (Staging included), without Release B `RequiredFloor = 3`, and without the ledger migration. Effective only after the exact policy is recorded. Bounded Testing override (method only, no endpoint). Wrap codes {1, 2} only in Development/Testing (AM-9) |
| Policy observation | `EnvelopeAcceptancePolicyObserved` on the **existing platform journal**; no new table (UQ-3, §6) |
| Journal | `ContentHeadCommitted` body gains `new_data_key_bindings` (the complete tuples; always present from CARV 3) |
| Store | L-13 `RequireDataKeyLedgerAsync`; catch-all covers bindings (R-CARV-26); 23505 translated to `422 ENVELOPE_INVALID: DATA_KEY_IDENTIFIER_REUSED`; P-1/P-2 refusals mapped to `503 DATA_KEY_LEDGER_INVARIANT_VIOLATED`; required-migration readiness (AM-12) |

Not delivered (by decision): BD-1, BD-2, E3-M2e-2 activation testing, M2f, product v2 writers, business changes, P-3.

---

## 2. Files

**Authority, new:**
- `Domain/Features/Content/ContentDataKeyBinding.cs`;
- `Application/Features/Content/DataKeyLedgerRules.cs`, `DataKeyLedgerFailures.cs`, `EnvelopeV2AcceptanceSwitch.cs`, `EnvelopeAcceptancePolicyObserver.cs`;
- `Infrastructure/Persistence/ContentDataKeyLedgerSql.cs`, `ContentAcceptanceFloor3Sql.cs`, `DataKeyLedgerRefusals.cs`;
- migration `Migrations/20261116031706_AuthorityContentDataKeyBindings` (+ Designer);
- floor migration `FloorMigrations/20261116040000_AuthorityContentAcceptanceRuleFloor3` (+ Designer);
- `Api/Composition/EnvelopeV2AcceptanceComposition.cs`.

**Authority, changed:**
- `ContentService.cs`, `EnvelopeV2Rules.cs` (classification split, `CommitViolation` retired, wrap-code helper), `ContentAcceptanceRules.cs`;
- `IAuthorityStore.cs` (ports), `AuthorityException.cs` (three codes);
- `AuthorityStore.cs`, `AuthorityDbContext.cs`, `AuthorityEntityConfigurations.cs`, `AuthorityRuntimeGrants.cs`;
- `ContentAcceptanceFloor2Sql.cs` (registry), `ContentAcceptanceRuleSchemaReader.cs`, the model snapshot, `Program.cs`.

**Tests:**
- new `ContributorWriteProtocolTests.DataKeyLedger.cs`, `DataKeyLedgerTests.cs`;
- extended `EnvelopeV2RulesTests.cs`, the in-memory store, two port fakes, the CARV/floor/policy/guard tests (§9.3), architecture `EnvelopeV2RuleBoundaryTests` and `SearchIndexBoundaryTests`, contract `AuthorityContentAcceptancePolicyStartupTests`.

**Gate:** `scripts/dev/e3-m2e1-ledger-pg-gate.ps1`; `tools/experiments/e3-m2e1-ledger-gate/` (HarnessE1, LegacyAgent, README).

**Documents:**
- this record;
- `E3_M2B1_CARV_OPERATIONS.md` §9;
- `trust_authority/trust-authority.openapi.yaml` header note (no endpoint or schema change);
- the spec manifest (regenerated).

No business source, no BusinessDb migration, no vector file and no ordinary migration other than the ledger changed.

---

## 3. Ledger schema and constraints (closure §3.1)

| Object | Definition |
|---|---|
| Columns | `content_data_key_binding_id` (PK), `data_key_identifier`, `organization_id`, `form_record_id`, `envelope_payload_kind_code` (smallint), `data_key_commitment_bytes`, `binding_item_count`, `binding_content_change_operation_id`, `data_key_bound_on` |
| Unique | `uq_content_data_key_binding_identifier (data_key_identifier)`: global (OD-M2e-01) |
| CHECKs | `ck_content_data_key_binding_kind` (`IN (1, 2)`), `_commitment` (32 bytes), `_count` (≥ 1; a document is exactly 1), `_identifier_v7` (version nibble 7, RFC 9562 variant) |
| FKs | organization (`fk_content_data_key_binding_org`) and operation (`fk_content_data_key_binding_operation`), both RESTRICT. EF models the operation relationship, so the operation is inserted first (gate E-09 adds the binding to the scope before the operation) |
| Indexes | `ix_content_data_key_bindings_record (organization_id, form_record_id)`, `ix_content_data_key_bindings_operation` |
| P-1 | `trg_content_data_key_binding_guard` BEFORE INSERT: the operation must be `Prepared`, in the same organization and record, with P IS NOT NULL AND P ≥ 3. Otherwise 23514 with the trigger's name |
| P-2 | `trg_content_data_key_binding_permanent` (BEFORE UPDATE OR DELETE, row) and `trg_content_data_key_binding_no_truncate` (BEFORE TRUNCATE, statement). Both always raise |
| Functions | plpgsql, `SET search_path = pg_catalog, pg_temp`, not SECURITY DEFINER, `REVOKE ALL … FROM PUBLIC`; no brace in any of the SQL |
| Grants | runtime `SELECT, INSERT` (`AuthorityRuntimeGrants.InsertOnlyTables`) |
| Down | refuses while any binding exists, then drops the triggers and functions before EF drops the table |

G08: no separate inventory file lists individual functions; the generic inventory query (`trust_authority/postgres-privilege-inventory.sql`) reports the two new functions and their (non-)SECURITY DEFINER status unchanged.

---

## 4. Prepare and Commit

### 4.1 Prepare (`ContentService.PrepareCoreAsync`)

1. Org lock → Release B fence → gate → idempotent replay (CARV rule unchanged).
2. CAS → parse → **AM-9 wrap codes** → snapshot proof → v1 cross-revision rules.
3. **`EnvelopeV2Rules.ClassifyAgainstCurrent`** (DK-1, DK-3, downgrade guard, current-v2 wrap separation, all unchanged). With acceptance off, any New row is refused `ENVELOPE_VERSION_NOT_ENABLED` here, at the E3-M2d position, so the OA-M2d-3 codes are unchanged.
4. Item identities → classification and authorization → protection barrier.
5. **DK-4** (only with acceptance on): L-13, then ledger lookup, then `DATA_KEY_IDENTIFIER_REUSED`.
6. Signing → operation (P = 3) → **DK-5** bindings → candidate leaves → one commit.

A 23505 on the unique index rolls the whole transaction back (L-3).

**Reachability.** DK-4 and DK-5 run only when acceptance is on, which this increment never allows. Their evidence is:
- the pure rules (`Dk4…`, `Dk5…` in `EnvelopeV2RulesTests`);
- the store-level registration on real PostgreSQL (gate E-09..E-11);
- the architecture placement check (`Dk4RunsAfterAuthorizationAndBeforeSigningAndDk5InsertsWithTheOperation`).

ContentService-level Prepare acceptance is E3-M2e-2 work.

### 4.2 Commit (`ContentService.CommitCoreAsync`)

1. Lock → fence → gate → committed replay → state → expiry → **CARV (P ≠ 3 → `OPERATION_RULES_OUTDATED`)** → quarantine → head CAS → authorization.
2. `VerifyStagedAgainstLeavesAsync` → S-9 → S-4 → v1/v2 shape and batch rules **+ AM-9 wrap codes** (abort `ENVELOPE_INVALID_AT_COMMIT`).
3. The ledger block:
   - L-13 when any v2 row is staged or any binding of this operation exists;
   - **DK-6** (the bindings of this operation equal the derived New groups: identifier, organization, record, kind, commitment, count);
   - **P-4** (every other v2 group is bound to a Committed operation of this organization and record, with the same kind and commitment, and staged rows ≤ the binding count).
4. On a mismatch:
   - abort `DATA_KEY_BINDING_MISMATCH`;
   - advance the content-integrity revision (W4);
   - journal `IntegrityVerificationFailed` with the identifier-free `IntegrityIncidentBody` (reason `DATA_KEY_BINDING_MISMATCH`; the existing kind keeps `SearchAccessJournalVerifier` valid);
   - commit, then `409 DATA_KEY_BINDING_MISMATCH`.
5. **Switch re-check:** New groups with acceptance off → abort `ENVELOPE_VERSION_NOT_ENABLED_AT_COMMIT`.
6. Item identities → manifest → head → C = 3 → index → **DK-8** body → checkpoint.

**Order:** DK-6/P-4 run before the switch re-check, following the OA-M2d-5 precedent. A forged or inconsistent operation is therefore an integrity incident whatever the switch says, and DK-6 is testable while acceptance is off (owner review item D-1, §10).

**P-4 limit (not claimed):** P-4 does not prove that a carried row comes from the current revision rather than an older revision of the same record (DK-1 at Prepare, CARV 3 and the head CAS cover that; R-M2e-3).

---

## 5. DK-4..DK-8 and P-4 evidence

| Rule | Unit (pure) | ContentService (acceptance off) | Real PostgreSQL | Removal check |
|---|---|---|---|---|
| DK-4 | `Dk4RefusesANewIdentifierBoundAnywhereInAnyStateAndAdmitsAFreshOne` | not reachable (off) | E-08 (global unique), E-10/E-11 (store refusal, race) | ME-01 detected |
| DK-5 | `Dk5BindsEachNewGroupToTheOperationOrganizationRecordKindCommitmentAndCount` | not reachable; placement: architecture | E-09 (one transaction, EF order) | ME-02 detected (architecture) |
| DK-6 | `Dk6AcceptsOnlyBindingsOfThisOperationThatEqualTheStagedNewGroups` | `Dk6ANewGroupMatchingItsBindingIsStillRefusedAtCommitWhileAcceptanceIsOff`, `Dk6AForgedOrInconsistentOperationIsAbortedWithAMinimalIncident` (6 variants) | E-17, E-18, E-19 | ME-05, ME-06, ME-12 detected |
| DK-7 | — (AM-2: Prepare classification + CARV 3 + CAS) | `AVersion2PreparedOperationCannotBeCommittedByAVersion3Build` | E-35 | ME-14 detected |
| DK-8 | `Dk8ArchivesTheCompleteTuplesInIdentifierByteOrder` | `Dk8EveryCommitArchivesTheBindingTupleArray…` (empty array; digest chain via `SearchAccessJournalVerifier`) | E-15 | ME-13 detected **only by the source-shape architecture check**; a non-empty body needs acceptance on (E3-M2e-2) |
| P-4 | `P4AcceptsACarriedGroupOnlyWhenBoundToACommittedOperation…` (positive and 6 negatives) | `P4ACarriedGroupThatTheLedgerDoesNotConfirmIsAbortedWithAnIncident` (6 variants), plus the two updated carried-v2 tests | E-17 (unbound) | ME-07, ME-08, ME-09 detected |
| L-13 | `TheLedgerVerificationRefusesEveryWeakenedObjectInAFixedOrder` | `TheLedgerIsVerifiedBeforeItIsConsulted…` | E-12, E-13 (5 weakenings), E-14 (lock), E-20 | ME-11, ME-24 detected |
| P-1 / P-2 | SQL text test | in-memory emulation | E-03..E-07, E-05 (owner/superuser) | ME-25 (text), ME-P1, ME-P2 (database) detected |

The positive Commit of a carried v2 group (P-4 satisfied, acceptance off: OD-M2e-07(c)) is implemented but **not executed** here. It commits v2 content, and M2-ENV-16 through Commit is assigned to E3-M2e-2. Only its pure decision is tested.

---

## 6. Policy observation and UQ-3

**Inspection.** The platform journal (`platform_journal_entries`, hash-chained, archived, replayed by the operator plane) accepts any event kind with an empty state delta, so replay is unaffected. It had no "latest entry of a kind" port and no index on the event kind. A query for the most recent policy event is therefore not bounded by itself.

**Implemented without a new table** (the owner's preference): a new port reads the newest **64** entries through the existing unique revision index (a bounded read). Under the head lock, after `EnsureCurrentAsync` (not behind the archive, not quarantined, archive reachable):
- the most recent `EnvelopeAcceptancePolicyObserved` entry inside the window is trusted only if its body matches its digest and records exactly the current policy digest;
- otherwise a new event is appended.

The bound can only cause an **extra** record, never a missing one. Durable history is the journal and its archive. The switch keeps only the digest of what was recorded and treats acceptance as effective only when that digest equals the current policy (fail closed); that gate state is not history.

Tests: `ThePolicyIsRecordedOnce…`, `ATamperedPreviousRecordIsNotTrusted…`, `TheBoundedWindowCanOnlyCauseAnExtraRecord…`; removals ME-17..ME-19 detected.

**Operational consequence (for owner review, D-6).** The background loop records the effective policy at every Authority start whose policy differs from the last recorded one. With the switch off (all environments today), the first start of this build writes one platform event (needing the archive). Failures are logged and retried, never fatal: an unrecorded policy can only keep acceptance off.

**Alternatives if the owner prefers:** (a) add an index on `(journal_event_kind, platform_journal_revision)` to the ledger migration (exact latest lookup, no table); (b) record only when acceptance is configured on.

---

## 7. CARV 3 and floor 3

**CARV 3:**
- Prepare stamps 3, Commit stamps 3, and the database enforces C = P (gate E-15).
- A version-2 Prepared operation is aborted `OPERATION_RULES_OUTDATED`.
- Committed (1,1), (2,2) and NULL/NULL operations replay first (unchanged).
- Marker `carv-v3`; a stale `carv-v1`/`carv-v2` is replaced.
- Readiness `content acceptance rules v3`.

**Floor 3 (AM-1).** Up runs in one transaction:
1. lock timeout 5 s, then `ACCESS EXCLUSIVE`;
2. the M2b-1 fence complete;
3. floor 2 installed, recorded and alone;
4. the ledger migration recorded, and the unique index and the three triggers enabled with exactly their renderings, plpgsql, not SECURITY DEFINER;
5. no unexpired operation with P < 3;
6. no writing session with a `carv-v` marker below 3;
7. add `floor_v3` NOT VALID and drop `floor_v2`.

Down raises. Reinstatement drops any remaining lower floor.

Gate evidence:

| Check | Result |
|---|---|
| E-22 | floor 3 before 1 and 2 → exit 2 |
| E-29 | `--migrate` never installs it |
| E-30 | a `carv-v2` session blocks the raise |
| E-31 | an in-flight P = 2 operation blocks the raise |
| E-32 | a weakened ledger blocks the raise |
| E-33 | applied under 4 writers; exact definition; three history rows; every outcome OK or fail-closed; committed P = C |
| E-34 | Release B `RequiredFloor = 3` ready and writing |
| E-35 | the CARV-2 build is refused |
| E-36 | a direct P = 2 insert is refused |
| E-37 | monotonic |
| E-38..E-41 | missing or lowered floor 3 → Release B refusals, exit 4, reinstatement |

---

## 8. Acceptance switch: fail-closed evidence

| Property | Evidence |
|---|---|
| Default off; only the literal `true` enables; off never refuses a start | `OnlyTheLiteralTrueEnables…` (7 values); host `TheComposedHostHasTheSwitchOffByDefault…` |
| Refused start outside Development/Testing | `ConfiguredOnOutsideDevelopmentOrTestingRefusesTheStart` (Production, Staging, Unknown); host `AStagingHostRefusesToStartWithAcceptanceConfiguredOn` |
| Requires Release B `RequiredFloor = 3` | `ConfiguredOnWithoutReleaseBFloor3RefusesTheStart` (4 policies); host `ADevelopmentHostRefusesToStartWithAcceptanceOnInReleaseA` |
| Requires the ledger migration | host `ADevelopmentHostRefusesToStartWithAcceptanceOnWhenTheLedgerMigrationCannotBeVerified`; readiness `ContentDataKeyLedgerMigrationMissing` |
| Transactional floor verification | Release B fence (unchanged) plus L-13 in every ledger-using transaction |
| Effective only once recorded | `AcceptanceIsNeverEffectiveBeforeItsExactPolicyIsRecorded` |
| Testing override bounded | `TheTestingOverrideIsTestingOnly…` |
| Environment wrap codes | `WrapCodesAreAdmittedOnlyInDevelopmentOrTesting`; `OutsideDevelopmentOrTestingNoV2RowIsAdmitted…` (Prepare and Commit) |
| Downgrade guard preserved | unchanged tests, plus gate E-26 |
| No fallback | removals ME-15..ME-17 detected |

`AcceptanceIsNeverEffectiveBeforeItsExactPolicyIsRecorded` sets the Testing override on a switch **object** to prove that a not-yet-recorded policy stays off. No content is accepted, and no `ContentService` ever ran with acceptance on.

---

## 9. Results

### 9.1 Disposable PostgreSQL (`scripts/dev/e3-m2e1-ledger-pg-gate.ps1`)

One fresh `postgres:18.0` container per run (tmpfs, loopback, removed). The build under test is this tree (Release, LF-normalized). The frozen builds are `git archive` Release builds of `a1127a1` (CARV 2) and `8915f77` (CARV 1).

**Runs of record:** `docs/evidence/e3-m2e1/pg-gate/record-1` and `record-2`. **Each: 44 checks, 43 passed, 1 failed (E-33b); container removed.** *(Pre-A-1. After A-1 the same gate passed 44 of 44 in three runs: §13.)*

| Group | Checks | Result |
|---|---|---|
| Ledger migration and database invariants | E-00..E-08 | pass |
| Real EF store, L-3, race, L-13, lock | E-09..E-14 | pass |
| ContentService, acceptance off: CARV 3, DK-6/P-4, incidents, switch re-check, Down guard | E-15..E-21 | pass |
| C-14 | E-22..E-28 | pass: **the CARV-1 build accepts a v2 → v1 owner downgrade on floor 1 (E-25) and is refused on floor 2 (E-28); this build refuses it (E-26)**. R-M2e-5 is now evidenced |
| Floor 3 and Release B floor 3 | E-29..E-42 | pass, **except E-33b** |
| **E-33b availability during the raise** | 1 | **FAIL in both runs:** 23 of 24 concurrent Release B writes committed; 1 refused `CONTENT_ACCEPTANCE_FLOOR_NOT_ENFORCED` (`FloorUnverifiable`); the server log shows exactly one `deadlock detected` on the fence facts statement. §10 F-E1 |

Development runs (not of record):
- Run 1 found the E-33 availability failure.
- Runs 2–5 added diagnostics (alert reasons, the server log). The E-33 check was then split into E-33 (security invariants) and E-33b (availability), without weakening either.
- Run 3's container was left running after a script error in the new log capture; that container was removed by hand and the script fixed.
- The touched files were found to have CRLF line endings, written by the edit scripts. They were normalized to LF, and every suite and both runs of record were repeated on the LF tree.

### 9.2 Suites (final LF tree; test executables run directly; Stage B not run)

| Suite | Before (E3-M2d) | Now |
|---|---|---|
| Authority | 972 / 0 / 4 | **1028 passed / 0 failed / 4 skipped** (1032) |
| Contract | 908 / 0 / 12 | **912 / 0 / 12** (924) |
| Architecture | 149 / 0 | **151 / 0** |
| Integrity | 73 / 0 | **73 / 0** |
| Vector files | — | unchanged (no diff) |

Skips are the real-Vault classes, unchanged.

### 9.3 Existing tests changed (for owner review; no assertion weakened)

- **CARV 2 → 3.** Readiness strings `v2` → `v3`; markers `carv-v2` → `carv-v3`, with a `carv-v2` stale-marker case added; "floor above the build" 3 → 4; `RequiredFloor` "3" now valid and "4" unsupported; "floor 2, floor 3 required" `FloorUnverifiable` → `FloorLowered`, with a "floor 4 required" case added; the floor context lists three migrations; the `CurrentVersion` assertions are 3.
- **Readiness fixtures** gain `DataKeyLedgerApplied: true`. Two new cases: missing, and unknown (both not ready).
- **"The CARV migration is the latest"** becomes "the ledger migration is the latest, the CARV migration precedes it, and neither installs a floor".
- **M2d carried-v2 Commit tests** (`AnOwnerEdit…`, `AShareEdit…` renamed `…WithoutALedgerBindingIsNotCommitted`, and `AtCommitATiedSchemaTwoRowIsStillRefused`). The expected refusal changes from `ENVELOPE_VERSION_NOT_ENABLED_AT_COMMIT` to `DATA_KEY_BINDING_MISMATCH` with an incident, because those heads were accepted outside the ledger. They still prove Prepare passes (OA-M2d-2) and nothing commits.
- **`CommitRefusesEveryV2RowAndNothingElse`** becomes `OnlyV2RowsFormDataKeyGroupsAtCommit`.
- **Architecture:** the v2 boundary tests follow `ClassifyAgainstCurrent` and `CommitDecision`. The revision-site count is 3 → 4 (the DK-6/P-4 incident is a new W4-style site).

### 9.4 Enforcement-removal checks (`docs/evidence/e3-m2e1/mutations/`)

- **Unit mode** (Authority + architecture focused classes): **26 / 26 detected.**
  - ME-13 (DK-8 tuples dropped) was first not detected. It is now detected only by a source-shape architecture assertion; its behavioural evidence belongs to E3-M2e-2.
- **Database mode** (full gate per mutation): **5 / 5 detected.**
  - ME-P1 (no binding guard): E-01, E-06, E-12, E-13.
  - ME-P2 (no TRUNCATE trigger): E-01, E-05, E-08, E-12, E-13.
  - ME-P3 (non-unique index): E-01, E-08, E-10, E-11, E-12, E-13.
  - ME-P4 (no ledger refusal translation): E-10, E-11.
  - ME-P5 (no floor-3 ledger precondition): E-32.
- Every mutated file was restored and verified by sha256. One unit run was stopped mid-mutation (a background run at risk of the shell time limit). ME-01 was found applied and restored from the driver's backup, all 12 files were compared byte-identical to their backups, and the run was repeated in foreground chunks.
- The mutation runs preceded the LF normalization; the recorded sha256 values are of the CRLF working copies (noted in the JSON).

---

## 10. Findings and items for owner review

| ID | Item | Detail | Requested |
|---|---|---|---|
| **F-E1** | Floor activation deadlocks with concurrent Release B writers | The floor command's EF migration lock (`LOCK TABLE ef_migrations_history IN ACCESS EXCLUSIVE MODE`, Npgsql provider) and its floor Up (ACCESS EXCLUSIVE on `content_change_operations`) can deadlock with a Release B write. The write holds ROW EXCLUSIVE on `content_change_operations` and then reads `ef_migrations_history` in the fence facts statement. PostgreSQL aborts the writer; the fence fails closed (`503 CONTENT_ACCEPTANCE_FLOOR_NOT_ENFORCED`, `FloorUnverifiable`, Critical alert). **Never an acceptance.** By construction the same command and fence are used for floors 1 and 2 (accepted RB-CARV-1 code, unchanged here), so the hazard applies to them too; this was inferred from the code, not re-run for floors 1 and 2. The M2d D-F6 runs presumably passed by timing. Reproduced in every run of this gate (1 of 24 writes) | Decide the remedy. **Not changed here:** it alters accepted RB-CARV-1 behaviour. Options: (a) the fence reads the history before taking its lock; (b) the floor command takes the table lock before EF's history lock (or does without EF's migration lock); (c) classify a deadlock as a retryable availability refusal; (d) accept as documented availability residual (operator: raise floors in a write pause). Until decided, E-33b stays a failing check. *(2026-11-16: option A in a stricter form, A-1, implemented (§13) and owner-accepted for Development/Testing (OA-FE1-1, §14).)* |
| D-1 | Commit order | DK-6/P-4 before the switch re-check (OA-M2d-5 precedent; the closure does not fix the order) | Confirm |
| D-2 | DK-4/DK-5 reachability | Reached only with acceptance on. Evidenced by pure, store-level, PostgreSQL and architecture tests; ContentService-level Prepare acceptance is E3-M2e-2 | Confirm |
| D-3 | Carried v2 with acceptance off | Implemented per OD-M2e-07(c) (P-4 decides); the positive path is not executed (it commits v2; E3-M2e-2) | Confirm |
| D-4 | M2d test expectations | §9.3: carried-v2 commits of heads accepted outside the ledger are now ledger mismatches with an incident (previously a silent abort) | Accept |
| D-5 | `CommitViolation` retired | Replaced by the ledger-backed Commit policy | Accept |
| D-6 | Policy event at every first start | §6 (one platform event per distinct policy; needs the archive; non-fatal when off) | Accept, or choose §6 (a)/(b) |
| D-7 | Testing override | Method only; no operator endpoint (no E3-M2e-1/-2 caller) | Accept |
| D-8 | L-13 scope | Verified in transactions that insert a binding or consult the ledger (UQ-2 minimum), not on every content write | Accept |
| D-9 | AM-9 scope | Applies to every v2 row, carried included, at Prepare and Commit | Accept |
| D-10 | DK-8 body | `new_data_key_bindings` entries carry `content_data_key_binding_id`, `data_key_identifier`, `envelope_payload_kind_code`, `data_key_commitment_hex` (lowercase hex), `binding_item_count`, `data_key_bound_on` (UTC, microseconds). Organization from the stream; record and operation from the body (UQ-1) | Accept |
| D-11 | Gate script | Also saves the container's server log in evidence | Accept |
| D-12 | Migration ids | Generated `20261116031706_…` (ledger); floor 3 `20261116040000_…` (after the ledger) | Note |

---

## 11. Risks and stop conditions

**Updated:**
- **R-M2e-5:** C-14 is now evidenced (E-25, E-28).
- **R-M2e-10:** the in-memory store emulates the unique index and P-1 (with scope rollback). Real behaviour is proven by the gate.
- **New R-M2e-13 (F-E1):** the floor-activation deadlock, an availability fault. *(2026-11-16: remedied in Development/Testing by A-1, owner-accepted (OA-FE1-1); §13, §14. Not production-closed.)*
- **New R-M2e-15 (F-E2, 2026-11-16):** the ordinary `--migrate` re-applies the runtime grants in separate autocommit statements; concurrent writes are refused fail closed, and a committed Commit can be reported as failed (§13). **Open**, tracked separately (OA-FE1-3; `F-E2_MIGRATE_GRANT_WINDOW_DEFECT.md`).
- **New R-M2e-14:** DK-8 non-empty content and the positive carried-group Commit are untested behaviourally until E3-M2e-2.

**Carried forward (none closed):**
- R-M2e-1..4, -6..-9, -11, -12 (BD-1, BD-2);
- R-M2d-1..8; R-M2c-2, R-M2c-6, R-M2c-11; R-S4-1/-3/-4; R-M2b2-1..10; R-CARV-11, -12, -18, -21..-27;
- R-M1-1..8; R-M2a-1..6;
- V2-IND-01; PB-03; PB-11/PB-12; RB-CARV-1 production controls; Azure PostgreSQL definition verification (now including the ledger index and trigger renderings).

**Stop conditions (closure §8.3; owner list).** None was hit:

| Condition | Evidence |
|---|---|
| No new v2 row became authoritative with acceptance off | E-16, E-18; ME-03, ME-10 detected |
| The runtime cannot modify or delete bindings | E-04, E-05 |
| Registration is atomic | E-09, E-10 |
| The unique constraint cannot be bypassed | E-08, E-11 |
| No unbound or incorrectly bound New group commits | E-17, E-19 |
| No CARV-2 operation commits | E-35; unit test |
| Floor enforcement is not bypassable | E-36..E-41 |
| `--migrate` never installs floor 3 | E-00, E-29 |
| S-4, S-9 and the downgrade guard hold | unchanged suites; E-26 |
| No new table; no persistent database or Azure; no M2f, BD-1 or BD-2 needed | — |

F-E1 is an availability defect of the accepted floor mechanism, not a stop condition. *(Remedied by A-1, §13.)*

---

## 12. Not claimed

- No Envelope v2 acceptance in any environment.
- No floor 3 outside disposable PostgreSQL.
- No BD-1 or BD-2.
- No proof that archived tuples alone establish complete ledger recovery.
- No Azure, staging or production evidence; no independent review; no production readiness.

---

## 13. Addendum: F-E1 remediation, RB-CARV-1 amendment A-1 (2026-11-16)

**Status.** The owner conditionally accepted this implementation, withheld commit authorization pending F-E1, and authorized a narrowly scoped correction. It is implemented for **Development/Testing only**. *(2026-11-16: A-1 owner-accepted for Development/Testing and included in the signed E3-M2e-1 local commit; §14.)* The normative record is `E3_M2B_IMPLEMENTATION_CONTRACT.md` §22.14 (root cause, verified wait-for graph, options A–D, invariant argument, residual risks). This section records only what changed in this increment and its results.

**Change (A-1):**
- the Release B write fence takes `LOCK TABLE authority.ef_migrations_history IN ACCESS SHARE MODE` before its operations-table lock;
- the floor command sets `lock_timeout = '5s'` on its open connection before EF's migration lock.

Every party now takes the migration history before `content_change_operations`. Nothing else changed: the facts SQL, `Evaluate`, the floor migrations and preconditions, the ledger (L-13), CARV, DK rules, the switch and policy observation are untouched.

**Files:**
- `ContentAcceptanceWriteFence.cs` (`HistoryLockSql`, documentation);
- `AuthorityStore.cs` (one statement in `RequireContentAcceptanceFloorAsync`);
- `ContentAcceptanceFloorCommand.cs` (`SessionLockTimeoutSql`, open connection);
- tests: `FloorLockOrderTests.cs` (architecture, new, 3 checks) and `ContentAcceptancePolicyTests.cs` (+1);
- harness `FloorDeadlockScenarios.cs` (new, `M2E_MODE=fe1`) and small hooks in `HarnessE1/Program.cs` (partial class, optional interceptor, evidence file names; the default gate is unchanged);
- gate script options (`-Mode`, `-NewBinaries`, `-Iterations`, `log_lock_waits`);
- this record, contract §22.4 (dated notes) and §22.14, and the operations guide.

**Results (`docs/evidence/e3-m2e1/fe1/`):**

| Check | Before A-1 | After A-1 |
|---|---|---|
| Full gate, E-33 and E-33b (original scenario) | E-33b failed in both runs of record | **44 of 44 in 3 of 3 runs**; 0 `deadlock detected` lines |
| Deterministic interleavings DA / DB / D2 (floor 3 writer-victim, floor 3 floor-victim, floor 2) | Cycle reproduced in all three; writer refused `FloorUnverifiable`, or floor command exit 3 | No cycle; the floor command waits at the history lock; write OK; raise OK |
| Floors 1 → 2 → 3 under concurrent writers, repeated | 6 iterations: every iteration failed; floor command aborted in 4 floor-1, 1 floor-2 and 1 floor-3 raise; writers aborted in 5 raises | **20 of 20** iterations; 4,093 concurrent floor-2/3 writes committed, 0 refused; 0 deadlocks |
| Lock timeout (writer held in its fence) | exit 3 at the operations lock | exit 3 (`55P03`) at the history lock after ≈6 s; nothing applied; queued writer and held writer both commit; re-run succeeds |
| History missing, corrupted, table missing; floor recorded but missing; restore to floor 2; CARV-2 build | — | all refused fail closed or reinstated as specified (H-1..H-8) |
| Ordinary `--migrate` under load (FM) | refusals and deadlocks | no deadlock; refusals in the grant window (F-E2); 8 of 20 iterations fail FM on F-E2's post-commit misreport |

**Enforcement removal:** MA-1, MA-2 and MA-3 were each detected by PostgreSQL checks and an architecture check; all were restored by hash and rebuilt. A first MA-3 run was invalid (it ran on a stale binary) and was discarded (§22.14.6).

**Suites after A-1:**
- Authority **1029 / 0 / 4** (1033);
- contract **912 / 0 / 12** (924);
- architecture **154 / 0**;
- integrity **73 / 0**.

**E3-M2e-1 confirmations (unchanged by A-1, re-run):**
- the ledger writes stay inside the fenced Prepare transaction (E-09..E-11, E-13, E-14);
- the DK-6/P-4 incident is durable under the permitted abort (E-17, E-19);
- policy observation never makes acceptance effective before its record (`AcceptanceIsNeverEffectiveBeforeItsExactPolicyIsRecorded` and three related unit tests);
- Envelope v2 acceptance was never enabled (the switch is never configured; `EnvelopeRules.AllowedEnvelopeVersions = 1`);
- the mutated files were restored by hash;
- every disposable container was removed.

**Risks:**
- R-M2e-13 is remedied (Development/Testing);
- new: R-A1-1..5 (contract §22.14.7) and R-M2e-15 / F-E2;
- every other risk is carried forward unchanged.

---

## 14. Owner acceptance of F-E1 / A-1 and commit authorization (2026-11-16)

The owner accepted the F-E1 remediation for **Development/Testing** and authorized **one signed local E3-M2e-1 commit** after final verification. Owner-led acceptance; **not** an independent security audit, **not** Release B deployment approval, **not** production closure of F-E1 or RB-CARV-1.

| # | Owner decision |
|---|---|
| **OA-FE1-1** | RB-CARV-1 amendment **A-1 is approved for Development/Testing**: consistent history-before-operations lock ordering (the Release B fence takes `ef_migrations_history` in ACCESS SHARE before `content_change_operations` in ROW EXCLUSIVE, matching EF's migrator), and the floor command's session `lock_timeout = '5s'` before EF's migration lock |
| **OA-FE1-2** | A **verified A-1-capable deployment inventory** is required before **any persistent floor raise** (every Release B instance confirmed to run a build containing A-1). The CARV connection marker (`carv-v3`) alone is **insufficient**: it cannot distinguish an A-1 build from the pre-A-1 CARV-3 tree, and `8915f77` / `a1127a1` builds lack A-1 (R-A1-1) |
| **OA-FE1-3** | **F-E2 is tracked separately** as an open migration-availability and post-Commit response defect (`F-E2_MIGRATE_GRANT_WINDOW_DEFECT.md`). It does **not** block this commit. It is not implemented in this increment |

**What the acceptance rests on (Development/Testing evidence, `docs/evidence/e3-m2e1/fe1/`):**

- **Verified root cause.** A lock-order inversion between EF's migration transaction (ACCESS EXCLUSIVE on `ef_migrations_history`, then ACCESS EXCLUSIVE on `content_change_operations`) and a Release B writer (ROW EXCLUSIVE on `content_change_operations`, then ACCESS SHARE on `ef_migrations_history` in the fence facts read). Established from the floor command's statement log, `pg_locks` / `pg_blocking_pids` and PostgreSQL's own deadlock report (contract §22.14.1). Both victims were observed; both outcomes failed closed.
- **Corrected order.** Every party now takes the migration history before the operations table (contract §22.14.3–§22.14.4).
- **Three passing full PostgreSQL gate runs** with A-1 (`gate-a1-run1..3`): 44 of 44 checks each, E-33 and E-33b passing, 0 `deadlock detected` lines.
- **20 focused concurrency iterations** (`a1`): floors 1 → 2 → 3 raised under concurrent writers in 20 of 20; 2,070 + 2,023 concurrent floor-2 and floor-3 writes committed; deterministic interleavings DA, DB, D2, lock-timeout and history checks pass; **0 deadlocks observed** (server log and `pg_stat_database`). The 8 failing checks are all F-E2 (`FM`).
- **Enforcement removal:** MA-1, MA-2, MA-3 each detected; all files restored by hash.

**Remaining unverified cases (carried, not closed by this acceptance):**

- **R-A1-2, catch-all fence paths:** no path that touches `content_change_operations` before the store catch-all fence exists today (code inspection only); architecture checks constrain the explicit fence, not every future path.
- **R-A1-3, other migration lock interactions:** ordinary migrations that alter tables writers lock before the fence (the organization row) can still form history → organization vs organization → history cycles; predates A-1, untested.
- Three-party interleavings (reinstatement plus an EF migration plus writers): untested.
- **R-A1-5, provider dependence:** the order relies on EF/Npgsql taking the migration lock first; a provider upgrade could change it (detected by the fe1 gate, not by the source-shape checks).
- R-A1-4 (privileged history DML) and R-CARV-18 (Azure PostgreSQL verification): unchanged.
- No Azure, staging or production evidence.

**F-E1 closure versus E3-M2e-2:**

- F-E1 is **remedied for Development/Testing** by A-1. It is **not** independently security-audited and **not** production-closed; RB-CARV-1 remains not production-closed and every §22.13 release gate is unchanged.
- This acceptance authorizes **only** the E3-M2e-1 local commit. It does **not** authorize E3-M2e-2 (acceptance activation testing), enabling Envelope v2 acceptance, installing any floor in a persistent environment, BD-1, BD-2, E3-M2f or F-E2 work. Each needs a separate owner decision.

# Phase C6 — Record lifecycle and history: implementation status

Branch `phase-c/record-lifecycle` from main `f859e42`. Architecture: `docs/architecture/PHASE_C6_RECORD_LIFECYCLE_AND_HISTORY.md` (revision 1), approved for controlled development by the owner on 2026-10-22 (decision **C-43**). Implementation decision: **C-44**.

**Milestone 0 (field-protection status-count stabilization, architecture §1.1) — provisionally accepted by the owner; final M0 commit `505b452`** (code `4a2bdca`; test-harness fixes `a6fa495`, `505b452`; documentation-only commits follow it). Milestone 1 (record lifecycle) is documented in §7. AC-1 and AC-2 are approved but **not started**; AC-3 is deferred; nothing is merged to main.

**What this is not:**
* not an independent security review;
* not a production approval or Level 2 certification;
* not a fix for G19: the record list the migration checks still comes from the business database.

All testing used synthetic data. Docker suites ran only on disposable GitHub-hosted stacks. The owner's persistent development stack (Vault, PostgreSQL, SpiceDB, signing keys) was not touched, stopped or rebuilt.

## 1. The defect, confirmed

The two hosted failures recorded in `PHASE_C5_STATUS.md` §12.3 (run 39765425164) were re-read from their logs:

| Attempt | Test | What the log shows |
|---|---|---|
| 2 | `FieldProtectionMigrationTests.ConcurrentUserEditsAndAWorkerRestart…` | Waited 300 s for `fullyEncrypted`; the migration was `Completed` (the worker's Authority post-check had passed) but the status stayed `EncryptionMigrationIncomplete` with a legacy plaintext count of 1. A **permanently** stale projection, not slowness. |
| 1 | `FieldProtectionDowngradeTests.InterruptedAndTamperedMigrations…` | `legacyEncryptedRecordCount` was 2; the Authority-accepted heads hold 1 (only the tampered record). |

Both match causes F-A (the Authority committed but the business projection update was lost) and F-C (the worker's no-op path never repaired the projection) in architecture §1.1.2, with F-B (fallback to the highest staged revision) and F-D (tests checking the estimate against itself) as contributing causes.

A further defect was found while implementing: a migration created when the local estimate found nothing to convert was marked **Completed and verified at creation**, without any Authority check. With a stale projection this could claim "all listed records encrypted" while an accepted head still held plaintext — the unsafe direction. Fixed (§2).

## 2. What changed

| Step | Change | Files |
|---|---|---|
| S3 — never guess | `AcceptedHeadProjection.Resolve`: Resolved / NoAcceptedContent / Unresolved. No fallback to the highest staged revision. Unresolved records are visited by migrations (value count 0) and reported as `estimateUnresolvedRecordCount`. | `Application/Features/FormRecords/Projection/AcceptedHeadProjection.cs`, `BusinessStore.EstimateOldFormRecordsAsync` |
| S2 — repair | `IBusinessScope.ProjectAcceptedHeadAsync`: one conditional, monotonic `UPDATE` (higher revision, or equal revision with a different operation; revision 0 = "no accepted content" only while nothing is projected). Used by create, update, upload commit, document removal, the worker's commit and no-op paths, the post-check and every verified read. | `BusinessStore.cs`, `VerifiedRecordReader.cs`, the five handlers |
| S1 — verified vs estimated | Per-item and per-migration results of the worker's Authority-head post-check; status field `verification` (with a `basis` text stating the limits) apart from the estimate. "All listed records …" needs a clean stored check **and** an agreeing estimate. Every migration is verified by the worker (no instant completion). Listed records the Authority will not report are **unverified**, never converted. | `FieldProtectionMigrationService.cs`, `FieldProtectionStatusEvaluator.cs`, `FormDesignerHandlers.cs`, `StageBContracts.cs` |
| S4 — attribution | `completed_by_operation_kind` (MigrationWorker / OtherMigration / UserOperation / NoAcceptedContent) from the accepted operation's identity (`ReprotectionAttribution` re-derives the record's reprotection operation ids). | `ReprotectionAttribution.cs` |
| S5 — oracle and reproductions | Read-only AuthorityDb test observer and `AuthorityOracle`; all affected assertions now take "the accepted operation" from the Authority; three deterministic reproductions (§4). | `ComposeStack.cs`, `AuthorityOracle.cs`, five integration test classes |
| UI | The field security panel shows the worker's last check and its limits, and "could not be matched locally" records, separately from the estimate. | `FieldSecuritySection.tsx`, regenerated API client |

**Business database migration** `20261022131900_PhaseC6ProtectionStatusVerification` (additive):
* nullable columns on `field_protection_migrations` and `field_protection_migration_items`, with check constraints;
* `ck_form_records_revision_projection` replaces `ck_form_records_revision_positive` (allows revision 0 only with no projected operation);
* data step: migrations that passed the worker's post-check get the equivalent stored result; the latest instantly-completed migration of each field is **re-queued** so the worker verifies it (on the owner's development database this happens when the migration is next applied; it was not applied there now).

**Not changed:** the Authorization Authority (protocol, code, schema), RowHmac, signed schemas, Merkle manifests, SpiceDB schema, cryptographic algorithms, keys.

## 3. Status semantics after M0

| Field | Meaning |
|---|---|
| `legacyPlaintextRecordCount` / `legacyEncryptedRecordCount` | Local estimate, over records whose accepted operation resolves locally (unchanged names and units). |
| `estimateUnresolvedRecordCount` | Records holding rows of the field whose accepted operation cannot be resolved locally. Never counted either way. |
| `verification` | The worker's last post-check: checked, remaining and unverified records, remaining rows, time, and `basis` (stored in the business database; not protected against a database administrator; records missing from the business database are not covered — G19). |
| `fullyEncrypted` / `fullyPlaintext` | Completed migration, stored check clean (nothing remaining, nothing unverified), **and** estimate clean with nothing unresolved. Display only; never used for authorization, decryption, reads or search. |

The UI keeps the wording "All listed records …" with the G19 caveat; no "fully verified" claim was introduced.

## 4. Verification

### 4.1 Local (no Docker; owner's stack untouched)

| Check | Result | Duration |
|---|---|---|
| `dotnet build EnterpriseForms.All.slnx -c Release` (warnings are errors) | success, 0 warnings | 33 s |
| Generated OpenAPI | changed only by the additive status fields (committed) | — |
| CI-equivalent .NET tests (`run_dotnet_tests.py`, 4 projects, one TRX each) | **314 / 314** (302 before M0 + 12 new in `PhaseC6StabilizationTests`): Authority 91, contract 123, architecture 66, integrity 34 | 16 s |
| Integration and security test projects, Release build | success, 0 warnings | — |
| Frontend typecheck, ESLint | clean | — |
| vitest | **166 / 166** | 63 s |
| Specification validator `--strict` | pass | — |

### 4.2 Hosted (disposable GitHub runners)

Three hosted runs were needed; each failure was a test defect, not a product defect, and is reported here.

| Commit | Workflow / run | Result |
|---|---|---|
| `4a2bdca` (code) | security-gate-a 39787772341 | **13 / 13** (SEC-P01…SEC-P12 = PASS, evidence bound to `4a2bdca`) |
| `4a2bdca` | stage-b-integration 39787765548 | integration 51 / 68. **All 17 failures were one harness bug**: the new read-only Authority observer queried `record_content_heads` without the Authority's `authority` schema on its search path (`relation does not exist`). Browser job 56 / 56 |
| `a6fa495` (observer search path) | stage-b-integration 39789892462 | integration 65 / 68; browser 56 / 56. **The 3 failures were one assertion bug**: PostgreSQL renders `boolean || text` as `true`/`false`, the oracle expected `t`/`f`. The attribution comparison itself had passed. The previously flaky `InterruptedAndTampered…` and the new decrypt-direction and abandoned-candidate reproductions passed |
| `505b452` (**final M0 commit**) | stage-b-integration 39793586804 | **integration 68 / 68 (10 m 16 s); browser 56 / 56; combined gate passed** |
| `505b452` | stage-b-integration 39796150658 (repeat) | **integration job 68 / 68 (11 m 08 s)**. The repeat's browser job hung for over an hour at "Install Playwright Chromium" (runner download, before any test code ran) and the run was cancelled by me. The browser suite on the same commit had already passed 56 / 56 in run 39793586804, so no second browser result exists for the repeat |
| `505b452` | security-gate-a 39796154761 (re-run) | **13 / 13**, evidence bound to `505b452` |

Tests that passed in the 68 / 68 runs and failed or flaked before M0: `ConcurrentUserEditsAndAWorkerRestartNeitherLoseDataNorBlockCompletion`, `InterruptedAndTamperedMigrationsNeverClaimPlaintextAndForgedCompletionIsIgnored`, plus the three new reproductions (§4.3). **Stability evidence is two consecutive full green integration runs (68 / 68 each) on the final commit, not ten.** The determinism comes from the fault-injected reproductions, as agreed (architecture §8 records that the "10 consecutive hosted runs" criterion was replaced by deterministic reproductions plus a limited number of repeat runs).

### 4.3 Deterministic reproductions

| ID | Test | Reproduces | Mechanism |
|---|---|---|---|
| C6-M0-01 | `FieldProtectionMigrationTests.ALostCommitResponseIsRepairedFromTheAuthorityHeadAndAttributedToTheWorker` | F-A + F-C, encrypt direction | Authority fault `CrashAfterContentHeadCommit` armed before strengthening: the worker's first reprotection commit is accepted, then the Authority process terminates before answering. Asserts one crash happened, the projection was repaired, status = Authority truth, both items `MigrationWorker`. Before M0 this test cannot pass: the status stays `EncryptionMigrationIncomplete`. |
| C6-M0-01b | `FieldProtectionDowngradeTests.ALostDeprotectionCommitResponseIsRepairedFromTheAuthorityHeadAndAttributedToTheWorker` | F-A + F-C, decrypt direction (the "2 instead of 1" family) | Same fault during an encryption downgrade. |
| C6-M0-02 / 03 | `FieldProtectionMigrationTests.AnAbandonedCandidateAtTheAcceptedRevisionIsNeverMistakenForTheRecordsState` | F-B; missing and forged pointer | Authority fault `PauseBeforeContentCommit` holds two owner edits prepared on the same revision; released together, one is accepted and one aborted (checked in the Authority), leaving two staged candidates at one revision that differ in the business rows. The pointer is then nulled (the migration visits the record as unresolved: totals 2 records / 1 value, where the old fallback gave 1 or 2 depending on row order) and later forged to the abandoned candidate (status only becomes more pessimistic; the next verified read repairs it). |

Existing tests made Authority-oracle-based: `FieldProtectionMigrationTests` (all), `FieldProtectionDowngradeTests` (all, including the line that failed with 2), `EncryptionTests` (strengthening), `RepeatingGroupsTests`, `RulesTests`. No retries, sleeps or timeouts were added or lengthened.

## 5. Residual issues and limits

* **G19 unchanged.** The post-check covers the records the business database lists. **The migration-status projections in the business database (the stored `verification` result, the item and migration counts, the accepted-operation pointer and the lifecycle/listing status) are not independently trustworthy and are not proof of complete record enumeration.** They help the operator; they are not security evidence.
* **The stored check is business data.** A hostile business-database administrator can forge it; the status therefore also requires the estimate to agree, which makes a forgery harder but is not a protection (both can be forged).
* **Attribution limit.** If a user's operation was accepted after the worker's own, the item is attributed to the user's operation (the Authority head read does not expose earlier accepted operations; AC-1 would).
* **Data step re-queues** the latest instantly-completed migration of each field on existing databases; those fields show "migration running" until the worker re-verifies them (seconds per record).
* Everything listed as still open in `PHASE_C5_STATUS.md` §12.6 remains open (G06, G19, G20, G21, U1–U11, the C-12 and C-28 Authority reviews, PB-14 manual accessibility, PB-08 step-up, the My Records scalability concern, the Form Designer usability debt, the unexplained intermittent Playwright failure, every production Level 2 gate). The field-protection status-count defect is fixed by M0 (subject to the hosted evidence above).

## 6. M0 closure

Owner conditions for closing M0 (all met): repeat integration run and SEC-P re-run on `505b452` pass (§4.2); this document, the architecture updates and decisions C-43 / C-44 are final; the deterministic reproductions replace the proposed 10 consecutive hosted runs (recorded in §4.2 and architecture §8); the G19 limitation is preserved (§5).

M1 is documented separately in §7.

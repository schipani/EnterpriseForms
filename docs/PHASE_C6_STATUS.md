# Phase C6 — Record lifecycle and history: implementation status

Branch `phase-c/record-lifecycle` from main `f859e42`. Architecture: `docs/architecture/PHASE_C6_RECORD_LIFECYCLE_AND_HISTORY.md` (revision 1), approved for controlled development by the owner on 2026-10-22 (decision **C-43**). Implementation decision: **C-44**.

**Milestone 0 (field-protection status-count stabilization, architecture §1.1) — provisionally accepted by the owner; final M0 commit `505b452`** (code `4a2bdca`; test-harness fixes `a6fa495`, `505b452`; documentation-only commits follow it). Milestone 1 (record lifecycle) is documented in §7, its AC-2 hardening in §8 (C-46), the recoverable development environment for browser acceptance in §9 (C-47), the quick record lookup in §10 (C-48), the owner's functional acceptance of M1 in §11 (C-49), and Milestone 2, trusted record history with AC-1, in §12 (C-50). AC-3 is deferred; restoration (the owner's M3) is not started; nothing is merged to main.

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

---

## 7. Milestone 1 — record lifecycle (Draft, Submitted, Archived)

**Status: implemented and verified on disposable hosted stacks; awaiting owner browser acceptance.** Final M1 commit `b674d51` (feature `64e2b73`; one ordering fix `b674d51`). Decision **C-45**. Authorized by the owner after provisional acceptance of M0. Not an independent review, not production approval, no Level 2 claim.

### 7.1 What was built

| Area | Behaviour |
|---|---|
| Transitions (owner only) | **Submit** Draft → Submitted (full validation). **Reopen** Submitted → Draft (reason mandatory, 1–500 characters). **Archive** Draft or Submitted → Archived (reason optional; confirmation dialog). **Unarchive** Archived → Submitted if the record was submitted and not reopened before archiving, otherwise Draft. Everything else is `409 LIFECYCLE_TRANSITION_NOT_ALLOWED`. The owner's request listed Archived → Submitted; the approved architecture (§2.1) makes Unarchive return the record to the exact state it was archived from, which needs no extra signed column (Archive keeps `submitted_on`, Reopen clears it). |
| Where status lives | The signed `record_status` / `submitted_on` columns of the record header (already part of the signed schema v2/v3). Every decision uses the status of the **Authority-verified header**, never the unsigned `form_records.record_status` projection, which is only kept in step for listing. |
| How a transition is written | One owner content operation through the ordinary prepare → stage → read-back → commit protocol. The new header carries the new status; **every value row, entry and document version is copied verbatim** (same helpers as document removal). Nothing is decrypted or re-encrypted; ciphertext, nonces, wrapped keys and document objects are byte-identical in the new revision. Submit decrypts to validate but writes nothing it decrypted. |
| Submit validation | The whole current content against the current effective layout: types, required and required-if, visibility rules, active and organization-enabled options, dependent dropdowns, repeating-section bounds; and no document upload still in flight (`409 UPLOADS_IN_PROGRESS`). Failures return `422 VALIDATION_FAILED` with `field_keys`. Archive, Unarchive and Reopen perform **no** value validation. |
| Locking | `409 RECORD_NOT_EDITABLE` for: owner edits, **shared-contributor edits**, upload start and commit, document removal, child-record create and unlink, on any non-Draft record. Sharing, revocation and ownership transfer stay possible (OD-06). Field-protection migrations are unaffected (they copy the header verbatim). |
| Concurrency | Optimistic: `If-Match` / `expectedContentRevision` (`409 CONTENT_REVISION_CONFLICT`) and `expectedStatus` (`409 LIFECYCLE_STATE_CONFLICT`); the Authority's compare-and-swap on the head decides races between a transition and any other write. |
| Listing | Archived records are hidden from My Records by default; `includeArchived=true`, or an explicit `recordStatus=Archived`, shows them. The list cursor is bound to this choice. The list's Edit link is hidden for non-Draft rows. |
| Audit | `RECORD_SUBMITTED`, `RECORD_REOPENED` (with reason), `RECORD_ARCHIVED`, `RECORD_UNARCHIVED`, and `RECORD_LIFECYCLE_DENIED` (outcome Denied, error code only, never values). |
| UI (existing Record Workspace) | Status badge and row in the summary, also for shared recipients (status is not field content, OD-08). Action buttons only for the owner, from the server's `lifecycle.allowedTransitions`: Submit, Reopen as draft, Archive, Unarchive. Each opens a confirmation dialog (focus trap, Escape, reason field where applicable); success notice; error panel, and for a failed submission the list of fields to fix. A lock banner explains Submitted / Archived. Edit, document and child-list actions disappear when locked, and the editor URL shows an explanation instead of a form. "Include archived records" checkbox on My Records. |
| API | `POST /api/v1/form-records/{id}/submit|reopen|archive|unarchive` (Idempotency-Key, If-Match); projection gains `lifecycle { status, submittedOn, allowedTransitions, contentEditable }`; list gains `includeArchived`. |

**Not changed:** the Authorization Authority (protocol, code, schema), RowHmac, signed schemas, Merkle manifests, SpiceDB, cryptography, journal format. History browsing, restoration, AC-1, AC-2 and AC-3 are **not** implemented. No lifecycle invariant needed an Authority change to be enforced for **owners**; the one that cannot be enforced inside the Authority without AC-2 is described in §7.4.

### 7.2 Verification

| Check | Result |
|---|---|
| .NET (CI-equivalent, Release, one TRX per project) | **331 / 331** (314 before M1 + 17 new `PhaseC6LifecycleTests`): Authority 91, contract 140, architecture 66, integrity 34 |
| Frontend typecheck / ESLint / vitest | clean / clean / 13 new + existing files passed locally (full suite last run in full before M1: 166 / 166) |
| Hosted Docker integration on `b674d51` (run 39806400678) | **76 / 76** (11 m 15 s): 68 existing + 8 `RecordLifecycleTests` |
| Hosted browser (Playwright, run 39806400678) | **61 / 61** (7.3 m): 56 existing + 5 `record-lifecycle.spec.ts` (submit, lock, shared recipient, reopen with reason, archive/list/unarchive, axe scans of both dialogs, the submitted record and My Records) |
| Hosted SEC-P01…SEC-P12 on `b674d51` (run 39810858142) | **13 / 13**; also 13 / 13 on `64e2b73` (run 39803409044) |
| Combined gate (run 39806400678) | passed |

**One failure on the way (run 39803403311 on `64e2b73`: integration 75 / 76, browser 61 / 61).** A shared contributor calling Reopen without a reason received `422 REASON_REQUIRED` instead of `403`: request validation ran before the ownership check. A real ordering defect in my handler, fixed in `b674d51` (authorization first); the test caught it, and was not changed.

Tests:
* `RecordLifecycleTests` (Docker): the owner walks every transition with audit events and the round-trip of the submission time; archive/unarchive keep encrypted values, plaintext values and the document byte-identical (SQL comparison of ciphertext, nonce, wrapped key, object key); strangers, an administrator, a designer, a cross-tenant caller and read/contributor shares are refused and audited; locks on every write path for owners and contributors (switch on), in both Submitted and Archived; submit validation against a newly required field, archive allowed regardless, upload-in-flight block and release; stale revision, stale status and a held Submit/Archive race (exactly one wins, checked in the Authority); archived hiding in the list; hostile business-database administrator (three scenarios, §7.3).
* `PhaseC6LifecycleTests` (unit): transition table (all other combinations refused), deterministic unarchive and round trips, lock on every non-Draft status, list default, audit actions.

### 7.3 Adversarial coverage (architecture §7.2 subset for M1)

| Case | Result |
|---|---|
| Unauthorized transitions (strangers, administrator, designer, other tenant, reader, contributor) | 404 / 403, nothing staged, `RECORD_LIFECYCLE_DENIED` audited |
| Incomplete submission | 422 with the missing field; head unchanged; archive still possible |
| Shared contributors | cannot transition; cannot edit a locked record (409 before scope rules) |
| Stale versions and concurrent writes | 409; exactly one of two held transitions commits |
| Database tampering — flip the unsigned status column to Draft on a Submitted record | edit still refused; the next verified read repairs the column |
| — fake Archived on a Draft record | affects only the unsigned list; opening the record shows Draft and repairs it |
| — edit the signed header row's status | `423 INTEGRITY_VERIFICATION_FAILED`, nothing released, no transition can be built on it |

Not covered by an automated test in M1 (stated, not hidden): archiving a record whose strengthened field still holds legacy plaintext (the Authority's protection barrier should answer `FIELD_PROTECTION_CHANGED`; it needs a worker held mid-migration), upload commit refusal on a record locked between upload and commit (code path guarded, not exercised), and a replayed transition request after success (a replay with the old revision is `CONTENT_REVISION_CONFLICT`, as for ordinary updates).

### 7.4 Limits and risks (for the independent reviewer)

* **R-01 — lock enforced by the business API for contributors.** The Authority does not interpret status for owner writes, and its share-editor Draft-only guard is **AC-2, not built** (owner instruction). A compromised business runtime could therefore let a contributor write to a locked record; a business-database administrator cannot change the status (signed) or the lock.
* **Unsigned listing status.** The list hides Archived by the unsigned projection; a database writer can hide or reveal records in the list (not in access). It is repaired by verified reads.
* **Submit decrypts to validate** (owner's own full read; nothing written).
* **No PB-08 step-up** on Restricted records for submit/archive/reopen (existing residual).
* **Child records** keep their own status; archiving or submitting a parent does not cascade (OD-05).
* **History and restore** are M2+, unchanged. Everything open in §5 and in `PHASE_C5_STATUS.md` §12.6 remains open: G06, G19, G20, G21, U1–U11, C-12/C-28 reviews, PB-14 manual accessibility, PB-08 step-up, My Records scalability, Form Designer usability debt, every production Level 2 gate.

### 7.5 Browser acceptance (owner)

Use the hosted evidence above, or a stack you choose to rebuild: **the new business migration `PhaseC6ProtectionStatusVerification` (M0) would be applied to your persistent development database the next time the dev stack's migrate service runs, so I did not rebuild it.** To try M1 locally:

1. From `infrastructure/docker`: `docker compose -f compose.yaml -f compose.dev.yaml --env-file .env up -d --no-deps --build --wait business-migrate api worker web` (authorizes the M0 + M1 schema change on your dev database; the Authority needs no change). Start `npm run dev -w apps/web` (http://localhost:5173) if you do not reuse yours.
2. Sign in as **Avery Owner (owner)**. Open any record you own (My Records → a Draft). The header shows **Submit**, **Archive**, **Refresh**, **Edit**; the summary shows a **Draft** badge.
3. **Submit** → dialog "Submit this record?" explains the lock → **Submit record**. Expect "Record submitted. It is now locked.", a **Submitted** badge, the banner "This record is submitted", no **Edit**, and **Reopen as draft** / **Archive** buttons. If the form has a required field that is empty, the dialog stays open, names the field and nothing changes.
4. Try `/app/records/<id>/edit` directly: the page says the record is submitted and cannot be edited. Documents and related-record actions are gone from the tabs.
5. **Reopen as draft**: the confirm button stays disabled until you type a reason. After confirming, **Edit** returns.
6. Share one field with **Casey Clerk** (Contributor), submit again, sign in as the clerk: the page shows **Submitted** and the lock, **no** lifecycle buttons and no **Edit shared fields**.
7. As the owner, **Archive** (optional reason). The record disappears from **My Records**; tick **Include archived records** (or choose status Archived) to find it. Open it → **Unarchive** → it returns to Submitted (if it had been submitted) or Draft.
8. Activity history tab: submitted / reopened (with your reason) / archived / unarchived events.

---

## 8. M1 hardening — AC-2 and regression coverage (decision C-46)

**Status: implemented for development and verified on disposable hosted stacks. AC-2 is NOT independently reviewed or certified.** Final commit `e2f6dc7` (AC-2 and tests `2c6b2f3`; harness fix `e2f6dc7`). The owner accepted the Unarchive rule (back to the state the record was archived from).

### 8.1 AC-2 — the Authority's own lifecycle lock for shared contributors

| Point | Implementation |
|---|---|
| Rule | The Authorization Authority refuses a **share editor's** content write unless the record's **accepted, signed** header status is `Draft` (new error code `RECORD_NOT_EDITABLE`, 409). An unreadable or missing status fails closed. |
| Source of truth | The `record_status` column of the signed header in the accepted head, decoded by the Authority — never the business `form_records.record_status` projection, never a value the business API asserts. |
| At prepare | In the share-editor branch, **after** the supplied current snapshot is bound to the accepted head (D4: exact coverage, per-item digests, recomputed root and leaf-index digest). Nothing is signed or stored for a refused attempt. A candidate that tries to flip the status back to Draft is refused by the same check. |
| At commit | For share-editor operations, on the **verified** staged header (equal to the accepted header except the operation binding, D15); a refusal aborts the operation (`RECORD_LOCKED_BEFORE_COMMIT`). Independently, the head compare-and-swap already rejects any candidate prepared before a lifecycle transition committed (`CONTENT_REVISION_CONFLICT`). |
| Owners | Unchanged: lifecycle transitions are owner content writes and remain allowed; ordinary owner edits of locked records are still refused by the business API only (R-01 for owners). |
| Unchanged | Signed row formats, Merkle manifests, the journal format, SpiceDB schema, the contributor-write switch (off by default everywhere) and the Production startup guard. AC-1 and AC-3 are not implemented. |
| Business API | Maps the Authority's refusal to `409 RECORD_NOT_EDITABLE` ("the Authorization Authority refused the change …"). |

**How AC-2 was shown to work (adversarial):**
* **Authority protocol tests** (real `ContentService`, real canonical encoding and HMAC, in-memory store — exactly the calls a compromised or bypassed business API would make with a contributor's valid share and the switch on): locked Submitted and Archived records refuse an otherwise perfect contributor candidate with nothing stored; a status flip back to Draft is refused; the owner's transitions still work; after the owner reopens, the same contributor write is accepted; a transition committed between a contributor's prepare and commit defeats the commit; the commit-time check fails closed for every non-Draft value and for a missing header; with the switch off the switch refusal still comes first. **Mutation check:** with the prepare-time guard removed, both attack tests fail (the write is accepted); with it restored, all pass.
* **Docker test** `WithTheBusinessLockBypassedTheAuthorityAloneRefusesContributorWritesToLockedRecords`: the api container is recreated with the Testing-only hook `Testing:BypassContributorLifecycleLock=true` (business lock off on the contributor path; the hook refuses to start outside the Testing environment, unit-tested). Through that bypassed API the contributor edits the Draft record (control), then the Authority refuses the same edit on the Submitted and on the Archived record: `409 RECORD_NOT_EDITABLE` with the Authority's message, **no new operation in AuthorityDb**, head revision unchanged, value unchanged. The hook is switched off again afterwards.

### 8.2 Regression coverage (expected behaviour, fail-closed outcome, recovery, audit)

| Case | Expected behaviour (tested) | Recovery | Audit |
|---|---|---|---|
| Archive while a field's encryption migration has not reached the record (worker held paused) | `409 FIELD_PROTECTION_MIGRATION_PENDING`: the verbatim copy still carries the legacy plaintext row and the Authority's protection barrier refuses it. Nothing committed; status unchanged; nothing decrypted. | When the worker converts the record (the migration processes records in any lifecycle state), the same archive succeeds; the value is preserved and now encrypted. | No lifecycle event for the refused attempt (nothing changed); the migration's own events as before. |
| An upload processed while Draft, record then locked | Submit waits (`409 UPLOADS_IN_PROGRESS`, OD-04). Archive is allowed; the later commit is refused `409 RECORD_NOT_EDITABLE`, the session stays `Processed`, no document is added, the head is unchanged. | After Unarchive, the same session commits (while within its 24-hour lifetime); Submit then succeeds. An unused session expires and its staging object is purged by the existing sweep. | `DOCUMENT_READY` only on the successful commit. |
| Replay of a successful transition with the same Idempotency-Key (retry after a lost response, double click) | The committed result is returned (same operation id and revision, `previousStatus` reconstructed); nothing is staged; no second audit event. | — | One `RECORD_SUBMITTED` (asserted). |
| The same transition with a **new** key after success | Refused as stale (`409 LIFECYCLE_STATE_CONFLICT` or `CONTENT_REVISION_CONFLICT`); never applied twice. | Reload. | none |

Residual noted (not hidden): two identical requests with the same key arriving **concurrently** resolve to one accepted operation (Authority idempotency), but each request writes its own business audit event, so the local timeline can show the event twice with the same operation id. The trusted Authority journal holds one commit.

### 8.3 Verification

| Check | Result |
|---|---|
| .NET (CI-equivalent, Release) | **337 / 337**: Authority 96 (+5 AC-2), contract 141 (+1 hook guard), architecture 66, integrity 34 |
| Hosted Docker integration on `e2f6dc7` (run 39874573992) | **80 / 80** (12 m 22 s): adds the AC-2 bypass test and the three regression tests |
| Hosted browser on `e2f6dc7` (run 39874573992) | **61 / 61** (5.3 m) |
| Hosted SEC-P01…SEC-P12 on `e2f6dc7` (run 39874578091) | **13 / 13**; also 13 / 13 on `2c6b2f3` (run 39872912968) |
| Combined gate (run 39874573992) | passed |

One failure on the way (run 39872908214 on `2c6b2f3`, integration 79 / 80, browser 61 / 61): my test verified the hook by running `printenv` inside the api container, whose image has no shell utilities. The attack step never ran. Fixed by reading the container environment with `docker inspect` (`e2f6dc7`); no product change.

### 8.4 Local development stack — blocked (found 2026-10-22), backup taken

*Resolved by §9 (C-47): a separate persistent development stack; the retired project below was preserved, not reused.*

* **Finding.** When I began item 3, the Docker engine on this machine was **not running** (it had stopped before this session; I did not stop it). I started Docker Desktop (no development service has an auto-restart policy, so nothing else started) and inspected read-only: every development container is `Exited (255)` — killed when the engine stopped. The persistent volumes are intact.
* **Consequence.** The development Vault runs `server -dev` **in memory** with no volume, so the **development organization signing keys no longer exist**. Starting the stack would leave every existing record unverifiable, and making them readable would require regenerating keys — which the owner prohibited. **I did not start, migrate or rebuild the development stack.** No volume, database, Vault, SpiceDB or key was modified.
* **Backup (recoverable, verified).** Read-only tar copies of all six volumes in `C:\Users\tsch\Documents\EnterpriseForms-dev-backups\20261022-193852\` with `MANIFEST.txt` (entries and SHA-256) and `containers.txt` (container inventory): business-db (1,563 entries, PostgreSQL 18 data directory), authority-db (1,412), spicedb-db (1,368), dev-seed (3); azurite and archive-store volumes were empty (development blobs were never persisted). They are crash-consistent copies (the databases were killed by the engine stop), so a restore performs normal PostgreSQL crash recovery.
* **Migration review (done).** The only new migration since main is the business migration `PhaseC6ProtectionStatusVerification` (M0). Forward steps: replace one check constraint with a more permissive one; rename one index; add nullable columns and checks that existing data satisfies; two data updates on `field_protection_migrations` (record existing verification results; re-queue instantly completed migrations). No drop, truncate or rewrite of record data. M1 and AC-2 add no migration (Business or Authority).
* **Decision needed from the owner** (§8.5).

### 8.5 Options for browser acceptance

1. **Fresh development stack, keep the old volumes untouched (recommended).** Run a fresh dev stack under a new compose project name with new volumes and new development keys; the old project's volumes stay as they are (and are backed up). The old project's *networks* must be removed first because both projects pin the same subnets (networks hold no data and are recreated). The old records stay unreadable in either case, because their keys are gone.
2. **Reset the existing development project** (`down -v` and `up`): new keys, the old development data deleted from the volumes (the backup remains). Needs explicit authorization.
3. **Review on the hosted evidence only** (§7.2, §8.3) without a local stack.

The owner chose option 1 (C-47); see §9. The root cause (in-memory Vault) is fixed for the new stack.

### 8.6 Browser acceptance (once a stack is available)

Personas: **Avery Owner (owner)**, **Casey Clerk (clerk)**. Use a Draft record you own (or create one).

1. **Submit.** Record page → **Submit** → confirm **Submit record**. Expect "Record submitted. It is now locked.", a **Submitted** badge, the lock banner, no **Edit**; **Reopen as draft** and **Archive** appear. A record with an empty required field stays in the dialog with the field named; nothing changes.
2. **Reopen with a reason.** **Reopen as draft** → the confirm button stays disabled until a reason is typed → confirm. **Edit** returns; the activity tab shows the reopen with your reason.
3. **Archive.** **Archive** (optional reason) → **Archive record**. Expect "Record archived.", an **Archived** badge, the lock banner, only **Unarchive**.
4. **Unarchive.** **Unarchive** → it returns to **Submitted** if it had been submitted (and not reopened) before archiving, otherwise **Draft**.
5. **Editing restrictions.** While Submitted or Archived: no **Edit**; document upload/remove and related-record add/remove are gone; opening `/app/records/<id>/edit` shows "This record is submitted (or archived) and can't be edited". As the clerk with a shared field: status and lock visible, no lifecycle buttons, no **Edit shared fields**.
6. **Archived visibility in My Records.** After archiving, the record is not listed; tick **Include archived records** (or choose status **Archived**) and it appears; after Unarchive it is listed again.

### 8.7 Still open

G06, G19, G20, G21; U1–U11; the C-12 and C-28 Authority reviews; independent review of AC-2 (new, recorded with G20's Authority-review family); PB-14 manual accessibility; PB-08 step-up; R-01 for owner edits; My Records scalability; Form Designer usability debt; every production Level 2 gate. Nothing here is a Level 2 claim.

## 9. Recoverable development environment and M1 acceptance (decision C-47)

Owner instruction: a fresh, isolated development stack that keeps the old databases, volumes and backups, and a development Vault whose keys survive restarts. Full procedures and evidence: `docs/infrastructure/DEV_ENVIRONMENT_PERSISTENT_VAULT.md`.

### 9.1 Old environment — preserved

* **Backups re-verified:** all six archives in `C:\Users\tsch\Documents\EnterpriseForms-dev-backups\20261022-193852\` match `MANIFEST.txt` (SHA-256 and entry counts).
* **Recovery sources checked:**
  * the old Vault container (`-dev`, nothing on disk; its anonymous `/vault/file` and `/vault/logs` volumes are empty);
  * every Docker volume on the machine, read-only;
  * the host user profile;
  * the Authority, which creates keys non-exportable with no plaintext backup.

  **No copy of the old signing keys exists.** No key was generated under the old identity (organization `01a1579b-…24a0`), which the tool now refuses.
* **Second defect found, Azurite:** the security-event archive and document blobs were stored in the containers' writable layer, not in their volumes (that is why those volumes were empty). I copied them read-only (`docker cp` from the stopped containers) to `...\20261022-193852-container-layers\` with `MANIFEST.sha256`.
* **Untouched:** the old project's containers (stopped), volumes, networks and images (`:local`) were not started, modified, reused or removed. The new Vault is not connected to the old databases.

### 9.2 New persistent stack

* **Project and isolation:** Compose project `enterpriseforms-dev`, with its own volumes, subnets (`172.31.0.0/24`, `172.31.1.0/24`), `:dev` image tags and host ports. No old network had to be removed.
* **Vault:** Raft storage on its own volume. It starts **sealed** and is unsealed from a custody file outside the repository (owner-only ACL) holding the unseal key, root token, Authority token and per-environment KEK.
* **Authority token:** least-privilege, and after seeding pinned to the single organization key. The pin was shown to deny the key's `config`, `rotate`, and creation of any other key.
* **Azurite:** now writes to its volumes (`--location /data`).
* **Data:** M0 and M1 migrations applied on a fresh database (the business migration ran at startup). New synthetic organization `01a16640-87f6-7488-a1f1-664db54fbff3`, with the owner, designer, clerk and reader personas.
* **Unchanged:** contributor editing stays disabled; the Production guard, the signing formats and the Authority are unchanged.
* **Operation:** `python scripts/devstack/devstack.py init | up | unseal | status | stop | backup | restore-test | verify-persistence | verify-workflow | prepare-acceptance`.

### 9.3 Persistence and restore evidence (2026-10-22)

| Check | Result |
|---|---|
| Vault container restart | sealed; verified read refused `503 SIGNING_UNAVAILABLE`; after unseal the probe record verifies identically (values, Authority head, key properties, probe MAC) |
| Vault container SIGKILL | same |
| `compose stop` / `up` of the whole stack | identical |
| `compose down` (volumes kept) / `up`, every container recreated | identical |
| Backup (7 volumes) → restore into a disposable project (own subnets and ports) → unseal with custody → verify | identical; disposable project removed; live stack untouched |

Not done:
* **Docker Desktop restart:** it would have stopped other projects' containers on this machine.
* **Host reboot.**

Both keep the named volumes and stop the containers, which the kill and full-recreation phases cover.

### 9.4 Verification on the new stack

| Suite | Result |
|---|---|
| `verify-workflow` (API, owner and clerk sessions) | **15 / 15** |
| Browser (Playwright, against the `:dev` web container at `127.0.0.1:8091`): `record-lifecycle`, `form-management`, `records-workspace`, `accused-workflow` | **14 / 14** |

The 15 API checks cover:
* Draft with Submit/Archive offered;
* Submit refused with `422` naming the missing required field;
* Submitted lock (owner edit `409 RECORD_NOT_EDITABLE`);
* the clerk sees the status but has no actions (reopen `403`, edit `409`);
* Reopen without a reason `422 REASON_REQUIRED`;
* Reopen with a reason gives Draft, and the edit is saved;
* Archive from Submitted;
* hidden from the default list, listed with `includeArchived`;
* Unarchive gives Submitted, and Draft archive/unarchive gives Draft;
* a Contributor share's edit is refused `403 CONTRIBUTOR_WRITES_DISABLED` (default off).

The browser suites include document uploads (ClamAV, worker, Azurite) and related lists. These test runs added synthetic forms and records to the new environment.

### 9.5 Browser acceptance (owner)

Open **http://127.0.0.1:8091** (if the machine or Docker restarted, first run `python scripts/devstack/devstack.py up`). Sign in with **Avery Owner (owner)**. The prepared form is **M1 Acceptance - Case intake**, with two Draft records:
* **REC-017EF770** "M1 acceptance case": complete; Casey Clerk has a Reader share of *Case title*;
* **REC-255FB708** "M1 acceptance case - missing case number": created before *Case number* became required.

1. **Submit validation:** open REC-255FB708 → **Submit**. The dialog names *Case number*, and the record stays Draft.
2. **Submit:** open REC-017EF770 → **Submit** → **Submit record**. Expect a Submitted badge and the lock banner, no **Edit**, and **Reopen as draft** and **Archive** offered.
3. **Reopen with a reason:** **Reopen as draft**. The confirm button stays disabled until you type a reason; after confirming, **Edit** returns.
4. **Edit after reopening:** **Edit** → change *Internal notes* → save. The value persists, and the activity shows the reopen reason.
5. **Archive:** **Submit** again, then **Archive** → **Archive record**. Expect an Archived badge and only **Unarchive**.
6. **Archived visibility:** **My Records**. The record is hidden; tick **Include archived records** (or filter by status Archived) and it appears.
7. **Unarchive:** open it → **Unarchive**. It returns to **Submitted**, the state it was archived from; a Draft you archive returns to **Draft**.
8. **Clerk restrictions:** sign out and sign in as **Casey Clerk (clerk)**, then open REC-017EF770. You see *Case title* only, plus the status and lock; there are no lifecycle buttons and no **Edit shared fields**. `/app/records/<id>/edit` explains the lock.

### 9.6 Still open

Everything in §8.7, plus:
* independent review of the development Vault custody design (development only);
* a Vault audit device, which is not configured.

Nothing in this section is a production or Level 2 claim.

## 10. Quick record lookup on My Records (decision C-48)

Owner request before M2: find a record quickly by pasting its reference or ID. This is a usability change, not a search feature: G06 search completeness is unchanged and still open.

### 10.1 How references work, and why the lookup is complete and authorization-safe

* A short reference (`REC-017EF770`) is **not stored**. It is derived from the record ID (`REC-` + the last eight hexadecimal digits; `RecordListQuery.ReferenceFor`).
* It is **not unique**: different records can share one, so a reference lookup can have several answers.
* **Exact record ID:** one Authorization Authority `Discover` check of that ID.
* **Exact reference:** matched against the caller's **complete** discoverable set. That is every page of the Authority's discover lookup at one authorization revision, the same enumerator My Records uses (C-29). The match uses the record IDs only, because the reference is a function of the ID.
  * The business database is not consulted to decide matches.
  * Above the enumeration bound (10,000 discoverable records) the lookup fails closed with `RECORD_LIST_TOO_LARGE`. It never answers from a partial set, and the user is told to paste the full ID instead.
* The business database only **describes** matches (form, status, dates, creator). This is the same allowlisted, unverified metadata as My Records, and opening a record performs the verified read.
* A record that does not exist and a record the caller may not discover produce the **same** empty answer. No count, metadata or existence of unauthorized records is returned.
* Only two input forms are accepted: a full UUID (hyphens and braces optional) or `REC-` + 8 hex digits. Anything else is refused (`422 LOOKUP_INVALID`), so no field value, name or free text is ever searched.
* Archived and related (child) records are included: an exact lookup is not subject to the list's default filters.

Endpoint: `GET /api/v1/form-records/lookup?q=…` (`LookupFormRecord`; feature owner FormRecords). The OpenAPI artifact and the generated TypeScript types were regenerated.

### 10.2 UI

* **My Records:** a **Find a record** panel at the top, with a labelled input, **Search** and **Clear** buttons, and Enter to search. The existing form, status, sort, page-size, child and archived controls are unchanged below it.
* **Outcomes, announced politely:**
  * **Record found:** an **Open record** button.
  * **Several records have this reference:** every match is listed with form, status and dates; none is chosen.
  * **No record found:** the wording does not distinguish missing from inaccessible.
  * **Invalid input:** an inline error.
  * **Technical failure:** "The lookup could not be completed. This is a technical problem, not a missing record", with **Try again** and a support reference.
  * **Too many records to check:** paste the full ID instead.
* **Record Overview:** **Copy reference** next to the reference in the record summary. It announces "Reference copied"; if the clipboard is refused, it selects the text and explains how to copy it.

### 10.3 Platform fix found while testing

**The bug:** in the shared request coordinator (`packages/work-queue`), the executor emitted the last failure of a safe request still marked `retryable` after its three retries. The coordinator settles only non-retryable failures, so a persistent 503 left the request pending forever: the UI showed "loading" indefinitely instead of an error. This affected every safe GET, not only the lookup.

**The fix:** failures that will not be retried are now marked `final`, and the coordinator settles them. `retryable` still says whether the error was transient.

**The test:** an executor test covers it, and mutation-checked: it fails without the fix.

### 10.4 Verification

| Suite | Result |
|---|---|
| .NET (all four test projects, CI runner) | **360 / 360**: contract tests 164 (23 new lookup tests: input forms and refusals; authority-only matching over every discover page; unauthorized record with the same reference never requested from the business database; duplicates all returned; archived found; missing business metadata reported; authority failure propagates; fail-closed above the bound) |
| Frontend unit (vitest) | **175 / 175**: new input-parser tests and the coordinator final-failure test |
| Lint, typecheck, OpenAPI determinism | clean |
| Docker integration (`RecordLookupTests`, new) | compiled locally; runs on the hosted stack (§10.5) |
| Browser on the persistent dev stack, rebuilt `api` and `web` only (Vault, databases and SpiceDB not restarted): `record-lookup` (new), `record-lifecycle`, `records-workspace` | **14 / 14** |

The new browser spec:
* finds by reference with Enter and by full ID with the Search button, opens the record, and copies its reference (clipboard verified);
* checks an unknown reference, invalid input, and an archived record that is hidden from the table yet found;
* shows that a reader without access gets "No record found";
* runs an axe scan.

The duplicate-reference and technical-failure UI cases replace the server response in the browser, because the real server cannot be made to issue colliding record IDs or fail on demand. Their server side is covered by the .NET tests.

### 10.5 Hosted verification

On `bf51fe7` (disposable GitHub-hosted stacks):

| Suite | Result |
|---|---|
| Hosted Docker integration (run 39895116830) | **84 / 84** (80 before; the 4 new `RecordLookupTests` included) |
| Hosted browser (same run) | **66 / 66** (61 before; the 5 new `record-lookup` tests included) |
| Hosted SEC-P01…SEC-P12 (run 39895119735) | **13 / 13** |

### 10.6 Browser acceptance (owner)

At **http://127.0.0.1:8091** (run `python scripts/devstack/devstack.py up` first if Docker or the machine restarted), signed in as **Avery Owner (owner)**:

1. **Copy a reference:** open any record (for example REC-017EF770). In **Record summary**, press **Copy reference**; "Reference copied" appears.
2. **Find by reference:** go to **My Records**, paste it into **Find a record** and press **Enter**. "Record found" appears with **Open record**; open it.
3. **Find by ID:** paste a full record ID (from the address bar of a record page) and press **Search**. Same result.
4. **No match:** search `REC-00000000`. "No record found" appears, with no other detail.
5. **Invalid input:** search a word such as `Smith`. The field shows an error asking for a reference or full ID, and nothing is searched.
6. **Archived:** archive a record (or use one archived earlier). It is not in the table by default, but **Find a record** finds it with an **Archived** badge.
7. **No access:** sign in as **Riley Reader (reader)** and search the same reference. "No record found" appears, identical to step 4.
8. **Shared:** sign in as **Casey Clerk (clerk)** and search REC-017EF770 (shared with the clerk). It is found.
9. **Unchanged:** the form, status, sort, rows-per-page, related-records and archived controls and paging below the panel work as before.

Duplicate references and technical failures cannot be produced on demand in the real application; they are covered by the automated tests.

## 11. Milestone 1 — owner functional acceptance (decision C-49)

On 2026-10-23 the owner personally tested M1 on the persistent development stack (§9) and accepted, for continued development:
* Submit;
* Reopen with a reason;
* Archive;
* Unarchive (back to the state it was archived from);
* record locking;
* archived-record visibility in My Records.

This is owner functional acceptance for continued development. It is **not**:
* an independent security review;
* manual accessibility certification (PB-14);
* Level 2 production approval.

Every security finding and limit in §5, §7.4, §8 and §8.7 stands. In particular, AC-2 is not independently reviewed, and R-01 (owner edits enforced by the business API only) is unchanged.

## 12. Milestone 2 — trusted record history (decision C-50)

Owner instruction: an Authority-backed version list (AC-1) and a read-only historical version viewer in the Record Workspace.

**Numbering.** The owner's "M2" covers the architecture's M2 (AC-1) and M3 (history UI). The owner's "M3", restoration (the architecture's M4), is **not started**. AC-3 stays deferred, and the Authority journal format is unchanged.

### 12.1 What was built

**AC-1, the Authority content history read:** `GET /internal/v1/content-revisions/{organizationId}/{formRecordId}/history?after=&limit=` (`GetContentHistory`).
* **Source:** the Authority's own rows only (`record_manifest_commits`, `content_change_operations`, `identity_bindings`).
* **Each entry:** revision, accepted time, actor kind (User with the Authority user id, ProtectionMigration with the reprotection command, or Unknown if an accepted commit lacks a consistent operation), leaf count, key version, `verifiableNow` and `isCurrent`.
* **Order and paging:** ascending (chronological), cursor by revision, default 50 and at most 100 per page.
* **Who may read it:** `form_record.read` (full reader now) at the projection ZedToken with the gate open. Everyone else, other records and other organizations get a concealment-safe 404; an unbound actor gets 403.
* **Fail-closed cases:** a quarantined head returns 423. The authorization state is re-read after the list is built, and if it moved the result is 503.
* **Read-only:** no state change, no journal entry, no row bytes and no signature work.
* **Store additions:** two read-only methods (`GetManifestCommitsAsync`, `GetOperationsAsync`). No schema, migration, signing, manifest, journal or SpiceDB change; the Authority diff adds lines only.

**Business API:**
* **`GET /api/v1/form-records/{id}/versions?after=&pageSize=`** (`ListRecordVersions`). It maps actor ids to display names from the local directory ("Unknown user" if absent).
  * **Authority-established action labels:** "Created" for version 1, and "Protection update (system)" for protection migrations.
  * **Other labels:** the local activity event of the same revision, used only when its actor equals the Authority's actor and marked `ActivityLog` (unverified). Otherwise the label is `None`, shown as "Not recorded".
* **`GET /api/v1/form-records/{id}/versions/{revision}`** (`GetRecordVersion`).
  * Resolves the revision's operation from AC-1, never from business rows.
  * Refuses a revision whose key is no longer accepted (`503 HISTORIC_VERSION_UNVERIFIABLE`) before reading anything.
  * Reads that operation's exact staged rows and has the Authority verify them against the manifest it accepted for that revision (the existing `HistoricCommittedOperationId`). It then checks that the verified revision is the one requested.
  * Returns a read-only projection: the current layout and labels, with the version's values, documents and status, no permissions, no transitions and no ETag.
  * Protected values are decrypted only after verification, for a full reader; an unavailable key fails the whole request.
  * Never updates the current-head projections.
  * Writes audit event `RECORD_VERSION_VIEWED` (revisions and operation id only, never values).

**UI:**
* **History tab** in the Record Workspace, for full readers only: version, saved time, person, change (with "from the activity log" where applicable), a Current badge, and View; Show more and an independent Refresh. The former "Activity and history" tab is now "Activity", so two tabs don't both say "history".
* **Version page** `/app/records/:id/versions/:revision`:
  * a prominent "You are viewing an earlier version: version N of M … read-only" banner;
  * an "About this version" card with "Checked when opened" and the version's status;
  * the note that field names and arrangement are today's while values are the version's;
  * the read-only renderer, with documents of that version downloadable through the existing historic download;
  * "Other values saved in this version" for fields no longer in the form;
  * related records noted as not part of a version;
  * plain-language unavailable, could-not-be-verified and key-unavailable messages.
* No cryptographic terms are shown to ordinary users.

### 12.2 Security behaviour

| Concern | Behaviour | Evidence |
|---|---|---|
| Unauthorized history access | No grant, design rights only: 404 for list and view | integration `OnlyAPersonWhoMayReadTheWholeRecordNowSeesHistory`; Playwright |
| Cross-tenant | Another organization's user: 404; Authority-level request through another organization: 404 | integration; Authority `AnotherOrganizationsRecordIsNotFound…` |
| Field-share reader | Sees the current shared field, but no History tab and 404 for list and view. V1 rule: history needs full read now, so field-level restrictions are never relaxed (field-scoped history is FR-C6-01 and would need a further Authority change) | integration; Playwright; Authority test |
| Revoked / stale authorization | After ownership transfer the former owner gets 404 at once; the new owner sees all versions. Authorization moving during the read: 503 | integration; Authority test (mutation-checked: the guard checking `discover` instead of `read` fails the test) |
| Tampered historical value | `423 INTEGRITY_VERIFICATION_FAILED`, nothing released; the record is quarantined and the list refused (AC-4 current behaviour kept) | integration (Theory: tampered); Authority (tampered, extra, rows of another revision) |
| Missing historical rows | Same as tampered | integration (Theory: missing); Authority |
| Forged revisions | Version 0, 5, 99: 404, nothing quarantined; an abandoned candidate, another record's operation or a random operation is not a version | integration; Authority |
| Encrypted historical fields | Decrypted only after verification for a full reader; the historical value matches the version (an encrypted field) | integration |
| Fields no longer in the current form | Value returned with the version and shown under "Other values saved in this version" with today's name | integration; Playwright |
| Listing integrity | Exactly the Authority's accepted manifest commits, contiguous, ascending; abandoned candidates never listed | integration (compares with `record_manifest_commits`); Authority |

### 12.3 Verification

| Suite | Result |
|---|---|
| .NET (CI runner, four projects) | **378 / 378**: Authority 108 (12 new AC-1 and historic-verification tests), contract 170 (6 new history tests) |
| Frontend unit (vitest) | **178 / 178**: new history presentation tests |
| Lint, typecheck, OpenAPI determinism (business and Authority internal artifacts regenerated) | clean |
| Browser on the persistent dev stack (authority, api and web rebuilt only; Vault, databases and SpiceDB not restarted): `record-history` (new, 5), `record-lifecycle`, `record-lookup`, `records-workspace`, `accused-workflow`, `pb14-accessibility` | **24 / 24** |
| Hosted Docker integration, browser, SEC-P | §12.5 |

One failure on the way, caused by the test, not the product: the history spec's setup submitted immediately after an edit, while the edit's integrity checkpoint was still waiting for its archive receipt (`503 AWAITING_ARCHIVE`, retryable by design). The setup now waits until the edited version is readable, as the UI does.

The owner's own M1 acceptance record (REC-017EF770) shows its real history on the dev stack: created, submitted, reopened, edited, submitted, archived. Version 1 opens with its original values, and the clerk (a field-share reader) gets 404.

### 12.4 Limits and risks (for the independent reviewer)

* **AC-1 is implemented for development and not independently reviewed** (R-02: it exposes actor ids and operation timing of every accepted revision to full readers).
* **Action labels other than "Created" and protection updates come from the local activity projection** and are labelled as such (AC-3 deferred). A hostile business-database administrator could alter or remove them; the version list, times and actors would still be correct.
* **Labels and layout shown with a version are today's** (G21: unsigned metadata). The UI says so.
* **AC-4 is unchanged:** one tampered historical row quarantines the current record (R-03). This must be decided before any history retention purge (PB-13, G16).
* **Field-share readers see no history (V1).** Child records keep their own histories.
* **Historic KEKs:** the local key provider holds one KEK. After a KEK rotation, older encrypted versions would fail closed (Stage B §7.1).

### 12.5 Hosted verification

Recorded after the hosted runs complete.

### 12.6 Browser acceptance (owner)

At **http://127.0.0.1:8091** (run `python scripts/devstack/devstack.py up` first if Docker or the machine restarted), signed in as **Avery Owner (owner)**:

1. **History list:** open REC-017EF770 (M1 Acceptance – Case intake) and choose the **History** tab. Expect every saved version, oldest first, with when, who and what changed. "Created" is shown plainly; the other changes say "from the activity log"; the last row is marked **Current**.
2. **Open an earlier version:** press **View** on version 1 (keyboard: Tab to it, Enter). Expect the banner "You are viewing an earlier version: version 1 of …", "Status in this version: Draft", and the original values. There is nothing to edit.
3. **Compare:** open another version and check that its values and status differ where the record changed. **Back to version history** returns to the tab; **Open current record** goes to the record.
4. **Removed field:** if a field was removed from a form after a version was saved, that version shows it under **Other values saved in this version**.
5. **Unavailable version:** change the address to `/versions/99`. Expect "This version isn't available".
6. **Shared reader:** sign in as **Casey Clerk (clerk)** and open REC-017EF770. There is no History tab, and `/app/records/<id>/versions/1` says the version isn't available.
7. **No access:** sign in as **Riley Reader (reader)** and open the same version URL. Expect the same "isn't available" message.
8. **Phone width:** narrow the window to about 320 px; the version page should stay readable without sideways scrolling.

Tampered or missing history can't be produced safely in the browser; the automated tests cover it (§12.2).

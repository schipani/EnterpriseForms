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

* **Finding.** When I began item 3, the Docker engine on this machine was **not running** (it had stopped before this session; I did not stop it). I started Docker Desktop (no development service has an auto-restart policy, so nothing else started) and inspected read-only: every development container is `Exited (255)` — killed when the engine stopped. The persistent volumes are intact.
* **Consequence.** The development Vault runs `server -dev` **in memory** with no volume, so the **development organization signing keys no longer exist**. Starting the stack would leave every existing record unverifiable, and making them readable would require regenerating keys — which the owner prohibited. **I did not start, migrate or rebuild the development stack.** No volume, database, Vault, SpiceDB or key was modified.
* **Backup (recoverable, verified).** Read-only tar copies of all six volumes in `C:\Users\tsch\Documents\EnterpriseForms-dev-backups\20261022-193852\` with `MANIFEST.txt` (entries and SHA-256) and `containers.txt` (container inventory): business-db (1,563 entries, PostgreSQL 18 data directory), authority-db (1,412), spicedb-db (1,368), dev-seed (3); azurite and archive-store volumes were empty (development blobs were never persisted). They are crash-consistent copies (the databases were killed by the engine stop), so a restore performs normal PostgreSQL crash recovery.
* **Migration review (done).** The only new migration since main is the business migration `PhaseC6ProtectionStatusVerification` (M0). Forward steps: replace one check constraint with a more permissive one; rename one index; add nullable columns and checks that existing data satisfies; two data updates on `field_protection_migrations` (record existing verification results; re-queue instantly completed migrations). No drop, truncate or rewrite of record data. M1 and AC-2 add no migration (Business or Authority).
* **Decision needed from the owner** (§8.5).

### 8.5 Options for browser acceptance

1. **Fresh development stack, keep the old volumes untouched (recommended).** Run a fresh dev stack under a new compose project name with new volumes and new development keys; the old project's volumes stay as they are (and are backed up). The old project's *networks* must be removed first because both projects pin the same subnets (networks hold no data and are recreated). The old records stay unreadable in either case, because their keys are gone.
2. **Reset the existing development project** (`down -v` and `up`): new keys, the old development data deleted from the volumes (the backup remains). Needs explicit authorization.
3. **Review on the hosted evidence only** (§7.2, §8.3) without a local stack.

Root cause to fix later (not done, needs its own decision): the development Vault keeps its transit keys only in memory. A file-backed development Vault (or a documented key export) would survive engine restarts.

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


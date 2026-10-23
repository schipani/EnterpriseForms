# Phase C6 — Architecture review: record lifecycle and history

**Status: Revision 1 — APPROVED FOR CONTROLLED DEVELOPMENT (owner, 2026-10-22, decision C-43). Milestone 0 implemented and provisionally accepted (§1.1.4); Milestone 1 implemented, awaiting owner browser acceptance (decision C-45, `docs/PHASE_C6_STATUS.md` §7); Milestones 2–5 not started.**

Approval terms (C-43): OD-01…OD-13 approved as recommended, with these clarifications — Submitted records are locked until an authorized owner reopens them with a reason; archive and unarchive preserve existing field values without revalidating them; restoration is exact and all-or-nothing; historical views clearly distinguish current labels from historical content; restoring values into a currently plaintext field requires explicit acknowledgement but can never bypass organizational encryption requirements. AC-1 and AC-2 approved for controlled development (not yet implemented); **AC-3 deferred** (the Authority journal format does not change); AC-4: the existing fail-closed behaviour is kept for V1. Only Milestone 0 is authorized so far. This is not production approval, an independent security review or Level 2 certification.

Branch `phase-c/record-lifecycle` from main `f859e42`. Revision 1 was prepared as an architecture review only: no application code, schema, signing protocol, security control, persistent development database, SpiceDB, Vault or signing key was changed or touched while preparing it. Milestone 0's implementation status and evidence are in `docs/PHASE_C6_STATUS.md`.

**What this is not:**
* not an implementation, and not permission to implement;
* not an independent cryptographic or security review;
* not a production approval or Level 2 certification.

Every Authority change proposed here (§5.3) is presented separately and needs its own approval and, before production, the independent Level 2 review.

**Inputs read:** CLAUDE.md §2.3, §2.5, §3, §4B, §4C, §4D, §4G, §4H, §4K–§4P, §4T, §4U; CLAUDE-DESIGN.MD PG-016, PG-017, PG-021, PG-025; `SECURITY_GAP_ASSESSMENT.md` (G06, G12, G16, G17, G19, G20, G21); `docs/PHASE_C4_STATUS.md`; `docs/PHASE_C5_STATUS.md` (§12.3 in particular); `docs/architecture/PHASE_C5_COLLABORATION_CONTRIBUTOR_WRITES.md`; `docs/decisions/PHASE_C_DECISION_LOG.md` (C-12, C-17, C-28, C-34…C-42); `trust_authority/CROSS_SYSTEM_CONSISTENCY_PROTOCOL.md` and `MERKLE_RECORD_MANIFEST_PROTOCOL.md`; and the code cited inline.

---

## Summary of the recommendation

1. **Milestone 0 — fix the status-count defect first, with no Authority change.** The intermittent failure has a deterministic cause: the business "accepted head" pointer is a lossy projection that is only updated on the commit-response path, and one worker branch never repairs it. Status claims should rest on Authority-verified results the worker already computes, and the estimate should report "unknown" instead of guessing (§1.1).
2. **Lifecycle (Draft → Submitted → Archived) works on the existing Authority protocol.** `record_status` and `submitted_on` are already signed header columns. An owner content write can change them today. V1 is owner-only, enforced by the business API, with one small optional Authority guard for share-editor writes (AC-2).
3. **History content already exists and is verifiable.** Every accepted revision's rows stay in insert-only business tables. The Authority keeps every accepted manifest commit with its complete leaf index. One version can already be verified with the existing `VerifyRecordProjection(HistoricCommittedOperationId)`. **What is missing is a trusted list of versions** (revision, time, actor). That needs one additive, read-only Authority endpoint (AC-1).
4. **Restore is safe for V1 with restrictions.** Owner-only, Draft-only, field values and repeating sections only, all-or-nothing validation against the current form, fresh identities for resurrected items, through the ordinary owner write path, as a new revision. Documents and child records are excluded in V1.
5. **No change** to RowHmac, Merkle v1, signed schemas, SpiceDB schema, key custody or the trust boundary is needed for any of this.

Thirteen product decisions need the owner (§9). Twelve risks are listed for the independent reviewer (§10).

---

## 1. Current implementation assessment

### 1.1 Stabilization: the intermittent field-protection status-count failure

**Symptom (PHASE_C5_STATUS §12.3).** Two hosted runs on `1fc1393` failed different tests of the same family:
* `FieldProtectionMigrationTests.ConcurrentUserEditsAndAWorkerRestart…`: migration `Completed` 24/24, but the legacy-plaintext count was 1, expected 0.
* `FieldProtectionDowngradeTests.InterruptedAndTamperedMigrations…`: legacy-encrypted count 2, expected 1.

The Authority-backed checks in both tests behaved correctly. Only the business-side count was wrong.

#### 1.1.1 How abandoned, pending and committed operations are distinguished today

| Where | Representation | Trusted? |
|---|---|---|
| AuthorityDb `content_change_operations.operation_state_name` | `Prepared`, `Committed`, `Aborted`, `Quarantined` (`ContentChangeOperation.cs`). An abandoned operation stays `Prepared` until it expires. It becomes `Aborted` only when someone later tries to commit it (`ContentService.CommitAsync` lines 245–262). Nothing sweeps expired operations. | Yes |
| AuthorityDb `record_content_heads` | The single current accepted `(revision, operation, root)` per record. | Yes |
| AuthorityDb `record_manifest_commits` (+ leaves) | One immutable row per **accepted** revision, never for an aborted one. Existence of a manifest commit for an operation = that operation was accepted. | Yes |
| BusinessDb `form_record_revisions.candidate_state` | Always `"Staged"`. Never updated (memory note for B-28; `RecordCandidateStager` sets it on insert only). | No, and unusable |
| BusinessDb `form_records.accepted_content_change_operation_id` + `record_revision` | A projection of the head, written only after a successful commit response, in a **separate** business transaction, by five handlers (create, update, upload commit, document remove, reprotection). `FormRecord.RecordAcceptedContent` keeps it monotonic. | No (hint) |
| BusinessDb staged rows | Insert-only for the runtime role (`BusinessRuntimeGrants`). Rows of accepted **and** abandoned candidates stay forever, side by side. | No |

So the only trustworthy answer to "which operation is committed" is the Authority. Accepted revisions are contiguous (each prepare uses `head + 1` and commit is a CAS on the head revision), but several **candidates** can share one revision number (a retry with a new idempotency key, a commit aborted by revocation, an interrupted reprotection attempt).

#### 1.1.2 Root causes (from code reading; the hosted logs do not prove which one fired)

`BusinessStore.CurrentOperationsAsync` (`BusinessStore.cs:342`) picks, per record, the projected pointer if a staged header exists at that operation **and** at `record_revision`; otherwise the highest staged revision. Four defects combine:

| ID | Defect | Effect |
|---|---|---|
| **F-A** | **Stale but consistent pointer.** The Authority commits, then the process stops before the second business transaction updates the pointer (worker restart, cancellation, API crash, DB error, lost HTTP response). The pointer still names the previous accepted operation, whose header still exists, so the consistency check passes. | The estimate counts the old revision's rows. A record already re-protected by the worker is counted as still holding plaintext. This matches the `ConcurrentUserEditsAndAWorkerRestart` failure exactly. |
| **F-B** | **Missing or inconsistent pointer falls back to "highest staged revision".** That includes abandoned candidates, and is ambiguous when an abandoned candidate shares a revision with the accepted one. | The estimate may read a candidate the Authority never accepted. |
| **F-C** | **The worker's no-op branch never repairs the projection** (`FieldProtectionMigrationService.cs:163–166`). After F-A, the next pass reads the Authority head, finds no old-form rows, and records the item complete **without calling `RecordAcceptedContent`**. It also labels it `CompletedByUserCommit = true`, although the worker's own earlier attempt made that commit. `FinalizeAsync` reads every Authority head too, and also does not repair. | F-A becomes permanent. The audit attribution is wrong. |
| **F-D** | **The test oracle reproduces the estimate.** `PlaintextRowsInLatestSnapshots` and `EncryptedRowsInLatestSnapshots` use `COALESCE(pointer, highest staged)` — without even the consistency check. | The test measures an untrusted projection, so it can fail while the Authority state is correct. |

Note that `fullyEncrypted` / `fullyPlaintext` require `legacyPlaintext == 0` / `legacyEncrypted == 0` from this estimate (`FormDesignerHandlers.cs:907–920`). A stale estimate therefore makes the status wrongly report **not** encrypted after a verified completion. The error is in the safe direction, but the status is still incorrect.

#### 1.1.3 Recommended deterministic fix (milestone M0; no Authority change, no signed-schema change)

| # | Change | Why it is deterministic |
|---|---|---|
| **S1** | **Status claims come only from Authority-verified results.** The worker's post-check (`FinalizeAsync`) already reads each record's Authority head and counts old-form rows in exactly that operation. Persist that per item: `verified_head_operation_id`, `verified_head_content_revision`, `verified_old_form_value_count`, `verified_on` on `field_protection_migration_items`, plus the aggregate on `field_protection_migrations`. The status endpoint reports `verifiedRemaining` from these. `fullyEncrypted` / `fullyPlaintext` use the verified result and the completed, verified migration. | Uses only Authority-accepted heads. After the protection requirement is effective, the Authority's barrier (`EnforceProtectionBarrierAsync`, at prepare and commit) refuses any candidate that writes the old form. So a verified zero for an enumerated record cannot later become non-zero. G19 (the record list comes from the business database) is unchanged. |
| **S2** | **Repair the projection wherever a head is observed.** Call the monotonic `RecordAcceptedContent` in the worker's no-op branch, in `FinalizeAsync`, after `VerifiedRecordReader` reads, and on idempotent replays. | Monotonic: never moves backwards; at equal revision the Authority's answer wins. |
| **S3** | **Stop guessing.** When the pointer is missing or inconsistent, `CurrentOperationsAsync` returns "unknown" for that record instead of the highest staged revision. The status reports these as `unverifiedRecordCount`, and the estimate is labelled an estimate. | No input can make the estimate pick an unaccepted candidate. |
| **S4** | **Correct attribution.** Before each prepare, store the attempt's operation id on the item (`last_attempt_operation_id`, derived as today from the idempotency key). On the no-op branch, `CompletedByUserCommit = head operation ∉ this item's attempted operations`. | Operation ids are derived deterministically (`RecordCandidateStager.DeriveOperationId`). |
| **S5** | **Tests.** Replace the SQL oracle with an Authority-derived one: the verified status fields from S1, cross-checked by a new read-only test observer that reads `record_content_heads` on the Authority database container. (Today the harness has only `AttackerSql`, which plays the hostile **BusinessDb** administrator; the observer must stay separate from it and exist only in the test harness.) Add a deterministic reproduction: arm the existing `CrashAfterContentHeadCommit` fault during a reprotection commit, restart the worker, and assert that the status converges to the verified count and the projection is repaired. Add a fixture with an abandoned candidate at the same revision as the accepted one. | Reproduces F-A, F-B and F-C on demand instead of by timing. No retries are added. |

Schema impact: one business migration (new nullable columns on the two migration tables). The response contract gains additive fields; existing ones keep their meaning, with the estimate labelled. No Authority, RowHmac, Merkle or SpiceDB change.

#### 1.1.4 Milestone 0 as implemented (decision C-44) — differences from §1.1.3

The hosted logs of the two failures confirm the diagnosis. Attempt 2 waited 300 s with the migration `Completed` (the Authority post-check had passed) while the status stayed `EncryptionMigrationIncomplete` with a legacy count of 1: a permanently stale projection (F-A + F-C). Attempt 1 counted 2 legacy-encrypted records where the Authority-accepted heads hold 1.

| Step | As implemented | Difference from §1.1.3 and why |
|---|---|---|
| S1 | Per item: `verified_head_operation_id`, `verified_head_content_revision`, `verified_old_form_value_count`, `verified_on`. Per migration: `verification_checked_on`, `verified_record_count`, `verified_remaining_record_count`, `verified_remaining_value_count`, `unverified_record_count`. Status field `verification` reports them with a `basis` text saying they are stored in the business database, not protected against a database administrator, and limited to listed records (G19). | **The local estimate is kept as a cross-check.** "All listed records encrypted / migrated to plaintext" needs a clean stored check **and** an estimate that agrees, so a forged stored result alone cannot produce the claim (the existing forged-completion tests now also forge the new columns). This is what made the flake visible, so S2 makes the estimate converge: the post-check repairs every checked record's projection before the estimate is read. |
| S1 (new) | **Every migration is verified by the worker.** A migration created when the estimate finds nothing to convert is queued, not marked completed. | Found during implementation: before M0 such a migration was marked "Completed and verified" at creation from the estimate alone, with no Authority check. With a stale projection that could claim encryption while an accepted head still held plaintext (the unsafe direction). The migration's data step re-queues the latest such migration of each field, so the worker verifies it; earlier migrations that did pass the worker's post-check keep their claim, with the equivalent stored result (remaining 0, unverified 0, checked count unknown). |
| S1 (new) | The post-check counts a listed record whose Authority head cannot be read (403/404/423) as **unverified** and never completes the migration with any; before, a tracked record answering 404 made the post-check throw and the migration loop as Blocked. | "Unverified rather than guessing". |
| S2 | One store method, `ProjectAcceptedHeadAsync`, applies the monotonic rule in a **single conditional `UPDATE`**; all projection writers use it (create, update, upload commit, document removal, worker, post-check, verified reads). | Atomicity: the earlier load-modify-save could regress the projection when two writers raced. `FormRecord.RecordAcceptedContent` remains as the documented in-memory rule. |
| S3 | `AcceptedHeadProjection.Resolve` returns Resolved / NoAcceptedContent / Unresolved. "No accepted content" is projected as revision 0 with no operation (constraint `ck_form_records_revision_projection` replaces `ck_form_records_revision_positive`). Unresolved records are visited by the migration (value count 0) and reported as `estimateUnresolvedRecordCount`. | A record whose creation never committed would otherwise stay "unknown" forever and block the claim; the Authority's answer (revision 0) now resolves it. A worker item for such a record completes as `NoAcceptedContent` instead of failing `NO_COMMITTED_CONTENT`: there is no copy to convert, and the barrier refuses one from then on. |
| S4 | `completed_by_operation_kind` = MigrationWorker / OtherMigration / UserOperation / NoAcceptedContent, decided by re-deriving this record's reprotection operation ids (`ReprotectionAttribution`); `completed_by_user_commit` is true only for UserOperation. | **No `last_attempt_operation_id` column:** operation ids are a pure function of the idempotency key, so derivation needs no extra write and also covers a crash before such a write. Limitation: when a user operation was accepted after the worker's own, the head (and so the attribution) is the user's; earlier accepted operations are not visible without AC-1. |
| S5 | `ComposeStack.AuthorityObserverSql` (read-only transaction inside the authority-db container, test harness only) and `AuthorityOracle`. Every migration, downgrade, encryption, repeating-group and rules assertion that took "the accepted operation" from the business projection or the highest staged revision now takes it from the Authority. New deterministic reproductions: lost commit response in both directions (`CrashAfterContentHeadCommit`), and an abandoned candidate at the accepted revision with a lost and then a forged projection (`PauseBeforeContentCommit`). | No retries, sleeps or longer timeouts were added. |

### 1.2 Content and versioning model (as built)

* **Every accepted revision is a complete snapshot.** Each content operation stages a full candidate (header, every value row, every occurrence, every attachment version) under its operation id (`RecordCandidateStager.StageAndCommitAsync`). Rows are insert-only for the runtime role. There is no purge of committed rows. A DBA can still delete them (detected, not prevented).
* **The Authority keeps the complete expected set for every accepted revision.** `record_manifest_commits` stores root, leaf count, leaf-index digest, key version and commit time. `record_manifest_leaves` stores every item key, payload digest and row signature. It does **not** store row bytes.
* **Actor and time are recorded in the Authority.** `content_change_operations` stores the actor identity binding, `operation_committed_on` and, for workload operations, `reprotection_command_id`. The content journal entry (`ContentCheckpointBody`) records the actor's Authority user id and authorization revision, and is archived.
* **Business user ids equal Authority user ids.** `RequestContext.LocalUserId` is the `authority_user_id` claim, so the business can label an Authority actor id with its user projection.
* **Signed header columns:** `form_records` v2/v3 sign `record_status`, `submitted_on`, `created_form_version_id`, `created_by_user_id`, `form_definition_id`, tenant and (v3) the parent link. `last_edited_form_version_id`, `modified_on` and owners are **not** signed (local projection only).

### 1.3 Record lifecycle (as built)

* Status is always `"Draft"`. Create sets it (`CreateFormRecordHandler.cs:138,182`). Every later write clones it (`CandidateBuilder.CloneHeader`). There is no Submit or Archive endpoint.
* The list filter offers `Draft`, `Submitted`, `Archived`, `Deleted` (`RecordListQuery.Statuses`) but reads the **unsigned** `form_records.record_status` projection.
* **The Authority does not interpret status.** For an owner, `AuthorizeMutations` returns immediately, and `ClassifyMutations` compares digests only. An owner write can set any `record_status` and `submitted_on`. For share editors, the header rule is `ShareUpdatable: false` with `record_status`/`submitted_on` in class C (`ContentDeltaRules.cs:463–466`), so a contributor can never change status.
* **SpiceDB has no lifecycle permission.** `form_record.read` and `edit` are `effective_owner` (owner, owning-team participant, parent record's owner). Shares grant only field-item and document-item permissions (`authority.schema.zed`).
* Requiredness and rules are enforced on **every** full-scope save, Draft included (`CandidateBuilder.cs:388–415`). CLAUDE.md §3's "define draft vs submitted validation separately" is not yet implemented.
* CLAUDE.md requirements: §2.3 statuses; §4B — Owner may archive, Manager may not, Contributor may "save/submit where lifecycle allows"; §4D — `RECORD_ARCHIVED`, `DATA_RESTORED`; §6 — `POST /form-records/{id}/submit`, `GET …/history`, `POST …/restore-values`; acceptance test 12.

### 1.4 History (as built) — answer to "content or only fingerprints?"

**Both are kept.** The content bytes of every accepted revision are in BusinessDb (all rows of the committed operation). The fingerprints (leaf index with signatures) are in AuthorityDb. Together they allow full verification of any earlier revision.

What already works:
* `VerifyRecordProjection` accepts `HistoricCommittedOperationId` (B-27, used for historic document versions). It verifies the observed rows against that revision's manifest: exact key set, digests, every HMAC under that revision's key version, and the root (`ContentService.cs:451–499`).
* It requires **full read now** (effective owner). Partial (share) readers get 404 for any historic revision.
* It refuses key versions below `MinimumVerificationKeyVersion` (fail closed).

What is missing or limiting:
1. **No trusted list of versions.** No Authority API enumerates a record's accepted revisions. The business can list staged headers, but that list mixes in abandoned candidates and cannot detect deleted revisions without the Authority.
2. **A historic verification failure quarantines the current head** (`ContentService.cs:491`), not only the historic version. Today any missing historic row means tampering, so this is conservative. It becomes an operational hazard as soon as any retention purge of history exists (PB-13, G16).
3. **Historic encrypted values depend on historic KEKs.** The local key provider accepts exactly one key reference (`AesGcmFileEncryptionService.cs:306–315`). KEK rotation is an open Stage B §7.1 item. After a rotation, older ciphertext fails closed with `EncryptionUnavailable`.
4. **Labels come from unsigned metadata.** Field labels, layouts and option labels are unsigned business rows (G21). A historic view can show a stored option id with a relabelled or deactivated label.
5. **The activity timeline is the local audit projection** (`GetRecordActivityHandler`), already labelled as untrusted.
6. **Child records** have their own heads and manifests. A parent's history does not include children's changes.

### 1.5 Restore (as built)

There is no restore. The owner write path is the natural base:
* `UpdateFormRecordHandler.OwnerUpdateAsync` → `CandidateBuilder.ApplyValuesDetailed` decrypts every carried value, re-creates it under the field's **current** protection (`Carry` → `CreateRow` → `Protect`), and validates the full scope against the current layout and option catalog. A deactivated option or a tightened rule blocks the save (`CandidateBuilder.cs:505–523`).
* Optimistic concurrency: `ExpectedContentRevision` + ETag `"<revision>-<operation>"`; the Authority CAS on the head; commit-time re-authorization under the organization lock (C5 §2).

### 1.6 Field-item share edges outlive removed values

`CommandPlanner` adds `field_item … shared_via` edges when a share is created. Only share revocation removes them. Nothing removes an edge when a value item leaves the manifest. So if a removed item **id** were ever written again, an old share would cover it again. This matters for restore (§4.2, rule I-R).

---

## 2. Proposed lifecycle state machine

### 2.1 States and transitions

> **As implemented in M1 (C-45):** the state machine below is built as designed. Differences: none in behaviour; the request-validation order was corrected to authorization first; a Draft → Archived transition and the deterministic Unarchive rule are implemented exactly as drawn. The Authority-side Draft-only guard for share editors (AC-2) was added afterwards (C-46): contributors are now locked by the Authority as well; R-01 remains for ordinary owner edits.

```
                 Submit (owner)                 Archive (owner)
   ┌─────────┐ ─────────────────▶ ┌───────────┐ ──────────────▶ ┌──────────┐
   │  Draft  │                    │ Submitted │                 │ Archived │
   └─────────┘ ◀───────────────── └───────────┘ ◀────────────── └──────────┘
        │       Reopen (owner, OD-03)            Unarchive (owner):
        │                                        back to Submitted if submitted_on is set,
        └──────────── Archive (owner) ─────────▶ otherwise back to Draft
```

| # | Transition | Header change (signed) | Who (V1) | Validation |
|---|---|---|---|---|
| T1 | create → Draft | `record_status = Draft`, `submitted_on = null` | as today | as today |
| T2 | Draft → Submitted | `Submitted`, `submitted_on = now` | effective owner | **full submission validation** (§2.3) |
| T3 | Submitted → Draft (Reopen) | `Draft`, `submitted_on = null` | effective owner | reason required; **OD-03** (allow at all?) |
| T4 | Draft → Archived | `Archived`, `submitted_on` unchanged (null) | effective owner | none on values |
| T5 | Submitted → Archived | `Archived`, `submitted_on` kept | effective owner | none on values |
| T6 | Archived → Draft or Submitted (Unarchive) | `submitted_on` set → `Submitted`, else `Draft` | effective owner | none on values |
| — | any other | — | — | refused, `409 LIFECYCLE_TRANSITION_NOT_ALLOWED` |

* **Deterministic unarchive without a new signed column.** Archive keeps `submitted_on`; Reopen clears it. So `Unarchive` restores exactly the pre-archive state, and no `status_before_archive` column (signed schema v4) is needed.
* **"Effective owner"** = the SpiceDB `effective_owner` (direct owner, active owning-team participant, the parent's owner for a child record). Not Managers (CLAUDE.md §4B), not Contributors, not SystemAdministrator, not Designers.
* `Deleted` is out of scope (retention/purge belongs to PB-13 and G16).

### 2.2 What each state permits

| Operation | Draft | Submitted | Archived |
|---|---|---|---|
| Owner edits values / sections | yes | **OD-02** (recommended: no; reopen first) | no |
| Contributor edits shared fields (C5, switch on) | yes | no | no |
| Upload / replace / remove documents | yes | as owner edits (OD-02) | no |
| Create / link / unlink child records | yes | as owner edits (OD-02) | no |
| Restore an earlier version (§4) | yes | no | no |
| View, history, download, activity | yes | yes | yes |
| Share, revoke, transfer ownership | yes | yes | **OD-06** (recommended: revoke and transfer always; new shares allowed) |
| Field-protection migration (workload) | yes | yes | **yes** — security wins over lifecycle; the workload copies the header verbatim |

Reads are never blocked by status. Status never grants or removes access.

### 2.3 Validation per transition

* **Submit (T2):** built on a verified **complete** read (`ProtectedAction.Edit`). Validate every current value against the effective edit layout (version behaviour per CLAUDE.md §3): types, required and required-if, visibility rules, active and organization-enabled options (§4G), dependent dropdowns, repeating-section bounds. Return all failures with `field_keys`, as the editor already handles. **OD-04:** also refuse while the record has upload sessions not yet committed or rejected. **OD-05:** children are not required to be submitted (no cascade in V1).
* **Archive, Unarchive (T4–T6):** verified complete read only. **No value validation.** Archiving must never be blocked by a deactivated option or a newly required field.
* **Reopen (T3):** verified complete read plus a bounded reason (1–500 characters).
* **Draft saves:** unchanged in V1. Introducing a lighter draft validation is a separate decision (**OD-07**, recommended: defer).

### 2.4 How a transition is written (existing protocol)

Each transition is one owner content operation: prepare → stage → read back → commit.

* **New business component: `LifecycleTransitionBuilder`.** It clones the header with the new status, and copies **every other row verbatim** (ciphertext, attachments, occurrences), exactly as `SharedFieldWriteBuilder` does, with the new operation binding. It does **not** run the owner candidate builder. So a transition never decrypts, never re-encrypts, and is never blocked by today's catalog. Verbatim ciphertext stays valid because value and document AAD do not bind the operation, revision or surrogate key (C5 §6, U3).
* For an owner, the Authority classifies by digests and authorizes by ownership. It accepts the candidate. No Authority change.
* **Protection barrier interaction.** If a field's protection changed and this record still holds rows of the old form (its migration item is pending), a verbatim copy would carry those rows, and the Authority's barrier (`EnforceProtectionBarrierAsync`, prepare and commit) refuses it with `409 FIELD_PROTECTION_CHANGED`. Recommended: let it fail closed with "A protection change is still being applied to this record; try again shortly." The migration processes records in every lifecycle state, so this resolves itself. The builder must not convert those rows itself: that would break its "never decrypts" property.
* After commit: update the `form_records` projection (`record_status`, `submitted_on`, pointer) monotonically with the accepted revision, and record the audit event. The record page shows status from the **verified** header; the list labels its status as the local projection, as today.

### 2.5 Effects on owners, readers and shared contributors

* **Owners and owning-team members** see the lifecycle actions (§6).
* **Field-share readers** keep reading their shared fields in every state. Status is shown to them only if the header is in their projection. Today partial projections show `"(partial)"`. **OD-08:** expose the status label to partial readers (recommended: yes; status is not field content).
* **Contributors** lose "Edit shared fields" outside Draft. The UI explains why (§6). The business refuses with `409 RECORD_NOT_EDITABLE`. Optional Authority guard AC-2 enforces it in the Authority too.
* **Children:** status is per record. Archiving a parent does not archive its children (**OD-05**).
* **My Records:** **OD-09** — hide Archived by default (recommended: yes, with the status filter to show them).

### 2.6 Audit events

All through the existing `AuditRecorder`, category `Record`, with `contentRevision`, `operationId`, `fromStatus`, `toStatus`, correlation id. Never values.

| Action | When |
|---|---|
| `RECORD_SUBMITTED` | T2 committed |
| `RECORD_REOPENED` | T3 committed (with reason) |
| `RECORD_ARCHIVED` | T4/T5 committed (optional reason) |
| `RECORD_UNARCHIVED` | T6 committed |
| `RECORD_LIFECYCLE_DENIED` | refused for permission, state or validation (outcome `Denied`/`Failed`, error code only) |
| `RECORD_VERSION_VIEWED` | a historic version was verified and shown (§3) |
| `RECORD_VERSION_RESTORED` (the §4D `DATA_RESTORED` family) | restore committed (§4), with source and new revision/operation and reason |
| `RECORD_HISTORY_VERIFICATION_FAILED` | historic verification failed (the Authority journal holds the trusted incident) |

The trusted evidence remains the Authority content journal (actor, time, revision, operation). The local events are the queryable projection (ADR-SEC-2026-10-16-01).

### 2.7 Concurrency and stale versions

* Every transition requires `If-Match` with the current ETag and the expected current status in the body. Status mismatch → `409 LIFECYCLE_STATE_CONFLICT`. Revision mismatch → `409 CONTENT_REVISION_CONFLICT` / `412` per PB-06.
* The Authority CAS on the head serializes transitions with edits, uploads, reprotection and other transitions. A contributor write prepared while Draft and committed after Submit fails the CAS: the submit advanced the head.
* Idempotency: an `Idempotency-Key` per transition. A replay returns the same accepted head (`StageAndCommitAsync` already does this).
* Double submit from two tabs: the second fails the CAS or the status check, never produces two submissions.

---

## 3. Historical version storage and retrieval

### 3.1 Storage

No new storage. History = the immutable staged rows of each accepted operation (BusinessDb) + the accepted manifest commits and leaves (AuthorityDb). Retention of history follows the record (G16, PB-13; no purge exists today).

### 3.2 Listing versions (needs AC-1)

`GET /api/v1/form-records/{formRecordId}/versions?cursor=…` (feature `FormRecords`, use case `ListRecordVersions`):
1. The business calls the new Authority read endpoint (AC-1, §5.3) as the delegated user.
2. The Authority checks `form_record.read` (effective owner) at the projection ZedToken with the gate open, and re-reads the gate afterwards, like `VerifyProjectionAsync`. Anyone else gets a concealment-safe 404.
3. It returns accepted commits newest first: revision, operation id, `committedOn`, actor kind (`User` or `ProtectionMigration`), actor Authority user id or reprotection command id, leaf count, key version, `verifiableNow` (key version ≥ minimum), `isCurrent`, and the AC-3 intent and restore source when present.
4. The business maps actor ids to display names from its user projection ("Unknown user" if absent) and adds a local action label (§3.4).

Listing does no HMAC work. It shows only Authority facts. It does **not** claim the rows of each version are intact: that is checked when a version is opened. The UI says so.

Because accepted revisions are contiguous, a gap in the Authority's list is impossible, and a version whose rows were deleted from BusinessDb is detected when it is opened.

### 3.3 Viewing one version

`GET /api/v1/form-records/{formRecordId}/versions/{contentRevision}`:
1. Resolve the operation id of that revision from AC-1 (never from business rows).
2. `ReadStagedAsync(record, operation)`.
3. `VerifyRecordProjection(Read, HistoricCommittedOperationId = operation)` — existing. Full read is required.
4. On success: decrypt with each envelope's recorded KEK reference and render read-only.
5. On any failure, fail closed, release nothing:

| Condition | Response |
|---|---|
| Not an effective owner now (including a revoked share, a former owner after transfer, another tenant) | `404 NOT_FOUND` (concealment) |
| Revision not accepted / not in the list | `404 VERSION_NOT_FOUND` |
| Rows missing, extra or altered | `423 INTEGRITY_VERIFICATION_FAILED`. The Authority journals an incident and, today, quarantines the head (AC-4). |
| Integrity key version below minimum | `503 HISTORIC_VERSION_UNVERIFIABLE` |
| KEK of a historic envelope unavailable | `503 HISTORIC_KEY_UNAVAILABLE` (no partial view) |
| Current version requested | redirect to the current record page |

**Current read authorization is applied to historic content.** Only people who may read the whole record **now** can open any earlier version. In V1, field-share readers, contributors and revoked users cannot see history at all, so a value they can no longer read cannot reach them through history. Field-scoped history for share readers would need an Authority change and is future work (FR-C6-01).

**Layout used for display (OD-10).** Recommended: the same layout resolution as the current record view (version behaviour, CLAUDE.md §3), plus an "Other recorded values" section for fields not placed in that layout, and the notice "Shown with the current form layout; labels may differ from when this version was saved." The signed header does not record which layout the version was edited with, and labels are unsigned (G21).

**Documents in a historic view:** list the attachment versions present in that revision; downloads use the existing historic document download (B-27). **Child records** are not part of a parent version; the view says so.

### 3.4 Action labels

The Authority stores no row bytes, so it cannot tell what an operation changed. Labels come from:
* the AC-3 operation intent, recorded in the Authority journal and labelled "recorded by the application" (the intent is an application assertion; the restore source is Authority-validated), if AC-3 is approved; otherwise
* the local audit event with the same operation id, labelled "local record (unverified)".

Actor, time, revision and the "System — field protection migration" attribution always come from the Authority.

---

## 4. Restoration design and security boundaries

### 4.1 V1 scope (recommended)

**Restore the field values and repeating sections of one accepted earlier version, as a new revision.**

| In V1 | Not in V1 |
|---|---|
| top-level field values, repeating-section entries and their values | documents (they keep their own version history, PG-021) |
| fields not placed in the current layout (data is preserved, CLAUDE.md §3) | child records, relationship links |
| encrypted values (re-sealed) | header lifecycle status, owners, shares (never restored) |
| | field-level or section-level restore (FR-C6-02) |

Whole-record restore is safe for V1 **with these restrictions**: effective owner only; record in Draft; one source version; all-or-nothing validation; fresh identities for resurrected items; documents and children excluded.

### 4.2 Algorithm

`POST /api/v1/form-records/{formRecordId}/versions/{contentRevision}/restore` with `If-Match` (current ETag), `Idempotency-Key`, body `{ expectedCurrentContentRevision, sourceOperationId, restoreReason }`. A separate `…/restore-preview` (GET) returns the change summary without writing.

1. **Current state.** Verified complete read of the current head with `ProtectedAction.Edit` (owner). Partial → `404`. Compare `If-Match` and `expectedCurrentContentRevision` → `409/412`.
2. **Lifecycle.** Current header status must be Draft → otherwise `409 RECORD_NOT_EDITABLE`.
3. **Source.** `sourceOperationId` must be the operation the Authority lists for `contentRevision`, and `contentRevision < head`. Verify it with `VerifyRecordProjection(Read, Historic…)` (§3.3). Any failure → refuse; nothing staged.
4. **Decrypt the source** with each envelope's KEK reference. Unavailable → `503 HISTORIC_KEY_UNAVAILABLE`; nothing staged.
5. **Target snapshot (exact restore of values, OD-11):** for every field, the source's values; a field with values now but none in the source is **cleared**. Documents are carried verbatim from the **current** head. The header is cloned from the current head (status unchanged).
6. **Identity rule I-R.** An item (value or entry) present in both the current head and the source keeps its id. An item present only in the source gets a **new id**. Reason: field-item share edges outlive removed values (§1.6); re-using an old id would silently re-expose the restored value to an old share. New-id values are therefore never covered by any existing share (CLAUDE.md §4H: new items are excluded by default).
7. **Re-create every value under the field's current protection** through `FieldValueCodec.CreateRow` + `Protect`: fresh DEK and nonce, current KEK, current classification. Restore never copies source ciphertext. (Verbatim copy would be impossible anyway for new ids, because the value AAD binds the item id.)
8. **Validate the full scope** against the current effective edit layout and catalog, with the owner validator (types, required, rules, active and enabled options, dependent dropdowns, section bounds). Any failure → `422 RESTORE_VALIDATION_FAILED` with `field_keys`; nothing staged.
9. **Prepare → stage → read back → commit** as an ordinary owner operation, with AC-3 metadata if approved. The Authority applies the CAS on the current head, the protection barrier, and commit-time re-authorization under the organization lock.
10. **After commit:** repair the projection; audit `RECORD_VERSION_RESTORED` with source revision/operation, new revision/operation, changed and cleared field keys, reason. Never values.

### 4.3 Changed form definitions and catalogs

| Change since the source version | Behaviour |
|---|---|
| Field still placed, same type | restored and validated |
| Field no longer placed (still defined) | restored, type-checked only, shown in the preview as "not in the current form" (**OD-12**) |
| Field newly required, source empty | validation fails; the owner must restore and then fill it, or not restore (**OD-11** exclusions) |
| Option deactivated or disabled by the organization (§4G) | fails with the disabled-option error for that field |
| Option list or dependency changed; parent no longer allows the child | fails |
| Section maximum lowered below the source's entries | fails |
| Field protection strengthened | value re-sealed under encryption (normal) |
| Field protection released (Amendment A1) | value written under the current, approved plaintext policy; the preview warns explicitly (**OD-13**: require an explicit acknowledgement) |
| Field now Restricted | owner may restore; PB-08 step-up is not available (existing residual) |
| Type change | impossible: storage type is immutable |
| Version behaviour `Historical` | validate against the created version's layout, as edits do |

### 4.4 Encrypted values and key rotation

* **Integrity keys:** the source must verify under its own recorded key version (≥ minimum). The new revision is signed with the current key version. Nothing old is re-signed.
* **Encryption keys:** source envelopes need their recorded KEK. New rows use the current KEK. Until historic KEK retention exists (Stage B §7.1), restore and history of values sealed under a retired KEK fail closed.
* No plaintext is written to logs, audit, the preview cache or the browser beyond the owner's authorized response (`Cache-Control: no-store`).

### 4.5 What restore never does

* never moves the Authority head backwards or rewrites history;
* never re-signs or copies old rows as accepted rows;
* never restores ownership, shares, protection policy or status;
* never bypasses the protection barrier, the CAS or commit-time authorization;
* never drops an invalid value silently (all-or-nothing in V1);
* never uses a business row to decide which version is accepted.

---

## 5. Required database and Authority changes

### 5.1 Traced: what works on the existing protocol (no change)

| Capability | Code path that already allows it |
|---|---|
| Submit, Reopen, Archive, Unarchive by an owner | `ContentService.PrepareAsync` → `graph.IsRecordOwner` → `ClassifyMutations` (digests) → `AuthorizeMutations` returns for owners; commit re-checks ownership under the organization lock. `record_status`/`submitted_on` are signed v2/v3 columns. |
| Viewing a historic version | `VerifyRecordProjection` with `HistoricCommittedOperationId` (full read only). |
| Restore as a new revision | Same as an owner update: prepare with the current head's revision and root, CAS at commit. |
| Contributors cannot change status | Header rule `ShareUpdatable: false`. |
| Migrations proceed for archived records | Reprotection rules copy the header verbatim and do not consider status. |

No change to RowHmac v1, Merkle v1, signed schemas (no `form_records` v4), SpiceDB schema, key custody, signing providers or the trust boundary.

### 5.2 Business database changes (no signed-schema change)

| Change | Milestone |
|---|---|
| `field_protection_migration_items`: `verified_head_operation_id`, `verified_head_content_revision`, `verified_old_form_value_count`, `verified_on`, `completed_by_operation_kind` (no `last_attempt_operation_id`, §1.1.4); aggregates on `field_protection_migrations`; `form_records` revision-0 projection constraint; data step (EF migration `PhaseC6ProtectionStatusVerification`) | M0 — **implemented** |
| `form_records` projection: start maintaining `record_status` and `submitted_on` (columns exist) | M1 |
| New audit action constants (§2.6) | M1, M3, M4 |
| OpenAPI: lifecycle, versions and restore routes; additive status fields | M0–M4 |

All new columns follow the multi-word naming rule. All are projections or worker bookkeeping, never consulted for authorization.

### 5.3 Proposed Authority changes — separate approval required

Each needs the owner's approval here and, before production, the independent Level 2 review (proposed new gap row **G22**, added only if approved). None changes signing, manifests or key custody.

| ID | Change | Type | Recommendation |
|---|---|---|---|
| **AC-1** | **IMPLEMENTED FOR DEVELOPMENT (C-50; not independently reviewed; ascending order per the owner's "chronological" instruction).** **Content history read endpoint.** `GET /internal/v1/content-revisions/{organizationId}/{formRecordId}/history?after=…&limit=…` (beside the existing head read) for delegated users. Requires `form_record.read` at the projection ZedToken with the gate open, and the post-read gate re-check. Reads existing rows only (`record_manifest_commits`, `content_change_operations`, `identity_bindings`). Cursor by revision, bounded page size. Concealment-safe 404. No state change, no journal entry. | Additive contract, read-only; no schema change | **Approve.** Needed for a trusted version list. |
| **AC-2** | **IMPLEMENTED FOR DEVELOPMENT (C-46; not independently reviewed).** **Lifecycle guard for share-editor writes.** In `ClassifyShareScopedAsync`, after the current snapshot is bound (D4), decode the accepted header's `record_status` and refuse unless it is `Draft` (`RECORD_NOT_EDITABLE`). | Content-mutation authorization change (G20 family) | **Approve** (defence in depth; small). |
| **AC-3** | **Operation intent and restore provenance.** Optional `operation_intent` (`Edit`, `Submit`, `Reopen`, `Archive`, `Unarchive`, `Restore`) and `restored_from_operation_id` on `PrepareContentChangeRequest`. Validate that the restore source is an accepted manifest commit of the same record below the head. Persist on `content_change_operations` (two nullable columns, AuthorityDb migration, runtime grants). Add both to `ContentCheckpointBody` → **journal body schema change**: the archive validator and DR replay must accept the new body version. | Contract + AuthorityDb schema + journal body version | **Approve or defer.** If deferred, action labels and restore provenance come from local audit, labelled unverified. |
| **AC-4** | **Historic verification failure handling.** Today it quarantines the **current** head. Alternative: journal the incident and mark only that historic commit unverifiable. | Behaviour change | **Keep current behaviour in V1** (conservative). Must be decided before any history retention purge (PB-13, G16). |
| AC-5 | Not proposed for V1: Authority-enforced lifecycle for owner writes; field-scoped history for share readers; contributor submit. | — | Future requirements FR-C6-01…03 |

---

## 6. UX workflow proposal (existing Record Workspace)

No separate application; no Form Designer change.

### 6.1 Record Overview (PG-016)

* **Status badge** in the existing Record summary, from the verified header: Draft (neutral), Submitted (info), Archived (warning). Beside the existing Integrity, Revision and Layout badges.
* **Action bar** (existing `PageHeader` actions, owner only):
  * Draft: `Edit` (existing), **`Submit`** (primary), overflow `More actions` → `Archive`.
  * Submitted: **`Reopen as draft`** (if OD-03), `More actions` → `Archive`.
  * Archived: **`Unarchive`**. `Edit` is hidden.
  * `Refresh` and `Edit shared fields` stay as they are. For a contributor outside Draft, `Edit shared fields` is replaced by the notice "This record is submitted (or archived). Shared editing is closed."
* **New `Versions` tab** next to `Activity and history`, shown only to effective owners. URL-addressable like the other tabs (`?tab=versions`).

### 6.2 Dialogs

| Action | Dialog content | Confirm button |
|---|---|---|
| Submit | "After submitting, the record can't be edited unless the owner reopens it." If validation fails: the list of fields with links to the editor. | `Submit record` |
| Reopen | Reason (required, 1–500 characters; "Don't include confidential details"). | `Reopen as draft` |
| Archive | "Archived records are read-only and hidden from My Records by default. You can unarchive it later." Optional reason. | `Archive record` |
| Unarchive | "The record returns to Draft (or Submitted)." | `Unarchive` |
| Restore | Change summary: fields changed, fields cleared, entries added or removed, fields not in the current form, encrypted values re-encrypted, released fields stored as plaintext (acknowledgement checkbox if OD-13), "Documents are not changed." Required reason. | `Restore as new version` |

All dialogs: focus trap and return, `Escape` closes, live-region result notice, usable at 320 px and 400 % zoom (PB-14; manual NVDA/VoiceOver review stays pending).

### 6.3 Versions tab

Table: **Version** (revision number; "Current" badge) · **Saved** (user's time-zone and date preferences) · **By** (display name, or "System — field protection migration") · **Action** (with "recorded by the application" or "local record (unverified)" note, §3.4) · **Verification** ("Verified when opened" / "Cannot be verified" when `verifiableNow` is false) · actions **View**, **Restore** (owner, Draft, not current). Cursor paging with the existing pagination component; independent refresh (PB-07).

**Version view** (route `/app/records/:formRecordId/versions/:contentRevision`, owned by `form-records`): the existing renderer in read-only mode; banner "Version 3 of 7 — saved … by …. You are viewing an earlier version." with `Back to current` and `Restore this version`; badge "Verified by the Authorization Authority"; fields that differ from the current version marked "Changed since" (**OD-10**: full side-by-side compare is future work).

### 6.4 Error messages

| Code | User message |
|---|---|
| `LIFECYCLE_TRANSITION_NOT_ALLOWED` | "This action isn't available for a record that is {status}." |
| `LIFECYCLE_STATE_CONFLICT`, `CONTENT_REVISION_CONFLICT` | "This record changed while you were working. Reload to see the latest version." |
| `RECORD_NOT_EDITABLE` | "This record is {status} and can't be changed. The owner can reopen it." |
| `VALIDATION_FAILED` (submit), `RESTORE_VALIDATION_FAILED` | "Some fields need attention before you can continue." + field list |
| `INTEGRITY_VERIFICATION_FAILED` | "This version could not be verified and can't be shown or restored. The record has been locked for review." |
| `HISTORIC_KEY_UNAVAILABLE`, `HISTORIC_VERSION_UNVERIFIABLE` | "This version can't be opened right now because a key needed to verify or decrypt it isn't available." |
| `NOT_FOUND`, `VERSION_NOT_FOUND` | the existing not-found page (no existence disclosure) |

### 6.5 Audit history presentation

The existing `Audit history` card stays, with its "local audit projection" note, and gains the new lifecycle and restore events. The Versions tab is the trusted view. The two are linked by operation id ("View this version" from an audit entry, when the viewer may open versions).

---

## 7. Test strategy

### 7.1 Layers

| Layer | Scope | Where it runs |
|---|---|---|
| Authority unit (in-memory store, real `ContentService`) | AC-1 permissions and paging; AC-2 status guard; AC-3 validation | local + CI |
| Business unit | `LifecycleTransitionBuilder` (verbatim copy, no decryption: decryption spy), `RestoreBuilder` (identity rule I-R, re-seal, validation, clearing), transition table, error mapping, M0 S1–S4 | local + CI |
| Architecture | new routes owned by `FormRecords`; no direct Authority SDK use; no business row used as "accepted" | local + CI |
| Docker integration | full protocol, faults, tampering, concurrency | hosted disposable stacks only |
| Playwright + axe | dialogs, tab, notices, 320/768/1440 px | hosted; dev-stack runs only with owner consent |
| SEC-P01…SEC-P12 | regression; must stay 13/13 | hosted, manual dispatch |

### 7.2 Adversarial matrix

| ID | Category | Scenario | Expected |
|---|---|---|---|
| C6-M0-01 | Stabilization | Authority commits a reprotection, worker restarts before the projection update (`CrashAfterContentHeadCommit`) | Item completes as the worker's own commit; projection repaired; verified status 0; `fullyEncrypted` true |
| C6-M0-02 | Stabilization | Abandoned candidate staged at the same revision as the accepted one; pointer missing | Estimate reports the record as unverified, never reads the abandoned rows; verified status unaffected |
| C6-M0-03 | Stabilization | Forged pointer and `record_revision` (DBA) | Estimate may be wrong and is labelled; verified status and completion unaffected (existing forged-pointer test extended) |
| C6-L-01 | Unauthorized transition | Reader, contributor, Manager, Designer-only, SystemAdministrator-only, no-grant user calls submit/archive/unarchive/reopen | 404 or 403, no operation prepared, audit `RECORD_LIFECYCLE_DENIED` |
| C6-L-02 | Unauthorized transition | Owning-team member removed from the team between prepare and commit | Commit refused (`PERMISSION_REVOKED_BEFORE_COMMIT`) |
| C6-L-03 | Invalid transition | Submit an Archived record; unarchive a Draft; reopen a Draft | `409 LIFECYCLE_TRANSITION_NOT_ALLOWED`, nothing staged |
| C6-L-04 | Validation | Submit with missing required, disabled option, broken dependency, too many entries | 422 with exact `field_keys`; head unchanged |
| C6-L-05 | Validation | Archive a draft holding a deactivated option | Succeeds (no value validation); value preserved byte-for-byte |
| C6-L-06 | Concurrency | Two tabs submit; owner submits while a contributor saves | Exactly one commit; the other gets 409; no lost edit |
| C6-L-07 | Locked edits | Contributor writes on Submitted / Archived via API | Business 409; with AC-2, the Authority also refuses a forged prepare |
| C6-L-08 | Locked edits | Upload commit, document remove, child create on Archived | Refused |
| C6-L-09 | Security over lifecycle | Field-protection migration over Archived and Submitted records | Completes; status and `submitted_on` unchanged |
| C6-L-09b | Barrier | Archive a record whose strengthened field still holds legacy plaintext (migration pending) | `409 FIELD_PROTECTION_CHANGED`, nothing committed, nothing decrypted; succeeds after the migration converts the record |
| C6-L-10 | Database tampering | DBA sets `form_records.record_status = Archived` (projection) | Record page shows the verified header status; actions follow the verified status |
| C6-L-11 | Database tampering | DBA edits `record_status` in the signed header row | Verification fails, quarantine, nothing released |
| C6-L-12 | Replay | Replay a transition request with the same idempotency key; replay with a different body | Same head returned; `IDEMPOTENCY_CONFLICT` |
| C6-H-01 | Unauthorized history | Field-share reader, contributor, revoked recipient, former owner after transfer, other tenant: list and view | 404 for all; no count or metadata disclosed |
| C6-H-02 | Revoked access | Owner revokes a share; the former recipient requests a version from before the revocation | 404 |
| C6-H-03 | Forged history | DBA alters, deletes or adds a row of an earlier accepted operation | View fails closed (`423`), incident journaled, head quarantined (AC-4 current behaviour) |
| C6-H-04 | Forged history | Request an aborted candidate's operation as a "version" | `404 VERSION_NOT_FOUND`; no quarantine |
| C6-H-05 | Forged history | DBA swaps rows between two accepted revisions of the same record | Fails (operation binding and leaf digests) |
| C6-H-06 | Cross-record | Version id or operation of another record or tenant | 404 |
| C6-H-07 | Missing data | Every row of an earlier version deleted from BusinessDb | Fails closed (`MISSING_ITEM`); never shows a partial version |
| C6-H-08 | Key rotation | Integrity key version below minimum; KEK reference unknown | `503`, nothing released |
| C6-H-09 | Listing integrity | AC-1 list compared with the Authority manifest commits | Exactly the accepted revisions, contiguous, newest first; no aborted operations |
| C6-H-10 | Labels | Option relabelled or deactivated after the version | Stored option id shown with "no longer available" where applicable; G21 caveat displayed |
| C6-R-01 | Unauthorized restore | Non-owner, contributor, Manager, revoked user | 404/403, nothing staged |
| C6-R-02 | Unauthorized restore | Owner restores a Submitted or Archived record | `409 RECORD_NOT_EDITABLE` |
| C6-R-03 | Stale version | Restore with an old `If-Match` after another save | 409/412; head unchanged |
| C6-R-04 | Replay | Replay a successful restore; replay the same request after a later edit | First returns the same head; second fails the CAS |
| C6-R-05 | Unverifiable source | Source rows tampered or missing; source key retired | Restore refused before any prepare |
| C6-R-06 | Forged source | `sourceOperationId` of an aborted candidate, another record, or the current head | 404/422, nothing staged |
| C6-R-07 | Encrypted values | Restore encrypted values sealed under an earlier DEK | New ciphertext (different nonce and wrapped DEK), decrypts to the source plaintext; no plaintext row created |
| C6-R-08 | Encrypted values | Field strengthened since the source (source plaintext) | Restored value encrypted; no plaintext in the new revision |
| C6-R-09 | Released field | Field released to plaintext since the source | Plaintext under current policy only after acknowledgement (OD-13) |
| C6-R-10 | Changed form | Field newly required, option deactivated, dependency changed, section bound lowered | 422 with field keys; nothing staged |
| C6-R-11 | Changed form | Field removed from the layout since the source | Restored per OD-12, type-checked |
| C6-R-12 | Share re-exposure | Value item removed after being shared; owner restores a version containing it | Restored with a **new** item id; the old share does not cover it; the recipient cannot read it |
| C6-R-13 | Share preservation | Item present in both versions and shared | Keeps its id; the share still covers it |
| C6-R-14 | Documents | Restore a version whose document set differs | Documents unchanged (current set) |
| C6-R-15 | Revocation race | Ownership transferred between prepare and commit | Commit refused |
| C6-R-16 | Barrier race | Protection requirement becomes effective between prepare and commit | `409 FIELD_PROTECTION_CHANGED` |
| C6-R-17 | Audit | Successful and refused restores | Events contain revisions, operations, field keys and reason; never values |
| C6-X-01 | Regression | SEC-P01…SEC-P12, C5 contributor-write suite, forged-pointer test | All pass |

---

## 8. Implementation milestones (after approval only)

| Milestone | Content | Exit criteria |
|---|---|---|
| **M0 — stabilization** | S1–S5 (§1.1.3, as implemented §1.1.4) | Deterministic reproductions C6-M0-01…03 pass; the formerly flaky tests use the Authority oracle; the full hosted integration suite passes on repeated runs; no retries added. *(Revised from "10 consecutive hosted runs" per the owner's instruction not to spend hours on repeated rebuilds: determinism comes from fault injection, and a limited number of repeat runs checks stability.)* — **implemented; see `docs/PHASE_C6_STATUS.md`** |
| **M1 — lifecycle** | Transition endpoints, `LifecycleTransitionBuilder`, status gates in every content-writing handler, projection, audit, UI actions and dialogs; AC-2 if approved | C6-L-* pass; contributor and owner suites unchanged |
| **M2 — Authority history read (and AC-3 if approved)** | AC-1 (and AC-3), Authority unit tests, internal OpenAPI artifact | Authority tests pass; independent-review package updated; SEC-P 13/13 — **AC-1 implemented (C-50); AC-3 deferred** |
| **M3 — history UI** | Versions list and version view, layout handling, error states | C6-H-* pass; axe clean — **implemented together with M2 as the owner's "M2 — Trusted Record History" (C-50); see `docs/PHASE_C6_STATUS.md` §12** |
| **M4 — restore** | Preview and restore endpoints, `RestoreBuilder`, dialog | C6-R-* pass |
| **M5 — evidence and closure** | `docs/PHASE_C6_STATUS.md`, decision log entries, gap register update, hosted evidence | Owner acceptance; independent review still outstanding |

Order: M0 first (it touches the same projection that M1 maintains). M1 does not depend on M2. M3 and M4 depend on M2.

---

## 9. Open decisions requiring owner approval

| ID | Decision | Recommendation |
|---|---|---|
| OD-01 | Approve the M0 stabilization approach (§1.1.3) | Approve |
| OD-02 | May an owner edit a **Submitted** record (values, documents, children) without reopening? | No: Submitted is locked; reopen first |
| OD-03 | Allow **Reopen** (Submitted → Draft) at all, and by whom? | Yes, effective owner, with reason |
| OD-04 | Must Submit wait for in-progress uploads to finish? | Yes |
| OD-05 | Does a parent's Submit or Archive cascade to child records, or require them? | No cascade, no requirement in V1 |
| OD-06 | On Archived records: allow new shares, revocation, ownership transfer? | Revocation and transfer always; new shares allowed |
| OD-07 | Introduce lighter Draft validation (save incomplete drafts)? | Defer; keep today's full validation |
| OD-08 | Show the status label to field-share (partial) readers? | Yes |
| OD-09 | Hide Archived records from My Records by default? | Yes, with the status filter |
| OD-10 | Layout used for historic views; scope of comparison | Current layout + "Other recorded values"; "Changed since" markers only |
| OD-11 | Restore semantics: exact (clear fields absent in the source) vs. only fields that had values; allow excluding invalid fields | Exact, all-or-nothing; field exclusion and field-level restore later (FR-C6-02) |
| OD-12 | Restore values of fields no longer placed in the current form? | Yes, type-checked, flagged in the preview |
| OD-13 | Require explicit acknowledgement when a restored value will be stored as plaintext under a released field? | Yes |
| AC-1…AC-4 | The Authority changes in §5.3, each separately | AC-1 approve; AC-2 approve; AC-3 approve or defer; AC-4 keep current behaviour |

Future requirements recorded, **not** proposed for approval now: FR-C6-01 field-scoped history for share readers; FR-C6-02 field- and section-level restore with exclusions; FR-C6-03 contributor submit; FR-C6-04 Authority-enforced lifecycle for owner writes; FR-C6-05 document restore within record restore; FR-C6-06 side-by-side version comparison.

---

## 10. Risks requiring independent security review

| ID | Risk | Notes |
|---|---|---|
| R-01 | Lifecycle rules are enforced by the business API, not the Authority, for owners (like form rules, U4) | A compromised business runtime could let an owner edit an archived record. A hostile DBA cannot: the status is signed. AC-2 covers contributors only. |
| R-02 | AC-1 exposes actor ids and operation timing of every accepted revision to effective owners | Owners can already see the activity timeline; review the metadata scope and paging limits. |
| R-03 | A historic verification failure quarantines the current record (AC-4) | Conservative but a denial-of-service lever for a hostile DBA (who can already delete current rows); blocks any future history retention purge. |
| R-04 | History keeps values that a later legal erasure or privacy request may require removing | Retention and erasure must cover history (G16, PB-13). |
| R-05 | Historic labels come from unsigned layouts and option catalogs | G21; a relabelled option changes the apparent meaning of history. |
| R-06 | Restore re-encrypts historic values under the current policy; under a released field it writes plaintext | Follows the approved A1 policy, but the reviewer should confirm that restore is not a downgrade path. OD-13 acknowledgement. |
| R-07 | Identity rule I-R depends on the claim that field-item edges are never removed with values | Traced in `CommandPlanner`/revocation; the reviewer should confirm no other edge lifecycle exists. |
| R-08 | Historic KEK retention is not implemented | Rotation would make history and restore fail closed; Stage B §7.1. |
| R-09 | AC-3 changes the content journal body | Archive validation, ledger anchoring and DR replay must accept the new body version; versioning must be explicit. |
| R-10 | Opening a version costs one full HMAC verification of every leaf | Under Managed HSM this is one call per leaf (G17); benchmark before production. |
| R-11 | M0 still relies on the business database to enumerate a form's records | G19 is unchanged by M0. |
| R-12 | PB-08 step-up is not available for restore or lifecycle actions on Restricted data | Existing residual; restore is a high-impact operation. |

**Preserved and unchanged by this proposal:** G06, G19, G20, G21; the C-12 and C-28 Authority reviews; U1–U11; PB-14 manual accessibility reviews; PB-08 step-up; the contributor-write switch (off by default, refused in Production); and every production Level 2 gate. Nothing here closes any of them.

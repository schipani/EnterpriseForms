# Phase C3 — Architecture assessment: repeating groups and dependent dropdowns

Status: assessment written **before** implementation (2026-10-20); implemented as assessed on the same branch (see `docs/PHASE_C3_STATUS.md`), on `phase-c/repeating-groups` from main `7b3c173`. Decisions C-15 … C-20 in `docs/decisions/PHASE_C_DECISION_LOG.md` record what was adopted.

## 1. Conclusion first

* **No change to the Authorization Authority, its command protocol, the RowHmac v1 canonical encoding or the Merkle v1 manifest is needed.** The trusted side already anticipated repeating groups:
  * `MerkleItemKind.RepeatedGroup = 2` is part of manifest v1 (`MerkleManifestV1`, `trust_authority/MERKLE_RECORD_MANIFEST_PROTOCOL.md` §kind codes);
  * `ContentService.AllowedEntityKinds` already accepts `RepeatedGroup` items whose canonical entity kind is `form_record_groups`;
  * every typed value row already signs `form_record_group_id` (nullable) — `BusinessSignedSchemas.ValueColumns`;
  * reprotection (`ReprotectionRules`) treats any non-target row generically (byte-identical or same-row-except-operation), so occurrence rows ride through encryption migrations unchanged;
  * full-record verification compares the complete accepted leaf index, so occurrence rows get completeness, anti-replay and tamper detection for free.
* What *is* new is business-side only: a new **registered business signed schema** (`form_record_groups` v1) for occurrence rows, staged and committed exactly like value rows, plus layout, API and UI work. Adding a signed schema follows the precedent of `field_date_time_values` / `field_guid_values` (Stage B).
* **Trust boundary changes: none.** Two deliberate restrictions keep it that way (§4): fields inside a repeating section are **not field-shareable** in this phase, and dependent-option checks, like conditional rules, are enforced for full-scope (owner) writes.

## 2. Repeating groups — storage and identity

| Concept | Representation |
|---|---|
| Group definition (stable, per form) | New catalog table `group_definitions` (`GroupDefinitionId` PK, `FormDefinitionId`, `GroupKey` unique per form, `GroupDisplayName`, lifecycle, audit) — CLAUDE.md §2.3. Insert-only for the runtime role. |
| Field membership | `field_definitions.group_definition_id` (nullable), **set at field creation and permanent** — a value is either top-level or per occurrence for the life of the field, so no version can reinterpret stored values (§2.2 "storage type is immutable"). |
| Occurrence | Staged signed row in `form_record_group_revisions` (`FormRecordGroupRevisionId` per physical row; **`FormRecordGroupId` = stable occurrence identity = Merkle item id**; `GroupDefinitionId`; `ParentFormRecordGroupId` NULL in V1; `GroupSortOrder`; operation/revision/signature envelope). Canonical entity kind `form_record_groups` — the same pattern as the record header (`form_record_revisions` table, canonical kind `form_records`). |
| Value inside an occurrence | Existing typed value rows with `FormRecordGroupId` = occurrence id (already signed). Uniqueness `(operation, record, field, group)` already exists. Multi-select and encrypted rows work unchanged. |
| Layout | Section property `repeat: { groupDefinitionId, minOccurrences, maxOccurrences, allowAdd, allowRemove, allowReorder }` (null-omitted, so older layouts serialize byte-for-byte as before). |

* **Stable identity:** an occurrence keeps its `FormRecordGroupId` across edits and reorders; reordering changes only the signed `GroupSortOrder`. Removing an occurrence removes its row and its value rows from the *next* accepted revision; earlier accepted revisions (and their history) are untouched.
* **Integrity:** moving a value to another occurrence, reordering, inserting or deleting an occurrence directly in PostgreSQL changes signed bytes or the leaf set → the authority's projection verification fails closed (quarantine), exactly as for value rows.
* **Limits:** at most 50 occurrences per group; the authority's 500-item candidate ceiling is checked before staging with a clear error. No nesting in V1 (no group inside a group). Document collections and related-form lists cannot be placed in a repeating section.
* **Distinction from child entities:** a repeating occurrence is data **inside one record** (one owner, one authorization scope, one content revision). `AccusedAddress`-style related forms remain independent child `FormRecords` with their own authority registration, ownership and shares; nothing in this phase changes them.

## 3. Write semantics (server-authoritative)

API (`/api/v1`): create and update take an optional `groups` list `{ groupKey, occurrences: [{ formRecordGroupId?, values }] }`. For a group named in an update, the list is the **complete new occurrence list in display order**: listed existing ids are kept (their omitted field keys unchanged), new entries have no id, omitted existing ids are removed. Responses carry each value's `formRecordGroupId` and the record's occurrence list.

Enforced on every user write (full scope): occurrence ids must belong to this record and group (no forged, duplicated or foreign ids); grouped fields only inside their occurrence; `allowAdd` / `allowRemove` / `allowReorder` against the current accepted state; min/max counts; per-occurrence validation, read-only, hidden-by-rule, clear-when-hidden and required / required-if (rules of a grouped field may read the same occurrence and top-level fields; a top-level rule cannot read a grouped field); optimistic concurrency by expected content revision as today. Partial-scope writers cannot change groups (they cannot write at all today — C-05 finding).

## 4. Authorization, sharing, encryption, audit

* **Authorization** is unchanged: the authority decides record read/edit; occurrences are content of the record.
* **Sharing:** the authority authorizes only value/selection leaves for partial reads, never `RepeatedGroup` leaves. Sharing a grouped field would release values without their occurrence structure and order. Rather than extend the authority, **V1 refuses field shares of fields in repeating sections** (`FIELD_IN_REPEATING_GROUP`) and the share UI does not offer them. Extending share scopes to occurrences is an Authority change and is deferred for review.
* **Encryption:** unchanged per value (AAD binds tenant/record/field/item; the occurrence id is a signed column). Encryption migrations carry occurrence rows forward unchanged (business change in `FieldProtectionMigrationService`; authority rules already permit it).
* **Audit:** record-updated events list changed field keys plus occurrences added/removed/reordered (ids and counts only, never values).

## 5. Dependent dropdowns

* Layout property on a dropdown element: `optionFilter: { parentFieldDefinitionId, parentOptionKeysByOption: { "<childKey>": ["<parentKey>", …] } }` — **option keys are the immutable stable identifiers** (labels never used). A child option without an entry is available for every parent value. Part of the immutable published version.
* **Validity:** a child selection is valid when its option has no entry, or one of the parent's selected keys is in its entry (multi-select parents and children supported).
* **Design-time validation (save and publish):** parent must be a placed dropdown in the same occurrence scope or top-level; mapping keys must exist in both catalogs; no self-dependency; **no cycles** (Country → Province → City → Country is rejected).
* **Parent change behavior:** the form clears child selections that become invalid (cascading down the chain) and says so; the **server never clears silently** — it refuses an invalid combination (`DEPENDENT_OPTION_INVALID`, 422), so a stored combination made invalid by a later version must be fixed before the record can be saved (like a deactivated option).

## 6. What would need review before it could be built

* Field-level sharing of repeating occurrences (needs the authority to authorize `RepeatedGroup` leaves per share).
* Nested repeating groups (parent occurrence links).
* Contributor (partial-scope) writes, which the authority currently refuses for every write because each candidate re-signs every leaf (C-05 finding).

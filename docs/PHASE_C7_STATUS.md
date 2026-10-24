# Phase C7 — Advanced Field Search: status

- **Branch:** `phase-c7/advanced-field-search` (from `main` 4c00ce7). Not merged; no PR.
- **State (2026-10-23):** architecture assessment complete. **Stopped at the security checkpoint for owner review** (decision C-55). No search code, Authority change, migration, API endpoint or UI has been implemented.
- **Architecture:** `docs/architecture/PHASE_C7_ADVANCED_FIELD_SEARCH.md`.
- **G06 and G19:** OPEN. Not changed by this phase so far.

## 1. Why the work stopped

ADR-PB-004 §4 requires the independent Authorization Authority to supply the complete eligible catalogue, with form, accepted content heads and authorization revision, at one coherent snapshot. Field search approval must not be weakenable through the business database. The current Authority lacks:

1. **Form-scoped trusted enumeration.** Discovery returns the caller's discoverable ids across the whole organization. The registry knows each record's form but does not filter or return it. A Form selection would therefore be untrusted business narrowing.
2. **Catalogue with content heads and a snapshot token.** Heads can only be read one record at a time. No coherent authorization + content snapshot can be checked cheaply between pages.
3. **Authority-held search approval.** `field_definitions.is_searchable` and `field_classification` are unsigned business rows. `is_searchable` is always `false` and no workflow can set it. Organization policy has no plaintext-search permission.

Item 1 is also the missing primitive behind G19: the migration post-check verifies only the records that the business database lists.

Proposed remedies: **AC-5** (form-scoped search catalogue + snapshot check) and **AC-6** (Authority-held field search policy and `ApproveFieldSearch` command). See architecture §4. Owner decisions **D1–D7** are listed in architecture §10.

## 2. What was verified during the assessment (code reading, no tests run)

* `PermissionCheckService.DiscoverAsync`: complete, gate re-read, 50,000 ceiling, revision-bound continuation (C-28).
* `VerifyRecordProjection`: complete leaf-set equality, HMAC, root, readable subset; quarantine on mismatch (AC-4).
* The signed record header binds `form_definition_id` and `record_status`. Plaintext and encrypted values are distinct signed row schemas.
* `ListRecordsHandler`: the Form/Status filters use unverified business metadata (labelled as such since C-29).
* `CreateFormDefinitionHandler` / `FormDesignerHandlers`: `IsSearchable = false` always.

## 3. Tests

None run in C7. No hosted runs. Existing suites are unchanged.

## 4. Next step

The owner reviews the architecture and decides D1–D7. With D7, M0 (safe UI layout + contracts behind a disabled server capability) can start while the Authority work is reviewed.

# Phase C9 — Form Version Management: architecture and M1 design

Status: **M1 implemented for Development/Testing; awaiting owner review.** Not independently reviewed; not a production
or Level 2 claim. Owner decisions D9-1…D9-9 are recorded in C-179; this document is the implementation baseline.

Scope of C9 (CLAUDE.md §1, §3, §5, §11 tests #1–#6): per-form version behaviour (Historical / Latest / Hybrid), the
Designer setting, publish impact reports, draft-versus-published comparison, version-aware record viewing and editing,
and preservation of stable field identities and values. **M1 (authorized, C-179)** = WP-0, WP-1, WP-2, WP-6.
**M2** (WP-3 semantic layout comparison, WP-4 publish impact report, WP-5 version comparison) and **M3** (WP-7
consolidated acceptance scenarios, WP-8 close-out) need separate authorization.

## 1. Baseline before C9 (verified on `main` 198aeae)

- `form_definitions.version_behavior` existed with the CHECK `Historical/Latest/Hybrid`, unsigned business metadata;
  every form was created `Latest`; no API or UI could change it; the Authority never reads it.
- One layout choice in `VerifiedRecordReader`: Historical → created version, otherwise current; the same for viewing and
  editing (Hybrid impossible), with a silent fallback to the created version when the chosen version was missing.
- `created_form_version_id` is part of the signed record header; `last_edited_form_version_id` is unsigned business
  metadata set from the reader's layout on update and restore.
- Values of fields outside the layout are carried forward on every save (`CandidateBuilder`); option validity uses the
  current option catalog and protection uses the field catalog, so an older layout cannot re-enable a deactivated option
  or weaken encryption.

## 2. Owner decisions (C-179)

| ID | Decision |
|---|---|
| D9-1 | The existing Authority **DesignForm** permission protects version-behaviour changes. Latest stays the default for new and existing forms. No Authority or cryptographic protocol change. G21 stays OPEN. |
| D9-2 | The selector lives in the Form Designer's **Form settings** card. New forms start Latest; the setting is changed afterwards (no choice in the create wizard). |
| D9-3 | New endpoints follow the Designer's **revision-in-body** convention (409 when stale). The difference from PB-06's `If-Match`/412/428 is documented here (§6), not treated as resolved platform-wide. |
| D9-4 | Impact reports give **approximate, record-level** counts restricted to records the caller may discover. No counts of field values. |
| D9-5 | When the edit layout changed after a record was opened, the stale save is refused with a **typed 409** and the user reloads. |
| D9-6 | Restore validates against the applicable **edit layout**: Historical → the record's created version; Latest and Hybrid → the current version. |
| D9-7 | Changing the behaviour requires a **reason of 3–500 characters** and an audit event committed atomically with the change. |
| D9-8 (amended) | Values of fields outside the selected layout are **preserved but not displayed** in a new "Other recorded values" section; they stay reachable through the existing authorized record-history view. No widening of value visibility, no bypass of field authorization. |
| D9-9 | **Deferred requirements** (recorded, not built): translations in comparisons (PG-036 not built); organization-disabled catalog values (§4G); a separate publish permission (§4B/PG-038). |

## 3. Contract reconciliation (K-1…K-10)

| # | Contract tension | Resolution |
|---|---|---|
| K-1 | §4B and PG-038 ask for an explicit publish permission; none exists (publish already uses DesignForm). | Accepted (D9-1); a separate permission is a deferred requirement (D9-9). |
| K-2 | PG-040 places the setting on the form-permissions page. | Form settings card (D9-2). |
| K-3 | PG-028 offers the choice at creation. | Latest at creation; change afterwards (D9-2). |
| K-4 | PB-06 `If-Match`/412/428 vs. the Designer's body revision/409. | Body revision/409 for C9 (D9-3); the platform-wide difference stays open (§6). |
| K-5 | §5 step 5 asks for counts of affected *values*; per-field value counts would reveal whether an unreadable field holds a value (§4C.2); PG-027 forbids confidential aggregates. | Record-level counts only (D9-4). |
| K-6 | Counts come from unverified business metadata. | Labelled approximate (`countsAreApproximate: true`, scope statement), §4G precedent (D9-4). |
| K-7 | C-53 says restore validates "against the current form" (written when every form was Latest). | Read as "the edit layout" (D9-6). |
| K-8 | G21: `form_versions`, `version_behavior` and `current_form_version_id` are unsigned. | No widening: the reader already trusted them; access, the released item set and encryption remain decided by the Authority and the field catalog independently of the layout. G21 stays OPEN; C9 claims nothing about it. |
| K-9 | `last_edited_form_version_id` is unsigned. | Kept as business metadata; signing it would change the signed record format (excluded by D9-1). |
| K-10 | §4H proposes a separate `authorized-edit` route. | `GET …/projection?purpose=edit` on the existing endpoint. |

## 4. M1 design

### 4.1 The rule (WP-1)

`Domain/Features/FormDefinitions/FormVersionBehavior.cs`: `VersionBehaviors` (constants, `IsKnown`), `RecordLayoutPurpose`
(`View`/`Edit`) and `FormVersionResolution.Resolve(behaviour, purpose, created, current)`:

| Behaviour | View | Edit |
|---|---|---|
| Historical | created version | created version |
| Latest | current version (else created) | current version |
| Hybrid | created version | current version |
| unknown value | current (else created) | **none** |

`Application/Features/FormRecords/Projection/VerifiedRecordReader.cs`:
- `RecordLayoutSelection.SelectAsync` loads the chosen versions. A version row counts only when it belongs to the record's
  form (a pointer to another form's version is ignored). **Editing fails closed**: no resolvable, existing, own edit version
  → `409 EDIT_LAYOUT_UNAVAILABLE`, nothing changed; another version is never substituted. Viewing keeps the earlier
  fallback to the created version.
- `ReadAsync(…, action, …)` picks the edit layout for `ProtectedAction.Edit` and the view layout otherwise; an overload
  takes an explicit purpose (the editor reads with `Read` access but needs the edit layout).
- `VerifiedRecord` carries `LayoutPurpose`, `EditLayoutVersion` (also resolved on view reads; null when unavailable) and
  `CreatedFormVersionNumber`.
- `EditLayoutGuard.Require(verified, expectedLayoutVersionId)` (D9-5): a write whose expected edit layout is no longer the
  record's edit layout is `409 EDIT_LAYOUT_CHANGED`. The expectation is optional on the wire (a client that omits it is
  validated against the current edit layout, as before); the web application always sends it.

Write paths (all through the edit layout): update (`UpdateFormRecordHandler`, owner and shared contributor), Submit
(`RecordLifecycleHandler`; Reopen/Archive/Unarchive validate no content and read the view layout, so they never fail
for a missing edit layout), restore preview and restore (`RecordRestoreHandler`, D9-6), upload begin and commit and
document removal (`UploadSessionHandlers`, `DocumentHandlers`), child-record creation and linking under a parent
(`CreateFormRecordHandler`, `ChildRecordHandlers`). `last_edited_form_version_id` is set from the edit layout on update
and restore (unchanged code, now the edit layout by construction). A child record follows its own form's behaviour.

### 4.2 The setting (WP-2)

| Route | Purpose |
|---|---|
| `PUT /api/v1/form-definitions/{id}/version-behavior` `{ versionBehavior, expectedFormDefinitionRevision, changeReason }` | Change (DesignForm, 404 otherwise; 422 unknown value or reason outside 3–500 characters or with control characters, checked before any access; 409 `FORM_DEFINITION_MODIFIED_CONCURRENTLY`; resending the current value changes nothing). One transaction: behaviour, `form_definition_revision`+1, `FORM_VERSION_BEHAVIOR_CHANGED` audit. |
| `GET /api/v1/form-definitions/{id}/version-behavior-impact?target=` | Impact preview (DesignForm, 404 otherwise; 422 unknown target). |

Handler: `Application/Features/FormDesigner/VersionManagement/FormDesignerHandlers.VersionBehavior.cs` (partial of
`FormDesignerHandlers`, same feature owner). `FormDefinitionSummaryResponse.VersionBehavior` is added (default `Latest`).
Audit catalog entry: previous/new behaviour (codes), form revision, current version id; the reason is withheld like the
publish reason.

Impact preview (D9-4): `AuthorizedRecordEnumerator` (Authority discover lookup, one authorization revision, fail closed
above 10,000) ∩ business `form_records` of the form. Returned: visible records, how many would be shown / edited with a
different layout, visible records per created version, authorized ids without business metadata (not counted), the form
revision, the authorization revision, `countsAreApproximate: true` and a scope statement. A Designer without record access
sees zero. No value row is read.

### 4.3 Version-aware record pages (WP-6)

- `GET /api/v1/form-records/{id}/projection?purpose=view|edit` (default view; 422 otherwise; same Read decision either
  way). The response adds `versionBehavior`, `layoutPurpose`, `createdFormVersionNumber`, `editLayoutVersionId`,
  `editLayoutVersionNumber`. `RestorePreviewResponse` adds `layoutVersionId`/`layoutVersionNumber` (the edit layout it
  validated against). Write requests add optional `expectedLayoutVersionId` (update, Submit, restore).
- Record page: badge "Layout vN (created with vM)"; for Hybrid when the edit layout differs, "Shown with layout vN; edits
  use layout vM"; when the owner could edit but no edit layout is available, a warning that the record can't be edited.
- Editor: reads `purpose=edit` (separate query key), sends `expectedLayoutVersionId`, explains the layout it edits with
  (Historical: the created version; Latest/Hybrid: the current version when it differs from the created one), and on
  `EDIT_LAYOUT_CHANGED` offers "Reload with the new layout and keep my changes" / "Discard my changes and reload"
  (not the "someone else saved" merge). Submit (record page) and restore send the expectation; a stale layout refreshes
  the record.
- Designer: "Record layout" row in Form settings with a Change dialog (three options in plain language, the impact
  summary, a reason) — `features/form-designer/components/VersionBehaviorDialog.tsx`.
- D9-8: no "Other recorded values" section; values outside the shown layout stay reachable through record history.

### 4.4 What M1 does not change

No business or Authority migration, no signed-row, Merkle, envelope or journal change, no new Authority command or
permission, no change to field identities, stored values, encryption or option validation, no Stage B workflow change.

## 5. Tests

- Contract (`tests/features/EnterpriseForms.Api.ContractTests/FormVersionBehaviorTests.cs`): the rule for every behaviour
  and purpose, missing/foreign/unknown versions fail closed for editing, the D9-5 guard, validation before access, 404
  without DesignForm, revision CAS, audit in the same commit, idempotent resend, deleted form, catalog entry, impact
  counts only over discoverable records of the form (hidden, other-form and undescribed records excluded).
- Stage B integration, focused (`tests/integration/EnterpriseForms.StageBTests/Tests/FormVersionBehaviorTests.cs`, group
  `forms-documents`): all three behaviours for view and edit; update, Submit and restore against the edit layout; stale
  layout 409s; values outside the layout kept; a nulled current pointer fails editing closed while viewing works; upload
  policy and commit from the edit layout; child creation and listing; a child form's own behaviour; the setting's
  authorization, validation, concurrency, audit, no data change and discoverable-only impact.
- Frontend unit (Vitest) for the dialog and notices; Playwright `form-version-behavior.spec.ts` (focused, isolated stack).

## 6. Open items and deferred requirements

- **PB-06 concurrency convention (K-4):** the Designer's form-metadata writes (rename, version behaviour, publish, draft)
  use a body revision and 409, not `If-Match`/412/428. Open platform-wide; not resolved by C9.
- **G21** stays OPEN (K-8). **Deferred (D9-9):** translations in comparisons; organization-disabled catalog values;
  a separate publish permission.
- `expectedLayoutVersionId` is optional for API compatibility; a client that omits it is validated against the current
  edit layout without the D9-5 refusal.
- M2/M3 (comparison, publish impact, version history comparison, consolidated §11 #1–#6 scenario, close-out) not started.

# Phase C9 — Form Version Management: architecture, M1, M2 and M3

Status: **M1 merged to `main` (`cd48e13`, PR #70); M2 merged (`491fa9a`, PR #71). M3 (WP-7 consolidated acceptance,
WP-8 close-out preparation, §8) implemented for Development/Testing on `phase-c9/m3-acceptance`; awaiting owner
review.** Not independently reviewed; not final C9 acceptance; not a production or Level 2 claim. Owner decisions
D9-1…D9-9 are recorded in C-179; this document is the implementation baseline.

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

Write paths (all through the edit layout; the D9-5 review is §4.5): update (`UpdateFormRecordHandler`, owner and shared contributor), Submit
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

### 4.5 D9-5 reconciliation (owner-directed review of PR #70, C-181)

**Write paths.** Every first-party record write was checked for whether it validates content against the record's edit
layout, and therefore whether it needs the expected edit layout:

| Path (server handler) | Validates against the edit layout? | First-party client sends `expectedLayoutVersionId` | Stale → 409 `EDIT_LAYOUT_CHANGED` |
|---|---|---|---|
| Update, owner and share editor (`UpdateFormRecordHandler`) | Yes: values, rules, required, read-only, repeating sections | Yes (`EditRecordPage`, from the edit projection) | Yes; guard before the owner/share branch |
| Submit (`RecordLifecycleHandler`) | Yes: submission validation | Yes (`LifecycleActions`, from the record page's `editLayoutVersionId`) | Yes |
| Reopen, Archive, Unarchive | No (no content validation; view layout) | Not applicable (null) | Not applicable; ignored |
| Restore (`RecordRestoreHandler`) | Yes (D9-6) | Yes (`RecordRestore`, from the preview's `layoutVersionId`) | Yes |
| **Upload begin**, new document and replace (`UploadSessionHandlers.BeginAsync`) | **Yes: the document collection's upload policy (size, types, count)** | **Added in C-181** (`DocumentCollection` → upload manager, from the record page's `editLayoutVersionId`) | **Yes (added in C-181)** |
| Upload chunks, complete, commit; document removal | No (the edit read only proves editability and access) | Not applicable | Not applicable |
| Child record creation under a parent; unlink (`CreateFormRecordHandler`, `ChildRecordHandlers`) | No for the parent (editability and relationship capacity only); the child's values are validated as a new record of the child form | Not applicable | Not applicable |
| New top-level record (`CreateFormRecordHandler`) | Not an opened record: validated against the form's current version | Not applicable (outside D9-5) | Not applicable (see observation below) |

**Guarantee.** `EditLayoutGuard.Require` compares the supplied id with the edit layout resolved in the same verified read
whose layout then validates and stages the write; a different id is refused before any value or policy validation and before
staging, so a supplied stale id can never be validated against a newer layout. An unavailable edit layout fails first with
`EDIT_LAYOUT_UNAVAILABLE`.

**Compatibility exception.** `expectedLayoutVersionId` is optional on update, Submit, restore and upload begin. A legacy
API client that omits it is validated against the record's current edit layout and receives **no guaranteed stale-layout
detection**: a version published (or a behaviour change made) after that client loaded the record is applied without a
409. The first-party web application always sends it. The other protections are unchanged for such clients (content
revision CAS, the current edit layout's validation, authorization, encryption).

**Reload with the new layout and keep my changes.** Values are matched by the field's permanent key (storage type is
immutable and keys are never reused), so a value is never applied to a different field. Before C-181 a change to a field
that the reloaded layout no longer let the person change (removed from the layout, made read-only, hidden from the form,
or no longer shared for editing), or to a repeating section no longer placed, was silently left out of the save and still
counted as "carried onto the latest version". Since C-181 such changes are **withheld**: the form shows the latest saved
value, nothing withheld is sent, and the person sees "Some of your changes can't be carried over" with each field's label
and the value they had entered. Changes the new layout accepts are carried over and validated against it (an option no
longer available or a tightened rule is reported as a validation error, never accepted silently).

**Observation (not changed).** Creating a record (top-level or child) validates against the form's current version at
the time of the request; a version published while the new-record page is open is applied without a 409. This is outside
D9-5 (no opened record) and unchanged from before C9.

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
- **Compatibility exception (§4.5):** `expectedLayoutVersionId` is optional on update, Submit, restore and upload begin;
  a legacy API client that omits it is validated against the current edit layout and receives no guaranteed stale-layout
  detection. The web application always sends it.
- M2 implemented (§7, C-182) and merged; M3 implemented (§8, C-187), awaiting owner review.
- **Search of values of fields no longer in the current version (§8.3):** not supported by the existing verified field
  search. CLAUDE.md §11 #2's searchability requirement is **UNMET — deferred** pending a separate owner-approved security
  design (C-188); authorized record history is a different capability and does not satisfy it. Not changed by C9.

## 7. M2 design (WP-3, WP-4, WP-5; authorized by the owner, C-182)

### 7.1 Semantic layout comparison (WP-3)

`Application/Features/FormDefinitions/Layout/FormLayoutComparison.cs` — pure, deterministic, no store and no record data.

- **Identity, never labels or positions:** fields and document collections by `FieldDefinitionId`, related lists by
  `FormRelationshipDefinitionId`, sections by `SectionKey`, options by `FieldOptionId`.
- **Change types:** `Added`, `Removed`, `Modified`, `Moved`. **Significance:** `Structure` (added or removed),
  `Behaviour` (validation, rules, read-only, shown in form, options, defaults, limits, protection), `Presentation`
  (label, help, placeholder, control, width/position, order, section and option wording). A move is a move, never a
  removal plus an addition; order is compared only among elements that stay in the same section, so an insertion or a
  move elsewhere does not mark neighbours as moved.
- **Aspects** are stable codes (`Label`, `Required`, `MaxLength`, `OptionDeactivated`, `RepeatLimits`, …) with before and
  after text and a `Tightened` flag for a rule or limit that can refuse input it accepted before (required turned on,
  stricter length/value/date limits, a pattern, deactivated or removed options, tighter upload or entry limits).
- **Order:** form title, then sections and elements in the order of the later layout, then removed items in the order
  of the earlier one. The first version compares against nothing (everything is added).
- **Not compared:** translations (none exist; D9-9), organization-disabled catalog values (D9-9), record data.

### 7.2 Publish impact (WP-4)

`GET /api/v1/form-definitions/{id}/publish-impact?expectedDraftRevision=` (`FormDesignerHandlers.PreviewPublishImpactAsync`):
DesignForm (404 otherwise), 409 `NO_DRAFT` / `DRAFT_REVISION_CONFLICT`, read-only (nothing written or audited). A `GET`
instead of the `POST` sketched in CLAUDE.md §6, because it is a read (documented deviation).

- **Comparison:** the current version against the draft **exactly as publishing would store it** — the label, protection
  and option snapshot is taken by the same `SnapshotForPublishAsync` that `PublishAsync` now uses.
- **`CanPublish`/blocked reason:** `NO_DRAFT_CHANGES` when the snapshot equals the current version, or the layout
  validation error (`LAYOUT_INVALID`) — the same rules publishing enforces.
- **Notices (potential impact on future edits, never data loss):** Attention — `NEW_REQUIRED_FIELDS`,
  `VALIDATION_TIGHTENED`, `OPTIONS_NO_LONGER_AVAILABLE`, `LIMITS_TIGHTENED`; Information — `ELEMENTS_REMOVED`,
  `READ_ONLY_OR_HIDDEN`, `PROTECTION_CHANGED`, `PRESENTATION_ONLY`, `NO_CHANGES`.
- **Records (D9-4):** approximate counts over the caller's discoverable records only — visible records of the form, how
  many would be shown and edited with the new version under the form's version behaviour (Latest: all; Hybrid: edited
  only; Historical: none), authorized records without listing details. No field value is read or counted.
- **Data statement:** publishing never deletes or changes stored values; values of fields no longer in the layout are
  kept; existing records are checked against the new version only when next edited with it.
- **Publish:** unchanged authorization, revision checks and transaction; `FORM_VERSION_PUBLISHED` audit metadata gains
  `addedElements`, `removedElements`, `behaviourChanges`, `presentationChanges` (counts only, catalog-allowlisted; never
  labels or values).
- **UI:** the Publish dialog (`PublishReview.tsx`, large dialog) shows the data statement, the attention and information
  notices, the approximate record counts and the comparison before the change reason; Publish is disabled when the server
  says the draft cannot be published.

### 7.3 Version comparison (WP-5)

`GET /api/v1/form-definitions/{id}/versions/compare?from=&to=` (`CompareVersionsAsync`): each side is a version id or
`draft`; DesignForm (404 otherwise); a version of another form 404 `VERSION_NOT_FOUND`; no draft 404 `NO_DRAFT`; a
malformed reference 422. The draft side is the publish snapshot (as in WP-4).

UI: the Versions tab gains a "Compare versions" card (`VersionComparePanel.tsx`) with "Compare from" and "to" selects
(published versions and the draft), Swap, and the shared `LayoutComparisonView.tsx`: a summary sentence and groups —
Added, Removed from the layout (stored values kept), Changes to how the form behaves, and Presentation-only changes
collapsed under a disclosure so they are never presented as destructive. Text carries the meaning (never colour alone).
"Restored from" in the versions table names the source version.

### 7.4 What M2 does not change

No migration; no Authority, encryption, signing, Merkle or journal change; Historical, Latest and Hybrid unchanged; no
record values in any comparison or impact; the separate publish permission, translations in comparisons and
organization-disabled catalog values remain deferred (D9-9).

### 7.5 Corrective review of PR #71 (owner direction, C-183)

- **Publish gate.** Publish is enabled only when a review for exactly the draft revision about to be published has
  loaded, is not being refreshed, did not fail, and says the draft can be published (`publishGate` in
  `PublishReview.tsx`); the dialog says why it is not available yet. The server still checks the draft revision, the
  current version and the layout on publish; the gate never replaces those checks.
- **Proven tightening only.** `Tightened` marks a rule or limit that is proven to refuse input it accepted before. A
  conditional requirement is a tightening only when it is added to a field that is not otherwise required, before or
  after; a replaced condition is a behaviour change of unknown direction; a removed one, or one that replaces an
  unconditional requirement, relaxes. Likewise only an added pattern is a tightening (a replaced pattern is not proven).
- **Completeness against the layout contract.** Every persisted element and section property is compared. Added in
  this review: a section that places a different repeating group (`RepeatGroup`, notice `REPEATING_SECTIONS_CHANGED`);
  protection recorded in one version and not the other (versions published before protection snapshots); an upload
  policy present in one version and not the other, and an element-kind change for the same field — the last two cannot
  occur under the current contract (a document collection always parses a policy and a field id is placed as one kind)
  but are reported defensively.
- **Compare selections.** The selection follows new versions while the person has not chosen; a person's choice is kept
  while both sides exist; a side that no longer exists (for example a removed draft) resets the comparison to the
  defaults with a visible notice, so something that is gone is never compared. A draft comparison is re-read whenever the
  draft revision changes (the revision is part of the query key and of the option label).

## 8. M3 — consolidated acceptance and close-out preparation (WP-7, WP-8; authorized by the owner, C-187)

M3 adds **tests and documentation only**: no production code, API contract, migration, Authority, cryptographic,
signing, Merkle or journal change. It demonstrates CLAUDE.md §11 #1–#6 end to end with the M1 and M2 implementation as
merged, and records what the existing implementation does and does not support.

### 8.1 Scenario design

| Scenario | Stage B (real stack) | Contract (handlers, in-memory store) | Browser (Playwright) |
|---|---|---|---|
| 1. V1 (first name, last name, phone); records with phone | `FormVersionAcceptanceTests.TheV1ToV4Lifecycle…` | `FormVersionLifecycleContractTests` (lifecycle) | `form-version-acceptance.spec.ts` test 1 |
| 2. V2: phone removed, email added; publish review; comparison | same fact | same class (comparisons, impact) | test 2 |
| 3. V1 restored as a draft, published as V3 | same fact | same class (restore lineage, snapshots) | test 3 (Versions tab "Restore to draft") |
| 4. The original email field re-added in V4 | same fact | same class (identity, no duplicate) | test 3 (existing-field picker; v3 → v4 comparison) |
| 5. Historical / Latest / Hybrid on three forms; stale layouts; fail-closed edit layouts | `…HistoricalLatestAndHybridFollowTheirLayoutsOnEquivalentHistories` | `…EachBehaviourSelectsItsLayoutsOverTheFourVersionHistory` | tests 4–5 (Hybrid; stale-layout reload with a withheld change) |
| 6. Changing the behaviour after records exist | `…ChangingTheBehaviourAfterRecordsExistIsDesignOnlyValidatedAuditedAndMigratesNothing` | (M1 `FormVersionBehaviorTests`) | test 4 (impact preview, reason) |

Each Stage B fact uses its own synthetic organization (plus a second organization for cross-tenant denials). Values
are read only through verified projections and authorized history; created and last-edited versions are read from the
business database with the read-only test identity (as the M1 tests do). Fail-closed edit layouts are provoked by
pointing `current_form_version_id` at another form's version or at nothing (the M1 technique), restored afterwards.

### 8.2 What the scenarios pin down (beyond M1/M2 coverage)

- A V3 restored from V1 has **exactly** V1's snapshot (byte-identical layout JSON in the contract test; no semantic
  differences on the real stack), `restoredFromFormVersionId` = V1, and leaves V1 and V2 unchanged.
- V4 re-uses the original email `FieldDefinitionId`; a second field with key `email_address` is refused
  (`409 FIELD_KEY_EXISTS`); the form keeps exactly four field definitions; values stay with their own records.
- Every published version is registered with the Authority with exactly the fields it places.
- The three behaviours on equivalent histories, including restore against the edit layout (D9-6), reader share
  projections that never include fields outside their scope whatever the layout, and refused writes that change
  neither the content revision, the values nor `last_edited_form_version_id`.
- Stale-layout refusals (`409 EDIT_LAYOUT_CHANGED`) for update, Submit, restore and upload begin after a publish, and
  for update, Submit and upload begin after a behaviour change; the compatibility exception (omitted
  `expectedLayoutVersionId`) is exercised and unchanged.

### 8.3 Unmet requirement (deferred, C-188): search of values of fields removed from the layout

CLAUDE.md §11 #2 asks that old phone values remain "searchable with authorization" after V2. **The existing verified
field search does not support this.** Search approvals are bound to the Authority's registration of the latest
published version (C7 M2, OD-3): registering a version that does not place an approved field invalidates that approval
(`FORM_VERSION_CHANGED`), and the search then refuses the field (`422 SEARCH_FIELD_NOT_ALLOWED`, no partial result).
Restoring the field (V3) does not reinstate the approval; a security administrator approves it again, after which the
original values are found on the original records, and an unchanged field's approval carries over to the next version
(V4). The values themselves are kept and remain readable through verified projections and authorized record history
throughout; **that is preservation and authorized access, not searchability**, and does not satisfy §11 #2.

**Owner decision (C-188):** the fail-closed search-approval behaviour is accepted as it is. The §11 #2 requirement that
historical values remain searchable with authorization while the field is absent from the current published version is
recorded as **UNMET — deferred pending a separate owner-approved security design**; it is neither implemented nor
passed. Supporting approved searches of off-layout historical fields requires a separate design (for example, how an
approval binds to a field across versions, what the Authority registers for fields outside the current layout, and how
completeness and revocation are proven) and its own security authorization. M3 tests the existing behaviour as it is,
changes no test expectation and adds no search capability.

### 8.4 Not changed by M3

D9-1…D9-9 including the compatibility exception (§4.5) and the D9-9 deferrals; G21; the PB-06 concurrency convention;
the observation that record creation validates against the current version without a 409 (§4.5).

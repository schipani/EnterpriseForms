# Phase C9 — Form Version Management: status

- **Branches:** M1 `phase-c9/form-version-management` (from `198aeae`), squash-merged to `main` as `cd48e13` (PR #70; branch kept, tag `archive/c9-m1-form-version-management-9395ec5`). M2 `phase-c9/m2-version-comparison` (from `main` `cd48e13974d3539bfa18e65af6d94cbf06f91364`), not merged.
- **Architecture:** `docs/architecture/PHASE_C9_FORM_VERSION_MANAGEMENT.md`.
- **Decisions:** C-178 (PR #69 merge record, C8); C-179 (owner decisions D9-1…D9-9; WP-0 and M1 authorized); C-180 (M1 implemented; awaiting owner review); C-181 (D9-5 reconciliation and correction, §4); C-182 (M2 implemented; awaiting owner review, §5).
- **State (2026-11-23):** M1 merged to `main` (code integration only). **M2 (WP-3, WP-4, WP-5) implemented for Development/Testing; stopped for owner review.** Not owner-accepted, not independently reviewed, no production or Level 2 claim. M3 not started. G21 OPEN.

## 1. What M1 delivers

| Work package | Delivered |
|---|---|
| WP-0 | Decisions C-178/C-179, architecture document, this status, feature-ownership entry, C8 status §46 (PR #69 merged as `198aeae` despite the obsolete commit-subject wording). |
| WP-1 | One selection rule for every record read and write (`FormVersionResolution`, `RecordLayoutSelection`): Historical views and edits with the created version, Latest with the current version, Hybrid views with the created version and edits with the current one. Editing fails closed (`409 EDIT_LAYOUT_UNAVAILABLE`) when the edit version is missing, belongs to another form or the stored behaviour is unknown; another version is never substituted. Update, Submit, restore (preview and apply), upload begin/commit, document removal and child creation validate against the edit layout; Reopen/Archive/Unarchive use the view layout (they validate no content). Stale edit layouts are refused (`409 EDIT_LAYOUT_CHANGED`, D9-5). |
| WP-2 | `PUT /api/v1/form-definitions/{id}/version-behavior` (DesignForm, revision CAS, 3–500 character reason, audited `FORM_VERSION_BEHAVIOR_CHANGED` in the same transaction, idempotent resend) and `GET …/version-behavior-impact?target=` (approximate record-level counts over the caller's discoverable records only). Designer Form settings card: "Record layout" row and dialog. |
| WP-6 | `GET …/projection?purpose=view|edit`; the record page badge "Layout vN (created with vM)" and the Hybrid notice; the editor reads the edit layout, explains it, sends the expected layout, and offers a reload that keeps the user's changes when the layout changed. Submit and restore send the expected layout. No "Other recorded values" section (D9-8). |

No business or Authority migration; no signed-row, Merkle, envelope, journal or Authority change; field identities, stored values, authorization and encryption unchanged.

## 2. Verification (local, 2026-11-22)

| Check | Result |
|---|---|
| .NET Release build of `EnterpriseForms.All.slnx` | Pass: 0 warnings, 0 errors. Stage A and Stage B test projects compile |
| OpenAPI | Regenerated from the compiled endpoints and committed (additive: two routes, optional fields) |
| Test-result accounting self-tests | Pass |
| `run_dotnet_tests.py` (full CI set) | 3,259 executed, 3,259 passed, 0 failed, 223 authorized skips (Authority 1,893, contract 1,043, architecture 227, integrity 73, platform console 23) |
| Contract tests `FormVersionBehaviorTests` | 39 pass (rule matrix, selection, fail-closed, D9-5 guard, setting, impact) |
| `AuditExplorerTests.EveryAuditActionHasExactlyOneCatalogEntry` | Count pin updated from 80 to 81 for `FORM_VERSION_BEHAVIOR_CHANGED` (it first failed on the pin, as designed) |
| Stage B focused class `FormVersionBehaviorTests` (`scripts/dev/test.ps1 -Level 2 -Class`, C-131 §1) | 3/3 on a freshly built isolated security-test stack |
| Directly affected Stage B classes, focused, same stack | RecordLifecycleTests 12/12, RecordRestoreTests 11/11, ParentChildTests 2/2, DocumentTests 3/3, ContributorWriteTests 7/7, RecordWorkspaceTests 5/5, DesignerTests 3/3, FormRenameTests 1/1 |
| Frontend (existing node_modules; `npm ci` not re-run) | Lint, typecheck and build pass; Vitest 66 files, 549 tests (13 new); no API-client drift (web and platform console) |
| Playwright `form-version-behavior.spec.ts` (`-Level 2 -Spec`, isolation guard verified, port 5173 free) | 3/3 including axe scans of the dialog, the record page and the editor's layout-changed state. Two earlier attempts failed on test defects, corrected without weakening an assertion: an ambiguous locator (the option label "Current version" duplicated an existing settings row, so the label was renamed "Current published version") and a URL check (the record page drops `?saved=true`; the spec now waits for "Changes saved") |
| Compose profiles (dev, integration, security-test) | Validate |
| Integrity vectors (Python) | 46 checks pass |
| `validate-specifications.py --strict` | Pass |

Not run: hosted Stage B (release-only, C-131), any manual workflow dispatch, deployment.

## 3. Deviations from the preparation plan and open items

- **Submit vs. other transitions:** only Submit reads the edit layout; Reopen, Archive and Unarchive read the view layout because they validate no content (so a missing edit layout does not block archiving).
- **Compatibility exception: `expectedLayoutVersionId` is optional** on update, Submit, restore and upload begin. A legacy API client that omits it is validated against the current edit layout and receives no guaranteed stale-layout detection; the web application always sends it (§4).
- **Related lists on the record page** come from the view layout: under Hybrid, a related list added in a later version appears in the editor's layout but not on the record page of an older record (the child records are kept and remain reachable).
- The option label for Latest is "Current published version" (the settings card already has a "Current version" row).
- **PB-06 concurrency convention** (body revision/409 vs `If-Match`/412/428) remains open platform-wide (D9-3).
- **Deferred (D9-9):** translations in comparisons; organization-disabled catalog values; a separate publish permission.
- **Outstanding for M1:** owner review; manual screen-reader review of the new dialog and notices (PB-14); hosted Stage B only under separate release authorization.
- **Not started:** M2 (WP-3 comparison, WP-4 publish impact, WP-5 version comparison) and M3 (WP-7, WP-8).

## 4. D9-5 reconciliation (owner-directed review of PR #70, C-181)

Details: `docs/architecture/PHASE_C9_FORM_VERSION_MANAGEMENT.md` §4.5.

| Item | Finding | Action |
|---|---|---|
| 1. First-party write paths supply `expectedLayoutVersionId` where applicable | Update, Submit and restore already did. **Upload begin did not**, although it validates the file against the edit layout's upload policy. Upload chunks/complete/commit, document removal, child creation and unlink do not validate against the record's edit layout, so D9-5 does not apply. Reopen/Archive/Unarchive validate no content. | **Corrected:** `BeginUploadRequest.ExpectedLayoutVersionId` (optional), guarded in `UploadSessionHandlers.BeginAsync`; the record page passes `editLayoutVersionId` through `DocumentCollection` and the upload manager; an upload refused for a changed layout releases the file (no retry with the stale id) and re-reads the record. |
| 2. A supplied stale id always conflicts | `EditLayoutGuard.Require` compares with the edit layout resolved in the same verified read whose layout validates and stages the write, before any value or policy validation; covered: owner update, share-editor update, Submit, restore, upload begin. | No code change beyond item 1. Tests added for the paths not yet demonstrated (below). |
| 3. Compatibility exception | Documented in the architecture §4.5 and §6, in the request contracts' XML documentation and in the upload-begin endpoint summary. | Documentation only. |
| 4. Tests | Not yet demonstrated: stale after a *publish* (only after a behaviour change), the share-editor path, upload begin. | Stage B `AStaleEditLayoutIsRefusedAfterANewerVersionIsPublishedForOwnersAndSharedEditors` (owner update, share-editor update, Submit after a publish; the share editor's reload then saves); the upload stale/current checks in `DocumentsUploadsAndChildRecordsUseTheirFormsEditLayout`; upload-manager unit tests (expected id sent; no retry after `EDIT_LAYOUT_CHANGED`). |
| 5. "Reload with the new layout and keep my changes" | Values match by permanent field key, so nothing is applied to another field. **Defect:** a change to a field the new layout no longer lets the person change (removed, read-only, hidden from the form, unshared) or to a repeating section no longer placed was silently left out of the save and counted as carried over. | **Corrected:** such changes are withheld and listed ("Some of your changes can't be carried over", label and entered value); the form shows the latest saved value; nothing withheld is sent. Unit tests (`layoutReload.test.ts`) and a Playwright scenario (a field removed while editing). |

Observation, unchanged and outside D9-5: creating a record (top-level or child) validates against the form's current version at request time; a version published while the new-record page is open is applied without a 409.

Verification of the C-181 correction (local, 2026-11-22):

| Check | Result |
|---|---|
| .NET Release build of `EnterpriseForms.All.slnx` and the Stage A/Stage B test projects | Pass: 0 warnings, 0 errors |
| `run_dotnet_tests.py` (full CI set) | 3,259 executed, 3,259 passed, 0 failed, 223 authorized skips |
| Stage B focused `FormVersionBehaviorTests` (fresh isolated stack, C-131 §1) | 4/4 (the new stale-layout test and the extended uploads test included) |
| Stage B focused `DocumentTests` (upload begin changed) | 3/3 |
| Playwright `form-version-behavior.spec.ts` (isolation guard verified, port 5173 free) | 4/4, including the new removed-field reload scenario with an axe scan |
| Frontend | Lint, typecheck and build pass; Vitest 67 files, 556 tests (7 new: 5 reload-merge, 2 upload-manager); no API-client drift |
| OpenAPI | Regenerated (optional `expectedLayoutVersionId` on upload begin; endpoint summary) |

## 5. M2 — semantic comparison, publish impact, version comparison (C-182)

Design: architecture §7.

| Work package | Delivered |
|---|---|
| WP-3 | `FormLayoutComparison`: matching by stable id (fields, document collections, related lists, sections, options), change types Added/Removed/Modified/Moved, significance Structure/Behaviour/Presentation, stable aspect codes with before/after and a `Tightened` flag, deterministic order; moves and reorders are presentation changes, never removals. |
| WP-4 | `GET …/publish-impact?expectedDraftRevision=` (read-only, DesignForm): the current version against the draft exactly as publishing would store it (shared snapshot helper), blocked reason (`NO_DRAFT_CHANGES`, layout errors), potential-impact notices, approximate record counts over discoverable records only (D9-4), and a data statement that publishing never deletes or changes stored values. Publish dialog shows the review before the change reason. `FORM_VERSION_PUBLISHED` audit gains counts of added/removed/behaviour/presentation changes. |
| WP-5 | `GET …/versions/compare?from=&to=` (version ids or `draft`; own versions only). Versions tab "Compare versions" card with From/to selects, Swap, and the shared comparison view (presentation-only changes collapsed); "Restored from" names the source version. |

Verification (local, 2026-11-23):

| Check | Result |
|---|---|
| .NET Release build of `EnterpriseForms.All.slnx` and the Stage A/Stage B test projects | Pass: 0 warnings, 0 errors |
| `run_dotnet_tests.py` (full CI set) | 3,275 executed, 3,275 passed, 0 failed, 223 authorized skips (contract tests 1,059, 16 new in `FormVersionComparisonTests`) |
| Test-result accounting self-tests; Stage B grouping | Pass (`FormVersionComparisonTests` added to `forms-documents`) |
| Stage B focused (fresh isolated stack, C-131 §1) | `FormVersionComparisonTests` 1/1; `DesignerTests` 3/3 (publish refactored); `FormVersionBehaviorTests` 4/4 |
| Playwright (isolation guard verified, port 5173 free) | `form-version-comparison.spec.ts` 3/3 with axe scans of the publish review dialog and the comparison; regression `form-version-behavior` 4/4, `designer-ux` 5/5, `form-management` 3/3, `repeating-groups` 2/2 |
| Frontend | Lint, typecheck and build pass; Vitest 68 files, 564 tests (8 new); no API-client drift |
| Integrity vectors | Python and Node, 46 checks each, pass |
| OpenAPI | Regenerated (two read endpoints; additive) |
| `validate-specifications.py --strict` | Pass |

Not run: hosted Stage B (release-only, C-131), manual workflow dispatches, deployment.

Deviations and limitations:
- The publish impact is a `GET` (a read), not the `POST` sketched in CLAUDE.md §6.
- Comparisons word the layouts' own labels; translations, organization-disabled catalog values and a separate publish permission remain deferred (D9-9).
- Rules (visibility, conditional requirement, dependent-choice filters) are reported as added, removed or changed, not described condition by condition.
- The draft side of a comparison is the publish snapshot taken now; catalog changes since (labels, option availability, protection) therefore appear as changes, which is what publishing would record.
- Manual screen-reader review of the review dialog and comparison view is outstanding (PB-14); automated axe scans pass.

# Phase C9 — Form Version Management: status

- **Branch:** `phase-c9/form-version-management` (from `main` `198aeaef1912fb5dc5b79db96c89d456998ad150`). Not merged.
- **Architecture:** `docs/architecture/PHASE_C9_FORM_VERSION_MANAGEMENT.md`.
- **Decisions:** C-178 (PR #69 merge record, C8); C-179 (owner decisions D9-1…D9-9; WP-0 and M1 authorized); C-180 (M1 implemented; awaiting owner review).
- **State (2026-11-22):** WP-0 and **M1 (WP-1, WP-2, WP-6) implemented for Development/Testing; stopped for owner review.** Not owner-accepted, not independently reviewed, no production or Level 2 claim. M2 and M3 not started. G21 OPEN.

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
- **`expectedLayoutVersionId` is optional** on update, Submit and restore for API compatibility; the web application always sends it. A client that omits it is validated against the current edit layout without the D9-5 refusal.
- **Related lists on the record page** come from the view layout: under Hybrid, a related list added in a later version appears in the editor's layout but not on the record page of an older record (the child records are kept and remain reachable).
- The option label for Latest is "Current published version" (the settings card already has a "Current version" row).
- **PB-06 concurrency convention** (body revision/409 vs `If-Match`/412/428) remains open platform-wide (D9-3).
- **Deferred (D9-9):** translations in comparisons; organization-disabled catalog values; a separate publish permission.
- **Outstanding for M1:** owner review; manual screen-reader review of the new dialog and notices (PB-14); hosted Stage B only under separate release authorization.
- **Not started:** M2 (WP-3 comparison, WP-4 publish impact, WP-5 version comparison) and M3 (WP-7, WP-8).

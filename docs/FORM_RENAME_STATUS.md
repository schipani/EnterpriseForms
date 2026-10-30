# Form rename — status (C-92)

Branch `feature/form-rename`, based on the owner-accepted baseline `ui/dashboard-security-monitoring` (`c042215`, C-91).
Not merged; awaiting owner acceptance. Development feature, not production Level 2 evidence.

## 1. Behaviour

| Item | Implementation |
|---|---|
| Where | Forms list: row "…" menu → **Rename** (only for forms the user may design). Form Designer: **Edit name** beside the form heading, and in Settings → Form name. |
| Dialog | Shows the current name; "New name" pre-filled; Save disabled until the name changes; client validation mirrors the server. |
| Rule | Trimmed, 1–200 characters, no control characters (`FormDefinitionNameRules`, now shared by both create routes and rename). Names need not be unique (unchanged; CLAUDE.md §2.1). |
| API | `PATCH /api/v1/form-definitions/{formDefinitionId}` `{ formDefinitionName, expectedFormDefinitionRevision }` → `FormDefinitionDetailResponse`. Feature owner `FormDesigner`. |
| Authorization | Authority `DesignForm` on that form, checked before any store read; otherwise the generic 404 (same as every designer write). Another organization's form is 404. |
| Concurrency | New column `form_definitions.form_definition_revision` (bigint, default 1; migration `20261030223036_FormDefinitionRename`, additive). A stale revision → 409 `FORM_DEFINITION_MODIFIED_CONCURRENTLY`; the dialog shows "Someone else renamed this form" and reloads. Re-sending a rename that already took effect is a no-op success. |
| Audit | `FORM_RENAMED` in the same transaction: previous and new name, new revision, current version id. Refused attempts write nothing. |
| Notification | Success message ("Form renamed …") on the Forms page or in the designer; cached form lists, records, dashboard, search and related-form views are refreshed. |

## 2. What a rename does not change

- The form id, the Authority registration (`RegisterFormDefinition` carries no name) and form permissions (`form_user` edges).
- Published versions: immutable layout snapshots, including the layout title they were published with. **No new version
  is created**: every page reads the catalog name, not the layout title, so versioning is not required by the current
  architecture.
- The draft (revision and layout) and any unsaved designer edits in the browser.
- Records, values, documents, shares and history.

## 3. Tests (executed 2026-10-30)

| Suite | Result |
|---|---|
| .NET contract (`EnterpriseForms.Api.ContractTests`, incl. 12 new `FormRenameTests`, OpenAPI snapshot, endpoint ownership) | 461 / 461 |
| Architecture (`EnterpriseForms.ArchitectureTests`) | 71 / 71 |
| Stage B real stack (isolated sectest stack rebuilt from this branch): new `FormRenameTests` + `FieldEditTests`, `DesignerTests`, `FormAccessTests` | 10 / 10 |
| Vitest (incl. 5 new `RenameFormDialog.test.tsx`) | 262 / 262 |
| ESLint, TypeScript | clean |
| Playwright (isolated, port 5199 → API 5180): new `form-rename.spec.ts` (3, incl. axe on the dialog) + `form-management.spec.ts` (3) | 6 / 6 |

The Stage B test proves on the real stack: clerk (record creator without design right) and another organization's
designer are refused; empty, 201-character and line-break names → 422; stale revision → 409 with no change; the
published form keeps its id, current version, every published snapshot (byte-equal after JSON normalization), its form
permissions and the clerk's record values, while the projection, record list and catalog show the new name; a
draft-only form is renamed with no version created and its draft untouched; exactly one `FORM_RENAMED` per effective
rename with both names, none for refused attempts.

Not run: complete Stage B and SEC-P suites; hosted CI (runs on the pull request).

## 4. Observation (pre-existing, not caused by this change)

FR-OBS-1: signing out of the Form Designer while it has unsaved changes leaves a blank page (the designer's navigation
guard blocks the post-sign-out navigation). Reproduced without any rename. Not fixed here; proposed as a small follow-up.

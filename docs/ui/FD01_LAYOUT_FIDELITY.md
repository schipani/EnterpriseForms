# FD-01 — Form Designer layout fidelity

**Status:** implemented on `feature/form-designer-layout-fidelity` (base `5bc0702`), Development/Testing. Owner-approved design (2026-11-12, owner-led; not an independent review). Not committed, merged or deployed. Awaiting owner visual acceptance.

## 1. Defect and root cause

A field placed in an empty cell of a four-column row (for example row 2, column 3) was displayed there in the designer but rendered elsewhere after saving: in Preview, in the published form and in the designer itself after reopening — usually beside fields of an earlier row.

Root cause (not CSS; the renderer always used explicit `grid-column` / `grid-row` values):

1. **The layout contract could not express a column.** Placement was only `{ columnSpan, startOnNewRow }` (server `LayoutPlacement` in `FormLayout.cs`; client `placement` schema in `packages/form-renderer/src/layout.ts`). The deterministic packer (`packElements`) places each element at the next free position, so an empty cell *before* a field had no representation.
2. **The designer drew positions it never saved.** The empty "+ Add Field" cells of "+ Add Row" / "+ Add Column" were designer-only placeholders that the canvas packed *together with* real fields, so a field placed in the third placeholder looked like column 3. When it was filled (`SlotOptions.place` in `DesignerInspector.tsx`, `addFieldMutation` in `DesignerPage.tsx`) the element got only `{ columnSpan: 1, startOnNewRow: false }` (only the first of the four placeholders started a row) and was appended after the previous fields; the packer therefore put it straight after them. The placeholders vanished on reload, so the designer compacted too.
3. **The server would have discarded a column anyway.** `FormLayoutCodec.ParsePlacement` read only the two properties and `Serialize` rewrites every saved draft, published version and restored draft.

Preview and the published form already used the same renderer and agreed with each other; only the designer differed.

## 2. Layout contract (schema v2, additive)

```json
"layout": { "columnSpan": 1, "startOnNewRow": false, "columnStart": 3 }
```

* `columnStart` is optional, an integer 1–4, and `columnStart + columnSpan − 1 ≤ 4`. Anything else is rejected with `LAYOUT_INVALID` (server) and by the client schema.
* The layout schema version stays **2**. Like earlier additions (`description`, `repeat`, `showInForm`, `optionFilter`), the property is omitted when absent, so every layout written before FD-01 serializes **byte-for-byte as before**. No database migration; no published version is rewritten.
* Preserved through `FormLayoutCodec.Parse` / `Serialize`, i.e. draft save, publish (including the "nothing changed" comparison) and restore-to-draft.

## 3. Canonical packing (one implementation)

`packElements` in `packages/form-renderer/src/packer.ts` is the single interpretation used by the designer canvas, Preview and the published renderer.

* **Desktop:** an element with `columnStart` is placed at that column; the cells before it stay empty. If the column is behind the current position (occupied, earlier or duplicate) — or the element asks for a new row — it starts the next row at that column. Two elements never overlap and nothing is reordered.
* **Elements without `columnStart`** (legacy layouts) pack exactly as before.
* **Tablet (2 columns) and phone (1 column)** ignore `columnStart` and reflow in source order without preserving empty desktop cells. Source order = DOM order = tab and screen-reader order at every width.
* Rows are implicit (element order + `startOnNewRow` + `columnStart`). A row with no field has no representation and closes up; this is the only intended difference between a pending empty row in the designer and the saved layout.
* Malformed values (only from hand-edited documents, since both validators reject them) are clamped deterministically so the element still fits.

## 4. Designer

`packages/form-designer/src/gridEdits.ts` (pure, unit-tested) performs every placement edit as (row, column) cells read through the canonical packer and writes them back with `rebuildSection`, which stores `columnStart` on every element of the edited section and sets `startOnNewRow` only where the packer needs it. Re-packing always reproduces the edited cells.

* **Empty cells** shown on the canvas: every empty cell of an occupied row (from the packed grid), the four cells of each pending row added with "+ Add Row" (designer state only, until filled), and one trailing cell that starts a new row (any width fits there).
* **Create / place:** "Place field here" and "New field…" place at the exact selected cell. Widths that do not fit the free cells from that column are listed as disabled ("no room here"). A refused placement changes nothing and says why; nothing is overwritten or displaced.
* **Drag-and-drop** (native HTML5, unchanged mechanism): onto an empty cell moves the field there; onto another field swaps the two; onto the section background moves it to a new row at the end. Invalid targets (occupied, too narrow, other scope) show a red outline and refuse the drop.
* **Keyboard:** Alt+ArrowUp/Down (and "Move earlier/later") swap with the neighbouring field in reading order (crossing into the adjacent section at the ends); Alt+ArrowLeft/Right move one column. Refusals are announced.
* **Width:** changed in place only into free cells; wider choices without room are disabled ("no room in this row").
* **Start on a new row:** on splits the row before the field (it and later fields of that row keep their columns on a new row); off joins the row above where its cells are free.
* **Legacy sections are pinned on first placement edit:** the section's current packed arrangement is written as explicit positions (visually identical on desktop, tablet and phone; tested), so later edits never move unrelated fields. Non-placement edits (labels, rules, validation) do not pin.
* "+ Add Column" now selects the next empty cell after the selection (the grid always has four columns); when every row is full it adds an empty row.

### 4.1 Rows and columns (owner visual review corrections, C-154)

* **No trailing cell.** The standalone single-column "+ Add Field" target below each section is gone. An empty section shows one row of four empty cells; otherwise the empty cells shown are exactly the empty cells of the section's rows.
* **"+ Add Row"** adds exactly one empty row of four cells at the end of the selected section and selects its first cell. Placing a new field, or dragging an existing one, into any of its cells fills that row; no further row appears. Moving a field "to a new row at the end" (drop on the section background, Move to section) uses the first trailing empty row before creating one.
* **Empty rows are designer-only.** A row without fields has no representation in the saved layout, so the designer keeps empty rows on the layout objects under a symbol key (`EMPTY_ROWS` in `gridEdits.ts`). Object spreads carry it through every edit and undo snapshot; the layout schema drops symbol keys, so it is never serialized, saved or published (layout contract, schema version 2 and database unchanged). Inside the grid edits each empty row is an invisible full-width placeholder in its own row, read through the canonical packer like any field. Empty rows survive Save draft during the session and close up on reload. A row emptied by moving its last field away still closes up, as before.
* **Row and column actions** — in the inspector's "Row · Column" panel for a selected field or empty cell, and in the field tile's "More actions" menu (`rowColumnEdits.ts`):
  * *Clear row*: removes the row's fields; the row stays as four empty cells.
  * *Delete row*: removes the row and its fields; the rows below move up and every other field keeps its columns. Deleting an empty row removes just that row; an empty section always keeps one row.
  * *Clear column*: removes the one field occupying that cell — a field spanning several columns is removed as a whole and the confirmation names its span; every other field stays put, and a row left without fields stays as an empty row.
  * *Delete column*: removes that column position within its row — the field in it (if any) is removed and the fields to its right move one column left. The grid stays four columns wide and other rows never move. It is **refused** (with an explanation, nothing changed) when the column is part of a field spanning several columns, because it would have to resize or remove that field.
* **Safeguards.** An operation that removes fields opens a confirmation naming the row or column (and span), the number of fields and their names, and stating the difference between clearing and deleting. It is **blocked** while a field that stays in the layout has a visibility/required rule or a dependent dropdown that reads a removed field (the server would reject that layout as `LAYOUT_INVALID`), so no reference is left dangling and the dependency protection is not bypassed. Removing a field only un-places it: field definitions, stable ids, stored values and documents are kept, and it can be placed again. Every operation (and "+ Add Row") is one undo step; Undo restores the removed fields with their configuration in their original cells.
* **Undo fix.** The existing undo recorded every change twice under React StrictMode (development builds), so each change needed two Undo clicks; one entry per change is now recorded.
* The existing tile "Remove from layout" action is unchanged (no confirmation, rule references checked by the server on save).

## 5. Conditional visibility and authorization

* **Form conditions / "Show in form":** on desktop a hidden field with `columnStart` keeps its (empty) cell, so the fields around it do not move; rows left with no shown field close up. A hidden legacy field (no `columnStart`) is removed before packing as before.
* **Authorization:** the server removes unauthorized fields before returning a partial (share-scoped) layout — unchanged — and FD-01 additionally removes **every** `columnStart` from a partial projection (`GetRecordProjectionHandler.AuthorizedLayout`), so the remaining fields reflow and no reserved cell or position can indicate where an unauthorized field was. Field ids, labels, rules and dependencies of unauthorized fields were already removed (verified by contract test). Complete readers receive the published layout unchanged. Unknown fields never reserve a cell in the renderer. No Authority, SpiceDB or encryption code changed.

## 6. Compatibility and rollback

**Existing forms:** published versions are untouched and render exactly as before (no `columnStart` → previous packing). A legacy draft changes only when a designer edits a section's placement (pinning, visually identical), and only the draft; publishing creates a new version.

**Why an older build is a risk:** a server older than FD-01 accepts `columnStart` and **silently drops it** on draft save, publish and restore (it ignores unknown placement properties and re-serializes). An older web client refuses to load a layout containing it (its schema rejects unknown properties) — loud, not silent, but forms published with positions would not open.

**Safeguards implemented**

1. FD-01 server: preserves and validates `columnStart` on every path (contract tests).
2. FD-01 designer: after every draft save it compares the stored draft with what it sent (`droppedPositions`). If positions were dropped — i.e. it is talking to an older server — it keeps the designed arrangement on screen, leaves the draft marked **unsaved**, and shows *"Column positions were not saved … Do not publish"*. Tested in the browser with a simulated older server.
3. Read-only detection query for operators (below).

**Not possible without a broader change:** a pre-FD-01 *server* cannot be made to fail closed on these layouts while the schema stays v2, because it ignores unknown placement properties. Making it fail loudly would require a layout schema v3 marker (older servers reject unknown schema versions) — a published-contract change that was **not** approved and is **not** implemented. Hence the supported rollback sequence below.

**Supported rollback sequence**

1. Before rolling the API back below the FD-01 build, run the read-only query against BusinessDb:

   ```sql
   BEGIN READ ONLY;
   SELECT 'draft' AS layout_source, form_definition_id
   FROM form_drafts
   WHERE jsonb_path_exists(layout_json, '$.sections[*].elements[*].layout.columnStart')
   UNION ALL
   SELECT 'published version ' || version_number, form_definition_id
   FROM form_versions
   WHERE jsonb_path_exists(layout_json, '$.sections[*].elements[*].layout.columnStart');
   COMMIT;
   ```

2. **No rows:** rolling back is safe (no layout uses explicit positions).
3. **Any rows:** do **not** roll the API back below FD-01. Roll back the web client only if needed (an older client cannot load those layouts, so it cannot save them), or roll forward. A rollback of the API below FD-01 with such rows is unsupported: those forms would lose their positions on the next save, publish or restore (published versions themselves are immutable and keep them).
4. Web and API are deployed together; never run an FD-01 web client against an older API except transiently — safeguard 2 then refuses to treat saves as complete.

## 7. Tests (focused; Stage B not run)

| Suite | Covers |
|---|---|
| `packages/form-renderer/src/packer.test.ts` | FD-01-01, 02, 06, 10, 11, 12, 14 at the packer |
| `packages/form-renderer/src/FormRenderer.fd01.test.tsx` | FD-01-09, 10, 12, 13; schema round-trip and rejection |
| `packages/form-designer/src/gridEdits.test.ts` | FD-01-01..07, 11, 14; swap, column moves, new-row toggle, removal, scope, rollback detector |
| `tests/features/EnterpriseForms.Api.ContractTests/FD01LayoutFidelityTests.cs` | server preservation, legacy byte-identity, invalid column/span rejection, authorization projection filtering |
| `packages/form-designer/src/rowColumnEdits.test.ts` | Add Row (exactly one row; drag/placement consumes it), Clear/Delete row, Clear/Delete column, spans and overlaps, dependency blocking, identity/undo, save/reload, legacy pinning |
| `apps/web/src/features/form-designer/components/GridOperationDialog.test.tsx` | confirmation contents (row/column, span, count, names, clear vs delete) and dependency blocking |
| `src/frontend/e2e/designer-row-column-management.spec.ts` | row and column management end to end: Add Row, drag into a new row, confirmation and Escape, keyboard, axe, Undo, Delete row, Clear/Delete column, spanning refusal, save → reload → Preview → publish → record page |
| `src/frontend/e2e/designer-layout-fidelity.spec.ts` | original-defect reproduction and FD-01-01..10, 13 end to end (design → save → reload → preview → publish → record page), drag-and-drop, widths, axe on the canvas, rollback protection |

## 8. Manual browser acceptance

See the FD-01 final report (owner visual acceptance procedure). Summary: build a 4-row layout with empty leading/interior cells (including a field alone in column 3), save, reload, check Preview at Desktop / Tablet 768 / Phone 320, drag a field between columns and rows, try a width that does not fit, publish, open the published form as a clerk at desktop width and create a record.

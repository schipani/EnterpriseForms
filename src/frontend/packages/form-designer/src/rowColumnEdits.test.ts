import { emptyLayout, packElements, parseLayout, serializeLayout, type FormLayout, type LayoutElement } from "@enterpriseforms/form-renderer";
import { describe, expect, it } from "vitest";
import { addEmptyRow, carryEmptyRows, emptyCells, emptyRowsOf, emptyRowsSignature, fieldCells, isEmptyRow, moveToCell, moveToNewRow, placeAtCell, rowCount, sectionGrid, setStartOnNewRow, swapElements } from "./gridEdits";
import { elementKey } from "./layoutEdits";
import { planGridOperation, removalDependencies, selectionAfter, type GridOperation } from "./rowColumnEdits";

/** FD-01 row and column management. Positions are always read back through the canonical renderer packer. */

const id = (n: number) => `0199a2b3-c4d5-7e6f-8123-${n.toString().padStart(12, "0")}`;
const field = (n: number, label: string, columnSpan: 1 | 2 | 3 | 4 = 1, extra: Partial<LayoutElement> = {}): LayoutElement => ({ kind: "field", fieldDefinitionId: id(n), control: "text", label, required: false, layout: { columnSpan, startOnNewRow: false }, ...extra }) as LayoutElement;
const A = field(1, "A");
const B = field(2, "B");
const C = field(3, "C");
const D = field(4, "D");
const E = field(5, "E");
const WIDE = field(6, "Wide", 2);

/** Saved positions {label: [row, column, span]} — what Preview and the published renderer draw. */
function positions(layout: FormLayout): Record<string, [number, number, number]> {
  return Object.fromEntries(packElements(layout.sections[0]!.elements, "desktop").map((cell) => [cell.element.label, [cell.row, cell.column, cell.span]]));
}

/** Designer positions, including empty rows (row numbers as drawn on the canvas). */
function canvas(layout: FormLayout): { fields: Record<string, [number, number]>; emptyRows: number[] } {
  const cells = sectionGrid(layout.sections[0]!);
  return {
    fields: Object.fromEntries(fieldCells(cells).map((cell) => [cell.element.label, [cell.row, cell.column]])),
    emptyRows: cells.filter((cell) => cell.empty).map((cell) => cell.row),
  };
}

function place(layout: FormLayout, element: LayoutElement, row: number, column: number): FormLayout {
  const result = placeAtCell(layout, 0, element, row, column);
  expect(result.ok).toBe(true);
  return result.layout;
}

function apply(layout: FormLayout, operation: Omit<GridOperation, "sectionKey">): FormLayout {
  const plan = planGridOperation(layout, { sectionKey: "main", ...operation });
  if (!plan.ok) {
    throw new Error(plan.message);
  }
  expect(plan.dependencies).toEqual([]);
  return plan.layout;
}

const roundTrip = (layout: FormLayout) => parseLayout(serializeLayout(layout));

/** A(1,1) B(1,3) | C(2,2) D(2,4) | E(3,1) */
function threeRows(): FormLayout {
  let layout = emptyLayout("Form");
  layout = place(layout, A, 1, 1);
  layout = place(layout, B, 1, 3);
  layout = place(layout, C, 2, 2);
  layout = place(layout, D, 2, 4);
  return place(layout, E, 3, 1);
}

describe("FD-01 Add Row", () => {
  it("an empty section shows exactly one row of four empty cells and no other target", () => {
    const section = emptyLayout("Form").sections[0]!;
    expect(emptyRowsOf(section)).toEqual([0]);
    expect(emptyCells(sectionGrid(section))).toEqual([1, 2, 3, 4].map((column) => ({ row: 1, column })));
  });

  it("1: Add Row creates exactly one four-column row, and no trailing row", () => {
    const layout = threeRows();
    const added = addEmptyRow(layout, 0);
    expect(added.row).toBe(4);
    const cells = sectionGrid(added.layout.sections[0]!);
    expect(rowCount(cells)).toBe(4);
    expect(emptyCells(cells).filter((cell) => cell.row === 4)).toHaveLength(4);
    expect(emptyCells(cells).filter((cell) => cell.row > 4)).toHaveLength(0);
    // Twice is two rows, never more.
    expect(rowCount(sectionGrid(addEmptyRow(added.layout, 0).layout.sections[0]!))).toBe(5);
    // Empty rows are designer-only: nothing changes in the serialized (saved) layout.
    expect(serializeLayout(added.layout)).toBe(serializeLayout(layout));
    expect(emptyRowsSignature(added.layout)).not.toBe(emptyRowsSignature(layout));
  });

  it("2: dragging an existing field into the new row consumes it instead of creating another row", () => {
    const added = addEmptyRow(threeRows(), 0);
    const moved = moveToCell(added.layout, elementKey(B), 0, 4, 3);
    expect(moved.ok).toBe(true);
    expect(canvas(moved.layout)).toEqual({ fields: { A: [1, 1], C: [2, 2], D: [2, 4], E: [3, 1], B: [4, 3] }, emptyRows: [] });
    expect(rowCount(sectionGrid(moved.layout.sections[0]!))).toBe(4);
    expect(positions(roundTrip(moved.layout))).toEqual({ A: [1, 1, 1], C: [2, 2, 1], D: [2, 4, 1], E: [3, 1, 1], B: [4, 3, 1] });
  });

  it("2: a new field placed into the empty row, and a field moved to a new row at the end, also consume it", () => {
    const added = addEmptyRow(threeRows(), 0);
    expect(canvas(place(added.layout, field(9, "N"), 4, 2)).emptyRows).toEqual([]);
    const appended = moveToNewRow(added.layout, elementKey(A), 0);
    expect(appended.ok).toBe(true);
    // A leaves row 1 (B stays there) and goes into the trailing empty row, which is consumed: still four rows.
    expect(canvas(appended.layout)).toEqual({ fields: { B: [1, 3], C: [2, 2], D: [2, 4], E: [3, 1], A: [4, 1] }, emptyRows: [] });
  });

  it("an empty row in the middle stays put while fields around it are moved, swapped and re-flagged", () => {
    let layout = apply(threeRows(), { kind: "clearRow", row: 2 }); // A B | (empty) | E
    expect(canvas(layout)).toEqual({ fields: { A: [1, 1], B: [1, 3], E: [3, 1] }, emptyRows: [2] });
    layout = swapElements(layout, elementKey(A), elementKey(E)).layout;
    expect(canvas(layout)).toEqual({ fields: { E: [1, 1], B: [1, 3], A: [3, 1] }, emptyRows: [2] });
    layout = setStartOnNewRow(layout, elementKey(B), true);
    expect(canvas(layout)).toEqual({ fields: { E: [1, 1], B: [2, 3], A: [4, 1] }, emptyRows: [3] });
  });
});

describe("FD-01 Clear row / Delete row", () => {
  it("3: Clear row removes its fields and keeps the row as four empty cells during editing", () => {
    const layout = apply(threeRows(), { kind: "clearRow", row: 2 });
    expect(canvas(layout)).toEqual({ fields: { A: [1, 1], B: [1, 3], E: [3, 1] }, emptyRows: [2] });
    expect(emptyCells(sectionGrid(layout.sections[0]!)).filter((cell) => cell.row === 2)).toHaveLength(4);
    // The cleared row remains usable: a field dropped into it fills it.
    const refilled = moveToCell(layout, elementKey(E), 0, 2, 4);
    expect(canvas(refilled.layout)).toEqual({ fields: { A: [1, 1], B: [1, 3], E: [2, 4] }, emptyRows: [] });
    // It is not saved: after Save draft the empty row closes up and every remaining field keeps its columns.
    expect(positions(roundTrip(layout))).toEqual({ A: [1, 1, 1], B: [1, 3, 1], E: [2, 1, 1] });
  });

  it("4: Delete row removes the row and its fields; later rows move up with their columns unchanged", () => {
    const layout = apply(threeRows(), { kind: "deleteRow", row: 1 });
    expect(canvas(layout)).toEqual({ fields: { C: [1, 2], D: [1, 4], E: [2, 1] }, emptyRows: [] });
    expect(positions(roundTrip(layout))).toEqual({ C: [1, 2, 1], D: [1, 4, 1], E: [2, 1, 1] });
  });

  it("Delete row of an empty row removes only that row", () => {
    const cleared = apply(threeRows(), { kind: "clearRow", row: 2 });
    expect(canvas(apply(cleared, { kind: "deleteRow", row: 2 }))).toEqual({ fields: { A: [1, 1], B: [1, 3], E: [2, 1] }, emptyRows: [] });
    const added = addEmptyRow(threeRows(), 0).layout;
    expect(canvas(apply(added, { kind: "deleteRow", row: 4 }))).toEqual(canvas(threeRows()));
  });

  it("refuses Clear row on an empty row and Delete row of an empty section's only row", () => {
    const cleared = apply(threeRows(), { kind: "clearRow", row: 2 });
    expect(planGridOperation(cleared, { kind: "clearRow", sectionKey: "main", row: 2 })).toMatchObject({ ok: false });
    expect(planGridOperation(emptyLayout("Form"), { kind: "deleteRow", sectionKey: "main", row: 1 })).toMatchObject({ ok: false });
  });
});

describe("FD-01 Clear column / Delete column", () => {
  it("5: Clear column removes only the occupying field; unrelated fields do not move", () => {
    const layout = apply(threeRows(), { kind: "clearColumn", row: 1, column: 3 });
    expect(canvas(layout)).toEqual({ fields: { A: [1, 1], C: [2, 2], D: [2, 4], E: [3, 1] }, emptyRows: [] });
  });

  it("5: clearing the last field of a row keeps the row, so the rows below do not move up", () => {
    const layout = apply(threeRows(), { kind: "clearColumn", row: 3, column: 1 });
    expect(canvas(layout)).toEqual({ fields: { A: [1, 1], B: [1, 3], C: [2, 2], D: [2, 4] }, emptyRows: [3] });
  });

  it("5/7: a field spanning several columns is cleared as one object from any of its columns, and the plan names its span", () => {
    const layout = place(place(emptyLayout("Form"), WIDE, 1, 2), A, 1, 4);
    const plan = planGridOperation(layout, { kind: "clearColumn", sectionKey: "main", row: 1, column: 3 });
    expect(plan).toMatchObject({ ok: true, affected: "row 1, columns 2–3" });
    expect(plan.ok && plan.removed.map((element) => element.label)).toEqual(["Wide"]);
    expect(canvas(plan.ok ? plan.layout : layout)).toEqual({ fields: { A: [1, 4] }, emptyRows: [] });
  });

  it("6: Delete column removes the field in that column and shifts the fields to its right one column left", () => {
    let layout = emptyLayout("Form");
    for (const [element, column] of [[A, 1], [B, 2], [C, 3], [D, 4]] as const) {
      layout = place(layout, element, 1, column);
    }
    layout = place(layout, E, 2, 2);
    const plan = planGridOperation(layout, { kind: "deleteColumn", sectionKey: "main", row: 1, column: 2 });
    expect(plan).toMatchObject({ ok: true, shifted: 2 });
    const next = plan.ok ? plan.layout : layout;
    // Row 1: A stays, C and D move left; column 4 is empty; the grid stays four columns; row 2 is untouched.
    expect(canvas(next)).toEqual({ fields: { A: [1, 1], C: [1, 2], D: [1, 3], E: [2, 2] }, emptyRows: [] });
    expect(emptyCells(sectionGrid(next.sections[0]!)).filter((cell) => cell.row === 1)).toEqual([{ row: 1, column: 4 }]);
    expect(positions(roundTrip(next))).toEqual({ A: [1, 1, 1], C: [1, 2, 1], D: [1, 3, 1], E: [2, 2, 1] });
  });

  it("6: Delete column of an empty column only closes the gap (no field is removed)", () => {
    const plan = planGridOperation(threeRows(), { kind: "deleteColumn", sectionKey: "main", row: 1, column: 2 });
    expect(plan).toMatchObject({ ok: true, removed: [], shifted: 1 });
    expect(canvas(plan.ok ? plan.layout : threeRows()).fields).toMatchObject({ A: [1, 1], B: [1, 2], C: [2, 2], D: [2, 4], E: [3, 1] });
  });

  it("7: Delete column is refused when the column is part of a wider field (it would resize or remove it)", () => {
    const layout = place(place(emptyLayout("Form"), WIDE, 1, 1), A, 1, 3);
    const plan = planGridOperation(layout, { kind: "deleteColumn", sectionKey: "main", row: 1, column: 2 });
    expect(plan.ok).toBe(false);
    expect(!plan.ok && plan.message).toMatch(/spans columns 1–2/);
  });

  it("7: after every operation no two fields overlap and every field stays inside the four-column grid", () => {
    let layout = place(place(place(emptyLayout("Form"), WIDE, 1, 1), A, 1, 3), field(7, "Wide2", 3), 2, 2);
    layout = place(layout, B, 2, 1);
    const operations: Array<Omit<GridOperation, "sectionKey">> = [
      { kind: "deleteColumn", row: 1, column: 3 },
      { kind: "clearColumn", row: 2, column: 3 },
      { kind: "deleteColumn", row: 2, column: 1 },
      { kind: "clearRow", row: 1 },
      { kind: "deleteRow", row: 1 },
    ];
    for (const operation of operations) {
      const plan = planGridOperation(layout, { sectionKey: "main", ...operation });
      if (!plan.ok) {
        continue;
      }
      layout = plan.layout;
      const cells = fieldCells(sectionGrid(layout.sections[0]!));
      for (const cell of cells) {
        expect(cell.column + cell.span - 1).toBeLessThanOrEqual(4);
        expect(cells.some((other) => other !== cell && other.row === cell.row && other.column < cell.column + cell.span && cell.column < other.column + other.span)).toBe(false);
      }
    }
  });

  it("deleting the only field of a row by column keeps the row as an empty row", () => {
    const layout = apply(threeRows(), { kind: "deleteColumn", row: 3, column: 1 });
    expect(canvas(layout)).toEqual({ fields: { A: [1, 1], B: [1, 3], C: [2, 2], D: [2, 4] }, emptyRows: [3] });
    expect(isEmptyRow(sectionGrid(layout.sections[0]!), 3)).toBe(true);
  });
});

describe("FD-01 row and column management safeguards", () => {
  it("9: every field keeps its stable identity and configuration; the previous layout object is untouched (Undo restores it)", () => {
    const before = threeRows();
    const snapshot = serializeLayout(before);
    const after = apply(before, { kind: "deleteRow", row: 1 });
    expect(serializeLayout(before)).toBe(snapshot);
    expect(after.sections[0]!.elements.map(elementKey)).toEqual([C, D, E].map(elementKey));
  });

  it("10: an operation that would leave a remaining field's rule or dependent dropdown reading a removed field is blocked", () => {
    const toggle = field(10, "Toggle");
    const country = { ...field(11, "Country"), control: "select" } as LayoutElement;
    const shownWhenToggle = field(12, "Note", 1, { visibleWhen: { op: "isNotEmpty", fieldDefinitionId: id(10) } } as Partial<LayoutElement>);
    const province = field(13, "Province", 1, { control: "select", optionFilter: { parentFieldDefinitionId: id(11), allowedOptionIdsByParent: {} } } as unknown as Partial<LayoutElement>);
    let layout = place(emptyLayout("Form"), toggle, 1, 1);
    layout = place(layout, country, 1, 2);
    layout = place(layout, shownWhenToggle, 2, 1);
    layout = place(layout, province, 2, 2);
    const plan = planGridOperation(layout, { kind: "clearRow", sectionKey: "main", row: 1 });
    expect(plan.ok && plan.dependencies).toEqual([
      { dependentFieldDefinitionId: id(12), sourceFieldDefinitionId: id(10), reference: "rule" },
      { dependentFieldDefinitionId: id(13), sourceFieldDefinitionId: id(11), reference: "choices" },
    ]);
    // Removing the dependent fields together with their sources leaves nothing dangling.
    expect(removalDependencies(apply(apply(layout, { kind: "deleteRow", row: 2 }), { kind: "deleteRow", row: 1 }), [toggle, country])).toEqual([]);
    // A field's own rule leaves with the field (removing row 2 is fine).
    expect(planGridOperation(layout, { kind: "deleteRow", sectionKey: "main", row: 2 })).toMatchObject({ ok: true, dependencies: [] });
  });

  it("11: save and reload keep the resulting layout; empty rows carried across a save stay usable", () => {
    const cleared = apply(threeRows(), { kind: "clearRow", row: 2 });
    const saved = roundTrip(cleared);
    expect(positions(saved)).toEqual({ A: [1, 1, 1], B: [1, 3, 1], E: [2, 1, 1] });
    expect(canvas(carryEmptyRows(cleared, saved))).toEqual(canvas(cleared));
  });

  it("13: a legacy section (no columnStart) is pinned without moving anything when a row is deleted", () => {
    const legacy: FormLayout = { ...emptyLayout("Form"), sections: [{ sectionKey: "main", title: "Main", gridColumns: 4, elements: [field(1, "A", 2), field(2, "B", 2), field(3, "C", 4), field(4, "D")] }] };
    expect(positions(legacy)).toEqual({ A: [1, 1, 2], B: [1, 3, 2], C: [2, 1, 4], D: [3, 1, 1] });
    const next = apply(legacy, { kind: "deleteRow", row: 2 });
    expect(positions(next)).toEqual({ A: [1, 1, 2], B: [1, 3, 2], D: [2, 1, 1] });
    expect(packElements(next.sections[0]!.elements, "tablet").map((cell) => cell.element.label)).toEqual(["A", "B", "D"]);
  });

  it("selection follows the affected cell", () => {
    const layout = apply(threeRows(), { kind: "clearColumn", row: 1, column: 3 });
    expect(selectionAfter(layout, { kind: "clearColumn", sectionKey: "main", row: 1, column: 3 })).toEqual({ kind: "slot", sectionKey: "main", slotId: "cell-1-3", row: 1, column: 3 });
    expect(selectionAfter(layout, { kind: "deleteRow", sectionKey: "main", row: 1 })).toEqual({ kind: "section", sectionKey: "main" });
  });
});

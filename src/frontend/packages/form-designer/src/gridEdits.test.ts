import { emptyLayout, packElements, parseLayout, serializeLayout, type FormLayout, type LayoutElement } from "@enterpriseforms/form-renderer";
import { describe, expect, it } from "vitest";
import { elementKey } from "./layoutEdits";
import { droppedPositions, emptyCells, maxSpanFor, moveByColumn, moveToCell, moveToNewRow, neighbourKey, pinSection, placeAtCell, removeFromGrid, sectionGrid, setSpan, setStartOnNewRow, swapElements } from "./gridEdits";

/** FD-01 — designer grid edits. Every assertion re-packs through the canonical renderer packer (what Preview/publish draw). */

const id = (n: number) => `0199a2b3-c4d5-7e6f-8123-${n.toString().padStart(12, "0")}`;
const field = (n: number, label: string, columnSpan: 1 | 2 | 3 | 4 = 1, extra: Partial<LayoutElement["layout"]> = {}): LayoutElement => ({ kind: "field", fieldDefinitionId: id(n), control: "text", label, required: false, layout: { columnSpan, startOnNewRow: false, ...extra } });
const A = field(1, "A");
const B = field(2, "B");
const C = field(3, "C");
const D = field(4, "D");

/** Logical layout as {label: [row, column, span]} through the renderer packer (desktop). */
function positions(layout: FormLayout, sectionIndex = 0): Record<string, [number, number, number]> {
  return Object.fromEntries(packElements(layout.sections[sectionIndex]!.elements, "desktop").map((cell) => [cell.element.label, [cell.row, cell.column, cell.span]]));
}

function place(layout: FormLayout, element: LayoutElement, row: number, column: number, sectionIndex = 0): FormLayout {
  const result = placeAtCell(layout, sectionIndex, element, row, column);
  expect(result.ok).toBe(true);
  return result.layout;
}

/** Simulates Save draft → reload: the canonical serialization and a fresh parse (the server preserves columnStart identically). */
function roundTrip(layout: FormLayout): FormLayout {
  return parseLayout(serializeLayout(layout));
}

describe("FD-01 designer grid placement", () => {
  it("FD-01-01: a field placed in column 3 of an empty row stays in column 3 through save and reload", () => {
    const layout = place(emptyLayout("Form"), A, 1, 3);
    expect(positions(layout)).toEqual({ A: [1, 3, 1] });
    expect(positions(roundTrip(layout))).toEqual({ A: [1, 3, 1] });
    expect(layout.sections[0]!.elements[0]!.layout).toEqual({ columnSpan: 1, startOnNewRow: false, columnStart: 3 });
  });

  it("FD-01-02: multiple rows keep every position, independent of the order fields were placed in", () => {
    let layout = emptyLayout("Form");
    layout = place(layout, B, 1, 4);
    layout = place(layout, A, 1, 1); // an empty leading cell of an existing row
    layout = place(layout, C, 2, 3); // the trailing "new row" cell
    layout = place(layout, D, 3, 2);
    expect(positions(layout)).toEqual({ A: [1, 1, 1], B: [1, 4, 1], C: [2, 3, 1], D: [3, 2, 1] });
    expect(positions(roundTrip(layout))).toEqual(positions(layout));
    // Source (= tab and reading) order follows the rows.
    expect(layout.sections[0]!.elements.map((e) => e.label)).toEqual(["A", "B", "C", "D"]);
  });

  it("FD-01-03: direct creation in row 4, column 3 (rows 1-3 occupied) persists", () => {
    let layout = emptyLayout("Form");
    layout = place(layout, A, 1, 1);
    layout = place(layout, B, 2, 1);
    layout = place(layout, C, 3, 1);
    layout = place(layout, D, 4, 3);
    expect(positions(roundTrip(layout)).D).toEqual([4, 3, 1]);
  });

  it("an empty row has no representation: a placement beyond the last row starts one new row at the end", () => {
    const layout = place(place(emptyLayout("Form"), A, 1, 2), B, 9, 3);
    expect(positions(layout)).toEqual({ A: [1, 2, 1], B: [2, 3, 1] });
  });

  it("FD-01-04: dragging a field from column 1 to column 4 persists the destination; the old cell becomes empty", () => {
    let layout = place(place(emptyLayout("Form"), A, 1, 1), B, 1, 2);
    const moved = moveToCell(layout, elementKey(A), 0, 1, 4);
    expect(moved.ok).toBe(true);
    layout = roundTrip(moved.layout);
    expect(positions(layout)).toEqual({ B: [1, 2, 1], A: [1, 4, 1] });
    expect(emptyCells(sectionGrid(layout.sections[0]!))).toEqual([
      { row: 1, column: 1 },
      { row: 1, column: 3 },
    ]);
  });

  it("FD-01-05: dragging between rows releases the original cell and leaves unrelated fields unchanged", () => {
    let layout = emptyLayout("Form");
    layout = place(layout, A, 1, 2);
    layout = place(layout, B, 1, 3);
    layout = place(layout, C, 2, 1);
    layout = place(layout, D, 3, 4);
    const moved = moveToCell(layout, elementKey(A), 0, 3, 2);
    expect(moved.ok).toBe(true);
    expect(positions(moved.layout)).toEqual({ B: [1, 3, 1], C: [2, 1, 1], A: [3, 2, 1], D: [3, 4, 1] });
  });

  it("FD-01-05: moving the only field of a row closes that row up; the other rows keep their columns", () => {
    let layout = emptyLayout("Form");
    layout = place(layout, A, 1, 3);
    layout = place(layout, B, 2, 2);
    const moved = moveToNewRow(layout, elementKey(A), 0);
    expect(positions(moved.layout)).toEqual({ B: [1, 2, 1], A: [2, 3, 1] });
  });

  it("FD-01-06: spans 1-4 at explicit positions; widths that leave the row or overlap are refused", () => {
    let layout = emptyLayout("Form");
    layout = place(layout, field(1, "One", 1), 1, 4);
    layout = place(layout, field(2, "Two", 2), 2, 3);
    layout = place(layout, field(3, "Three", 3), 3, 2);
    layout = place(layout, field(4, "Four", 4), 4, 1);
    expect(positions(roundTrip(layout))).toEqual({ One: [1, 4, 1], Two: [2, 3, 2], Three: [3, 2, 3], Four: [4, 1, 4] });
    // Beyond the row boundary.
    expect(placeAtCell(layout, 0, field(5, "Wide", 2), 5, 4)).toMatchObject({ ok: false, reason: "too-wide" });
    // Overlap with an occupied cell.
    expect(placeAtCell(layout, 0, field(5, "X", 1), 2, 4)).toMatchObject({ ok: false, reason: "occupied" });
    expect(placeAtCell(layout, 0, field(5, "X", 2), 1, 3)).toMatchObject({ ok: false, reason: "too-wide" });
    // Resizing in place: allowed only into free cells.
    expect(setSpan(layout, elementKey(field(1, "One")), 2)).toMatchObject({ ok: false, reason: "too-wide" });
    const narrowed = setSpan(layout, elementKey(field(4, "Four")), 2);
    expect(narrowed.ok).toBe(true);
    expect(positions(narrowed.layout).Four).toEqual([4, 1, 2]);
    expect(maxSpanFor(narrowed.layout, elementKey(field(2, "Two")))).toBe(2);
  });

  it("resizing does not move unrelated fields of a legacy (flow) section", () => {
    // Legacy: two half-width fields flow side by side, a full-width one below.
    let layout: FormLayout = { ...emptyLayout("Form"), sections: [{ sectionKey: "main", gridColumns: 4, elements: [field(1, "A", 2), field(2, "B", 2), field(3, "C", 4)] }] };
    const before = positions(layout);
    const narrowed = setSpan(layout, elementKey(A), 1);
    expect(narrowed.ok).toBe(true);
    layout = narrowed.layout;
    expect(positions(layout)).toEqual({ ...before, A: [1, 1, 1] }); // B stays at column 3 (legacy flow would have moved it)
  });

  it("FD-01-11: pinning a legacy section is visually identical and keeps tablet reflow unchanged", () => {
    const legacy = [field(1, "A", 1), field(2, "B", 1), field(3, "C", 1), field(4, "D", 2), field(5, "E", 4, { startOnNewRow: true }), field(6, "F", 3)];
    const section = { sectionKey: "main", gridColumns: 4 as const, elements: legacy };
    const pinned = pinSection(section);
    const view = (elements: readonly LayoutElement[], breakpoint: "desktop" | "tablet" | "phone") => packElements(elements, breakpoint).map((c) => [c.element.label, c.row, c.column, c.span]);
    for (const breakpoint of ["desktop", "tablet", "phone"] as const) {
      expect(view(pinned.elements, breakpoint)).toEqual(view(legacy, breakpoint));
    }
    expect(pinned.elements.every((e) => e.layout.columnStart !== undefined)).toBe(true);
    expect(pinSection(pinned)).toEqual(pinned); // idempotent
  });

  it("swapping two fields (Move earlier/later, drop onto a field) exchanges their cells", () => {
    let layout = emptyLayout("Form");
    layout = place(layout, A, 1, 1);
    layout = place(layout, B, 1, 3);
    layout = place(layout, C, 2, 2);
    const swapped = swapElements(layout, elementKey(A), elementKey(C));
    expect(positions(swapped.layout)).toEqual({ C: [1, 1, 1], B: [1, 3, 1], A: [2, 2, 1] });
    expect(neighbourKey(layout, elementKey(B), 1)).toBe(elementKey(C));
    expect(neighbourKey(layout, elementKey(A), -1)).toBeNull();
  });

  it("a wider field swapped into a narrower cell yields to a new row instead of displacing a third field", () => {
    let layout = emptyLayout("Form");
    layout = place(layout, field(1, "Wide", 4), 1, 1);
    layout = place(layout, field(2, "Narrow", 2), 2, 1);
    layout = place(layout, field(3, "Other", 2), 2, 3);
    const swapped = swapElements(layout, elementKey(field(1, "Wide")), elementKey(field(2, "Narrow")));
    expect(positions(swapped.layout)).toEqual({ Narrow: [1, 1, 2], Other: [2, 3, 2], Wide: [3, 1, 4] });
  });

  it("Alt+ArrowLeft/Right moves one column within the row and is refused at the edge or into a neighbour", () => {
    let layout = place(place(emptyLayout("Form"), A, 1, 2), B, 1, 4);
    const left = moveByColumn(layout, elementKey(A), -1);
    expect(positions(left.layout).A).toEqual([1, 1, 1]);
    expect(moveByColumn(left.layout, elementKey(A), -1).ok).toBe(false);
    layout = moveByColumn(layout, elementKey(A), 1).layout;
    expect(positions(layout).A).toEqual([1, 3, 1]);
    expect(moveByColumn(layout, elementKey(A), 1)).toMatchObject({ ok: false, reason: "occupied" });
  });

  it("Start on a new row splits a row before the field; turning it off joins the row above when the cells are free", () => {
    let layout = emptyLayout("Form");
    layout = place(layout, A, 1, 1);
    layout = place(layout, B, 1, 3);
    layout = place(layout, C, 1, 4);
    layout = setStartOnNewRow(layout, elementKey(B), true);
    expect(positions(layout)).toEqual({ A: [1, 1, 1], B: [2, 3, 1], C: [2, 4, 1] });
    layout = setStartOnNewRow(layout, elementKey(B), false);
    expect(positions(layout)).toEqual({ A: [1, 1, 1], B: [1, 3, 1], C: [1, 4, 1] });
  });

  it("removing a field from the layout leaves every other field in place", () => {
    const legacy: FormLayout = { ...emptyLayout("Form"), sections: [{ sectionKey: "main", gridColumns: 4, elements: [field(1, "A", 1), field(2, "B", 1), field(3, "C", 1)] }] };
    expect(positions(removeFromGrid(legacy, elementKey(A)))).toEqual({ B: [1, 2, 1], C: [1, 3, 1] });
  });

  it("refuses a second placement of the same field and moves between different scopes", () => {
    const layout = place(emptyLayout("Form"), A, 1, 1);
    expect(placeAtCell(layout, 0, A, 2, 1)).toMatchObject({ ok: false, reason: "already-placed" });
    const withRepeat: FormLayout = { ...layout, sections: [...layout.sections, { sectionKey: "entries", gridColumns: 4, elements: [], repeat: { groupDefinitionId: id(99), minOccurrences: 0, maxOccurrences: 5, allowAdd: true, allowRemove: true, allowReorder: true } }] };
    expect(moveToCell(withRepeat, elementKey(A), 1, 1, 1)).toMatchObject({ ok: false, reason: "different-scope" });
  });

  it("moves between sections keep the column and empty the source cell", () => {
    let layout: FormLayout = { ...emptyLayout("Form"), sections: [{ sectionKey: "one", gridColumns: 4, elements: [] }, { sectionKey: "two", gridColumns: 4, elements: [] }] };
    layout = place(layout, A, 1, 3, 0);
    layout = place(layout, B, 1, 1, 0);
    const moved = moveToCell(layout, elementKey(A), 1, 1, 2);
    expect(moved.ok).toBe(true);
    expect(positions(moved.layout, 0)).toEqual({ B: [1, 1, 1] });
    expect(positions(moved.layout, 1)).toEqual({ A: [1, 2, 1] });
  });
});

describe("FD-01 rollback protection (droppedPositions)", () => {
  it("detects a server that silently dropped explicit columns on save, and accepts a server that kept them", () => {
    const sent = place(place(emptyLayout("Form"), A, 1, 3), B, 2, 2);
    // What a pre-FD-01 server stores: the same layout without columnStart.
    const stripped: FormLayout = { ...sent, sections: sent.sections.map((section) => ({ ...section, elements: section.elements.map((element) => ({ ...element, layout: { columnSpan: element.layout.columnSpan, startOnNewRow: element.layout.startOnNewRow } }) as LayoutElement) })) };
    expect(droppedPositions(sent, stripped)).toEqual([elementKey(A), elementKey(B)]);
    expect(droppedPositions(sent, roundTrip(sent))).toEqual([]);
    // Legacy layouts carry no explicit columns, so there is nothing to lose.
    const legacy = { ...emptyLayout("Form"), sections: [{ sectionKey: "main", gridColumns: 4 as const, elements: [field(1, "A", 2)] }] };
    expect(droppedPositions(legacy, legacy)).toEqual([]);
  });
});

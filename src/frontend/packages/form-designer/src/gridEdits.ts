import { explicitColumn, packElements, type FormLayout, type LayoutElement, type LayoutSection } from "@enterpriseforms/form-renderer";
import { elementKey, findElement, sameScope } from "./layoutEdits";

/**
 * FD-01 — explicit desktop grid placement for the designer (pure, immutable, unit-tested; no React).
 *
 * The canonical interpretation of a section's layout is the renderer's packer (`packElements`, desktop): every function
 * here reads positions through it, edits them as (row, column) cells, and writes them back with `rebuildSection`, which
 * stores an explicit `columnStart` on every element of the edited section and sets `startOnNewRow` only where the packer
 * needs it. Re-packing the rebuilt section always reproduces the edited cells, so the designer, Preview and the published
 * renderer agree. Rows are implicit (element order + `startOnNewRow` + `columnStart`); a row with no element has no
 * representation and closes up. Only presentation changes: field definitions, stable ids and stored values are untouched.
 *
 * Legacy sections (no `columnStart`) are read unchanged and render exactly as before; the first placement edit of a section
 * "pins" its current packed arrangement (visually identical) so later edits never move unrelated fields.
 *
 * Designer-only empty rows (FD-01 row management): "+ Add Row", "Clear row" and "Clear column" can leave a row with no
 * field. Such a row has no representation in the saved layout, so the designer keeps it beside the section under a symbol
 * key (`EMPTY_ROWS`): the object spreads of every immutable edit carry it along (and undo snapshots keep it), while the
 * layout schema drops symbol keys, so it is never serialized, saved or published. Inside the grid edits each empty row is
 * an invisible full-width placeholder occupying its own row, so every edit reads and writes it through the same packer;
 * a field placed (or dropped) into an empty row consumes it, and a row emptied by moving its last field away closes up.
 */

export interface GridCell {
  key: string;
  element: LayoutElement;
  /** Original index in the section's element array (-1 for an empty row). */
  index: number;
  row: number;
  column: number;
  span: number;
  /** A designer-only empty row (no field): all four of its cells are free. */
  empty?: boolean;
}

/** Why a placement edit was refused (the layout is returned unchanged). */
export type GridRefusal = "occupied" | "too-wide" | "already-placed" | "not-found" | "different-scope";

export type GridEditResult = { ok: true; layout: FormLayout } | { ok: false; layout: FormLayout; reason: GridRefusal };

export const GRID_COLUMNS = 4;

/** Designer-only empty rows of a section: for each, the number of occupied rows above it (sorted). Never serialized. */
export const EMPTY_ROWS: unique symbol = Symbol("designer.emptyRows");
const EMPTY_ROW_PLACEHOLDER: unique symbol = Symbol("designer.emptyRowPlaceholder");
type DesignerSection = LayoutSection & { [EMPTY_ROWS]?: readonly number[] };

/** The designer-only empty rows of a section. An empty section always has one, so there is a place to start. */
export function emptyRowsOf(section: LayoutSection): readonly number[] {
  const rows = (section as DesignerSection)[EMPTY_ROWS] ?? [];
  return rows.length === 0 && section.elements.length === 0 ? [0] : rows;
}

function withEmptyRows(section: LayoutSection, rows: readonly number[]): LayoutSection {
  return { ...section, [EMPTY_ROWS]: [...rows].sort((a, b) => a - b) } as DesignerSection;
}

let placeholderCounter = 0;

function placeholder(): LayoutElement {
  placeholderCounter += 1;
  return { kind: "field", fieldDefinitionId: `empty-row-${placeholderCounter}`, control: "text", label: "", required: false, layout: { columnSpan: GRID_COLUMNS, startOnNewRow: true, columnStart: 1 }, [EMPTY_ROW_PLACEHOLDER]: true } as unknown as LayoutElement;
}

function isPlaceholder(element: LayoutElement): boolean {
  return (element as unknown as Record<symbol, unknown>)[EMPTY_ROW_PLACEHOLDER] === true;
}

/** The section's elements with one placeholder per empty row, before the occupied row it precedes (the packer gives each its own row). */
function elementsWithEmptyRows(section: LayoutSection, elements: readonly LayoutElement[] = section.elements): LayoutElement[] {
  const markers = [...emptyRowsOf(section)];
  if (markers.length === 0) {
    return [...elements];
  }
  const result: LayoutElement[] = [];
  let currentRow = 0;
  for (const cell of packElements(elements, "desktop")) {
    if (cell.row !== currentRow) {
      while (markers.length > 0 && markers[0]! < cell.row) {
        result.push(placeholder());
        markers.shift();
      }
      currentRow = cell.row;
    }
    result.push(cell.element);
  }
  markers.forEach(() => result.push(placeholder()));
  return result;
}

/** The section's elements (and designer-only empty rows) as desktop cells, through the canonical packer. */
export function sectionGrid(section: LayoutSection): GridCell[] {
  let index = 0;
  return packElements(elementsWithEmptyRows(section), "desktop").map((cell) => {
    const empty = isPlaceholder(cell.element);
    const result: GridCell = { key: elementKey(cell.element), element: cell.element, index: empty ? -1 : index++, row: cell.row, column: cell.column, span: cell.span };
    return empty ? { ...result, empty: true } : result;
  });
}

/** The cells of placed elements only (no empty rows). */
export function fieldCells(cells: readonly GridCell[]): GridCell[] {
  return cells.filter((cell) => !cell.empty);
}

export function rowCount(cells: readonly GridCell[]): number {
  return cells.reduce((max, cell) => Math.max(max, cell.row), 0);
}

/** The number of rows that hold at least one field (designer-only empty rows excluded). */
export function occupiedRowCount(cells: readonly GridCell[]): number {
  return new Set(fieldCells(cells).map((cell) => cell.row)).size;
}

/** Whether `row` is a designer-only empty row. */
export function isEmptyRow(cells: readonly GridCell[], row: number): boolean {
  return cells.some((cell) => cell.empty === true && cell.row === row);
}

/** The row a field put "on a new row at the end" goes to: the first of the section's trailing empty rows, otherwise a new row. */
export function appendRow(cells: readonly GridCell[]): number {
  let row = rowCount(cells);
  while (row > 0 && isEmptyRow(cells, row)) {
    row -= 1;
  }
  return row + 1;
}

/** Whether `span` columns starting at (row, column) are inside the grid and free (ignoring the element `ignoreKey`). */
export function cellsFree(cells: readonly GridCell[], row: number, column: number, span: number, ignoreKey?: string): boolean {
  if (column < 1 || column + span - 1 > GRID_COLUMNS) {
    return false;
  }
  return !cells.some((cell) => !cell.empty && cell.key !== ignoreKey && cell.row === row && cell.column < column + span && column < cell.column + cell.span);
}

/** Refusal reason for placing `span` columns at (row, column), or null when it fits. */
export function placementProblem(cells: readonly GridCell[], row: number, column: number, span: number, ignoreKey?: string): GridRefusal | null {
  if (column < 1 || column > GRID_COLUMNS) {
    return "too-wide";
  }
  const occupied = cells.some((cell) => !cell.empty && cell.key !== ignoreKey && cell.row === row && cell.column <= column && column < cell.column + cell.span);
  if (occupied) {
    return "occupied";
  }
  return cellsFree(cells, row, column, span, ignoreKey) ? null : "too-wide";
}

/** The widest span (1..4) that fits at (row, column), or 0 when the cell itself is not free. */
export function maxSpanAt(cells: readonly GridCell[], row: number, column: number, ignoreKey?: string): number {
  let span = 0;
  while (span < GRID_COLUMNS && cellsFree(cells, row, column, span + 1, ignoreKey)) {
    span += 1;
  }
  return span;
}

/** Empty desktop cells of the section's rows, including every cell of an empty row (each is a "+ Add Field" target). */
export function emptyCells(cells: readonly GridCell[]): Array<{ row: number; column: number }> {
  const result: Array<{ row: number; column: number }> = [];
  for (let row = 1; row <= rowCount(cells); row++) {
    for (let column = 1; column <= GRID_COLUMNS; column++) {
      if (cellsFree(cells, row, column, 1)) {
        result.push({ row, column });
      }
    }
  }
  return result;
}

export interface Placement {
  element: LayoutElement;
  row: number;
  column: number;
  /** Elements being moved yield to stationary ones on conflict. */
  moved?: boolean;
}

/**
 * Writes cells back as a section whose packing reproduces them. Cells are ordered by (row, column) — the reading order.
 * Rows may be any numbers (fractions insert between rows); they are renumbered densely and rows without a field disappear,
 * except designer-only empty rows (placeholders), which are kept beside the section — unless a field is placed in the same
 * row, which consumes the empty row. A cell that would extend past column 4 is moved left to fit; a cell that overlaps
 * another in its row is placed on a new row directly below it (the moved element yields; a stationary one keeps its
 * place), so no two elements ever overlap.
 */
export function rebuildSection(section: LayoutSection, placements: readonly Placement[]): LayoutSection {
  const real = placements.filter((placement) => !isPlaceholder(placement.element));
  const occupiedRows = new Set(real.map((placement) => placement.row));
  const emptyRows = [...new Set(placements.filter((placement) => isPlaceholder(placement.element) && !occupiedRows.has(placement.row)).map((placement) => placement.row))];
  const rows = groupRows(real.map((placement, order) => ({ ...placement, order, column: Math.min(Math.max(1, placement.column), GRID_COLUMNS + 1 - spanOf(placement.element)) })));
  // Every output row comes from one requested row (a row split by an overlap stays directly below its source), so an
  // empty row lies below exactly the output rows whose requested row is above it.
  const markers = emptyRows.map((emptyRow) => rows.filter((row) => row[0]!.row < emptyRow).length);
  const elements: LayoutElement[] = [];
  const withPlacement = (element: LayoutElement, column: number, startOnNewRow: boolean) => ({ ...element, layout: { ...element.layout, startOnNewRow, columnStart: column } }) as LayoutElement;
  rows.forEach((row, rowIndex) => {
    row.forEach((placement, position) => {
      const own = placement.element.layout.startOnNewRow;
      let candidate = withPlacement(placement.element, placement.column, position === 0 ? own : false);
      // The first element of a row keeps its own "start on a new row" flag when the canonical packer already starts the
      // row there (so tablet reflow is unchanged for pinned legacy sections); otherwise the flag is what starts the row.
      if (position === 0 && rowIndex > 0 && packElements([...elements, candidate], "desktop").at(-1)!.row !== rowIndex + 1) {
        candidate = withPlacement(placement.element, placement.column, true);
      }
      elements.push(candidate);
    });
  });
  return withEmptyRows({ ...section, elements }, markers);
}

function spanOf(element: LayoutElement): number {
  return Math.min(GRID_COLUMNS, Math.max(1, element.layout.columnSpan));
}

function groupRows<P extends Placement & { order: number }>(placements: P[]): P[][] {
  const byRow = new Map<number, P[]>();
  for (const placement of placements) {
    byRow.set(placement.row, [...(byRow.get(placement.row) ?? []), placement]);
  }
  const pending = [...byRow.entries()].sort((a, b) => a[0] - b[0]).map(([, items]) => items);
  const result: P[][] = [];
  while (pending.length > 0) {
    const items = pending.shift()!.sort((a, b) => a.column - b.column || Number(a.moved ?? false) - Number(b.moved ?? false) || a.order - b.order);
    // Stationary elements claim their cells first; a moved element that collides yields to the row below.
    const accepted: P[] = [];
    const overflow: P[] = [];
    for (const item of [...items].sort((a, b) => Number(a.moved ?? false) - Number(b.moved ?? false) || a.column - b.column || a.order - b.order)) {
      const span = spanOf(item.element);
      if (accepted.some((other) => other.column < item.column + span && item.column < other.column + spanOf(other.element))) {
        overflow.push(item);
      } else {
        accepted.push(item);
      }
    }
    if (accepted.length > 0) {
      result.push(accepted.sort((a, b) => a.column - b.column));
    }
    if (overflow.length > 0) {
      pending.unshift(overflow);
    }
  }
  return result;
}

/** Pins a section's current packed arrangement as explicit positions (visually identical; a no-op for an already pinned section). */
export function pinSection(section: LayoutSection): LayoutSection {
  return rebuildSection(section, stationary(sectionGrid(section)));
}

/** The cells as unmoved placements (empty rows included). */
export function stationary(cells: readonly GridCell[]): Placement[] {
  return cells.map((cell) => ({ element: cell.element, row: cell.row, column: cell.column }));
}

/** An empty-row placement at `row`, for the row and column operations. */
export function emptyRowPlacement(row: number): Placement {
  return { element: placeholder(), row, column: 1 };
}

/** Adds exactly one designer-only empty row of four columns at the end of the section ("+ Add Row"); returns its row number. */
export function addEmptyRow(layout: FormLayout, sectionIndex: number): { layout: FormLayout; row: number } {
  const section = layout.sections[sectionIndex];
  if (!section) {
    return { layout, row: 0 };
  }
  const occupied = occupiedRowCount(sectionGrid(section));
  const next = withEmptyRows(section, [...emptyRowsOf(section), occupied]);
  return { layout: replaceSection(layout, sectionIndex, next), row: rowCount(sectionGrid(next)) };
}

/**
 * Carries the designer-only empty rows of `from` over to `to` (the layout the server stored after a save), for sections
 * with the same key and the same occupied rows, so the rows stay usable after Save draft. They are never saved.
 */
export function carryEmptyRows(from: FormLayout, to: FormLayout): FormLayout {
  const bySection = new Map(from.sections.map((section) => [section.sectionKey, section]));
  return {
    ...to,
    sections: to.sections.map((section) => {
      const previous = bySection.get(section.sectionKey);
      const rows = previous ? ((previous as DesignerSection)[EMPTY_ROWS] ?? []) : [];
      if (!previous || rows.length === 0 || occupiedRowCount(sectionGrid(previous)) !== occupiedRowCount(sectionGrid(section))) {
        return section;
      }
      return withEmptyRows(section, rows);
    }),
  };
}

/** The designer-only empty rows of every section, for the undo/redo identity of the designer state (with the serialized layout). */
export function emptyRowsSignature(layout: FormLayout): string {
  return JSON.stringify(layout.sections.map((section) => (section as DesignerSection)[EMPTY_ROWS] ?? []));
}

export function replaceSection(layout: FormLayout, index: number, section: LayoutSection): FormLayout {
  const sections = [...layout.sections];
  sections[index] = section;
  return { ...layout, sections };
}

function refuse(layout: FormLayout, reason: GridRefusal): GridEditResult {
  return { ok: false, layout, reason };
}

/** Places a new element at an exact desktop cell. A row beyond the last occupied row starts a new row at the end. */
export function placeAtCell(layout: FormLayout, sectionIndex: number, element: LayoutElement, row: number, column: number): GridEditResult {
  const section = layout.sections[sectionIndex];
  if (!section) {
    return refuse(layout, "not-found");
  }
  if (findElement(layout, elementKey(element))) {
    return refuse(layout, "already-placed");
  }
  const cells = sectionGrid(section);
  const problem = placementProblem(cells, row, column, spanOf(element));
  if (problem) {
    return refuse(layout, problem);
  }
  const placements = [...cells.map((cell) => ({ element: cell.element, row: cell.row, column: cell.column })), { element, row, column, moved: true }];
  return { ok: true, layout: replaceSection(layout, sectionIndex, rebuildSection(section, placements)) };
}

/** Moves a placed element to an exact desktop cell of the same or another (same-scope) section. The old cell becomes empty. */
export function moveToCell(layout: FormLayout, key: string, targetSectionIndex: number, row: number, column: number): GridEditResult {
  const source = findElement(layout, key);
  const target = layout.sections[targetSectionIndex];
  if (!source || !target) {
    return refuse(layout, "not-found");
  }
  if (!sameScope(layout.sections[source.sectionIndex]!, target)) {
    return refuse(layout, "different-scope");
  }
  const span = spanOf(source.element);
  if (source.sectionIndex === targetSectionIndex) {
    const cells = sectionGrid(target);
    const problem = placementProblem(cells, row, column, span, key);
    if (problem) {
      return refuse(layout, problem);
    }
    const placements = cells.map((cell) => (cell.key === key ? { element: cell.element, row, column, moved: true } : { element: cell.element, row: cell.row, column: cell.column }));
    return { ok: true, layout: replaceSection(layout, targetSectionIndex, rebuildSection(target, placements)) };
  }
  const targetCells = sectionGrid(target);
  const problem = placementProblem(targetCells, row, column, span);
  if (problem) {
    return refuse(layout, problem);
  }
  const sourceSection = layout.sections[source.sectionIndex]!;
  const remaining = sectionGrid(sourceSection).filter((cell) => cell.key !== key);
  let next = replaceSection(layout, source.sectionIndex, rebuildSection(sourceSection, remaining.map((cell) => ({ element: cell.element, row: cell.row, column: cell.column }))));
  next = replaceSection(next, targetSectionIndex, rebuildSection(target, [...targetCells.map((cell) => ({ element: cell.element, row: cell.row, column: cell.column })), { element: source.element, row, column, moved: true }]));
  return { ok: true, layout: next };
}

/** Moves an element to a new row at the end (`atStart` false) or start of a section, keeping its column where it fits. */
export function moveToNewRow(layout: FormLayout, key: string, targetSectionIndex: number, atStart = false): GridEditResult {
  const source = findElement(layout, key);
  const target = layout.sections[targetSectionIndex];
  if (!source || !target) {
    return refuse(layout, "not-found");
  }
  const cells = sectionGrid(target).filter((cell) => cell.key !== key);
  const column = explicitColumn(source.element.layout, spanOf(source.element)) ?? 1;
  const row = atStart ? 0 : appendRow(cells);
  if (source.sectionIndex !== targetSectionIndex) {
    return moveToCell(layout, key, targetSectionIndex, row, column);
  }
  const placements = [...cells.map((cell) => ({ element: cell.element, row: cell.row, column: cell.column })), { element: source.element, row, column, moved: true }];
  return { ok: true, layout: replaceSection(layout, targetSectionIndex, rebuildSection(target, placements)) };
}

/**
 * Exchanges the positions of two placed elements (drag onto a field, "Move earlier/later", Alt+ArrowUp/Down). With equal
 * widths this is an exact swap; when a wider element would overlap a third one, that wider element moves to a new row
 * directly below its target row and the third element stays where it is.
 */
export function swapElements(layout: FormLayout, keyA: string, keyB: string): GridEditResult {
  const a = findElement(layout, keyA);
  const b = findElement(layout, keyB);
  if (!a || !b || keyA === keyB) {
    return refuse(layout, "not-found");
  }
  const sectionA = layout.sections[a.sectionIndex]!;
  const sectionB = layout.sections[b.sectionIndex]!;
  if (!sameScope(sectionA, sectionB)) {
    return refuse(layout, "different-scope");
  }
  const cellsA = sectionGrid(sectionA);
  const cellA = cellsA.find((cell) => cell.key === keyA)!;
  if (a.sectionIndex === b.sectionIndex) {
    const cellB = cellsA.find((cell) => cell.key === keyB)!;
    const placements = cellsA.map((cell) =>
      cell.key === keyA ? { element: cell.element, row: cellB.row, column: cellB.column, moved: true } : cell.key === keyB ? { element: cell.element, row: cellA.row, column: cellA.column, moved: true } : { element: cell.element, row: cell.row, column: cell.column },
    );
    return { ok: true, layout: replaceSection(layout, a.sectionIndex, rebuildSection(sectionA, placements)) };
  }
  const cellsB = sectionGrid(sectionB);
  const cellB = cellsB.find((cell) => cell.key === keyB)!;
  let next = replaceSection(layout, a.sectionIndex, rebuildSection(sectionA, cellsA.map((cell) => (cell.key === keyA ? { element: b.element, row: cellA.row, column: cellA.column, moved: true } : { element: cell.element, row: cell.row, column: cell.column }))));
  next = replaceSection(next, b.sectionIndex, rebuildSection(sectionB, cellsB.map((cell) => (cell.key === keyB ? { element: a.element, row: cellB.row, column: cellB.column, moved: true } : { element: cell.element, row: cell.row, column: cell.column }))));
  return { ok: true, layout: next };
}

/** The neighbouring element in reading order within the section (for "Move earlier/later"), or null. */
export function neighbourKey(layout: FormLayout, key: string, direction: -1 | 1): string | null {
  const position = findElement(layout, key);
  if (!position) {
    return null;
  }
  const cells = fieldCells(sectionGrid(layout.sections[position.sectionIndex]!));
  const at = cells.findIndex((cell) => cell.key === key);
  return cells[at + direction]?.key ?? null;
}

/** One column left or right within the element's row (Alt+ArrowLeft/Right, "Move one column left/right"). */
export function moveByColumn(layout: FormLayout, key: string, delta: -1 | 1): GridEditResult {
  const position = findElement(layout, key);
  if (!position) {
    return refuse(layout, "not-found");
  }
  const cell = sectionGrid(layout.sections[position.sectionIndex]!).find((candidate) => candidate.key === key)!;
  return moveToCell(layout, key, position.sectionIndex, cell.row, cell.column + delta);
}

/** Changes an element's width in place. Refused when the wider element would overlap a neighbour or leave the grid. */
export function setSpan(layout: FormLayout, key: string, columnSpan: 1 | 2 | 3 | 4): GridEditResult {
  const position = findElement(layout, key);
  if (!position) {
    return refuse(layout, "not-found");
  }
  const section = layout.sections[position.sectionIndex]!;
  const cells = sectionGrid(section);
  const cell = cells.find((candidate) => candidate.key === key)!;
  if (!cellsFree(cells, cell.row, cell.column, columnSpan, key)) {
    return refuse(layout, "too-wide");
  }
  const placements = cells.map((candidate) => ({ element: candidate.key === key ? ({ ...candidate.element, layout: { ...candidate.element.layout, columnSpan } } as LayoutElement) : candidate.element, row: candidate.row, column: candidate.column }));
  return { ok: true, layout: replaceSection(layout, position.sectionIndex, rebuildSection(section, placements)) };
}

/** The widest span the element can have in its current position. */
export function maxSpanFor(layout: FormLayout, key: string): number {
  const position = findElement(layout, key);
  if (!position) {
    return 0;
  }
  const cells = sectionGrid(layout.sections[position.sectionIndex]!);
  const cell = cells.find((candidate) => candidate.key === key)!;
  return maxSpanAt(cells, cell.row, cell.column, key);
}

/**
 * "Start on a new row": on splits the element's row before it (the element and the fields after it in that row move to a
 * new row below, keeping their columns); off joins it to the row above when its cells there are free, otherwise nothing moves.
 */
export function setStartOnNewRow(layout: FormLayout, key: string, startOnNewRow: boolean): FormLayout {
  const position = findElement(layout, key);
  if (!position) {
    return layout;
  }
  const section = pinSection(layout.sections[position.sectionIndex]!);
  const elements = elementsWithEmptyRows(section).map((element) => (!isPlaceholder(element) && elementKey(element) === key ? ({ ...element, layout: { ...element.layout, startOnNewRow } } as LayoutElement) : element));
  const repacked = packElements(elements, "desktop");
  return replaceSection(layout, position.sectionIndex, rebuildSection(section, repacked.map((cell) => ({ element: cell.element, row: cell.row, column: cell.column }))));
}

/** Removes a placed element; every other element keeps its position (the section is pinned first). */
export function removeFromGrid(layout: FormLayout, key: string): FormLayout {
  const position = findElement(layout, key);
  if (!position) {
    return layout;
  }
  const section = layout.sections[position.sectionIndex]!;
  const remaining = sectionGrid(section).filter((cell) => cell.key !== key);
  return replaceSection(layout, position.sectionIndex, rebuildSection(section, remaining.map((cell) => ({ element: cell.element, row: cell.row, column: cell.column }))));
}

/** The first empty cell after (row, column) in reading order inside the occupied rows, or null (for "+ Add Column"). */
export function nextEmptyCell(cells: readonly GridCell[], row: number, column: number): { row: number; column: number } | null {
  return emptyCells(cells).find((cell) => cell.row > row || (cell.row === row && cell.column > column)) ?? null;
}

/** Human-readable refusal text for status announcements and inspector notices. */
export function refusalMessage(reason: GridRefusal): string {
  switch (reason) {
    case "occupied":
      return "That cell is already occupied. Choose an empty cell, or move the other field first.";
    case "too-wide":
      return "There is not enough free space in that row for this width. Choose a narrower width or free the neighbouring cells.";
    case "already-placed":
      return "That field is already placed in this form.";
    case "different-scope":
      return "Fields cannot move between a section that allows multiple entries and other sections.";
    default:
      return "That placement is no longer available.";
  }
}

/**
 * FD-01 rollback/compatibility safeguard: the keys of elements whose explicit column (`columnStart`) was sent but is not in
 * the layout the server stored. A server that predates FD-01 accepts the property and silently drops it on save; the
 * designer compares the saved draft with what it sent and refuses to treat such a save as a success.
 */
export function droppedPositions(sent: FormLayout, saved: FormLayout): string[] {
  const savedColumns = new Map(saved.sections.flatMap((section) => section.elements.map((element) => [elementKey(element), element.layout.columnStart] as const)));
  return sent.sections.flatMap((section) => section.elements.filter((element) => element.layout.columnStart !== undefined && savedColumns.get(elementKey(element)) !== element.layout.columnStart).map(elementKey));
}

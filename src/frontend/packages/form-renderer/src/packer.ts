import type { LayoutElement } from "./layout";

export type Breakpoint = "phone" | "tablet" | "desktop";

export interface PackedCell<T = LayoutElement> {
  element: T;
  /** 1-based start column on the visible grid. */
  column: number;
  span: number;
  row: number;
}

/** The placement properties the packer reads (FD-01: `columnStart` is optional; legacy elements omit it). */
export interface PackablePlacement {
  columnSpan: number;
  startOnNewRow: boolean;
  columnStart?: number | undefined;
}

export function visibleColumns(breakpoint: Breakpoint): 1 | 2 | 4 {
  return breakpoint === "phone" ? 1 : breakpoint === "tablet" ? 2 : 4;
}

/** Tablet maps desktop spans conservatively: 1–2 logical units → 1 visible column, 3–4 → 2 (CLAUDE.md §4I). */
export function effectiveSpan(columnSpan: number, breakpoint: Breakpoint): number {
  if (breakpoint === "phone") {
    return 1;
  }
  if (breakpoint === "tablet") {
    return columnSpan >= 3 ? 2 : 1;
  }
  return Math.min(4, Math.max(1, columnSpan));
}

/**
 * The explicit desktop start column of a placement, or null when it has none. A malformed value (the server and the
 * layout schema reject these, so it only arises from a hand-edited document) is clamped deterministically so the element
 * still fits the four-column grid instead of overflowing it.
 */
export function explicitColumn(placement: PackablePlacement, span: number): number | null {
  const start = placement.columnStart;
  if (typeof start !== "number" || !Number.isFinite(start)) {
    return null;
  }
  return Math.min(Math.max(1, Math.trunc(start)), 5 - span);
}

/**
 * Deterministic ordered packing (no auto-placement back-filling, the one canonical interpretation used by the designer
 * canvas, Preview and the published renderer). Elements are laid out in declared order, which is also the tab and
 * screen-reader order at every breakpoint.
 *
 * - An element starts the next row when it requests a new row on a multi-column grid, or when it does not fit the
 *   remaining row width.
 * - Desktop only (FD-01): an element with an explicit `columnStart` is placed at that column, leaving the cells before it
 *   empty. If that column is already behind the current position (occupied, or an earlier/duplicate column), the element
 *   starts the next row at that column, so two elements never overlap and nothing is reordered.
 * - Tablet and phone ignore `columnStart` and reflow in source order without preserving empty desktop cells.
 */
export function packElements<T extends { layout: PackablePlacement }>(elements: readonly T[], breakpoint: Breakpoint): PackedCell<T>[] {
  const columns = visibleColumns(breakpoint);
  const cells: PackedCell<T>[] = [];
  let row = 1;
  let nextColumn = 1;
  for (const element of elements) {
    const span = effectiveSpan(element.layout.columnSpan, breakpoint);
    const explicit = breakpoint === "desktop" ? explicitColumn(element.layout, span) : null;
    const forceBreak = breakpoint !== "phone" && element.layout.startOnNewRow && nextColumn !== 1;
    if (explicit !== null) {
      if (forceBreak || explicit < nextColumn) {
        row += 1;
      }
      nextColumn = explicit;
    } else if (forceBreak || nextColumn + span - 1 > columns) {
      row += 1;
      nextColumn = 1;
    }
    cells.push({ element, column: nextColumn, span, row });
    nextColumn += span;
    if (nextColumn > columns) {
      row += 1;
      nextColumn = 1;
    }
  }
  return cells;
}

/**
 * Packing for rendering with hidden elements (FD-01). `isShown(element)` false means the element is not drawn.
 *
 * On desktop, a hidden element that has an explicit `columnStart` keeps its cell (the cell is left empty) so the fields
 * around it do not move when a form condition hides it; rows that end up with no shown element are closed up. A hidden
 * element without `columnStart` (a legacy layout) is removed before packing, exactly as before FD-01, so existing forms
 * reflow as they always did. Tablet and phone always remove hidden elements before packing (source-order reflow).
 *
 * Authorization never relies on this: elements the viewer may not see are removed by the server before the layout is
 * returned, and the server strips `columnStart` from partial projections so no reserved position can reveal them.
 */
export function packVisibleElements<T extends { layout: PackablePlacement }>(elements: readonly T[], breakpoint: Breakpoint, isShown: (element: T) => boolean): PackedCell<T>[] {
  if (breakpoint !== "desktop") {
    return packElements(elements.filter(isShown), breakpoint);
  }
  const reserved = elements.filter((element) => isShown(element) || element.layout.columnStart !== undefined);
  const shown = packElements(reserved, breakpoint).filter((cell) => isShown(cell.element));
  const rows = [...new Set(shown.map((cell) => cell.row))].sort((a, b) => a - b);
  const dense = new Map(rows.map((row, index) => [row, index + 1]));
  return shown.map((cell) => ({ ...cell, row: dense.get(cell.row)! }));
}

/** Breakpoint from a measured container width (container queries), falling back to viewport semantics. */
export function breakpointForWidth(widthPx: number): Breakpoint {
  return widthPx < 640 ? "phone" : widthPx < 1024 ? "tablet" : "desktop";
}

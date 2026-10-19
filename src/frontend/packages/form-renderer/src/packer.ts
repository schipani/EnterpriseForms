import type { LayoutElement } from "./layout";

export type Breakpoint = "phone" | "tablet" | "desktop";

export interface PackedCell<T = LayoutElement> {
  element: T;
  /** 1-based start column on the visible grid. */
  column: number;
  span: number;
  row: number;
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
 * Deterministic ordered packing (no auto-placement back-filling): elements flow in declared order; an element that
 * does not fit the remaining row width, or that requests a new row on a multi-column grid, starts the next row.
 * Source order, tab order and visual order are therefore identical at every breakpoint.
 */
export function packElements<T extends { layout: { columnSpan: number; startOnNewRow: boolean } }>(elements: readonly T[], breakpoint: Breakpoint): PackedCell<T>[] {
  const columns = visibleColumns(breakpoint);
  const cells: PackedCell<T>[] = [];
  let row = 1;
  let nextColumn = 1;
  for (const element of elements) {
    const span = effectiveSpan(element.layout.columnSpan, breakpoint);
    const forceBreak = breakpoint !== "phone" && element.layout.startOnNewRow && nextColumn !== 1;
    if (forceBreak || nextColumn + span - 1 > columns) {
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

/** Breakpoint from a measured container width (container queries), falling back to viewport semantics. */
export function breakpointForWidth(widthPx: number): Breakpoint {
  return widthPx < 640 ? "phone" : widthPx < 1024 ? "tablet" : "desktop";
}

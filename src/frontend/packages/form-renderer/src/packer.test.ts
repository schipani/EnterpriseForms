import { describe, expect, it } from "vitest";
import { effectiveSpan, explicitColumn, packElements, packVisibleElements } from "./packer";

const el = (columnSpan: number, startOnNewRow = false) => ({ layout: { columnSpan, startOnNewRow } });

describe("deterministic four-column packer (CLAUDE.md §4I)", () => {
  it("packs two half-width controls on one desktop row and breaks before a full-width new-row element", () => {
    const cells = packElements([el(2), el(2), el(4, true)], "desktop");
    expect(cells.map((c) => [c.row, c.column, c.span])).toEqual([
      [1, 1, 2],
      [1, 3, 2],
      [2, 1, 4],
    ]);
  });

  it("never back-fills an earlier gap (ordered flow, not auto-placement)", () => {
    const cells = packElements([el(3), el(2), el(1)], "desktop");
    expect(cells.map((c) => [c.row, c.column])).toEqual([
      [1, 1],
      [2, 1],
      [2, 3],
    ]);
  });

  it("honours an explicit row break even when space remains", () => {
    const cells = packElements([el(1), el(1, true)], "desktop");
    expect(cells.map((c) => c.row)).toEqual([1, 2]);
  });

  it("stacks everything on phone and maps spans conservatively on tablet", () => {
    expect(packElements([el(1), el(2), el(4)], "phone").map((c) => [c.row, c.span])).toEqual([
      [1, 1],
      [2, 1],
      [3, 1],
    ]);
    expect([1, 2, 3, 4].map((span) => effectiveSpan(span, "tablet"))).toEqual([1, 1, 2, 2]);
    const tablet = packElements([el(2), el(2), el(3)], "tablet");
    expect(tablet.map((c) => [c.row, c.column, c.span])).toEqual([
      [1, 1, 1],
      [1, 2, 1],
      [2, 1, 2],
    ]);
  });
});

// FD-01 — explicit desktop column placement (columnStart), legacy compatibility and hidden-field reservation.
const at = (columnStart: number, columnSpan = 1, startOnNewRow = false) => ({ layout: { columnSpan, startOnNewRow, columnStart } });
const rc = (cells: { row: number; column: number; span: number }[]) => cells.map((c) => [c.row, c.column, c.span]);

describe("FD-01 explicit column placement", () => {
  it("FD-01-01: a field placed in column 3 stays in column 3 with empty leading cells", () => {
    expect(rc(packElements([at(3)], "desktop"))).toEqual([[1, 3, 1]]);
  });

  it("FD-01-02: multiple rows with gaps keep every position (no compaction into earlier rows)", () => {
    const cells = packElements([at(1), at(4), at(3, 1, true), at(2, 1, true)], "desktop");
    expect(rc(cells)).toEqual([
      [1, 1, 1],
      [1, 4, 1],
      [2, 3, 1],
      [3, 2, 1],
    ]);
  });

  it("an explicit column behind the current position starts the next row (no overlap, no reordering)", () => {
    expect(rc(packElements([at(2, 2), at(3)], "desktop"))).toEqual([
      [1, 2, 2],
      [2, 3, 1],
    ]);
  });

  it("FD-01-14: duplicate and out-of-range positions resolve deterministically", () => {
    expect(rc(packElements([at(3), at(3)], "desktop"))).toEqual([
      [1, 3, 1],
      [2, 3, 1],
    ]);
    // A malformed start that would overflow the grid is clamped so the element still fits (server and schema reject these).
    expect(rc(packElements([at(4, 3)], "desktop"))).toEqual([[1, 2, 3]]);
    expect(rc(packElements([at(0)], "desktop"))).toEqual([[1, 1, 1]]);
    expect(explicitColumn({ columnSpan: 2, startOnNewRow: false }, 2)).toBeNull();
  });

  it("FD-01-06: spans 1-4 at explicit columns stay inside the row", () => {
    expect(rc(packElements([at(1, 1), at(2, 3)], "desktop"))).toEqual([
      [1, 1, 1],
      [1, 2, 3],
    ]);
    expect(rc(packElements([at(1, 2), at(3, 2)], "desktop"))).toEqual([
      [1, 1, 2],
      [1, 3, 2],
    ]);
    expect(rc(packElements([at(1, 4), at(1, 4)], "desktop"))).toEqual([
      [1, 1, 4],
      [2, 1, 4],
    ]);
  });

  it("FD-01-11: legacy elements without columnStart pack exactly as before, also when mixed with explicit ones", () => {
    expect(rc(packElements([el(2), el(2), el(4, true)], "desktop"))).toEqual([
      [1, 1, 2],
      [1, 3, 2],
      [2, 1, 4],
    ]);
    expect(rc(packElements([at(3), el(1)], "desktop"))).toEqual([
      [1, 3, 1],
      [1, 4, 1],
    ]);
  });

  it("FD-01-10: tablet and phone ignore desktop columns and reflow in source order without blank cells", () => {
    const elements = [at(3), at(4), at(2, 1, true)];
    expect(rc(packElements(elements, "phone"))).toEqual([
      [1, 1, 1],
      [2, 1, 1],
      [3, 1, 1],
    ]);
    expect(rc(packElements(elements, "tablet"))).toEqual([
      [1, 1, 1],
      [1, 2, 1],
      [2, 1, 1],
    ]);
  });
});

describe("FD-01-12 hidden fields (packVisibleElements)", () => {
  type E = { id: string; layout: { columnSpan: number; startOnNewRow: boolean; columnStart?: number } };
  const e = (id: string, columnStart: number | undefined, startOnNewRow = false, columnSpan = 1): E => ({ id, layout: columnStart === undefined ? { columnSpan, startOnNewRow } : { columnSpan, startOnNewRow, columnStart } });
  const ids = (cells: { element: E; row: number; column: number }[]) => cells.map((c) => [c.element.id, c.row, c.column]);

  it("a hidden field with an explicit column keeps its empty cell on desktop; neighbours do not move", () => {
    const elements = [e("a", 1), e("b", 2), e("c", 3)];
    expect(ids(packVisibleElements(elements, "desktop", (x) => x.id !== "b"))).toEqual([
      ["a", 1, 1],
      ["c", 1, 3],
    ]);
  });

  it("rows that become empty close up, and later rows keep their columns", () => {
    const elements = [e("a", 1), e("b", 2, true), e("c", 4, true)];
    expect(ids(packVisibleElements(elements, "desktop", (x) => x.id !== "b"))).toEqual([
      ["a", 1, 1],
      ["c", 2, 4],
    ]);
  });

  it("a hidden legacy field (no columnStart) is removed before packing, exactly as before FD-01", () => {
    const elements = [e("a", undefined), e("b", undefined), e("c", undefined)];
    expect(ids(packVisibleElements(elements, "desktop", (x) => x.id !== "b"))).toEqual([
      ["a", 1, 1],
      ["c", 1, 2],
    ]);
  });

  it("tablet and phone reflow without reserving hidden cells", () => {
    const elements = [e("a", 1), e("b", 2), e("c", 3)];
    expect(ids(packVisibleElements(elements, "tablet", (x) => x.id !== "a"))).toEqual([
      ["b", 1, 1],
      ["c", 1, 2],
    ]);
  });
});

import { describe, expect, it } from "vitest";
import { effectiveSpan, packElements } from "./packer";

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

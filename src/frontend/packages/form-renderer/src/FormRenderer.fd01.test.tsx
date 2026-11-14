import { render } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { FormRenderer } from "./FormRenderer";
import { emptyLayout, parseLayout, serializeLayout, type FormLayout, type LayoutElement } from "./layout";
import type { RuleCondition } from "./rules";
import { validateValues, type RendererField } from "./validation";

/** FD-01 — the published renderer (also used by Preview) draws explicit desktop positions; rules, validation and values are unchanged. */

const id = (n: number) => `0199a2b3-c4d5-7e6f-8123-${n.toString().padStart(12, "0")}`;
const fields: RendererField[] = [
  { fieldDefinitionId: id(1), fieldKey: "toggle", fieldDisplayName: "Toggle", fieldDataType: "Boolean", options: [] },
  { fieldDefinitionId: id(2), fieldKey: "secret_note", fieldDisplayName: "Secret note", fieldDataType: "String", options: [] },
  { fieldDefinitionId: id(3), fieldKey: "given_name", fieldDisplayName: "Given name", fieldDataType: "String", options: [] },
  { fieldDefinitionId: id(4), fieldKey: "notes", fieldDisplayName: "Notes", fieldDataType: "String", options: [] },
];
const shownWhenToggle: RuleCondition = { op: "equals", fieldDefinitionId: id(1), value: "true" };
const element = (n: number, label: string, placement: LayoutElement["layout"], extra: Partial<LayoutElement> = {}): LayoutElement =>
  ({ kind: "field", fieldDefinitionId: id(n), control: n === 1 ? "checkbox" : "text", label, required: false, layout: placement, ...extra }) as LayoutElement;

function layoutWith(positions: boolean): FormLayout {
  const at = (columnStart: number, startOnNewRow = false) => (positions ? { columnSpan: 1, startOnNewRow, columnStart } : { columnSpan: 1, startOnNewRow });
  return {
    ...emptyLayout("Form"),
    sections: [
      {
        sectionKey: "main",
        title: "Main",
        gridColumns: 4,
        elements: [
          element(1, "Toggle", at(1)),
          element(2, "Secret note", at(2), { visibleWhen: shownWhenToggle, required: true } as Partial<LayoutElement>),
          element(3, "Given name", at(3), { required: true } as Partial<LayoutElement>),
          element(4, "Notes", at(4, true)),
        ],
      },
    ],
  };
}

function cellOf(fieldKey: string): { column: string; row: string } {
  const cell = document.querySelector<HTMLElement>(`[data-field-key="${fieldKey}"]`);
  if (!cell) {
    throw new Error(`no cell for ${fieldKey}`);
  }
  return { column: cell.style.gridColumn, row: cell.style.gridRow };
}

describe("FD-01 renderer placement", () => {
  it("FD-01-09: draws every field at its explicit desktop row and column, including leading empty cells", () => {
    render(<FormRenderer layout={layoutWith(true)} fields={fields} values={{ toggle: "true" }} breakpoint="desktop" />);
    expect(cellOf("toggle")).toEqual({ column: "1 / span 1", row: "1" });
    expect(cellOf("secret_note")).toEqual({ column: "2 / span 1", row: "1" });
    expect(cellOf("given_name")).toEqual({ column: "3 / span 1", row: "1" });
    expect(cellOf("notes")).toEqual({ column: "4 / span 1", row: "2" });
  });

  it("FD-01-12: a field hidden by a form condition leaves its cell empty; the other fields keep their positions", () => {
    render(<FormRenderer layout={layoutWith(true)} fields={fields} values={{ toggle: "false" }} breakpoint="desktop" />);
    expect(document.querySelector('[data-field-key="secret_note"]')).toBeNull();
    expect(cellOf("given_name")).toEqual({ column: "3 / span 1", row: "1" });
    expect(cellOf("notes")).toEqual({ column: "4 / span 1", row: "2" });
  });

  it("FD-01-12/11: a legacy layout (no columnStart) keeps its pre-FD-01 reflow when a condition hides a field", () => {
    render(<FormRenderer layout={layoutWith(false)} fields={fields} values={{ toggle: "false" }} breakpoint="desktop" />);
    expect(cellOf("toggle")).toEqual({ column: "1 / span 1", row: "1" });
    expect(cellOf("given_name")).toEqual({ column: "2 / span 1", row: "1" });
  });

  it("FD-01-10: phone and tablet reflow in source order; tab order equals the DOM (source) order", () => {
    const { unmount } = render(<FormRenderer layout={layoutWith(true)} fields={fields} values={{ toggle: "true" }} breakpoint="phone" />);
    expect(["toggle", "secret_note", "given_name", "notes"].map((key) => cellOf(key))).toEqual([
      { column: "1 / span 1", row: "1" },
      { column: "1 / span 1", row: "2" },
      { column: "1 / span 1", row: "3" },
      { column: "1 / span 1", row: "4" },
    ]);
    unmount();
    render(<FormRenderer layout={layoutWith(true)} fields={fields} values={{ toggle: "true" }} breakpoint="tablet" />);
    expect(cellOf("given_name").column).toBe("1 / span 1");
    expect(Array.from(document.querySelectorAll("[data-field-key]"), (node) => node.getAttribute("data-field-key"))).toEqual(["toggle", "secret_note", "given_name", "notes"]);
  });

  it("FD-01-13: validation and the values reported are identical with and without explicit positions", () => {
    for (const values of [{}, { toggle: "true" }, { toggle: "true", secret_note: "x", given_name: "Ava" }]) {
      expect(validateValues(layoutWith(true), fields, values)).toEqual(validateValues(layoutWith(false), fields, values));
    }
    expect(validateValues(layoutWith(true), fields, { toggle: "true" })).toEqual({ secret_note: "Secret note is required", given_name: "Given name is required" });
  });

  it("round-trips columnStart through the shared layout schema and rejects positions outside the grid", () => {
    const layout = layoutWith(true);
    expect(parseLayout(serializeLayout(layout)).sections[0]!.elements.map((e) => e.layout.columnStart)).toEqual([1, 2, 3, 4]);
    const tooWide = { ...layout, sections: [{ ...layout.sections[0]!, elements: [element(1, "Toggle", { columnSpan: 2, startOnNewRow: false, columnStart: 4 })] }] };
    expect(() => parseLayout(JSON.stringify(tooWide))).toThrow(/beyond the four-column grid/);
    const outOfRange = { ...layout, sections: [{ ...layout.sections[0]!, elements: [element(1, "Toggle", { columnSpan: 1, startOnNewRow: false, columnStart: 5 })] }] };
    expect(() => parseLayout(JSON.stringify(outOfRange))).toThrow();
  });
});

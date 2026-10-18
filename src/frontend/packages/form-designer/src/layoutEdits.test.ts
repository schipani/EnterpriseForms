import { emptyLayout, type LayoutElement } from "@enterpriseforms/form-renderer";
import { describe, expect, it } from "vitest";
import { appendElement, moveElement, placedIds, removeElement, updateElement } from "./layoutEdits";

const a: LayoutElement = { kind: "field", fieldDefinitionId: "0199a2b3-c4d5-7e6f-8123-456789abcdef", control: "text", label: "A", required: false, layout: { columnSpan: 4, startOnNewRow: false } };
const b: LayoutElement = { kind: "field", fieldDefinitionId: "0199a2b3-c4d5-7e6f-8123-456789abcdf0", control: "date", label: "B", required: true, layout: { columnSpan: 2, startOnNewRow: false } };
const rel: LayoutElement = { kind: "relatedFormList", formRelationshipDefinitionId: "0199a2b3-c4d5-7e6f-8123-456789abcdf1", label: "Addresses", layout: { columnSpan: 4, startOnNewRow: true }, summaryFieldKeys: [] };

describe("designer layout edits are immutable and keep stable ids", () => {
  it("appends, reorders and removes without mutating the input", () => {
    const base = emptyLayout("Accused");
    const withAll = appendElement(appendElement(appendElement(base, 0, a), 0, b), 0, rel);
    expect(base.sections[0]!.elements).toHaveLength(0);
    const moved = moveElement(withAll, 0, 2, 0);
    expect(moved.sections[0]!.elements.map((e) => e.kind)).toEqual(["relatedFormList", "field", "field"]);
    expect(removeElement(moved, 0, 0).sections[0]!.elements).toEqual([a, b]);
    expect(placedIds(withAll).fieldIds.size).toBe(2);
    expect(placedIds(withAll).relationshipIds.size).toBe(1);
  });

  it("updates spans and row breaks in place", () => {
    const layout = appendElement(emptyLayout("x"), 0, a);
    const updated = updateElement(layout, 0, 0, (element) => ({ ...element, layout: { columnSpan: 1, startOnNewRow: true } }));
    expect(updated.sections[0]!.elements[0]!.layout).toEqual({ columnSpan: 1, startOnNewRow: true });
    expect(layout.sections[0]!.elements[0]!.layout.columnSpan).toBe(4);
  });
});

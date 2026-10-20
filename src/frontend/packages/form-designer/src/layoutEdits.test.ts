import { emptyLayout, type LayoutElement } from "@enterpriseforms/form-renderer";
import { describe, expect, it } from "vitest";
import { appendElement, duplicateSection, elementKey, findElement, insertElement, insertSection, moveElement, moveElementTo, moveSection, newSectionKey, placedIds, removeElement, removeSection, updateElement, updateSection } from "./layoutEdits";

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

describe("sections created by + Add Row contain their own placements (C1 designer rebuild)", () => {
  const base = appendElement(appendElement(emptyLayout("Accused"), 0, a), 0, b);

  it("inserts a titled empty section after the selected one with a server-valid unique key", () => {
    const key = newSectionKey(base, () => "1a2b3c4d5e");
    expect(key).toMatch(/^[a-z][a-z0-9_]{0,63}$/);
    const withRow = insertSection(base, 0, key, "New section");
    expect(withRow.sections.map((s) => s.title)).toEqual(["Accused", "New section"]);
    expect(withRow.sections[1]!.elements).toEqual([]);
    expect(withRow.sections[1]!.gridColumns).toBe(4);
    // Duplicate keys are rejected; a key collision makes newSectionKey try again.
    expect(insertSection(withRow, 0, key, "Again")).toBe(withRow);
    let calls = 0;
    expect(newSectionKey(withRow, () => (calls++ === 0 ? "1a2b3c4d5e" : "ffff"))).toBe("section_ffff");
  });

  it("places a field at an exact cell, moves fields between sections and never duplicates a placement", () => {
    const withRow = insertSection(base, null, "section_contact", "Contact Information");
    const placed = insertElement(withRow, 1, 0, rel);
    expect(placed.sections[1]!.elements).toEqual([rel]);
    expect(insertElement(placed, 0, 0, rel)).toBe(placed); // already placed elsewhere
    const moved = moveElementTo(placed, elementKey(a), 1, 0);
    expect(moved.sections[0]!.elements).toEqual([b]);
    expect(moved.sections[1]!.elements).toEqual([a, rel]);
    expect(findElement(moved, elementKey(a))).toMatchObject({ sectionIndex: 1, index: 0 });
    // Same-section insertion index counts before removal: moving b to the end of its own section is a no-op.
    expect(moveElementTo(moved, elementKey(b), 0, 1)).toBe(moved);
    const reordered = moveElementTo(moved, elementKey(rel), 1, 0);
    expect(reordered.sections[1]!.elements).toEqual([rel, a]);
    expect(placedIds(reordered).fieldIds.size).toBe(2);
  });

  it("renames, describes, reorders and duplicates sections", () => {
    const layout = insertSection(base, null, "section_contact", "New section");
    const renamed = updateSection(layout, 1, { title: "Contact Information", description: "How to reach them" });
    expect(renamed.sections[1]).toMatchObject({ title: "Contact Information", description: "How to reach them" });
    expect("description" in updateSection(renamed, 1, { description: "  " }).sections[1]!).toBe(false);
    expect(moveSection(renamed, 1, 0).sections.map((s) => s.sectionKey)).toEqual(["section_contact", "main"]);
    const copy = duplicateSection(appendElement(renamed, 1, rel), 1, "section_copy");
    expect(copy.sections[2]).toMatchObject({ sectionKey: "section_copy", title: "Contact Information (copy)", description: "How to reach them", elements: [] });
  });

  it("removing a section requires an explicit disposition and never loses a placement silently", () => {
    const layout = appendElement(insertSection(base, null, "section_contact", "Contact"), 1, rel);
    expect(removeSection(layout, 1, { kind: "empty" })).toBe(layout); // not empty → refused
    const moved = removeSection(layout, 1, { kind: "moveTo", targetSectionIndex: 0 });
    expect(moved.sections).toHaveLength(1);
    expect(moved.sections[0]!.elements).toEqual([a, b, rel]);
    const unplaced = removeSection(layout, 1, { kind: "unplace" });
    expect(unplaced.sections).toHaveLength(1);
    expect(placedIds(unplaced).relationshipIds.size).toBe(0); // un-placed from the layout only; definitions are server-side and untouched
    expect(removeSection(emptyLayout("x"), 0, { kind: "empty" }).sections).toHaveLength(1); // last section stays
    const empty = insertSection(base, null, "section_empty", "Empty");
    expect(removeSection(empty, 1, { kind: "empty" }).sections).toHaveLength(1);
  });
});

import { formLayoutSchema, type FieldElement, type FormLayout, type RuleField } from "@enterpriseforms/form-renderer";
import { describe, expect, it } from "vitest";
import { elementKey, moveElementTo, removeSection, sameScope, setSectionRepeat } from "./layoutEdits";
import { candidateFilterParents, candidateSources, findOptionFilterCycle, inScope } from "./ruleEdit";

const group = "0199a2b3-c4d5-7e6f-8123-000000000001";
const other = "0199a2b3-c4d5-7e6f-8123-000000000002";
const id = (n: number) => `0199a2b3-c4d5-7e6f-8123-0000000001${String(n).padStart(2, "0")}`;
const placement = { columnSpan: 4, startOnNewRow: false };
const repeat = (groupDefinitionId: string) => ({ groupDefinitionId, minOccurrences: 0, maxOccurrences: 5, allowAdd: true, allowRemove: true, allowReorder: true });
const field = (n: number, control: "text" | "select" = "text", optionFilter?: { parentFieldDefinitionId: string; parentOptionKeysByOption: Record<string, string[]> }): FieldElement => ({ kind: "field", fieldDefinitionId: id(n), control, label: `F${n}`, required: false, layout: placement, ...(optionFilter ? { optionFilter } : {}) });

const layout: FormLayout = formLayoutSchema.parse({
  layoutSchemaVersion: 2,
  title: "t",
  sections: [
    { sectionKey: "main", title: "Main", elements: [field(1, "select"), field(2)] },
    { sectionKey: "people", title: "People", repeat: repeat(group), elements: [field(3, "select"), field(4)] },
    { sectionKey: "vehicles", title: "Vehicles", repeat: repeat(other), elements: [field(5)] },
    { sectionKey: "empty", title: "Empty", elements: [] },
  ],
});
const fields: RuleField[] = [1, 2, 3, 4, 5].map((n) => ({ fieldDefinitionId: id(n), fieldKey: `f${n}`, fieldDataType: n === 1 || n === 3 ? "Option" : "String" }));

describe("repeating sections in the designer", () => {
  it("only moves elements between sections of the same scope", () => {
    expect(sameScope(layout.sections[0]!, layout.sections[3]!)).toBe(true);
    expect(sameScope(layout.sections[1]!, layout.sections[2]!)).toBe(false);
    const key = (n: number) => elementKey(field(n));
    expect(moveElementTo(layout, key(2), 3, 0)).not.toBe(layout); // record-level → record-level
    expect(moveElementTo(layout, key(2), 1, 0)).toBe(layout); // record-level → repeating: refused
    expect(moveElementTo(layout, key(4), 0, 0)).toBe(layout); // per-entry → record-level: refused
    expect(moveElementTo(layout, key(4), 2, 0)).toBe(layout); // another group's section: refused
    expect(moveElementTo(layout, key(4), 1, 0)).not.toBe(layout); // reorder inside the section: fine
    expect(removeSection(layout, 1, { kind: "moveTo", targetSectionIndex: 0 })).toBe(layout);
  });

  it("switches multiple entries on or off only for an empty section, and never places a group twice", () => {
    const on = setSectionRepeat(layout, 3, repeat("0199a2b3-c4d5-7e6f-8123-000000000003"));
    expect(on.sections[3]!.repeat?.groupDefinitionId).toBe("0199a2b3-c4d5-7e6f-8123-000000000003");
    expect(setSectionRepeat(layout, 0, repeat("0199a2b3-c4d5-7e6f-8123-000000000003"))).toBe(layout);
    expect(setSectionRepeat(layout, 1, undefined)).toBe(layout);
    expect(setSectionRepeat(layout, 3, repeat(group))).toBe(layout);
    // Settings of an already repeating section can change at any time.
    const changed = setSectionRepeat(layout, 1, { ...repeat(group), maxOccurrences: 9 });
    expect(changed.sections[1]!.repeat?.maxOccurrences).toBe(9);
    expect(formLayoutSchema.safeParse(changed).success).toBe(true);
  });

  it("offers rule sources in scope: an entry may read itself and the record, the record never reads entries", () => {
    expect(inScope(layout, id(1), id(4))).toBe(true);
    expect(inScope(layout, id(3), id(4))).toBe(true);
    expect(inScope(layout, id(4), id(2))).toBe(false);
    expect(inScope(layout, id(5), id(4))).toBe(false);
    expect(candidateSources(layout, id(4), fields, () => true).map((f) => f.fieldDefinitionId)).toEqual([id(1), id(2), id(3)]);
    expect(candidateSources(layout, id(2), fields, () => true).map((f) => f.fieldDefinitionId)).toEqual([id(1)]);
  });

  it("offers dependent-dropdown parents in scope without loops and detects a circular chain", () => {
    expect(candidateFilterParents(layout, id(3), fields).map((f) => f.fieldDefinitionId)).toEqual([id(1)]);
    expect(candidateFilterParents(layout, id(1), fields).map((f) => f.fieldDefinitionId)).toEqual([]);
    const chained: FormLayout = { ...layout, sections: [{ ...layout.sections[0]!, elements: [field(1, "select", { parentFieldDefinitionId: id(3), parentOptionKeysByOption: {} }), field(2)] }, { ...layout.sections[1]!, elements: [field(3, "select", { parentFieldDefinitionId: id(1), parentOptionKeysByOption: {} }), field(4)] }] };
    expect(findOptionFilterCycle(chained)).toEqual([id(1), id(3), id(1)]);
    expect(findOptionFilterCycle(layout)).toBeNull();
  });
});

import { describe, expect, it } from "vitest";
import { changeInOccurrence, changeTopLevel, groupPayload, initialGroups, moveOccurrence, toGroupValueMap, validateGroups, type GroupInfo, type GroupValueMap } from "./groups";
import { formLayoutSchema, type FormLayout } from "./layout";
import { cascadeDependentClears, dependentChoicesFor, dependentViolation } from "./options";
import { validateValues, type RendererField } from "./validation";

const groupId = "0199a2b3-c4d5-7e6f-8123-000000000001";
const ids = {
  title: "0199a2b3-c4d5-7e6f-8123-000000000010",
  country: "0199a2b3-c4d5-7e6f-8123-000000000011",
  province: "0199a2b3-c4d5-7e6f-8123-000000000012",
  city: "0199a2b3-c4d5-7e6f-8123-000000000013",
  name: "0199a2b3-c4d5-7e6f-8123-000000000020",
  role: "0199a2b3-c4d5-7e6f-8123-000000000021",
  notes: "0199a2b3-c4d5-7e6f-8123-000000000022",
  region: "0199a2b3-c4d5-7e6f-8123-000000000023",
  status: "0199a2b3-c4d5-7e6f-8123-000000000024",
};
const opts = (...keys: string[]) => keys.map((key, i) => ({ fieldOptionId: `0199a2b3-c4d5-7e6f-8123-1000000000${String(i).padStart(2, "0")}`, optionKey: key, optionDisplayText: key.toUpperCase(), optionSortOrder: i, isActive: true }));
const fields: RendererField[] = [
  { fieldDefinitionId: ids.title, fieldKey: "case_title", fieldDisplayName: "Case title", fieldDataType: "String", options: [] },
  { fieldDefinitionId: ids.country, fieldKey: "country", fieldDisplayName: "Country", fieldDataType: "Option", options: opts("canada", "usa") },
  { fieldDefinitionId: ids.province, fieldKey: "province", fieldDisplayName: "Province", fieldDataType: "Option", options: opts("ontario", "quebec", "texas") },
  { fieldDefinitionId: ids.city, fieldKey: "city", fieldDisplayName: "City", fieldDataType: "Option", optionSelectionMode: "Multiple", options: opts("toronto", "ottawa", "montreal", "austin") },
  { fieldDefinitionId: ids.name, fieldKey: "person_name", fieldDisplayName: "Name", fieldDataType: "String", options: [], groupDefinitionId: groupId },
  { fieldDefinitionId: ids.role, fieldKey: "person_role", fieldDisplayName: "Role", fieldDataType: "Option", options: opts("witness", "suspect"), groupDefinitionId: groupId },
  { fieldDefinitionId: ids.notes, fieldKey: "person_notes", fieldDisplayName: "Notes", fieldDataType: "String", options: [], groupDefinitionId: groupId },
  { fieldDefinitionId: ids.region, fieldKey: "person_region", fieldDisplayName: "Region", fieldDataType: "Option", options: opts("ontario", "texas"), groupDefinitionId: groupId },
  { fieldDefinitionId: ids.status, fieldKey: "person_status", fieldDisplayName: "Status", fieldDataType: "Option", options: opts("active", "closed"), groupDefinitionId: groupId },
];
const infos: GroupInfo[] = [{ groupDefinitionId: groupId, groupKey: "people" }];
const placement = { columnSpan: 4, startOnNewRow: false };
const eq = (fieldDefinitionId: string, value: string) => ({ op: "equals" as const, fieldDefinitionId, value });

const layout: FormLayout = formLayoutSchema.parse({
  layoutSchemaVersion: 2,
  title: "Case",
  sections: [
    {
      sectionKey: "main",
      title: "Case",
      elements: [
        { kind: "field", fieldDefinitionId: ids.title, control: "text", label: "Case title", required: true, layout: placement },
        { kind: "field", fieldDefinitionId: ids.country, control: "select", label: "Country", layout: placement },
        { kind: "field", fieldDefinitionId: ids.province, control: "select", label: "Province", layout: placement, optionFilter: { parentFieldDefinitionId: ids.country, parentOptionKeysByOption: { ontario: ["canada"], quebec: ["canada"], texas: ["usa"] } } },
        { kind: "field", fieldDefinitionId: ids.city, control: "multiselect", label: "City", layout: placement, optionFilter: { parentFieldDefinitionId: ids.province, parentOptionKeysByOption: { toronto: ["ontario"], ottawa: ["ontario"], montreal: ["quebec"], austin: ["texas"] } } },
      ],
    },
    {
      sectionKey: "people",
      title: "People",
      repeat: { groupDefinitionId: groupId, minOccurrences: 1, maxOccurrences: 3, allowAdd: true, allowRemove: true, allowReorder: true },
      elements: [
        { kind: "field", fieldDefinitionId: ids.name, control: "text", label: "Name", required: true, layout: placement },
        { kind: "field", fieldDefinitionId: ids.role, control: "select", label: "Role", layout: placement },
        { kind: "field", fieldDefinitionId: ids.notes, control: "text", label: "Notes", layout: placement, visibleWhen: eq(ids.role, "suspect"), requiredWhen: eq(ids.role, "suspect") },
        { kind: "field", fieldDefinitionId: ids.region, control: "select", label: "Region", layout: placement, optionFilter: { parentFieldDefinitionId: ids.country, parentOptionKeysByOption: { ontario: ["canada"], texas: ["usa"] } } },
        { kind: "field", fieldDefinitionId: ids.status, control: "select", label: "Status", layout: placement, readOnly: true, defaultOptionKeys: ["active"] },
      ],
    },
  ],
});

const entry = (formRecordGroupId: string | null, values: Record<string, string>) => ({ formRecordGroupId, clientKey: formRecordGroupId ?? `k-${Math.random()}`, values });

describe("repeating sections", () => {
  it("parses repeat settings and rejects impossible ones", () => {
    const section = layout.sections[1]!;
    expect(section.repeat?.maxOccurrences).toBe(3);
    const bad = (repeat: Record<string, unknown>) => formLayoutSchema.safeParse({ layoutSchemaVersion: 2, title: "t", sections: [{ sectionKey: "s", elements: [], repeat: { groupDefinitionId: groupId, minOccurrences: 0, maxOccurrences: 3, allowAdd: true, allowRemove: true, allowReorder: true, ...repeat } }] }).success;
    expect(bad({})).toBe(true);
    expect(bad({ minOccurrences: 4 })).toBe(false);
    expect(bad({ maxOccurrences: 51 })).toBe(false);
    expect(bad({ allowAdd: false })).toBe(false);
    expect(bad({ extra: 1 })).toBe(false);
  });

  it("starts a new record with the minimum entries, each with its dropdown defaults", () => {
    const groups = initialGroups(layout, fields, infos);
    expect(groups.people).toHaveLength(1);
    expect(groups.people![0]!.formRecordGroupId).toBeNull();
    expect(groups.people![0]!.values).toEqual({ person_status: "active" });
  });

  it("groups record values into entries by their stable ids and in the server's order", () => {
    const groups = toGroupValueMap(
      [
        { fieldKey: "case_title", stringValue: "Case 1", formRecordGroupId: null },
        { fieldKey: "person_name", stringValue: "Bo", formRecordGroupId: "b" },
        { fieldKey: "person_name", stringValue: "Ada", formRecordGroupId: "a" },
      ],
      [{ groupKey: "people", occurrences: [{ formRecordGroupId: "a" }, { formRecordGroupId: "b" }] }],
    );
    expect(groups.people!.map((e) => [e.formRecordGroupId, e.values.person_name])).toEqual([
      ["a", "Ada"],
      ["b", "Bo"],
    ]);
  });

  it("validates bounds and every entry with the server's path keys, evaluating rules per entry", () => {
    const top = { case_title: "Case" };
    expect(validateGroups(layout, fields, top, { people: [] }, infos)).toEqual({ people: "People needs at least 1 entry" });
    const errors = validateGroups(layout, fields, top, { people: [entry(null, { person_name: "Ada", person_role: "witness" }), entry(null, { person_role: "suspect" })] }, infos);
    expect(Object.keys(errors).sort()).toEqual(["people[2].person_name", "people[2].person_notes"]);
    expect(errors["people[2].person_notes"]).toContain("(entry 2)");
    const tooMany = validateGroups(layout, fields, top, { people: [1, 2, 3, 4].map(() => entry(null, { person_name: "x" })) }, infos);
    expect(tooMany.people).toBe("People allows at most 3 entries");
  });

  it("does not validate grouped fields as record-level values", () => {
    expect(validateValues(layout, fields, { case_title: "Case" }, "create")).toEqual({});
  });

  it("sends the complete entry list with only the fields a person can change", () => {
    const groups: GroupValueMap = {
      people: [entry("b", { person_name: "Bo", person_role: "witness", person_notes: "kept", person_status: "active" }), entry(null, { person_name: "Cy", person_role: "suspect", person_notes: "seen", person_status: "active" })],
    };
    const payload = groupPayload(layout, fields, {}, groups, infos);
    expect(payload).toEqual([
      {
        groupKey: "people",
        occurrences: [
          // Notes are hidden for a witness and Status is read-only: neither is sent (the server keeps them).
          { formRecordGroupId: "b", values: { person_name: "Bo", person_role: "witness" } },
          { formRecordGroupId: null, values: { person_name: "Cy", person_role: "suspect", person_notes: "seen" } },
        ],
      },
    ]);
  });

  it("reorders entries without changing their identity", () => {
    const entries = [entry("a", {}), entry("b", {}), entry("c", {})];
    expect(moveOccurrence(entries, 2, -1).map((e) => e.formRecordGroupId)).toEqual(["a", "c", "b"]);
    expect(moveOccurrence(entries, 0, -1).map((e) => e.formRecordGroupId)).toEqual(["a", "b", "c"]);
  });
});

describe("dependent dropdowns", () => {
  const province = layout.sections[0]!.elements[2]!;
  const provinceField = fields[2]!;
  const cityField = fields[3]!;

  it("filters choices by the parent selection, keeping a stored invalid selection visible but marked", () => {
    if (province.kind !== "field") throw new Error("field expected");
    expect(dependentChoicesFor(province, provinceField, [], "canada").map((c) => c.optionKey)).toEqual(["ontario", "quebec"]);
    expect(dependentChoicesFor(province, provinceField, [], "").map((c) => c.optionKey)).toEqual([]);
    const stored = dependentChoicesFor(province, provinceField, ["texas"], "canada");
    expect(stored.map((c) => [c.optionKey, c.allowedByParent])).toEqual([
      ["ontario", true],
      ["quebec", true],
      ["texas", false],
    ]);
  });

  it("mirrors the server's validity rule (unmapped options always allowed, multi-select supported)", () => {
    const filter = { parentFieldDefinitionId: ids.country, parentOptionKeysByOption: { ontario: ["canada"], texas: ["usa"] } };
    expect(dependentViolation(filter, provinceField, "ontario", "canada")).toBeUndefined();
    expect(dependentViolation(filter, provinceField, "ontario", "usa")).toBe("ontario");
    expect(dependentViolation(filter, provinceField, "elsewhere", "")).toBeUndefined();
    expect(dependentViolation(filter, cityField, "ontario,texas", "canada,usa")).toBeUndefined();
    expect(dependentViolation(filter, cityField, "ontario,texas", "canada")).toBe("texas");
  });

  it("clears invalid children down the chain when a parent changes, keeping still-valid multi-select choices", () => {
    const elements = layout.sections[0]!.elements.filter((e) => e.kind === "field");
    const result = cascadeDependentClears(elements, fields, { country: "usa", province: "ontario", city: "toronto,ottawa" }, ["country"]);
    expect(result.values).toMatchObject({ country: "usa", province: "", city: "" });
    expect(result.clearedFieldKeys).toEqual(["province", "city"]);
    // Changing to a parent that still allows the child leaves it alone.
    expect(cascadeDependentClears(elements, fields, { country: "canada", province: "quebec", city: "montreal" }, ["country"]).clearedFieldKeys).toEqual([]);
  });

  it("propagates a record-level parent change into every entry and reports entry paths", () => {
    const groups: GroupValueMap = { people: [entry("a", { person_name: "Ada", person_region: "ontario" }), entry("b", { person_name: "Bo", person_region: "texas" })] };
    const result = changeTopLevel(layout, fields, { country: "canada", province: "ontario" }, groups, infos, "country", "usa");
    expect(result.values).toMatchObject({ country: "usa", province: "" });
    expect(result.groups.people![0]!.values.person_region).toBe("");
    expect(result.groups.people![1]!.values.person_region).toBe("texas");
    expect(result.clearedFieldKeys).toEqual(["province", "people[1].person_region"]);
  });

  it("changes inside one entry never touch another entry", () => {
    const groups: GroupValueMap = { people: [entry("a", { person_name: "Ada" }), entry("b", { person_name: "Bo" })] };
    const result = changeInOccurrence(layout, layout.sections[1]!, fields, {}, groups, "people", 1, "person_name", "Bob");
    expect(result.groups.people!.map((e) => e.values.person_name)).toEqual(["Ada", "Bob"]);
    expect(result.groups.people![0]).toBe(groups.people![0]);
  });

  it("validation refuses a stored combination the parent no longer allows", () => {
    const errors = validateValues(layout, fields, { case_title: "Case", country: "usa", province: "ontario" }, "edit");
    expect(errors.province).toContain("not available for the chosen Country");
  });
});

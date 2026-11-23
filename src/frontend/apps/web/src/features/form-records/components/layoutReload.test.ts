import { emptyLayout, type FormLayout, type RendererField } from "@enterpriseforms/form-renderer";
import { describe, expect, it } from "vitest";
import { mergeEdits, withholdUnaccepted, type EditSnapshot } from "./editMerge";
import { editableTargets, layoutValues } from "./recordModel";

/**
 * Phase C9 (D9-5): "Reload with the new layout and keep my changes". Values are matched by the field's permanent key; a
 * change the new layout can take is carried over; a change it cannot take (field removed, made read-only, hidden from the
 * form, unshared, or a repeating section no longer placed) is withheld and reported — never silently dropped by the save,
 * never counted as carried over, never applied to another field.
 */

const ids = { given: "0199a2b3-c4d5-7e6f-8123-456789abc001", phone: "0199a2b3-c4d5-7e6f-8123-456789abc002", alias: "0199a2b3-c4d5-7e6f-8123-456789abc003", status: "0199a2b3-c4d5-7e6f-8123-456789abc004", group: "0199a2b3-c4d5-7e6f-8123-456789abc005", plate: "0199a2b3-c4d5-7e6f-8123-456789abc006" };
const fields: RendererField[] = [
  { fieldDefinitionId: ids.given, fieldKey: "given_name", fieldDisplayName: "Given", fieldDataType: "String", options: [] },
  { fieldDefinitionId: ids.phone, fieldKey: "phone_number", fieldDisplayName: "Phone", fieldDataType: "String", options: [] },
  { fieldDefinitionId: ids.alias, fieldKey: "alias_name", fieldDisplayName: "Alias", fieldDataType: "String", options: [] },
  { fieldDefinitionId: ids.status, fieldKey: "case_status", fieldDisplayName: "Status", fieldDataType: "String", options: [] },
  { fieldDefinitionId: ids.plate, fieldKey: "plate_number", fieldDisplayName: "Plate", fieldDataType: "String", options: [], groupDefinitionId: ids.group },
];
const groups = [{ groupDefinitionId: ids.group, groupKey: "vehicles" }];
const element = (fieldDefinitionId: string, extra: Record<string, unknown> = {}) => ({ kind: "field" as const, fieldDefinitionId, control: "text" as const, label: "x", required: false, layout: { columnSpan: 4 as const, startOnNewRow: false }, ...extra });
const repeatSection = { sectionKey: "vehicles", gridColumns: 4 as const, repeat: { groupDefinitionId: ids.group, minOccurrences: 0, maxOccurrences: 5, allowAdd: true, allowRemove: true, allowReorder: true }, elements: [element(ids.plate)] };

/** v2: given, phone, status; vehicles placed. */
const v2: FormLayout = { ...emptyLayout("T"), sections: [{ sectionKey: "main", gridColumns: 4, elements: [element(ids.given), element(ids.phone), element(ids.status)] }, repeatSection] };
/** v3: phone removed, alias added, status read-only, vehicles no longer placed. */
const v3: FormLayout = { ...emptyLayout("T"), sections: [{ sectionKey: "main", gridColumns: 4, elements: [element(ids.given), element(ids.alias), element(ids.status, { readOnly: true })] }] };

const entry = (id: string, plate: string) => ({ formRecordGroupId: id, clientKey: id, values: { plate_number: plate } });
const base: EditSnapshot = { values: { given_name: "Avery", phone_number: "555-0101", case_status: "open" }, groups: { vehicles: [entry("g1", "ABC 123")] } };
const mine: EditSnapshot = { values: { given_name: "Avery B", phone_number: "555-0199", case_status: "closed" }, groups: { vehicles: [entry("g1", "XYZ 789")] } };
const theirs: EditSnapshot = base; // nobody else saved: only the layout changed

describe("editableTargets", () => {
  it("lists the top-level fields a person can change in a layout and the repeating sections it places", () => {
    expect([...editableTargets(v2, fields, groups, null).fieldKeys].sort()).toEqual(["case_status", "given_name", "phone_number"]);
    expect([...editableTargets(v2, fields, groups, null).groupKeys]).toEqual(["vehicles"]);
    const targets = editableTargets(v3, fields, groups, null);
    expect([...targets.fieldKeys].sort()).toEqual(["alias_name", "given_name"]); // status is read-only now
    expect([...targets.groupKeys]).toEqual([]);
  });

  it("limits a share editor to the fields shared for editing", () => {
    expect([...editableTargets(v2, fields, groups, new Set(["given_name"])).fieldKeys]).toEqual(["given_name"]);
  });
});

describe("reload onto a new layout keeping my changes", () => {
  it("carries over what the new layout accepts and withholds, with my value, what it no longer accepts", () => {
    const merged = withholdUnaccepted(mergeEdits(base, mine, theirs), base, mine, theirs, editableTargets(v3, fields, groups, null));
    expect(merged.values.given_name).toBe("Avery B");
    expect(merged.reapplied).toEqual(["given_name"]);
    expect(merged.withheld).toEqual([
      { fieldKey: "case_status", mine: "closed" },
      { fieldKey: "phone_number", mine: "555-0199" },
    ]);
    // The form now holds the latest saved values for withheld fields and sections, so nothing withheld is sent or reported as dirty.
    expect(merged.values.phone_number).toBe("555-0101");
    expect(merged.values.case_status).toBe("open");
    expect(merged.withheldSections).toEqual(["vehicles"]);
    expect(merged.groups.vehicles).toEqual(base.groups.vehicles);
    expect(merged.reapplied).not.toContain("vehicles");
    // What the save would send with the new layout: my accepted change, the stored value of the read-only field, no removed field.
    expect(layoutValues(v3, fields, merged.values, theirs.values)).toEqual({ given_name: "Avery B", alias_name: "", case_status: "open" });
  });

  it("withholds a change someone else also made rather than asking me to resolve a field I can no longer change", () => {
    const other: EditSnapshot = { ...base, values: { ...base.values, phone_number: "555-0123" } };
    const merged = withholdUnaccepted(mergeEdits(base, mine, other), base, mine, other, editableTargets(v3, fields, groups, null));
    expect(merged.conflicts.map((item) => item.fieldKey)).not.toContain("phone_number");
    expect(merged.withheld).toContainEqual({ fieldKey: "phone_number", mine: "555-0199" });
    expect(merged.values.phone_number).toBe("555-0123");
  });

  it("changes nothing when the new layout accepts every change", () => {
    const merged = withholdUnaccepted(mergeEdits(base, mine, theirs), base, mine, theirs, editableTargets(v2, fields, groups, null));
    expect(merged.withheld).toEqual([]);
    expect(merged.withheldSections).toEqual([]);
    expect(merged.values).toMatchObject(mine.values);
    expect(merged.groups.vehicles).toEqual(mine.groups.vehicles);
  });
});

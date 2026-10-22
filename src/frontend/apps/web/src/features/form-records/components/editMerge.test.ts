import { describe, expect, it } from "vitest";
import { isDirty, mergeEdits, type EditSnapshot } from "./editMerge";

const entry = (id: string | null, values: Record<string, string>, clientKey = id ?? "new") => ({ formRecordGroupId: id, clientKey, values });

const base: EditSnapshot = {
  values: { given_name: "Avery", family_name: "Synthetic", case_status: "open", notes: "" },
  groups: { aliases: [entry("g1", { alias_name: "AV" })], vehicles: [entry("v1", { plate_number: "ABC 123" })] },
};

describe("record edit merge after a concurrent save (C-31)", () => {
  it("takes their value where only they changed a field and reapplies mine where only I did", () => {
    const mine = { ...base, values: { ...base.values, given_name: "Avery-Mine" } };
    const theirs = { ...base, values: { ...base.values, case_status: "closed" } };
    const merged = mergeEdits(base, mine, theirs);
    expect(merged.values).toMatchObject({ given_name: "Avery-Mine", case_status: "closed", family_name: "Synthetic" });
    expect(merged.reapplied).toEqual(["given_name"]);
    expect(merged.conflicts).toEqual([]);
  });

  it("reports a field both changed differently as a conflict and never silently picks one", () => {
    const mine = { ...base, values: { ...base.values, family_name: "Mine" } };
    const theirs = { ...base, values: { ...base.values, family_name: "Theirs" } };
    const merged = mergeEdits(base, mine, theirs);
    expect(merged.conflicts).toEqual([{ fieldKey: "family_name", mine: "Mine", theirs: "Theirs" }]);
    expect(merged.reapplied).toEqual([]);
  });

  it("treats identical changes on both sides as agreement, and clearing as a change", () => {
    const mine = { ...base, values: { ...base.values, family_name: "Same", given_name: "" } };
    const theirs = { ...base, values: { ...base.values, family_name: "Same" } };
    const merged = mergeEdits(base, mine, theirs);
    expect(merged.conflicts).toEqual([]);
    expect(merged.values.family_name).toBe("Same");
    expect(merged.values.given_name).toBe("");
    expect(merged.reapplied).toEqual(["given_name"]);
  });

  it("merges repeating sections as a whole and keeps the latest version when both changed one", () => {
    const mine: EditSnapshot = { ...base, groups: { aliases: [entry("g1", { alias_name: "AVE" })], vehicles: [entry("v1", { plate_number: "XYZ 999" })] } };
    const theirs: EditSnapshot = { ...base, groups: { aliases: [entry("g1", { alias_name: "AV" }), entry("g2", { alias_name: "Ave" })], vehicles: base.groups.vehicles! } };
    const merged = mergeEdits(base, mine, theirs);
    expect(merged.groups.aliases).toEqual(theirs.groups.aliases);
    expect(merged.groups.vehicles).toEqual(mine.groups.vehicles);
    expect(merged.sectionConflicts).toEqual(["aliases"]);
    expect(merged.reapplied).toEqual(["vehicles"]);
  });

  it("detects unsaved changes independently of entry render keys and empty values", () => {
    expect(isDirty(base, { values: { ...base.values, notes: undefined as unknown as string }, groups: { aliases: [entry("g1", { alias_name: "AV" }, "other-key")], vehicles: base.groups.vehicles! } })).toBe(false);
    expect(isDirty(base, { ...base, values: { ...base.values, notes: "x" } })).toBe(true);
    expect(isDirty(base, { ...base, groups: { ...base.groups, aliases: [] } })).toBe(true);
  });
});

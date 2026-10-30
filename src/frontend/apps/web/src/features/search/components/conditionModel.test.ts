import type { FieldSearchCapabilitiesResponse } from "@enterpriseforms/api-client";
import { describe, expect, it } from "vitest";
import { addCondition, arity, MAX_CONDITIONS_V1, maximumConditions, newCondition, operatorLabel, removeCondition, setField, setOperator, setValue, toFieldSearchRequest, validateConditions, type ConditionDraft } from "./conditionModel";

const name = "0199a2b3-c4d5-7e6f-8123-456789abcde1";
const age = "0199a2b3-c4d5-7e6f-8123-456789abcde2";
const born = "0199a2b3-c4d5-7e6f-8123-456789abcde3";
const custody = "0199a2b3-c4d5-7e6f-8123-456789abcde4";
const status = "0199a2b3-c4d5-7e6f-8123-456789abcde5";
const active = "0199a2b3-c4d5-7e6f-8123-456789abcdf1";
const capabilities: FieldSearchCapabilitiesResponse = {
  formDefinitionId: "0199a2b3-c4d5-7e6f-8123-456789abcdf9",
  fieldValueSearchAvailable: true,
  unavailableReasonCode: null,
  maximumConditions: 5,
  conditionJoin: "And",
  policyRevision: 7,
  notSearchableFields: [],
  fields: [
    { fieldDefinitionId: name, displayLabel: "Given name", valueType: "String", operators: ["equals", "startsWith"], options: [], minimumTextLength: 3, maximumTextLength: 200 },
    { fieldDefinitionId: age, displayLabel: "Age", valueType: "Integer", operators: ["equals", "between"], options: [], minimumTextLength: null, maximumTextLength: null },
    { fieldDefinitionId: born, displayLabel: "Born", valueType: "Date", operators: ["equals", "before"], options: [], minimumTextLength: null, maximumTextLength: null },
    { fieldDefinitionId: custody, displayLabel: "In custody", valueType: "Boolean", operators: ["isTrue", "isFalse"], options: [], minimumTextLength: null, maximumTextLength: null },
    { fieldDefinitionId: status, displayLabel: "Status", valueType: "Option", operators: ["is"], options: [{ fieldOptionId: active, displayText: "Active" }], minimumTextLength: null, maximumTextLength: null },
  ],
};

function one(): [ConditionDraft[], string] {
  const list = [newCondition()];
  return [list, list[0]!.key];
}

describe("Advanced Search condition model", () => {
  it("adds conditions up to the V1 maximum of five and never beyond", () => {
    let list = [newCondition()];
    for (let i = 0; i < 10; i++) {
      list = addCondition(list, maximumConditions(capabilities));
    }
    expect(list).toHaveLength(MAX_CONDITIONS_V1);
    expect(new Set(list.map((c) => c.key)).size).toBe(5);
  });

  it("honours a lower server maximum and ignores an announced maximum above five", () => {
    expect(maximumConditions({ ...capabilities, maximumConditions: 3 })).toBe(3);
    expect(maximumConditions({ ...capabilities, maximumConditions: 9 })).toBe(5);
    expect(maximumConditions(undefined)).toBe(5);
  });

  it("removing the only condition leaves one empty condition", () => {
    const [list, key] = one();
    const after = removeCondition(list, key);
    expect(after).toHaveLength(1);
    expect(after[0]!.key).not.toBe(key);
  });

  it("choosing a field takes its first server operator and clears the value; unknown fields are not selectable", () => {
    const [list, key] = one();
    const typed = setValue(setField(list, key, name, capabilities), key, "value", "Avery");
    const switched = setField(typed, key, age, capabilities);
    expect(switched[0]).toMatchObject({ fieldDefinitionId: age, operator: "equals", value: "" });
    const forged = setField(list, key, "0199a2b3-c4d5-7e6f-8123-456789abcdee", capabilities);
    expect(forged[0]).toMatchObject({ fieldDefinitionId: undefined, operator: undefined });
  });

  it("changing between a single value and a range clears the values", () => {
    const [list, key] = one();
    const single = setValue(setField(list, key, age, capabilities), key, "value", "40");
    const range = setOperator(single, key, "between");
    expect(range[0]).toMatchObject({ operator: "between", value: "", lowerBound: "", upperBound: "" });
    expect(arity("isTrue")).toBe("none");
    expect(arity("between")).toBe("range");
    expect(arity("equals")).toBe("single");
  });

  it("labels operators for people, not with wire names", () => {
    expect(operatorLabel("equals", "String")).toBe("Is exactly");
    expect(operatorLabel("equals", "Date")).toBe("On");
    expect(operatorLabel("between", "Integer")).toBe("Between (inclusive)");
    expect(operatorLabel("isTrue", "Boolean")).toBe("Is Yes");
  });

  it("validates like the server: field and operator from the capability list, typed values, bounds order, minimum partial-text length", () => {
    const [list, key] = one();
    expect(validateConditions(list, capabilities).get(key)).toEqual({ field: "Choose a field." });
    const prefix = setValue(setOperator(setField(list, key, name, capabilities), key, "startsWith"), key, "value", "Av");
    expect(validateConditions(prefix, capabilities).get(key)?.value).toBe("Enter at least 3 characters.");
    const number = setValue(setField(list, key, age, capabilities), key, "value", "1,234");
    expect(validateConditions(number, capabilities).get(key)?.value).toContain("whole number");
    const range = setValue(setValue(setOperator(setField(list, key, age, capabilities), key, "between"), key, "lowerBound", "50"), key, "upperBound", "10");
    expect(validateConditions(range, capabilities).get(key)?.upperBound).toContain("must not be before");
    const option = setValue(setField(list, key, status, capabilities), key, "value", "0199a2b3-c4d5-7e6f-8123-456789abcdee");
    expect(validateConditions(option, capabilities).get(key)?.value).toBe("Choose an option.");
    const flag = setField(list, key, custody, capabilities);
    expect(validateConditions(flag, capabilities).size).toBe(0);
  });

  it("builds an invariant request body with AND conditions and the metadata filters", () => {
    let list = [newCondition(), newCondition(), newCondition()];
    const [a, b, c] = list.map((x) => x.key) as [string, string, string];
    list = setValue(setField(list, a, name, capabilities), a, "value", "Avery");
    list = setValue(setValue(setOperator(setField(list, b, age, capabilities), b, "between"), b, "lowerBound", " 18 "), b, "upperBound", "65");
    list = setField(list, c, custody, capabilities);
    expect(validateConditions(list, capabilities).size).toBe(0);
    const request = toFieldSearchRequest(capabilities.formDefinitionId, list, capabilities, { recordStatus: "Submitted", includeArchived: false, includeChildren: true, sortBy: "createdOn", sortDirection: "asc", pageSize: 50 });
    expect(request).toEqual({
      formDefinitionId: capabilities.formDefinitionId,
      conditions: [
        { fieldDefinitionId: name, operator: "equals", valueType: "String", value: "Avery" },
        { fieldDefinitionId: age, operator: "between", valueType: "Integer", lowerBound: "18", upperBound: "65" },
        { fieldDefinitionId: custody, operator: "isTrue", valueType: "Boolean" },
      ],
      recordStatus: "Submitted",
      includeArchived: false,
      includeChildren: true,
      sortBy: "createdOn",
      sortDirection: "asc",
      pageSize: 50,
    });
  });

  it("sends an unambiguous UTC instant for a date-time value", () => {
    const withDateTime: FieldSearchCapabilitiesResponse = { ...capabilities, fields: [{ fieldDefinitionId: born, displayLabel: "Arrested", valueType: "DateTime", operators: ["after"], options: [], minimumTextLength: null, maximumTextLength: null }] };
    const [list, key] = one();
    const filled = setValue(setField(list, key, born, withDateTime), key, "value", "2026-10-23T14:30");
    const request = toFieldSearchRequest(withDateTime.formDefinitionId, filled, withDateTime, { pageSize: 25 });
    expect(request.conditions![0]!.value).toBe(new Date("2026-10-23T14:30").toISOString());
    expect(request.conditions![0]!.value).toMatch(/Z$/);
  });
});

import { describe, expect, it } from "vitest";
import vectorsJson from "./rule-vectors.json";
import { defaultSelection, describeSelection, choicesFor } from "./options";
import { allowedOperators, describeCondition, evaluateCondition, evaluateRuleStates, parseCondition, type RuleCondition, type RuleField } from "./rules";
import { emptyLayout, type FormLayout } from "./layout";
import { validateValues, type RendererField } from "./validation";

interface Vectors {
  fields: Array<RuleField>;
  cases: Array<{ name: string; condition: unknown; values: Record<string, string>; expected: boolean }>;
  invalid: Array<{ name: string; condition: unknown }>;
}
const vectors = vectorsJson as unknown as Vectors;
const fieldsById = new Map(vectors.fields.map((field) => [field.fieldDefinitionId, field]));
const idByKey = new Map(vectors.fields.map((field) => [field.fieldKey, field.fieldDefinitionId]));

describe("rule evaluation shared with the .NET server", () => {
  it.each(vectors.cases.map((vector) => [vector.name, vector] as const))("%s", (_name, vector) => {
    const condition = parseCondition(vector.condition);
    const values = new Map(Object.entries(vector.values).map(([key, value]) => [idByKey.get(key)!, value]));
    expect(evaluateCondition(condition, fieldsById, (id) => values.get(id) ?? "")).toBe(vector.expected);
  });

  it.each(vectors.invalid.map((vector) => [vector.name, vector] as const))("rejects: %s", (_name, vector) => {
    expect(() => parseCondition(vector.condition)).toThrow();
  });

  it("keeps the breadth of the shared vectors", () => {
    expect(vectors.cases.length).toBeGreaterThanOrEqual(50);
  });
});

const a = "0199a2b3-c4d5-7e6f-8123-456789abc001";
const b = "0199a2b3-c4d5-7e6f-8123-456789abc002";
const c = "0199a2b3-c4d5-7e6f-8123-456789abc003";
const ruleFields = new Map<string, RuleField>([a, b, c].map((id, index) => [id, { fieldDefinitionId: id, fieldKey: ["alpha", "beta", "gamma"][index]!, fieldDataType: "String" }]));
const equals = (id: string, value: string): RuleCondition => ({ op: "equals", fieldDefinitionId: id, value });

describe("rule states", () => {
  it("hiding propagates through chains and a hidden field is never required", () => {
    const elements = [{ fieldDefinitionId: a }, { fieldDefinitionId: b, visibleWhen: equals(a, "show") }, { fieldDefinitionId: c, visibleWhen: equals(b, "x"), required: true }];
    const shown = evaluateRuleStates(elements, ruleFields, new Map([[a, "show"], [b, "x"]]));
    expect(shown.get(c)).toEqual({ visible: true, required: true });
    const hidden = evaluateRuleStates(elements, ruleFields, new Map([[a, "other"], [b, "x"]]));
    expect(hidden.get(b)).toEqual({ visible: false, required: false });
    expect(hidden.get(c)).toEqual({ visible: false, required: false });
  });

  it("required-if applies only while the condition holds", () => {
    const elements = [{ fieldDefinitionId: a }, { fieldDefinitionId: b, requiredWhen: equals(a, "yes") }];
    expect(evaluateRuleStates(elements, ruleFields, new Map([[a, "yes"]])).get(b)?.required).toBe(true);
    expect(evaluateRuleStates(elements, ruleFields, new Map([[a, "no"]])).get(b)?.required).toBe(false);
  });

  it("fails closed (hidden) if a cycle ever reaches the evaluator", () => {
    const elements = [{ fieldDefinitionId: a, visibleWhen: equals(b, "x") }, { fieldDefinitionId: b, visibleWhen: equals(a, "x") }];
    const states = evaluateRuleStates(elements, ruleFields, new Map([[a, "x"], [b, "x"]]));
    expect(states.get(a)?.visible).toBe(false);
  });

  it("offers only operators that fit the field type", () => {
    expect(allowedOperators({ fieldDataType: "Integer" })).toContain("greaterThan");
    expect(allowedOperators({ fieldDataType: "String" })).not.toContain("greaterThan");
    expect(allowedOperators({ fieldDataType: "Option", optionSelectionMode: "Single" })).not.toContain("contains");
    expect(allowedOperators({ fieldDataType: "Option", optionSelectionMode: "Multiple" })).toContain("contains");
    expect(allowedOperators({ fieldDataType: "Boolean" })).toEqual(["equals", "notEquals"]);
    expect(allowedOperators({ fieldDataType: "File" })).toEqual([]);
  });

  it("describes a rule in plain language", () => {
    const named = new Map([...ruleFields].map(([id, field]) => [id, { ...field, fieldDisplayName: field.fieldKey === "alpha" ? "Kind" : "Company name" }]));
    const text = describeCondition({ op: "and", conditions: [equals(a, "company"), { op: "isNotEmpty", fieldDefinitionId: b }] }, named, (_id, key) => key.toUpperCase());
    expect(text).toBe("(Kind is COMPANY and Company name is not empty)");
  });
});

const statusField: RendererField = {
  fieldDefinitionId: "0199a2b3-c4d5-7e6f-8123-456789abd001",
  fieldKey: "status",
  fieldDisplayName: "Status",
  fieldDataType: "Option",
  options: [
    { fieldOptionId: "o1", optionKey: "open", optionDisplayText: "Open (current)", optionSortOrder: 1, isActive: true },
    { fieldOptionId: "o2", optionKey: "closed", optionDisplayText: "Closed (current)", optionSortOrder: 2, isActive: false },
    { fieldOptionId: "o3", optionKey: "later", optionDisplayText: "Added after publish", optionSortOrder: 3, isActive: true },
  ],
};
const snapshotElement = {
  options: [
    { fieldOptionId: "o1", optionKey: "open", label: "Open (published)", sortOrder: 2, isActive: true },
    { fieldOptionId: "o2", optionKey: "closed", label: "Closed (published)", sortOrder: 1, isActive: true },
  ],
  defaultOptionKeys: ["closed", "open"],
};

describe("dropdown choices", () => {
  it("uses the published snapshot for labels and order and the live catalog for availability", () => {
    const choices = choicesFor(snapshotElement, statusField);
    // 'closed' was deactivated after publishing: no longer offered; 'later' was added after publishing: not part of this version.
    expect(choices.map((choice) => choice.optionKey)).toEqual(["open"]);
    expect(choices[0]!.label).toBe("Open (published)");
  });

  it("keeps a stored but now-unavailable selection visible and marked, and never offers it as a default", () => {
    expect(choicesFor(snapshotElement, statusField, ["closed"]).map((choice) => [choice.optionKey, choice.isActive])).toEqual([["closed", false], ["open", true]]);
    expect(describeSelection(snapshotElement, statusField, "closed")).toBe("Closed (published) (no longer available)");
    expect(defaultSelection(snapshotElement, statusField)).toBe("open");
  });

  it("falls back to the catalog for a version published without a snapshot", () => {
    expect(choicesFor(undefined, statusField).map((choice) => choice.label)).toEqual(["Open (current)", "Added after publish"]);
  });

  it("lists multiple selections in display order", () => {
    const multi = { ...statusField, optionSelectionMode: "Multiple" };
    expect(describeSelection(undefined, multi, "later,open")).toBe("Open (current), Added after publish");
  });
});

describe("conditional validation in the client mirror", () => {
  const kind: RendererField = { fieldDefinitionId: a, fieldKey: "kind", fieldDisplayName: "Kind", fieldDataType: "String", options: [] };
  const company: RendererField = { fieldDefinitionId: b, fieldKey: "company", fieldDisplayName: "Company", fieldDataType: "String", options: [] };
  const layout = (extra: object): FormLayout => ({
    ...emptyLayout("T"),
    sections: [
      {
        sectionKey: "main",
        gridColumns: 4,
        elements: [
          { kind: "field", fieldDefinitionId: a, control: "text", label: "Kind", required: false, layout: { columnSpan: 2, startOnNewRow: false } },
          { kind: "field", fieldDefinitionId: b, control: "text", label: "Company", required: false, layout: { columnSpan: 2, startOnNewRow: false }, ...extra } as FormLayout["sections"][number]["elements"][number],
        ],
      },
    ],
  });

  it("demands a required-if field only while its condition holds", () => {
    const conditional = layout({ requiredWhen: equals(a, "company") });
    expect(validateValues(conditional, [kind, company], { kind: "person" })).toEqual({});
    expect(validateValues(conditional, [kind, company], { kind: "company" })).toEqual({ company: "Company is required" });
    expect(validateValues(conditional, [kind, company], { kind: "company", company: "Acme" })).toEqual({});
  });

  it("does not demand a statically required field that a rule hides, and skips read-only fields", () => {
    const hidden = layout({ required: true, visibleWhen: equals(a, "company") });
    expect(validateValues(hidden, [kind, company], { kind: "person" })).toEqual({});
    expect(validateValues(hidden, [kind, company], { kind: "company" })).toEqual({ company: "Company is required" });
    expect(validateValues(layout({ required: true, readOnly: true }), [kind, company], {})).toEqual({});
  });

  it("evaluates no rules for a partial projection", () => {
    const conditional = layout({ requiredWhen: equals(a, "company") });
    expect(validateValues(conditional, [kind, company], { kind: "company" }, "create", { applyRules: false })).toEqual({});
  });
});

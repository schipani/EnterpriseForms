import { emptyLayout, type FormLayout, type RuleCondition, type RuleField } from "@enterpriseforms/form-renderer";
import { describe, expect, it } from "vitest";
import { appendCondition, asGroup, candidateSources, conditionProblem, dependsOn, findRuleCycle, getNode, removeNode, replaceNode, ruleDependencies } from "./ruleEdit";

const ids = ["0199a2b3-c4d5-7e6f-8123-456789abc001", "0199a2b3-c4d5-7e6f-8123-456789abc002", "0199a2b3-c4d5-7e6f-8123-456789abc003"] as const;
const [a, b, c] = ids;
const equals = (id: string, value: string): RuleCondition => ({ op: "equals", fieldDefinitionId: id, value });
const element = (id: string, extra: object = {}) => ({ kind: "field" as const, fieldDefinitionId: id, control: "text" as const, label: id.slice(-1), required: false, layout: { columnSpan: 4 as const, startOnNewRow: false }, ...extra });
const layoutOf = (...elements: ReturnType<typeof element>[]): FormLayout => ({ ...emptyLayout("T"), sections: [{ sectionKey: "main", gridColumns: 4, elements }] });
const ruleField = (id: string, fieldDataType = "String"): RuleField => ({ fieldDefinitionId: id, fieldKey: `k${id.slice(-1)}`, fieldDataType });

describe("rule graph", () => {
  it("lists what each field reads and finds no cycle in a chain", () => {
    const layout = layoutOf(element(a), element(b, { visibleWhen: equals(a, "x") }), element(c, { requiredWhen: equals(b, "y") }));
    expect([...ruleDependencies(layout).get(c)!]).toEqual([b]);
    expect(findRuleCycle(layout)).toBeNull();
    expect(dependsOn(layout, c, a)).toBe(true);
    expect(dependsOn(layout, a, c)).toBe(false);
  });

  it("detects a two-field and a three-field cycle", () => {
    const two = layoutOf(element(a, { visibleWhen: equals(b, "x") }), element(b, { requiredWhen: equals(a, "y") }));
    expect(findRuleCycle(two)).toEqual([a, b, a]);
    const three = layoutOf(element(a, { visibleWhen: equals(b, "x") }), element(b, { visibleWhen: equals(c, "x") }), element(c, { visibleWhen: equals(a, "x") }));
    const cycle = findRuleCycle(three)!;
    expect(cycle[0]).toBe(cycle[cycle.length - 1]);
    expect(new Set(cycle)).toEqual(new Set([a, b, c]));
  });

  it("never offers a source that would create a cycle, the field itself, or an unplaced field", () => {
    const layout = layoutOf(element(a), element(b, { visibleWhen: equals(a, "x") }), element(c));
    const all = [a, b, c, "0199a2b3-c4d5-7e6f-8123-456789abc009"].map((id) => ruleField(id));
    // Rule on a: b already reads a, so a may not read b.
    expect(candidateSources(layout, a, all, () => true).map((f) => f.fieldDefinitionId)).toEqual([c]);
    expect(candidateSources(layout, c, all, () => true).map((f) => f.fieldDefinitionId)).toEqual([a, b]);
  });
});

describe("condition tree edits", () => {
  const root: RuleCondition = { op: "and", conditions: [equals(a, "1"), { op: "or", conditions: [equals(b, "2"), equals(c, "3")] }] };

  it("reads, replaces and appends without mutating the original", () => {
    expect(getNode(root, [1, 0])).toEqual(equals(b, "2"));
    const replaced = replaceNode(root, [1, 0], equals(b, "9"));
    expect(getNode(replaced, [1, 0])).toEqual(equals(b, "9"));
    expect(getNode(root, [1, 0])).toEqual(equals(b, "2"));
    const appended = appendCondition(root, [1], equals(a, "4"));
    expect((getNode(appended, [1]) as { conditions: unknown[] }).conditions).toHaveLength(3);
  });

  it("removes a node and prunes groups left empty", () => {
    const afterOne = removeNode(root, [1, 0])!;
    expect((getNode(afterOne, [1]) as { conditions: unknown[] }).conditions).toHaveLength(1);
    const afterBoth = removeNode(removeNode(root, [1, 0])!, [1, 0])!;
    expect((afterBoth as { conditions: unknown[] }).conditions).toHaveLength(1);
    expect(removeNode(equals(a, "1"), [])).toBeUndefined();
  });

  it("wraps a bare comparison into a group before editing", () => {
    expect(asGroup(equals(a, "1"))).toEqual({ op: "and", conditions: [equals(a, "1")] });
    expect(appendCondition(equals(a, "1"), [], equals(b, "2"))).toEqual({ op: "and", conditions: [equals(a, "1"), equals(b, "2")] });
  });
});

describe("operand feedback", () => {
  const fields = new Map([ruleField(a, "Integer"), ruleField(b, "Date"), ruleField(c, "Boolean")].map((field) => [field.fieldDefinitionId, field]));
  it("flags values that do not fit the source type", () => {
    expect(conditionProblem({ op: "greaterThan", fieldDefinitionId: a, value: "abc" }, fields)).toBe("Enter a whole number");
    expect(conditionProblem({ op: "greaterThan", fieldDefinitionId: a, value: "18" }, fields)).toBeNull();
    expect(conditionProblem({ op: "equals", fieldDefinitionId: b, value: "tomorrow" }, fields)).toBe("Enter a date");
    expect(conditionProblem({ op: "equals", fieldDefinitionId: c, value: "yes" }, fields)).toBe("Choose true or false");
    expect(conditionProblem({ op: "isEmpty", fieldDefinitionId: a }, fields)).toBeNull();
    expect(conditionProblem({ op: "and", conditions: [{ op: "equals", fieldDefinitionId: a, value: "" }] }, fields)).toBe("Enter a value");
  });
});

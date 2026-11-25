// Regression tests for the zod v3 -> v4 migration (Dependabot #1-#3, GHSA-mrhx-c7xc-m3w5). They pin the layout and rule
// validation semantics the migration must preserve: GUID acceptance, strict objects, collection bounds, defaults, text
// safety and the server-range integers. The server (FormLayoutCodec, the Authority) remains the authority for every rule.
import { describe, expect, it } from "vitest";
import { formLayoutSchema, parseLayout, tryParseLayout } from "./layout";
import { RULE_MAX_CHILDREN, RULE_MAX_SET_VALUES, RULE_MAX_VALUE_LENGTH, parseCondition, ruleConditionSchema } from "./rules";

const id = (n: number) => `0199a2b3-c4d5-7e6f-8123-${n.toString(16).padStart(12, "0")}`;
const field = (extra: Record<string, unknown> = {}) => ({ kind: "field", fieldDefinitionId: id(1), ...extra });
const layoutWith = (element: Record<string, unknown>, section: Record<string, unknown> = {}) => ({
  layoutSchemaVersion: 2,
  title: "Form",
  sections: [{ sectionKey: "main", elements: [element], ...section }],
});
const ok = (input: unknown) => formLayoutSchema.safeParse(input).success;

describe("GUID identifiers keep the v3 acceptance (any 8-4-4-4-12 hexadecimal form)", () => {
  const accepted = [
    "0199a2b3-c4d5-7e6f-8123-456789abcdef", // UUIDv7
    "0199A2B3-C4D5-7E6F-8123-456789ABCDEF", // upper case
    "00000000-0000-0000-0000-000000000000", // nil
    "0199a2b3-c4d5-7e6f-c123-456789abcdef", // non-RFC variant (v4's strict uuid() would refuse it)
    "0199a2b3-c4d5-0e6f-8123-456789abcdef", // version nibble 0
  ];
  const refused = ["{0199a2b3-c4d5-7e6f-8123-456789abcdef}", "0199a2b3c4d57e6f8123456789abcdef", "0199a2b3-c4d5-7e6f-8123-456789abcdeg", "", "x"];

  it.each(accepted)("accepts %s in layouts and rules", (guid) => {
    expect(ok(layoutWith(field({ fieldDefinitionId: guid })))).toBe(true);
    expect(ruleConditionSchema.safeParse({ op: "isEmpty", fieldDefinitionId: guid }).success).toBe(true);
  });

  it.each(refused)("refuses %j in layouts and rules", (guid) => {
    expect(ok(layoutWith(field({ fieldDefinitionId: guid })))).toBe(false);
    expect(ruleConditionSchema.safeParse({ op: "isEmpty", fieldDefinitionId: guid }).success).toBe(false);
  });
});

describe("strict objects refuse unknown properties at every level", () => {
  it.each([
    ["root", { ...layoutWith(field()), extra: 1 }],
    ["section", layoutWith(field(), { extra: 1 })],
    ["section layout", layoutWith(field(), { layout: { columnGap: "small", extra: 1 } })],
    ["field element", layoutWith(field({ extra: 1 }))],
    ["placement", layoutWith(field({ layout: { columnSpan: 4, startOnNewRow: false, extra: 1 } }))],
    ["validation", layoutWith(field({ validation: { maxLength: 10, extra: 1 } }))],
    ["protection", layoutWith(field({ protection: { classification: "Public", protectionMode: "Standard", extra: 1 } }))],
    ["option snapshot", layoutWith(field({ options: [{ fieldOptionId: id(2), optionKey: "a", label: "A", sortOrder: 1, isActive: true, extra: 1 }] }))],
    ["repeat", layoutWith(field(), { repeat: { groupDefinitionId: id(3), minOccurrences: 1, maxOccurrences: 2, allowAdd: true, allowRemove: true, allowReorder: true, extra: 1 } })],
    ["document element", layoutWith({ kind: "documentCollection", fieldDefinitionId: id(4), extra: 1 })],
    ["upload policy", layoutWith({ kind: "documentCollection", fieldDefinitionId: id(4), upload: { maxFiles: 2, extra: 1 } })],
    ["related list", layoutWith({ kind: "relatedFormList", formRelationshipDefinitionId: id(5), extra: 1 })],
  ])("%s", (_name, input) => {
    expect(ok(input)).toBe(false);
  });

  it("rule comparisons and groups", () => {
    expect(ruleConditionSchema.safeParse({ op: "isEmpty", fieldDefinitionId: id(1), extra: 1 }).success).toBe(false);
    expect(ruleConditionSchema.safeParse({ op: "and", conditions: [{ op: "isEmpty", fieldDefinitionId: id(1) }], extra: 1 }).success).toBe(false);
  });
});

describe("collection bounds", () => {
  const many = <T,>(n: number, make: (i: number) => T) => Array.from({ length: n }, (_, i) => make(i));
  it("sections 1..50", () => {
    const section = (i: number) => ({ sectionKey: `s${i}`, elements: [] });
    expect(ok({ layoutSchemaVersion: 2, sections: [] })).toBe(false);
    expect(ok({ layoutSchemaVersion: 2, sections: many(50, section) })).toBe(true);
    expect(ok({ layoutSchemaVersion: 2, sections: many(51, section) })).toBe(false);
  });
  it("elements per section at most 250", () => {
    expect(ok({ layoutSchemaVersion: 2, sections: [{ sectionKey: "m", elements: many(250, (i) => field({ fieldDefinitionId: id(i + 1) })) }] })).toBe(true);
    expect(ok({ layoutSchemaVersion: 2, sections: [{ sectionKey: "m", elements: many(251, (i) => field({ fieldDefinitionId: id(i + 1) })) }] })).toBe(false);
  });
  it("options at most 500, default keys at most 100, summary keys at most 6", () => {
    const option = (i: number) => ({ fieldOptionId: id(i + 10), optionKey: `o${i}`, label: "L", sortOrder: i, isActive: true });
    expect(ok(layoutWith(field({ control: "select", options: many(500, option) })))).toBe(true);
    expect(ok(layoutWith(field({ control: "select", options: many(501, option) })))).toBe(false);
    expect(ok(layoutWith(field({ defaultOptionKeys: many(101, (i) => `k${i}`) })))).toBe(false);
    expect(ok(layoutWith({ kind: "relatedFormList", formRelationshipDefinitionId: id(5), summaryFieldKeys: many(7, (i) => `k${i}`) }))).toBe(false);
  });
  it("rule set values, children and value length", () => {
    const set = (n: number) => ({ op: "in", fieldDefinitionId: id(1), values: many(n, (i) => `v${i}`) });
    expect(ruleConditionSchema.safeParse(set(RULE_MAX_SET_VALUES)).success).toBe(true);
    expect(ruleConditionSchema.safeParse(set(RULE_MAX_SET_VALUES + 1)).success).toBe(false);
    expect(ruleConditionSchema.safeParse({ op: "in", fieldDefinitionId: id(1), values: [] }).success).toBe(false);
    const children = (n: number) => ({ op: "or", conditions: many(n, (i) => ({ op: "isEmpty", fieldDefinitionId: id(i + 1) })) });
    expect(ruleConditionSchema.safeParse(children(RULE_MAX_CHILDREN)).success).toBe(true);
    expect(ruleConditionSchema.safeParse(children(RULE_MAX_CHILDREN + 1)).success).toBe(false);
    expect(ruleConditionSchema.safeParse({ op: "equals", fieldDefinitionId: id(1), value: "x".repeat(RULE_MAX_VALUE_LENGTH + 1) }).success).toBe(false);
  });
  it("repeat occurrence refinements", () => {
    const repeat = (r: Record<string, unknown>) => layoutWith(field(), { repeat: { groupDefinitionId: id(3), allowAdd: true, allowRemove: true, allowReorder: true, ...r } });
    expect(ok(repeat({ minOccurrences: 0, maxOccurrences: 50 }))).toBe(true);
    expect(ok(repeat({ minOccurrences: 3, maxOccurrences: 2 }))).toBe(false);
    expect(ok(repeat({ minOccurrences: 0, maxOccurrences: 51 }))).toBe(false);
    expect(ok(repeat({ minOccurrences: 0, maxOccurrences: 2, allowAdd: false }))).toBe(false);
  });
});

describe("required values, defaults and text safety", () => {
  it("applies the documented defaults, freshly for every parse", () => {
    const first = formLayoutSchema.parse(layoutWith(field()));
    const element = first.sections[0]!.elements[0]!;
    expect(first.title).toBe("Form");
    expect(first.sections[0]!.gridColumns).toBe(4);
    expect(element).toMatchObject({ control: "text", label: "", required: false, layout: { columnSpan: 4, startOnNewRow: false } });
    (element.layout as { columnSpan: number }).columnSpan = 1;
    const second = formLayoutSchema.parse(layoutWith(field()));
    expect(second.sections[0]!.elements[0]!.layout.columnSpan).toBe(4);
    const related = formLayoutSchema.parse(layoutWith({ kind: "relatedFormList", formRelationshipDefinitionId: id(5) })).sections[0]!.elements[0]!;
    expect(related).toMatchObject({ summaryFieldKeys: [], layout: { columnSpan: 4, startOnNewRow: true } });
  });
  it("refuses missing required properties and unknown versions", () => {
    expect(ok({ title: "t", sections: [{ sectionKey: "m", elements: [] }] })).toBe(false);
    expect(ok({ layoutSchemaVersion: 3, sections: [{ sectionKey: "m", elements: [] }] })).toBe(false);
    expect(ok(layoutWith({ kind: "field" }))).toBe(false);
    expect(ok(layoutWith(field({ layout: { columnSpan: 4 } })))).toBe(false);
    expect(ruleConditionSchema.safeParse({ op: "equals", fieldDefinitionId: id(1) }).success).toBe(false); // operand required
    expect(ruleConditionSchema.safeParse({ op: "isEmpty", fieldDefinitionId: id(1), value: "x" }).success).toBe(false); // operand forbidden
  });
  it("refuses angle brackets in layout text and rule values", () => {
    expect(ok(layoutWith(field({ label: "<script>" })))).toBe(false);
    expect(ok({ ...layoutWith(field()), title: "a>b" })).toBe(false);
    expect(ruleConditionSchema.safeParse({ op: "equals", fieldDefinitionId: id(1), value: "<b>" }).success).toBe(false);
  });
});

describe("integers: the server's 32-bit range is accepted; v4 refuses unsafe integers", () => {
  // The only behaviour difference found by the v3/v4 differential comparison: zod v4's int() also refuses integers beyond
  // Number.MAX_SAFE_INTEGER on the two otherwise unbounded fields. The server stores both as 32-bit integers
  // (LayoutValidation.MinLength, LayoutOption.SortOrder), so no persisted layout can contain such a value.
  const option = (sortOrder: number) => layoutWith(field({ control: "select", options: [{ fieldOptionId: id(2), optionKey: "a", label: "A", sortOrder, isActive: true }] }));
  it("accepts the full 32-bit range", () => {
    expect(ok(option(2_147_483_647))).toBe(true);
    expect(ok(option(-2_147_483_648))).toBe(true);
    expect(ok(layoutWith(field({ validation: { minLength: 2_147_483_647 } })))).toBe(true);
  });
  it("refuses unsafe integers and non-integers", () => {
    expect(ok(option(2 ** 53 + 2))).toBe(false);
    expect(ok(option(1.5))).toBe(false);
    expect(ok(layoutWith(field({ validation: { minLength: 2 ** 53 + 2 } })))).toBe(false);
  });
});

describe("error behaviour", () => {
  it("tryParseLayout reports refusals as text, including the schema's own messages", () => {
    const wide = tryParseLayout(JSON.stringify(layoutWith(field({ layout: { columnSpan: 3, startOnNewRow: false, columnStart: 3 } }))));
    expect(wide.ok).toBe(false);
    expect(wide.ok ? "" : wide.error).toMatch(/beyond the four-column grid/);
    expect(tryParseLayout("{").ok).toBe(false);
    expect(() => parseLayout(JSON.stringify(layoutWith(field({ label: "<x>" }))))).toThrow(/Angle brackets are not permitted/);
  });
  it("parseCondition refuses invalid and over-deep rules", () => {
    expect(() => parseCondition({ op: "bogus", fieldDefinitionId: id(1) })).toThrow();
    let deep: Record<string, unknown> = { op: "isEmpty", fieldDefinitionId: id(1) };
    for (let i = 0; i < 6; i++) deep = { op: "not", conditions: [deep] };
    expect(() => parseCondition(deep)).toThrow();
  });
});

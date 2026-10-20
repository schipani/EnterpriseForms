import { z } from "zod";

/**
 * Declarative conditional rules (CLAUDE.md §4 "Rule engine", Phase C2). A condition is a JSON tree over stable field ids
 * with an allow-listed operator set; there is no expression language, script, SQL or URL anywhere in the grammar. This
 * module mirrors Application/FormDefinitions/Layout/FormRules.cs: the shared vectors in rule-vectors.json are run
 * against both implementations. The server is authoritative; the renderer evaluates the same tree for immediate feedback.
 */
export const RULE_MAX_DEPTH = 4;
export const RULE_MAX_NODES = 25;
export const RULE_MAX_CHILDREN = 10;
export const RULE_MAX_SET_VALUES = 50;
export const RULE_MAX_VALUE_LENGTH = 200;

export const GROUP_OPERATORS = ["and", "or", "not"] as const;
export const VALUE_OPERATORS = ["equals", "notEquals", "contains", "greaterThan", "lessThan"] as const;
export const SET_OPERATORS = ["in", "notIn"] as const;
export const PRESENCE_OPERATORS = ["isEmpty", "isNotEmpty"] as const;
export type ComparisonOperator = (typeof VALUE_OPERATORS)[number] | (typeof SET_OPERATORS)[number] | (typeof PRESENCE_OPERATORS)[number];

export interface RuleGroupCondition {
  op: "and" | "or" | "not";
  conditions: RuleCondition[];
}
export interface RuleComparisonCondition {
  op: ComparisonOperator;
  fieldDefinitionId: string;
  value?: string;
  values?: string[];
}
export type RuleCondition = RuleGroupCondition | RuleComparisonCondition;

const operand = z
  .string()
  .max(RULE_MAX_VALUE_LENGTH)
  .refine((text) => !text.includes("<"), "Angle brackets are not permitted in rule values");

const comparisonSchema = z
  .object({
    op: z.enum([...VALUE_OPERATORS, ...SET_OPERATORS, ...PRESENCE_OPERATORS]),
    fieldDefinitionId: z.string().uuid(),
    value: operand.optional(),
    values: z.array(operand).min(1).max(RULE_MAX_SET_VALUES).optional(),
  })
  .strict()
  .refine((c) => {
    if ((VALUE_OPERATORS as readonly string[]).includes(c.op)) {
      return c.value !== undefined && c.values === undefined;
    }
    if ((SET_OPERATORS as readonly string[]).includes(c.op)) {
      return c.values !== undefined && c.value === undefined;
    }
    return c.value === undefined && c.values === undefined;
  }, "The operator does not match the supplied operand");

export const ruleConditionSchema: z.ZodType<RuleCondition> = z.lazy(() =>
  z.union([
    comparisonSchema as unknown as z.ZodType<RuleCondition>,
    z.object({ op: z.enum(["and", "or"]), conditions: z.array(ruleConditionSchema).min(1).max(RULE_MAX_CHILDREN) }).strict() as unknown as z.ZodType<RuleCondition>,
    z.object({ op: z.literal("not"), conditions: z.array(ruleConditionSchema).length(1) }).strict() as unknown as z.ZodType<RuleCondition>,
  ]),
);

/** Depth and node-count limits (the schema cannot count across the whole tree). Returns a message or null. */
export function ruleShapeProblem(condition: RuleCondition): string | null {
  let nodes = 0;
  const walk = (node: RuleCondition, depth: number): string | null => {
    nodes += 1;
    if (depth > RULE_MAX_DEPTH) {
      return `Conditions may be nested at most ${RULE_MAX_DEPTH} levels deep`;
    }
    if (nodes > RULE_MAX_NODES) {
      return `A rule may contain at most ${RULE_MAX_NODES} conditions`;
    }
    if ("conditions" in node) {
      for (const child of node.conditions) {
        const problem = walk(child, depth + 1);
        if (problem) {
          return problem;
        }
      }
    }
    return null;
  };
  return walk(condition, 1);
}

export function parseCondition(json: unknown): RuleCondition {
  const parsed = ruleConditionSchema.parse(json);
  const problem = ruleShapeProblem(parsed);
  if (problem) {
    throw new Error(problem);
  }
  return parsed;
}

/** Every comparison leaf of a condition. */
export function comparisonsOf(condition: RuleCondition | undefined): RuleComparisonCondition[] {
  if (!condition) {
    return [];
  }
  if ("conditions" in condition) {
    return condition.conditions.flatMap((child) => comparisonsOf(child));
  }
  return [condition];
}

/** The slice of a field the evaluator needs. */
export interface RuleField {
  fieldDefinitionId: string;
  fieldKey: string;
  fieldDataType: string;
  optionSelectionMode?: string | undefined;
}

/** Operators that make sense for a field (mirrors FormRules.AllowedOperators). */
export function allowedOperators(field: Pick<RuleField, "fieldDataType" | "optionSelectionMode">): ComparisonOperator[] {
  switch (field.fieldDataType) {
    case "String":
      return ["equals", "notEquals", "contains", "in", "notIn", "isEmpty", "isNotEmpty"];
    case "Integer":
    case "Decimal":
    case "Date":
    case "DateTime":
      return ["equals", "notEquals", "greaterThan", "lessThan", "isEmpty", "isNotEmpty"];
    case "Boolean":
      return ["equals", "notEquals"];
    case "Guid":
      return ["equals", "notEquals", "in", "notIn", "isEmpty", "isNotEmpty"];
    case "Option":
      return field.optionSelectionMode === "Multiple" ? ["equals", "notEquals", "contains", "in", "notIn", "isEmpty", "isNotEmpty"] : ["equals", "notEquals", "in", "notIn", "isEmpty", "isNotEmpty"];
    default:
      return [];
  }
}

export const OPERATOR_LABEL: Record<ComparisonOperator, string> = {
  equals: "is",
  notEquals: "is not",
  contains: "contains",
  greaterThan: "is greater than",
  lessThan: "is less than",
  in: "is one of",
  notIn: "is none of",
  isEmpty: "is empty",
  isNotEmpty: "is not empty",
};

/** Wire value of a multi-select: option keys joined by comma. */
export function splitSelection(wire: string): string[] {
  return [...new Set(wire.split(",").map((part) => part.trim()).filter((part) => part.length > 0))];
}

// Alternatives are mutually exclusive (no overlapping quantifiers), so matching is linear in the input length.
const DECIMAL = /^[+-]?(?:\d+(?:\.\d*)?|\.\d+)$/;
const DATE = /^(\d{4})-(\d{2})-(\d{2})$/;
const INSTANT = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2})?(Z|[+-]\d{2}:\d{2})$/;
const GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const DECIMAL_SCALE = 18;

/** Exact decimal as a scaled BigInt (no floating point: 15-digit values with four fraction digits do not fit a double). */
function parseDecimal(wire: string): bigint | null {
  if (!DECIMAL.test(wire)) {
    return null;
  }
  const negative = wire.startsWith("-");
  const unsigned = wire.replace(/^[+-]/, "");
  const [whole = "0", fraction = ""] = unsigned.split(".");
  if (fraction.length > DECIMAL_SCALE) {
    return null;
  }
  const scaled = BigInt((whole || "0") + fraction.padEnd(DECIMAL_SCALE, "0"));
  return negative ? -scaled : scaled;
}

function parseDay(wire: string): number | null {
  const match = DATE.exec(wire);
  if (!match) {
    return null;
  }
  const [year, month, day] = [Number(match[1]), Number(match[2]), Number(match[3])];
  const time = Date.UTC(year, month - 1, day);
  const check = new Date(time);
  if (check.getUTCFullYear() !== year || check.getUTCMonth() !== month - 1 || check.getUTCDate() !== day) {
    return null;
  }
  return time / 86_400_000;
}

type Comparable = bigint | number | string;

function parseComparable(field: RuleField, wire: string): Comparable | null {
  switch (field.fieldDataType) {
    case "Integer":
    case "Decimal":
      return parseDecimal(wire);
    case "Date":
      return parseDay(wire);
    case "DateTime": {
      if (!INSTANT.test(wire)) {
        return null;
      }
      const parsed = Date.parse(wire);
      return Number.isNaN(parsed) ? null : parsed;
    }
    case "Boolean":
      return wire === "true" ? 1 : wire === "false" || wire === "" ? 0 : null;
    case "Guid":
      return GUID.test(wire) ? wire.toLowerCase() : null;
    default:
      return wire;
  }
}

function compare3(field: RuleField, left: string, right: string): number | null {
  const a = parseComparable(field, left);
  const b = parseComparable(field, right);
  if (typeof a === "string" && typeof b === "string") {
    return a < b ? -1 : a > b ? 1 : 0;
  }
  if (typeof a === "bigint" && typeof b === "bigint") {
    return a < b ? -1 : a > b ? 1 : 0;
  }
  if (typeof a === "number" && typeof b === "number") {
    return a < b ? -1 : a > b ? 1 : 0;
  }
  return null;
}

function compare(field: RuleField, condition: RuleComparisonCondition, actual: string): boolean {
  const empty = actual.length === 0;
  if (condition.op === "isEmpty") {
    return empty;
  }
  if (condition.op === "isNotEmpty") {
    return !empty;
  }
  const negative = condition.op === "notEquals" || condition.op === "notIn";
  if (empty && field.fieldDataType !== "Boolean") {
    return negative;
  }
  const value = condition.value ?? "";
  const values = condition.values ?? [];
  if (field.fieldDataType === "Option" && field.optionSelectionMode === "Multiple") {
    const selected = new Set(splitSelection(actual));
    const wanted = splitSelection(value);
    switch (condition.op) {
      case "equals":
        return selected.size === wanted.length && wanted.every((key) => selected.has(key));
      case "notEquals":
        return !(selected.size === wanted.length && wanted.every((key) => selected.has(key)));
      case "contains":
        return wanted.every((key) => selected.has(key));
      case "in":
        return values.some((key) => selected.has(key));
      case "notIn":
        return !values.some((key) => selected.has(key));
      default:
        return false;
    }
  }
  switch (condition.op) {
    case "equals":
      return compare3(field, actual, value) === 0;
    case "notEquals":
      return compare3(field, actual, value) !== 0;
    case "greaterThan": {
      const order = compare3(field, actual, value);
      return order !== null && order > 0;
    }
    case "lessThan": {
      const order = compare3(field, actual, value);
      return order !== null && order < 0;
    }
    case "contains":
      return actual.toLowerCase().includes(value.toLowerCase());
    case "in":
      return values.some((candidate) => compare3(field, actual, candidate) === 0);
    case "notIn":
      return !values.some((candidate) => compare3(field, actual, candidate) === 0);
    default:
      return false;
  }
}

/** Evaluates one condition; `read` returns a field's current wire value ("" when empty). Unknown fields never satisfy a rule. */
export function evaluateCondition(condition: RuleCondition, fieldsById: ReadonlyMap<string, RuleField>, read: (fieldDefinitionId: string) => string): boolean {
  if ("conditions" in condition) {
    switch (condition.op) {
      case "and":
        return condition.conditions.every((child) => evaluateCondition(child, fieldsById, read));
      case "or":
        return condition.conditions.some((child) => evaluateCondition(child, fieldsById, read));
      default:
        return !evaluateCondition(condition.conditions[0]!, fieldsById, read);
    }
  }
  const field = fieldsById.get(condition.fieldDefinitionId);
  return field ? compare(field, condition, read(condition.fieldDefinitionId)) : false;
}

export interface RuleElement {
  fieldDefinitionId: string;
  required?: boolean | undefined;
  visibleWhen?: RuleCondition | undefined;
  requiredWhen?: RuleCondition | undefined;
}

export interface FieldRuleState {
  visible: boolean;
  required: boolean;
}

/**
 * Visibility and requiredness of every element, from values keyed by field id. A field hidden by a rule counts as empty
 * for the rules that read it, so hiding propagates through chains exactly as on the server.
 */
export function evaluateRuleStates(elements: readonly RuleElement[], fieldsById: ReadonlyMap<string, RuleField>, values: ReadonlyMap<string, string>): Map<string, FieldRuleState> {
  const byId = new Map(elements.map((element) => [element.fieldDefinitionId, element]));
  const visible = new Map<string, boolean>();
  const inProgress = new Set<string>();
  const isVisible = (id: string): boolean => {
    const known = visible.get(id);
    if (known !== undefined) {
      return known;
    }
    const element = byId.get(id);
    if (!element?.visibleWhen) {
      visible.set(id, true);
      return true;
    }
    if (inProgress.has(id)) {
      return false; // a cycle cannot be saved; fail closed
    }
    inProgress.add(id);
    const result = evaluateCondition(element.visibleWhen, fieldsById, effective);
    inProgress.delete(id);
    visible.set(id, result);
    return result;
  };
  const effective = (id: string): string => (isVisible(id) ? (values.get(id) ?? "") : "");
  const states = new Map<string, FieldRuleState>();
  for (const element of elements) {
    const shown = isVisible(element.fieldDefinitionId);
    const required = shown && (element.required === true || (element.requiredWhen !== undefined && evaluateCondition(element.requiredWhen, fieldsById, effective)));
    states.set(element.fieldDefinitionId, { visible: shown, required });
  }
  return states;
}

/** Plain-language summary of a condition for the designer ("Status is Open and Age is greater than 17"). */
export function describeCondition(condition: RuleCondition, fieldsById: ReadonlyMap<string, RuleField & { fieldDisplayName?: string }>, optionLabel?: (fieldId: string, key: string) => string): string {
  if ("conditions" in condition) {
    if (condition.op === "not") {
      return `not (${describeCondition(condition.conditions[0]!, fieldsById, optionLabel)})`;
    }
    const joiner = condition.op === "and" ? " and " : " or ";
    const parts = condition.conditions.map((child) => describeCondition(child, fieldsById, optionLabel));
    return parts.length === 1 ? parts[0]! : `(${parts.join(joiner)})`;
  }
  const field = fieldsById.get(condition.fieldDefinitionId);
  const name = field?.fieldDisplayName ?? field?.fieldKey ?? "unknown field";
  const label = (key: string) => (optionLabel ? optionLabel(condition.fieldDefinitionId, key) : key);
  const operandText = condition.values ? condition.values.map(label).join(", ") : condition.value !== undefined ? label(condition.value) : "";
  return `${name} ${OPERATOR_LABEL[condition.op]}${operandText ? ` ${operandText}` : ""}`;
}

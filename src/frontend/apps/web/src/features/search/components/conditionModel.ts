import type { FieldSearchCapabilitiesResponse, FieldSearchCapabilityField, FieldSearchCondition, FieldSearchRequest } from "@enterpriseforms/api-client";

/**
 * Advanced Search condition state (Phase C7). Conditions are always combined with AND (V1, decision C-56 D6); there is no
 * OR, grouping or expression. Which fields and operators exist comes only from the server capability response — nothing
 * here infers searchability from a field's type. Values are kept as the text the person typed and converted to the
 * invariant wire text only when a request is built.
 */

/** V1 ceiling (D4/D6); the server may announce a lower one in `maximumConditions`. */
export const MAX_CONDITIONS_V1 = 5;

export interface ConditionDraft {
  /** Stable React key and accessible-name suffix; never sent to the server. */
  key: string;
  fieldDefinitionId: string | undefined;
  operator: string | undefined;
  value: string;
  lowerBound: string;
  upperBound: string;
}

export type ConditionErrors = Partial<Record<"field" | "operator" | "value" | "lowerBound" | "upperBound", string>>;

let sequence = 0;

export function newCondition(): ConditionDraft {
  sequence += 1;
  return { key: `condition-${sequence}`, fieldDefinitionId: undefined, operator: undefined, value: "", lowerBound: "", upperBound: "" };
}

export function maximumConditions(capabilities: FieldSearchCapabilitiesResponse | undefined): number {
  const announced = Number(capabilities?.maximumConditions ?? MAX_CONDITIONS_V1);
  return Number.isFinite(announced) && announced >= 1 ? Math.min(announced, MAX_CONDITIONS_V1) : MAX_CONDITIONS_V1;
}

export function addCondition(conditions: readonly ConditionDraft[], maximum: number): ConditionDraft[] {
  return conditions.length >= maximum ? [...conditions] : [...conditions, newCondition()];
}

/** Removing the only condition leaves one empty condition, so the section never shows zero rows. */
export function removeCondition(conditions: readonly ConditionDraft[], key: string): ConditionDraft[] {
  const remaining = conditions.filter((condition) => condition.key !== key);
  return remaining.length > 0 ? remaining : [newCondition()];
}

export function fieldFor(capabilities: FieldSearchCapabilitiesResponse | undefined, fieldDefinitionId: string | undefined): FieldSearchCapabilityField | undefined {
  return fieldDefinitionId === undefined ? undefined : capabilities?.fields.find((field) => field.fieldDefinitionId === fieldDefinitionId);
}

/** Choosing a field resets the condition to that field's first server-allowed operator and clears the value. */
export function setField(conditions: readonly ConditionDraft[], key: string, fieldDefinitionId: string, capabilities: FieldSearchCapabilitiesResponse | undefined): ConditionDraft[] {
  const field = fieldFor(capabilities, fieldDefinitionId);
  return conditions.map((condition) => (condition.key === key ? { ...condition, fieldDefinitionId: field?.fieldDefinitionId, operator: field?.operators[0], value: "", lowerBound: "", upperBound: "" } : condition));
}

export function setOperator(conditions: readonly ConditionDraft[], key: string, operator: string): ConditionDraft[] {
  return conditions.map((condition) => {
    if (condition.key !== key) {
      return condition;
    }
    const sameShape = arity(condition.operator) === arity(operator);
    return { ...condition, operator, ...(sameShape ? {} : { value: "", lowerBound: "", upperBound: "" }) };
  });
}

export function setValue(conditions: readonly ConditionDraft[], key: string, part: "value" | "lowerBound" | "upperBound", text: string): ConditionDraft[] {
  return conditions.map((condition) => (condition.key === key ? { ...condition, [part]: text } : condition));
}

export type Arity = "none" | "single" | "range";

export function arity(operator: string | undefined): Arity {
  return operator === "isTrue" || operator === "isFalse" ? "none" : operator === "between" ? "range" : "single";
}

const LABELS: Record<string, string> = {
  equals: "Equals",
  startsWith: "Starts with",
  contains: "Contains",
  greaterThan: "Greater than",
  greaterThanOrEqual: "Greater than or equal to",
  lessThan: "Less than",
  lessThanOrEqual: "Less than or equal to",
  between: "Between (inclusive)",
  before: "Before",
  after: "After",
  is: "Is",
  includes: "Includes",
  isTrue: "Is Yes",
  isFalse: "Is No",
};

export function operatorLabel(operator: string, valueType: string | undefined): string {
  if (operator === "equals" && valueType === "Date") {
    return "On";
  }
  if (operator === "equals" && valueType === "String") {
    return "Is exactly";
  }
  return LABELS[operator] ?? operator;
}

const DECIMAL = /^-?\d{1,28}(\.\d{1,10})?$/;
const INTEGER = /^-?\d{1,18}$/;
const DATE = /^\d{4}-\d{2}-\d{2}$/;

/** Client checks mirror the server's request-shape validation for quick feedback; the server remains the authority. */
export function validateCondition(condition: ConditionDraft, capabilities: FieldSearchCapabilitiesResponse | undefined): ConditionErrors {
  const field = fieldFor(capabilities, condition.fieldDefinitionId);
  if (!field) {
    return { field: "Choose a field." };
  }
  if (!condition.operator || !field.operators.includes(condition.operator)) {
    return { operator: "Choose a condition." };
  }
  const kind = arity(condition.operator);
  if (kind === "none") {
    return {};
  }
  const check = (text: string, label: string): string | undefined => {
    const trimmed = text.trim();
    if (trimmed === "") {
      return `Enter ${label}.`;
    }
    switch (field.valueType) {
      case "String": {
        const maximum = Number(field.maximumTextLength ?? 200);
        const minimum = condition.operator === "contains" || condition.operator === "startsWith" ? Number(field.minimumTextLength ?? 3) : 1;
        return trimmed.length < minimum ? `Enter at least ${minimum} characters.` : text.length > maximum ? `Enter at most ${maximum} characters.` : undefined;
      }
      case "Integer":
        return INTEGER.test(trimmed) ? undefined : "Enter a whole number without separators, for example 1250.";
      case "Decimal":
        return DECIMAL.test(trimmed) ? undefined : "Enter a number using a dot for decimals and no separators, for example 1250.75.";
      case "Date":
        return DATE.test(trimmed) ? undefined : "Enter a date.";
      case "DateTime":
        return Number.isNaN(Date.parse(trimmed)) ? "Enter a date and time." : undefined;
      case "Option":
        return field.options.some((option) => option.fieldOptionId === trimmed) ? undefined : "Choose an option.";
      default:
        return undefined;
    }
  };
  if (kind === "range") {
    const errors: ConditionErrors = {};
    const lower = check(condition.lowerBound, "the lower bound");
    const upper = check(condition.upperBound, "the upper bound");
    if (lower) errors.lowerBound = lower;
    if (upper) errors.upperBound = upper;
    if (!lower && !upper && compare(field.valueType, condition.lowerBound.trim(), condition.upperBound.trim()) > 0) {
      errors.upperBound = "The upper bound must not be before the lower bound.";
    }
    return errors;
  }
  const value = check(condition.value, "a value");
  return value ? { value } : {};
}

function compare(valueType: string, lower: string, upper: string): number {
  if (valueType === "Integer" || valueType === "Decimal") {
    return Number(lower) - Number(upper);
  }
  if (valueType === "DateTime") {
    return Date.parse(lower) - Date.parse(upper);
  }
  return lower < upper ? -1 : lower > upper ? 1 : 0;
}

export function validateConditions(conditions: readonly ConditionDraft[], capabilities: FieldSearchCapabilitiesResponse | undefined): Map<string, ConditionErrors> {
  const result = new Map<string, ConditionErrors>();
  for (const condition of conditions) {
    const errors = validateCondition(condition, capabilities);
    if (Object.keys(errors).length > 0) {
      result.set(condition.key, errors);
    }
  }
  return result;
}

/** A `datetime-local` value is wall-clock time in the browser's zone; the wire needs an unambiguous UTC instant. */
function wireText(valueType: string, text: string): string {
  const trimmed = valueType === "String" ? text : text.trim();
  return valueType === "DateTime" ? new Date(trimmed).toISOString() : trimmed;
}

export interface SearchFilters {
  recordStatus?: string | undefined;
  includeArchived?: boolean | undefined;
  includeChildren?: boolean | undefined;
  sortBy?: string | undefined;
  sortDirection?: string | undefined;
  pageSize: number;
}

/** Builds the request body; call only after `validateConditions` returned no errors. */
export function toFieldSearchRequest(formDefinitionId: string, conditions: readonly ConditionDraft[], capabilities: FieldSearchCapabilitiesResponse, filters: SearchFilters): FieldSearchRequest {
  const wire: FieldSearchCondition[] = conditions.map((condition) => {
    const field = fieldFor(capabilities, condition.fieldDefinitionId)!;
    const kind = arity(condition.operator);
    return {
      fieldDefinitionId: field.fieldDefinitionId,
      operator: condition.operator!,
      valueType: field.valueType,
      ...(kind === "single" ? { value: wireText(field.valueType, condition.value) } : {}),
      ...(kind === "range" ? { lowerBound: wireText(field.valueType, condition.lowerBound), upperBound: wireText(field.valueType, condition.upperBound) } : {}),
    };
  });
  return {
    formDefinitionId,
    conditions: wire,
    ...(filters.recordStatus ? { recordStatus: filters.recordStatus } : {}),
    includeArchived: Boolean(filters.includeArchived),
    includeChildren: Boolean(filters.includeChildren),
    ...(filters.sortBy ? { sortBy: filters.sortBy } : {}),
    ...(filters.sortDirection ? { sortDirection: filters.sortDirection } : {}),
    pageSize: filters.pageSize,
  };
}

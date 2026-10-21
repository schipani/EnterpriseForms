import type { FieldElement, FormLayout } from "./layout";
import { choicesFor, dependentViolation } from "./options";
import { evaluateRuleStates, splitSelection, type FieldRuleState, type RuleField } from "./rules";

export interface RendererField {
  fieldDefinitionId: string;
  fieldKey: string;
  fieldDisplayName: string;
  fieldDataType: string;
  /** True when the field is stored application-encrypted (shown as a badge; never a search capability). */
  applicationEncrypted?: boolean;
  /** Dropdowns: "Single" (default) or "Multiple". Permanent for the field. */
  optionSelectionMode?: string;
  options: Array<{ fieldOptionId: string; optionKey: string; optionDisplayText: string; optionSortOrder: number; isActive: boolean }>;
  /** Fields of a repeating section (Phase C3): one value per entry. Permanent for the field. */
  groupDefinitionId?: string | null | undefined;
}

export type ValueMap = Record<string, string>;
export type ErrorMap = Record<string, string>;

const DATE = /^\d{4}-\d{2}-\d{2}$/;
/** Wire form of a DateTime value: ISO 8601 instant in UTC at second precision (the server rejects sub-second input). */
const DATE_TIME = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2})?Z$/;
const GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const INTEGER = /^-?\d{1,18}$/;
const DECIMAL = /^-?\d{1,15}(\.\d{1,4})?$/;

export interface RuleContext {
  /** Visibility, required-if and clear rules apply only to a complete projection; a partial (shared) view never evaluates them. */
  applyRules: boolean;
}

/** Field id → current wire value, from the values keyed by field key. */
export function valuesById(fields: readonly RendererField[], values: ValueMap): Map<string, string> {
  return new Map(fields.map((field) => [field.fieldDefinitionId, values[field.fieldKey] ?? ""]));
}

export function ruleFieldsOf(fields: readonly RendererField[]): Map<string, RuleField> {
  return new Map(fields.map((field) => [field.fieldDefinitionId, { fieldDefinitionId: field.fieldDefinitionId, fieldKey: field.fieldKey, fieldDataType: field.fieldDataType, optionSelectionMode: field.optionSelectionMode }]));
}

/** Rule state of every placed field element for the given values (everything visible/as-authored when rules do not apply). */
export function ruleStatesFor(layout: FormLayout, fields: readonly RendererField[], values: ValueMap, applyRules = true) {
  const elements = layout.sections.flatMap((section) => section.elements).filter((element): element is FieldElement => element.kind === "field");
  if (!applyRules) {
    return new Map(elements.map((element) => [element.fieldDefinitionId, { visible: true, required: element.required === true }]));
  }
  return evaluateRuleStates(elements, ruleFieldsOf(fields), valuesById(fields, values));
}

/**
 * Client-side mirror of the server rules (FieldValueCodec + CandidateBuilder) for immediate feedback. The server
 * re-validates every value; nothing here grants or withholds authorization.
 */
export function validateValues(layout: FormLayout, fields: readonly RendererField[], values: ValueMap, scope: "create" | "edit" = "create", context: RuleContext = { applyRules: true }): ErrorMap {
  const errors: ErrorMap = {};
  const byId = new Map(fields.map((field) => [field.fieldDefinitionId, field]));
  const states = ruleStatesFor(layout, fields, values, context.applyRules);
  for (const section of layout.sections) {
    if (section.repeat) {
      continue; // entries of a repeating section are validated per entry (validateGroups)
    }
    for (const element of section.elements) {
      if (element.kind !== "field") {
        continue;
      }
      const field = byId.get(element.fieldDefinitionId);
      const message = field ? validateElement(element, field, values, states.get(element.fieldDefinitionId), scope === "create" || field.fieldKey in values, context.applyRules, byId) : undefined;
      if (message && field) {
        errors[field.fieldKey] = message;
      }
    }
  }
  return errors;
}

/**
 * One field element against the values of its scope (the record, or one entry merged with the record's values), in the
 * server's order: hidden → nothing to check; read-only → the server keeps it; missing → required?; present → type and
 * format rules, then (full projections only) the dependent-dropdown filter.
 */
export function validateElement(element: FieldElement, field: RendererField, scopeValues: ValueMap, state: FieldRuleState | undefined, requireMissing: boolean, applyRules: boolean, byId: ReadonlyMap<string, RendererField>): string | undefined {
  if (state && !state.visible) {
    return undefined; // hidden by a rule: nothing is required, and its (retained) value is not re-validated here
  }
  if (element.readOnly) {
    return undefined; // the user cannot change it; the server applies its default and keeps its value
  }
  const raw = scopeValues[field.fieldKey];
  if (raw === undefined || raw === "") {
    return state?.required && requireMissing ? `${labelOf(element, field)} is required` : undefined;
  }
  const message = validateOne(element, field, raw);
  if (message || !applyRules || !element.optionFilter) {
    return message;
  }
  const parent = byId.get(element.optionFilter.parentFieldDefinitionId);
  if (parent && dependentViolation(element.optionFilter, field, raw, scopeValues[parent.fieldKey] ?? "") !== undefined) {
    return `${labelOf(element, field)}: the selection is not available for the chosen ${parent.fieldDisplayName}; choose another`;
  }
  return undefined;
}

function labelOf(element: FieldElement, field: RendererField): string {
  return element.label || field.fieldDisplayName;
}

function validateOne(element: FieldElement, field: RendererField, raw: string): string | undefined {
  const label = labelOf(element, field);
  const rules = element.validation ?? {};
  switch (field.fieldDataType) {
    case "String": {
      if (raw.length > (rules.maxLength ?? 4000)) {
        return `${label} must be at most ${rules.maxLength ?? 4000} characters`;
      }
      if (rules.minLength !== undefined && raw.length < rules.minLength) {
        return `${label} must be at least ${rules.minLength} characters`;
      }
      if (rules.pattern) {
        try {
          if (!new RegExp(`^(?:${rules.pattern})$`, "u").test(raw)) {
            return `${label} does not match the required format`;
          }
        } catch {
          return undefined; // an invalid pattern is rejected by the server when the layout is saved
        }
      }
      return undefined;
    }
    case "Integer": {
      if (!INTEGER.test(raw)) {
        return `${label} must be a whole number`;
      }
      const value = Number(raw);
      if (rules.minimum !== undefined && value < rules.minimum) {
        return `${label} must be at least ${rules.minimum}`;
      }
      if (rules.maximum !== undefined && value > rules.maximum) {
        return `${label} must be at most ${rules.maximum}`;
      }
      return undefined;
    }
    case "Decimal": {
      if (!DECIMAL.test(raw)) {
        return `${label} must be a number with at most four decimal places`;
      }
      const value = Number(raw);
      if (rules.minimum !== undefined && value < rules.minimum) {
        return `${label} must be at least ${rules.minimum}`;
      }
      if (rules.maximum !== undefined && value > rules.maximum) {
        return `${label} must be at most ${rules.maximum}`;
      }
      return undefined;
    }
    case "Date": {
      if (!DATE.test(raw) || Number.isNaN(Date.parse(`${raw}T00:00:00Z`))) {
        return `${label} must be a date in YYYY-MM-DD form`;
      }
      if (rules.minDate && raw < rules.minDate) {
        return `${label} must be on or after ${rules.minDate}`;
      }
      if (rules.maxDate && raw > rules.maxDate) {
        return `${label} must be on or before ${rules.maxDate}`;
      }
      return undefined;
    }
    case "DateTime": {
      if (!DATE_TIME.test(raw) || Number.isNaN(Date.parse(raw))) {
        return `${label} must be a date and time`;
      }
      const day = raw.slice(0, 10);
      if (rules.minDate && day < rules.minDate) {
        return `${label} must be on or after ${rules.minDate}`;
      }
      if (rules.maxDate && day > rules.maxDate) {
        return `${label} must be on or before ${rules.maxDate}`;
      }
      return undefined;
    }
    case "Guid":
      return GUID.test(raw) && raw !== "00000000-0000-0000-0000-000000000000" ? undefined : `${label} must be a UUID such as 0199a2b3-c4d5-7e6f-8123-456789abcdef`;
    case "Boolean":
      return raw === "true" || raw === "false" ? undefined : `${label} must be checked or unchecked`;
    case "Option": {
      const keys = field.optionSelectionMode === "Multiple" ? splitSelection(raw) : [raw];
      if (keys.length === 0) {
        return undefined;
      }
      const choices = choicesFor(element, field, keys);
      const catalog = new Map(field.options.map((option) => [option.optionKey, option]));
      for (const key of keys) {
        if (!catalog.has(key)) {
          return `${label} must be one of the listed options`;
        }
        if (!catalog.get(key)!.isActive || choices.find((choice) => choice.optionKey === key)?.isActive === false) {
          return `${label}: the selected option is no longer available; choose another`;
        }
      }
      return undefined;
    }
    default:
      return undefined;
  }
}

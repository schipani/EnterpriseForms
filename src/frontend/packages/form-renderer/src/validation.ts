import type { FieldElement, FormLayout } from "./layout";

export interface RendererField {
  fieldDefinitionId: string;
  fieldKey: string;
  fieldDisplayName: string;
  fieldDataType: string;
  options: Array<{ fieldOptionId: string; optionKey: string; optionDisplayText: string; optionSortOrder: number; isActive: boolean }>;
}

export type ValueMap = Record<string, string>;
export type ErrorMap = Record<string, string>;

const DATE = /^\d{4}-\d{2}-\d{2}$/;
/** Wire form of a DateTime value: ISO 8601 instant in UTC at second precision (the server rejects sub-second input). */
const DATE_TIME = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2})?Z$/;
const GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const INTEGER = /^-?\d{1,18}$/;
const DECIMAL = /^-?\d{1,15}(\.\d{1,4})?$/;

/**
 * Client-side mirror of the server rules (FieldValueCodec + CandidateBuilder) for immediate feedback. The server
 * re-validates every value; nothing here grants or withholds authorization.
 */
export function validateValues(layout: FormLayout, fields: readonly RendererField[], values: ValueMap, scope: "create" | "edit" = "create"): ErrorMap {
  const errors: ErrorMap = {};
  const byId = new Map(fields.map((field) => [field.fieldDefinitionId, field]));
  for (const section of layout.sections) {
    for (const element of section.elements) {
      if (element.kind !== "field") {
        continue;
      }
      const field = byId.get(element.fieldDefinitionId);
      if (!field) {
        continue;
      }
      const raw = values[field.fieldKey];
      const present = raw !== undefined && raw !== "";
      if (!present) {
        if (element.required && (scope === "create" || field.fieldKey in values)) {
          errors[field.fieldKey] = `${labelOf(element, field)} is required`;
        }
        continue;
      }
      const message = validateOne(element, field, raw);
      if (message) {
        errors[field.fieldKey] = message;
      }
    }
  }
  return errors;
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
      const option = field.options.find((candidate) => candidate.optionKey === raw);
      if (!option) {
        return `${label} must be one of the listed options`;
      }
      return option.isActive ? undefined : `${label}: the selected option is no longer available; choose another`;
    }
    default:
      return undefined;
  }
}

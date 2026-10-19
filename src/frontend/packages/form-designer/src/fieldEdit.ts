import type { DocumentElement, FieldElement, FormLayout, LayoutElement } from "@enterpriseforms/form-renderer";

/**
 * Pure model behind the Edit Field dialog (unit-tested; no React). A field has a permanent identity (id, key, storage
 * type) and per-version presentation in the layout draft: label, help text, required flag, validation rules and — for a
 * document collection — the upload restrictions. Everything here edits the *draft* element; published versions are
 * immutable snapshots and change only when the draft is published as a new version.
 */

export interface FieldLocation {
  sectionIndex: number;
  index: number;
  element: FieldElement | DocumentElement;
}

export interface FieldEditValues {
  label: string;
  helpText: string;
  required: boolean;
  minLength: string;
  maxLength: string;
  minimum: string;
  maximum: string;
  minDate: string;
  maxDate: string;
  maxFiles: string;
  maxFileSizeMb: string;
  allowedContentTypes: string;
}

export type FieldEditErrors = Partial<Record<keyof FieldEditValues, string>>;

export function locateField(layout: FormLayout, fieldDefinitionId: string): FieldLocation | null {
  for (let sectionIndex = 0; sectionIndex < layout.sections.length; sectionIndex++) {
    const elements = layout.sections[sectionIndex]!.elements;
    for (let index = 0; index < elements.length; index++) {
      const element = elements[index]!;
      if ((element.kind === "field" || element.kind === "documentCollection") && element.fieldDefinitionId === fieldDefinitionId) {
        return { sectionIndex, index, element };
      }
    }
  }
  return null;
}

const text = (value: number | undefined) => (value === undefined ? "" : String(value));

export function toEditValues(element: FieldElement | DocumentElement | null, catalogName: string): FieldEditValues {
  const rules = element?.validation ?? {};
  const upload = element?.kind === "documentCollection" ? element.upload : undefined;
  return {
    label: element?.label || catalogName,
    helpText: element?.helpText ?? "",
    required: element?.required ?? false,
    minLength: text(rules.minLength),
    maxLength: text(rules.maxLength),
    minimum: text(rules.minimum),
    maximum: text(rules.maximum),
    minDate: rules.minDate ?? "",
    maxDate: rules.maxDate ?? "",
    maxFiles: text(upload?.maxFiles ?? (element?.kind === "documentCollection" ? 20 : undefined)),
    maxFileSizeMb: upload?.maxFileSizeBytes === undefined ? (element?.kind === "documentCollection" ? "50" : "") : String(Math.round(upload.maxFileSizeBytes / (1024 * 1024))),
    allowedContentTypes: (upload?.allowedContentTypes ?? []).join(", "),
  };
}

const integer = /^-?\d+$/;
const decimal = /^-?\d+(\.\d+)?$/;

/** Client-side mirror of the server's rule/type compatibility checks; the server remains authoritative. */
export function validateEditValues(values: FieldEditValues, dataType: string): FieldEditErrors {
  const errors: FieldEditErrors = {};
  if (values.label.trim().length === 0 || values.label.length > 200) {
    errors.label = "Enter a label of 1 to 200 characters";
  }
  if (/[<>]/.test(values.label) || /[<>]/.test(values.helpText)) {
    errors.helpText = "Angle brackets are not permitted";
  }
  if (values.helpText.length > 1000) {
    errors.helpText = "Help text may be at most 1000 characters";
  }
  if (dataType === "String") {
    if (values.minLength && !/^\d+$/.test(values.minLength)) errors.minLength = "Enter a whole number";
    if (values.maxLength && (!/^\d+$/.test(values.maxLength) || Number(values.maxLength) < 1 || Number(values.maxLength) > 4000)) errors.maxLength = "Enter a whole number from 1 to 4000";
    if (!errors.minLength && !errors.maxLength && values.minLength && values.maxLength && Number(values.minLength) > Number(values.maxLength)) errors.minLength = "The minimum length cannot exceed the maximum length";
  }
  if (dataType === "Integer" || dataType === "Decimal") {
    const pattern = dataType === "Integer" ? integer : decimal;
    if (values.minimum && !pattern.test(values.minimum)) errors.minimum = dataType === "Integer" ? "Enter a whole number" : "Enter a number";
    if (values.maximum && !pattern.test(values.maximum)) errors.maximum = dataType === "Integer" ? "Enter a whole number" : "Enter a number";
    if (!errors.minimum && !errors.maximum && values.minimum && values.maximum && Number(values.minimum) > Number(values.maximum)) errors.minimum = "The minimum cannot exceed the maximum";
  }
  if (dataType === "Date" || dataType === "DateTime") {
    if (values.minDate && values.maxDate && values.minDate > values.maxDate) errors.minDate = "The earliest date cannot be after the latest date";
  }
  if (dataType === "File") {
    if (!/^\d+$/.test(values.maxFiles) || Number(values.maxFiles) < 1 || Number(values.maxFiles) > 100) errors.maxFiles = "Enter a whole number from 1 to 100";
    if (!/^\d+$/.test(values.maxFileSizeMb) || Number(values.maxFileSizeMb) < 1 || Number(values.maxFileSizeMb) > 500) errors.maxFileSizeMb = "Enter a whole number from 1 to 500";
  }
  return errors;
}

/** Applies validated edit values to the draft element; rules that do not apply to the storage type are never written. */
export function applyEditValues(element: LayoutElement, values: FieldEditValues, dataType: string): LayoutElement {
  if (element.kind === "relatedFormList") {
    return element;
  }
  const rules: NonNullable<FieldElement["validation"]> = {};
  if (dataType === "String") {
    if (values.minLength) rules.minLength = Number(values.minLength);
    if (values.maxLength) rules.maxLength = Number(values.maxLength);
    if (element.validation?.pattern) rules.pattern = element.validation.pattern;
  } else if (dataType === "Integer" || dataType === "Decimal") {
    if (values.minimum) rules.minimum = Number(values.minimum);
    if (values.maximum) rules.maximum = Number(values.maximum);
  } else if (dataType === "Date" || dataType === "DateTime") {
    if (values.minDate) rules.minDate = values.minDate;
    if (values.maxDate) rules.maxDate = values.maxDate;
  }
  const help = values.helpText.trim();
  const withoutHelp = Object.fromEntries(Object.entries(element).filter(([key]) => key !== "helpText")) as typeof element;
  const withHelp = { ...withoutHelp, label: values.label.trim(), required: values.required, validation: rules, ...(help ? { helpText: help } : {}) };
  if (element.kind === "documentCollection") {
    return {
      ...withHelp,
      upload: {
        ...(element.upload ?? {}),
        maxFiles: Number(values.maxFiles),
        maxFileSizeBytes: Number(values.maxFileSizeMb) * 1024 * 1024,
        allowedContentTypes: values.allowedContentTypes.split(",").map((type) => type.trim()).filter(Boolean),
      },
    } as DocumentElement;
  }
  return withHelp as FieldElement;
}

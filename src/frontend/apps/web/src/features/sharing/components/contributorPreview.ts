import type { RecordProjectionResponse } from "@enterpriseforms/api-client";
import { comparisonsOf, tryParseLayout, type FieldElement } from "@enterpriseforms/form-renderer";

type ProjectionField = RecordProjectionResponse["fields"][number];

/**
 * Phase C5 share-dialog preview: which of the selected fields a contributor could NOT change, and why — a client mirror
 * of the server's static eligibility (SharedFieldEligibility: read-only, restricted, multi-select, document, repeating
 * section, and rules R1/R2 against the selected field set). Advisory only: the server and the Authority decide on save.
 */
export function contributorViewOnlyReasons(layoutJson: string, fields: readonly ProjectionField[], selectedKeys: ReadonlySet<string>): Map<string, string> {
  const result = new Map<string, string>();
  const parsed = tryParseLayout(layoutJson);
  if (!parsed.ok) {
    return result;
  }
  const elements = parsed.layout.sections.flatMap((section) => section.elements).filter((element): element is FieldElement => element.kind === "field");
  const elementById = new Map(elements.map((element) => [element.fieldDefinitionId, element]));
  const closureOf = (fieldId: string): Set<string> => {
    const closure = new Set<string>();
    const pending: string[] = [];
    const add = (id: string, withRequiredAndFilter: boolean) => {
      const element = elementById.get(id);
      if (!element) {
        return;
      }
      const inputs = comparisonsOf(element.visibleWhen).map((c) => c.fieldDefinitionId);
      if (withRequiredAndFilter) {
        inputs.push(...comparisonsOf(element.requiredWhen).map((c) => c.fieldDefinitionId));
        if (element.optionFilter) {
          inputs.push(element.optionFilter.parentFieldDefinitionId);
        }
      }
      for (const input of inputs) {
        if (input !== fieldId && !closure.has(input)) {
          closure.add(input);
          pending.push(input);
        }
      }
    };
    add(fieldId, true);
    while (pending.length > 0) {
      add(pending.pop()!, false);
    }
    return closure;
  };
  const closures = new Map(elements.map((element) => [element.fieldDefinitionId, closureOf(element.fieldDefinitionId)]));
  const selectedIds = new Set(fields.filter((field) => selectedKeys.has(field.fieldKey)).map((field) => field.fieldDefinitionId));
  const allSelected = (ids: Iterable<string>) => [...ids].every((id) => selectedIds.has(id));

  const reasonFor = (field: ProjectionField): string | null => {
    const element = elementById.get(field.fieldDefinitionId);
    if (!element) {
      return "not part of this form";
    }
    if (element.readOnly) {
      return "read-only in this form";
    }
    if (field.fieldClassification === "Restricted") {
      return "restricted information";
    }
    if (field.fieldDataType === "File") {
      return "a document field";
    }
    if (field.groupDefinitionId) {
      return "in a section with multiple entries";
    }
    if (field.fieldDataType === "Option" && field.optionSelectionMode === "Multiple") {
      return "a multiple-choice field";
    }
    // R1: whatever decides this field's visibility, requiredness or choices must be shared too.
    if (!allSelected(closures.get(field.fieldDefinitionId) ?? [])) {
      return "depends on fields not included in this share";
    }
    // R2: every field this one drives must be shared, with everything that decides it.
    const drives = [...closures].filter(([id, closure]) => id !== field.fieldDefinitionId && closure.has(field.fieldDefinitionId));
    if (drives.some(([id, closure]) => !selectedIds.has(id) || !allSelected(closure))) {
      return "affects fields not included in this share";
    }
    return null;
  };

  for (const field of fields) {
    const reason = selectedKeys.has(field.fieldKey) ? reasonFor(field) : null;
    if (reason) {
      result.set(field.fieldKey, reason);
    }
  }
  return result;
}

import type { FieldDefinitionResponse, FormRecordResponse, RecordProjectionResponse } from "@enterpriseforms/api-client";
import { ruleStatesFor, splitSelection, toGroupValueMap, tryParseLayout, type FormLayout, type GroupInfo, type GroupValueMap, type RendererField, type ValueMap } from "@enterpriseforms/form-renderer";

export function toRendererFields(fields: readonly FieldDefinitionResponse[]): RendererField[] {
  return fields.map((field) => ({ fieldDefinitionId: field.fieldDefinitionId, fieldKey: field.fieldKey, fieldDisplayName: field.fieldDisplayName, fieldDataType: field.fieldDataType, optionSelectionMode: field.optionSelectionMode, applicationEncrypted: field.fieldProtectionMode === "ApplicationEncrypted", groupDefinitionId: field.groupDefinitionId ?? null, options: field.options.map((option) => ({ ...option, optionSortOrder: Number(option.optionSortOrder) })) }));
}

/**
 * The record's own values keyed by field key (values of repeating-section entries are excluded: see toGroups). A
 * multi-select arrives as one value per selected option; they are joined into the comma-separated wire form.
 */
export function toValueMap(record: FormRecordResponse): ValueMap {
  const values: ValueMap = {};
  for (const value of record.values) {
    if (value.formRecordGroupId) {
      continue;
    }
    values[value.fieldKey] = value.fieldKey in values ? [...splitSelection(values[value.fieldKey]!), value.stringValue].join(",") : value.stringValue;
  }
  return values;
}

/** Entries of every repeating section of the record, in the server's order, each with its stable id. */
export function toGroups(record: FormRecordResponse): GroupValueMap {
  return toGroupValueMap(record.values, record.groups ?? []);
}

/** Group keys of the form's repeating groups (from a form definition's or a record's group list). */
export function toGroupInfos(groups: ReadonlyArray<{ groupDefinitionId: string; groupKey: string }> | null | undefined): GroupInfo[] {
  return (groups ?? []).map((group) => ({ groupDefinitionId: group.groupDefinitionId, groupKey: group.groupKey }));
}

export function parseProjectionLayout(projection: RecordProjectionResponse): { layout: FormLayout | null; error: string | null } {
  const parsed = tryParseLayout(projection.layoutVersion.layoutJson);
  return parsed.ok ? { layout: parsed.layout, error: null } : { layout: null, error: parsed.error };
}

/**
 * Values submitted to the server: every field placed in the layout outside repeating sections, as invariant strings
 * (missing = empty string). A field the user cannot change in this form — read-only, not shown in the form, or hidden by a
 * rule — is submitted with its starting value, never with anything typed meanwhile, so retained values stay exactly as
 * stored (the server refuses changes to such fields in any case). Entry values travel separately (groupPayload).
 */
export function layoutValues(layout: FormLayout, fields: readonly RendererField[], values: ValueMap, initial: ValueMap = {}, applyRules = true): Record<string, string> {
  const byId = new Map(fields.map((field) => [field.fieldDefinitionId, field]));
  const states = ruleStatesFor(layout, fields, values, applyRules);
  const result: Record<string, string> = {};
  for (const section of layout.sections) {
    if (section.repeat) {
      continue;
    }
    for (const element of section.elements) {
      if (element.kind === "field") {
        const field = byId.get(element.fieldDefinitionId);
        if (field && !field.groupDefinitionId) {
          const editable = !element.readOnly && element.showInForm !== false && states.get(element.fieldDefinitionId)?.visible !== false;
          result[field.fieldKey] = editable ? (values[field.fieldKey] ?? "") : (initial[field.fieldKey] ?? "");
        }
      }
    }
  }
  return result;
}

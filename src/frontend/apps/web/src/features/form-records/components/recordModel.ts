import type { FieldDefinitionResponse, FormRecordResponse, RecordProjectionResponse } from "@enterpriseforms/api-client";
import { tryParseLayout, type FormLayout, type RendererField, type ValueMap } from "@enterpriseforms/form-renderer";

export function toRendererFields(fields: readonly FieldDefinitionResponse[]): RendererField[] {
  return fields.map((field) => ({ fieldDefinitionId: field.fieldDefinitionId, fieldKey: field.fieldKey, fieldDisplayName: field.fieldDisplayName, fieldDataType: field.fieldDataType, options: field.options.map((option) => ({ ...option, optionSortOrder: Number(option.optionSortOrder) })) }));
}

export function toValueMap(record: FormRecordResponse): ValueMap {
  const values: ValueMap = {};
  for (const value of record.values) {
    values[value.fieldKey] = value.stringValue;
  }
  return values;
}

export function parseProjectionLayout(projection: RecordProjectionResponse): { layout: FormLayout | null; error: string | null } {
  const parsed = tryParseLayout(projection.layoutVersion.layoutJson);
  return parsed.ok ? { layout: parsed.layout, error: null } : { layout: null, error: parsed.error };
}

/** Values submitted to the server: every field placed in the layout, as invariant strings (missing = empty string). */
export function layoutValues(layout: FormLayout, fields: readonly RendererField[], values: ValueMap): Record<string, string> {
  const byId = new Map(fields.map((field) => [field.fieldDefinitionId, field]));
  const result: Record<string, string> = {};
  for (const section of layout.sections) {
    for (const element of section.elements) {
      if (element.kind === "field") {
        const field = byId.get(element.fieldDefinitionId);
        if (field) {
          result[field.fieldKey] = values[field.fieldKey] ?? "";
        }
      }
    }
  }
  return result;
}

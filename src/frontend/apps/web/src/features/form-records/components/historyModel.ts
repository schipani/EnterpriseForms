import type { FieldDefinitionResponse, FieldValueResponse, RecordVersionItemResponse } from "@enterpriseforms/api-client";
import type { FormLayout } from "@enterpriseforms/form-renderer";

/**
 * Phase C6 M2 presentation rules for record history. Facts the Authorization Authority establishes (version number, time,
 * person, current version) are shown plainly; a change description from the local activity log says where it came from, and
 * nothing is invented when no description is recorded.
 */
export interface ChangeText {
  text: string;
  note?: string;
}

export function changeText(item: Pick<RecordVersionItemResponse, "actionLabel" | "actionSource">): ChangeText {
  if (!item.actionLabel || item.actionSource === "None") {
    return { text: "Not recorded" };
  }
  return item.actionSource === "ActivityLog" ? { text: item.actionLabel, note: "from the activity log" } : { text: item.actionLabel };
}

export function actorText(item: Pick<RecordVersionItemResponse, "actorKind" | "actorDisplayName">): string {
  if (item.actorKind === "ProtectionMigration") {
    return item.actorDisplayName ?? "System (field protection update)";
  }
  if (item.actorKind === "User") {
    return item.actorDisplayName ?? "Unknown user";
  }
  return "Not known";
}

/** Field ids placed anywhere in the layout (ordinary and repeating sections). */
export function placedFieldIds(layout: FormLayout): Set<string> {
  const ids = new Set<string>();
  for (const section of layout.sections) {
    for (const element of section.elements) {
      if (element.kind === "field" || element.kind === "documentCollection") {
        ids.add(element.fieldDefinitionId);
      }
    }
  }
  return ids;
}

export interface OtherRecordedValue {
  fieldDefinitionId: string;
  label: string;
  value: string;
  retired: boolean;
}

/**
 * Values the version holds for fields the current form no longer places. They are shown separately (with the field's current
 * name), so data saved under an earlier form layout stays visible instead of silently disappearing from the view.
 */
export function otherRecordedValues(layout: FormLayout, values: readonly FieldValueResponse[], fields: readonly FieldDefinitionResponse[]): OtherRecordedValue[] {
  const placed = placedFieldIds(layout);
  const byId = new Map(fields.map((field) => [field.fieldDefinitionId, field]));
  return values
    .filter((value) => !placed.has(value.fieldDefinitionId))
    .map((value) => {
      const field = byId.get(value.fieldDefinitionId);
      return { fieldDefinitionId: value.fieldDefinitionId, label: field?.fieldDisplayName || value.fieldKey, value: value.stringValue ?? "", retired: field?.lifecycleStatus === "Retired" };
    })
    .sort((a, b) => a.label.localeCompare(b.label));
}

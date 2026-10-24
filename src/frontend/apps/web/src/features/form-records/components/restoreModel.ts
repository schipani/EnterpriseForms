import type { FieldDefinitionResponse, RecordProjectionResponse, RestoreFieldChangeResponse, RestorePreviewResponse } from "@enterpriseforms/api-client";
import { formatDateTime } from "./recordList";

/**
 * Phase C6 M3 presentation rules for restoring an earlier version. Whether the Restore action is offered is a convenience
 * derived from the current verified projection; the server decides again (owner, Draft, Authority-verified version,
 * unchanged head, current form validation) and refuses anything else.
 */
export type RestoreEligibility = { kind: "eligible" } | { kind: "hidden" } | { kind: "explain"; title: string; detail: string };

export const RESTORE_REASON_MAX_LENGTH = 500;

export function restoreEligibility(isCurrentVersion: boolean, projection: RecordProjectionResponse | undefined): RestoreEligibility {
  if (isCurrentVersion || !projection) {
    return { kind: "hidden" };
  }
  // Only an owner restores: ownership transfer is an owner right, and the whole record must be readable.
  if (!projection.permissions.canTransferOwnership || !projection.permissions.isCompleteProjection) {
    return { kind: "hidden" };
  }
  const status = projection.lifecycle?.status ?? projection.record.recordStatus;
  if (status === "Draft" && (projection.lifecycle?.contentEditable ?? true) && projection.permissions.canEdit) {
    return { kind: "eligible" };
  }
  if (status === "Submitted") {
    return { kind: "explain", title: "This version can't be restored while the record is submitted", detail: "Only a draft can be restored to an earlier version. Reopen the record as a draft first, then come back to this version." };
  }
  if (status === "Archived") {
    return { kind: "explain", title: "This version can't be restored while the record is archived", detail: "Only a draft can be restored to an earlier version. Unarchive the record (and reopen it as a draft if it was submitted) first." };
  }
  return { kind: "hidden" };
}

/** Fields the restore would change (unchanged fields are summarized, not listed). */
export function changedFields(preview: Pick<RestorePreviewResponse, "fields">): RestoreFieldChangeResponse[] {
  return preview.fields.filter((field) => field.change !== "Unchanged");
}

export function changeLabel(change: string): string {
  switch (change) {
    case "Added":
      return "Value comes back";
    case "Cleared":
      return "Value is removed";
    case "Changed":
      return "Value changes";
    default:
      return "Unchanged";
  }
}

/**
 * Readable form of a wire value for the comparison: option keys become their current display text, booleans Yes/No,
 * instants the person's date format. Fields the current form no longer has keep the raw value.
 */
export function displayValue(wire: string, field: FieldDefinitionResponse | undefined): string {
  if (wire === "") {
    return "Empty";
  }
  if (!field) {
    return wire;
  }
  switch (field.fieldDataType) {
    case "Option": {
      const byKey = new Map(field.options.map((option) => [option.optionKey, option.optionDisplayText]));
      return wire
        .split(",")
        .map((key) => byKey.get(key) ?? key)
        .join(", ");
    }
    case "Boolean":
      return wire === "true" ? "Yes" : wire === "false" ? "No" : wire;
    case "DateTime":
      return formatDateTime(wire);
    default:
      return wire;
  }
}

export function reasonIsValid(reason: string): boolean {
  const trimmed = reason.trim();
  return trimmed.length > 0 && trimmed.length <= RESTORE_REASON_MAX_LENGTH;
}

export function needsPlaintextAcknowledgement(preview: Pick<RestorePreviewResponse, "plaintextAcknowledgementFieldKeys">): boolean {
  return preview.plaintextAcknowledgementFieldKeys.length > 0;
}

export function canConfirmRestore(preview: Pick<RestorePreviewResponse, "canRestore" | "plaintextAcknowledgementFieldKeys">, reason: string, acknowledged: boolean): boolean {
  return preview.canRestore && reasonIsValid(reason) && (!needsPlaintextAcknowledgement(preview) || acknowledged);
}

/** What a restore never brings back, always shown so nobody expects it to. */
export function exclusions(preview: Pick<RestorePreviewResponse, "unchangedDocumentCount" | "formHasRelatedRecords">): string[] {
  const count = Number(preview.unchangedDocumentCount);
  const documents = count === 1 ? "The record's 1 current document stays as it is." : `The record's ${count} current documents stay as they are.`;
  return [
    count > 0 ? documents : "Documents are not restored.",
    preview.formHasRelatedRecords ? "Related records are not restored; they keep their own history." : "Related records are not restored.",
    "Sharing, access and ownership stay as they are now.",
    "The record's status stays Draft.",
    "Values are stored under the form's current protection settings, not the settings used when the version was saved.",
  ];
}

import type { RecordProjectionResponse } from "@enterpriseforms/api-client";
import { StatusMessage } from "@enterpriseforms/ui";

/*
 * Phase C9 (CLAUDE.md §3): which published layout a record is shown and edited with depends on the form's version
 * behaviour — Historical (the version the record was created with, for both), Latest (the current version, for both) or
 * Hybrid (shown with the created version, edited with the current one). The server chooses; these helpers only explain it.
 * Values of fields that are not part of the layout shown are kept unchanged and are not displayed here (D9-8); the
 * record's version history shows them to people allowed to read them.
 */

type VersionInfo = Pick<RecordProjectionResponse, "layoutVersion" | "versionBehavior" | "createdFormVersionNumber" | "editLayoutVersionId" | "editLayoutVersionNumber" | "layoutPurpose">;

const versionNumber = (value: number | string | null | undefined): number | null => (value === null || value === undefined ? null : Number(value));

/** "Layout v3", or "Layout v3 (created with v1)" when the record was created with another version. */
export function layoutBadgeText(projection: VersionInfo): string {
  const shown = Number(projection.layoutVersion.versionNumber);
  const created = versionNumber(projection.createdFormVersionNumber);
  return created !== null && created !== shown ? `Layout v${shown} (created with v${created})` : `Layout v${shown}`;
}

/** On the record page: says when edits use another layout than the one shown, or when no edit layout is available. */
export function ViewLayoutNotice({ projection, canEdit }: { projection: VersionInfo; canEdit: boolean }) {
  const shown = Number(projection.layoutVersion.versionNumber);
  const edit = versionNumber(projection.editLayoutVersionNumber);
  if (canEdit && (projection.editLayoutVersionId === null || projection.editLayoutVersionId === undefined)) {
    return (
      <StatusMessage tone="warning" title="This record can't be edited right now">
        The form version this record is edited with is not available, so changes can't be validated. Nothing about the record has changed. Contact the form&apos;s designer.
      </StatusMessage>
    );
  }
  if (projection.versionBehavior === "Hybrid" && edit !== null && edit !== shown) {
    return (
      <StatusMessage tone="info" title={`Shown with layout v${shown}; edits use layout v${edit}`}>
        This form shows each record with the version it was created with, and edits it with the current version. Fields added since may be empty or required when you edit.
      </StatusMessage>
    );
  }
  return null;
}

/** On the edit page: which layout this edit is validated against, and how it relates to the version the record was created with. */
export function EditLayoutNotice({ projection }: { projection: VersionInfo }) {
  const editing = Number(projection.layoutVersion.versionNumber);
  const created = versionNumber(projection.createdFormVersionNumber);
  if (projection.versionBehavior === "Historical") {
    return (
      <div className="mb-4">
        <StatusMessage tone="info" title={`Editing with layout v${editing}, the version this record was created with`}>
          This form keeps each record on the version it was created with, for viewing and editing.
        </StatusMessage>
      </div>
    );
  }
  if (created === null || created === editing) {
    return null;
  }
  return (
    <div className="mb-4">
      <StatusMessage tone="info" title={`Editing with the current layout v${editing}`}>
        {projection.versionBehavior === "Hybrid"
          ? `This record was created with layout v${created} and is shown with it; edits use the current version. Fields added since may be empty or required.`
          : `This record was created with layout v${created}. Fields added since may be empty or required.`}{" "}
        Values of fields that are no longer part of the form are kept unchanged.
      </StatusMessage>
    </div>
  );
}

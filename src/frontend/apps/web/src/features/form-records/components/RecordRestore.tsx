import { presentError, useApi } from "@enterpriseforms/app-runtime";
import type { RecordProjectionResponse, RecordRestoreResponse, RecordVersionItemResponse, RestorePreviewResponse } from "@enterpriseforms/api-client";
import { ActionIcons, Badge, Button, Checkbox, DataTable, Dialog, LoadingBlock, StatusMessage, TextInput } from "@enterpriseforms/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { ErrorPanel } from "../../support";
import { actorText, changeText } from "./historyModel";
import { formatDateTime } from "./recordList";
import { RESTORE_REASON_MAX_LENGTH, canConfirmRestore, changeLabel, changedFields, displayValue, exclusions, needsPlaintextAcknowledgement, restoreEligibility } from "./restoreModel";

/**
 * Phase C6 M3 — "Restore this version". Shown to the owner of a Draft record viewing an earlier version. The dialog loads a
 * server preview (current against restored values, entries, what is not restored, validation against today's form) and
 * asks for a reason before restoring. Restoring adds a new version; nothing earlier is changed. The server checks every
 * condition again — this component never decides eligibility on its own.
 */
export function RestoreVersionAction({ formRecordId, version, projection, onRestored }: { formRecordId: string; version: RecordVersionItemResponse; projection: RecordProjectionResponse | undefined; onRestored: (result: RecordRestoreResponse) => void }) {
  const [open, setOpen] = useState(false);
  const eligibility = restoreEligibility(version.isCurrent, projection);
  if (eligibility.kind === "hidden") {
    return null;
  }
  if (eligibility.kind === "explain") {
    return (
      <StatusMessage tone="info" title={eligibility.title}>
        {eligibility.detail}
      </StatusMessage>
    );
  }
  return (
    <>
      <Button icon={ActionIcons.restoreVersion} onClick={() => setOpen(true)}>
        Restore this version
      </Button>
      {open ? (
        <RestoreDialog
          formRecordId={formRecordId}
          revision={Number(version.contentRevision)}
          projection={projection!}
          onClose={() => setOpen(false)}
          onRestored={(result) => {
            setOpen(false);
            onRestored(result);
          }}
        />
      ) : null}
    </>
  );
}

function RestoreDialog({ formRecordId, revision, projection, onClose, onRestored }: { formRecordId: string; revision: number; projection: RecordProjectionResponse; onClose: () => void; onRestored: (result: RecordRestoreResponse) => void }) {
  const api = useApi();
  const queryClient = useQueryClient();
  const [reason, setReason] = useState("");
  const [acknowledged, setAcknowledged] = useState(false);
  const [attempted, setAttempted] = useState(false);
  const preview = useQuery<RestorePreviewResponse>({
    queryKey: ["records", "restore-preview", formRecordId, revision],
    queryFn: ({ signal }) => api.records.restorePreview(formRecordId, revision, signal).then((r) => r.result),
    retry: false,
    staleTime: 0,
    gcTime: 0,
  });
  const mutation = useMutation({
    mutationFn: (data: RestorePreviewResponse) =>
      api.records
        .restore(formRecordId, revision, {
          expectedCurrentContentRevision: Number(data.currentContentRevision),
          sourceOperationId: data.sourceOperationId,
          restoreReason: reason.trim(),
          acknowledgePlaintext: acknowledged,
        })
        .then((r) => r.result),
    onSuccess: async (result) => {
      // The version page itself stays as it is (it shows this earlier version); everything about the current record refreshes.
      await queryClient.invalidateQueries({ predicate: (query) => query.queryKey[0] === "records" && query.queryKey[1] !== "version" && query.queryKey[1] !== "restore-preview" });
      onRestored(result);
    },
  });

  const data = preview.data;
  const reasonMissing = attempted && reason.trim().length === 0;
  const confirmable = data ? canConfirmRestore(data, reason, acknowledged) : false;
  const fieldsByKey = new Map(projection.fields.map((field) => [field.fieldKey, field]));
  const presented = mutation.isError ? presentError(mutation.error) : null;
  const stale = presented?.code === "CONTENT_REVISION_CONFLICT" || presented?.code === "PRECONDITION_FAILED" || presented?.code === "VERSION_CHANGED";

  return (
    <Dialog
      open
      size="large"
      onOpenChange={(next) => (next ? undefined : onClose())}
      title={`Restore version ${revision}?`}
      description="Restoring copies this version's field values and section entries into a new version of the record. Earlier versions, including the current one, are kept unchanged in the history."
      locked={mutation.isPending}
      footer={
        <>
          <Button variant="secondary" onClick={onClose} disabled={mutation.isPending}>
            Cancel
          </Button>
          {data?.canRestore ? (
            <Button
              icon={ActionIcons.restoreVersion}
              busy={mutation.isPending}
              disabled={mutation.isPending || (attempted && !confirmable)}
              onClick={() => {
                setAttempted(true);
                if (confirmable) {
                  mutation.mutate(data);
                }
              }}
            >
              {`Restore version ${revision}`}
            </Button>
          ) : null}
        </>
      }
    >
      {preview.isPending ? <LoadingBlock label={`Checking version ${revision} against the current record`} /> : null}
      {preview.isError ? <PreviewError error={preview.error} onRetry={() => void preview.refetch()} /> : null}
      {data ? (
        <div className="flex flex-col gap-4">
          <section aria-labelledby="restore-version-heading" className="rounded border border-line bg-page px-4 py-3">
            <h3 id="restore-version-heading" className="font-semibold">
              Version {revision}
            </h3>
            <p className="text-sm">
              Saved {formatDateTime(data.version.acceptedOn)} by {actorText(data.version)}
              {(() => {
                const change = changeText(data.version);
                return change.note ? ` · ${change.text} (${change.note})` : ` · ${change.text}`;
              })()}
            </p>
            <p className="text-sm text-muted">
              The version number, time and person come from the Authorization Authority, which checked this version's saved content just now. The current record is version {String(data.currentContentRevision)}; restoring creates version {Number(data.currentContentRevision) + 1}.
            </p>
          </section>

          {!data.canRestore ? (
            <StatusMessage tone={data.blockedReasonCode === "RESTORE_NO_CHANGES" ? "info" : "warning"} title={blockedTitle(data.blockedReasonCode)}>
              {data.blockedReason}
              {data.validation && data.validation.fieldKeys.length > 0 ? (
                <ul className="mt-2 list-disc pl-5">
                  {data.validation.fieldKeys.map((key) => (
                    <li key={key}>{fieldsByKey.get(key)?.fieldDisplayName ?? key}</li>
                  ))}
                </ul>
              ) : null}
            </StatusMessage>
          ) : null}

          <ComparisonTable data={data} fieldsByKey={fieldsByKey} />

          {data.sections.length > 0 ? (
            <DataTable
              caption="Section entries before and after the restore"
              rowKey={(row) => row.groupKey}
              rows={data.sections}
              columns={[
                { key: "section", header: "Section", isRowHeader: true, render: (row) => <span>{row.label}{row.inCurrentForm ? null : <> <Badge tone="warning">No longer in the form</Badge></>}</span> },
                { key: "current", header: "Entries now", render: (row) => String(row.currentEntryCount) },
                { key: "restored", header: "Entries after restore", render: (row) => String(row.restoredEntryCount) },
                { key: "detail", header: "Detail", render: (row) => sectionDetail(Number(row.newEntryCount), Number(row.removedEntryCount)) },
              ]}
            />
          ) : null}

          <section aria-labelledby="restore-excluded-heading" className="rounded border border-line px-4 py-3">
            <h3 id="restore-excluded-heading" className="font-semibold">
              Not restored
            </h3>
            <ul className="mt-1 list-disc pl-5 text-sm">
              {exclusions(data).map((line) => (
                <li key={line}>{line}</li>
              ))}
            </ul>
          </section>

          {data.canRestore ? (
            <>
              {needsPlaintextAcknowledgement(data) ? (
                <StatusMessage tone="warning" title="Some restored values will no longer be encrypted">
                  These fields were encrypted when this version was saved, but the form now stores them without application encryption:{" "}
                  {data.plaintextAcknowledgementFieldKeys.map((key) => fieldsByKey.get(key)?.fieldDisplayName ?? key).join(", ")}.
                  <Checkbox label="I understand these values will be stored under the field's current setting (not encrypted)" checked={acknowledged} onCheckedChange={(checked) => setAcknowledged(checked === true)} error={attempted && !acknowledged ? "Confirm this to restore the version." : undefined} />
                </StatusMessage>
              ) : null}
              <TextInput
                label="Reason for restoring"
                value={reason}
                required
                maxLength={RESTORE_REASON_MAX_LENGTH}
                helpText="Recorded in the activity history. Don't include confidential details."
                error={reasonMissing ? "Enter a reason for restoring this version." : undefined}
                onChange={(event) => setReason(event.target.value)}
              />
            </>
          ) : null}

          {mutation.isError ? (
            stale ? (
              <StatusMessage
                tone="warning"
                title="The record changed since this preview was prepared"
                actions={
                  <Button size="sm" variant="secondary" onClick={() => { mutation.reset(); void preview.refetch(); }}>
                    Review the changes again
                  </Button>
                }
              >
                Someone saved the record (or its history moved on). Nothing was restored. Review the comparison again before restoring.
              </StatusMessage>
            ) : (
              <div className="flex flex-col gap-2">
                <ErrorPanel error={mutation.error} />
                {presented && presented.fieldKeys.length > 0 ? (
                  <ul className="list-disc pl-5 text-sm">
                    {presented.fieldKeys.map((key) => (
                      <li key={key}>{fieldsByKey.get(key)?.fieldDisplayName ?? key}</li>
                    ))}
                  </ul>
                ) : null}
              </div>
            )
          ) : null}
        </div>
      ) : null}
    </Dialog>
  );
}

function ComparisonTable({ data, fieldsByKey }: { data: RestorePreviewResponse; fieldsByKey: Map<string, RecordProjectionResponse["fields"][number]> }) {
  const changed = changedFields(data);
  const unchanged = data.fields.length - changed.length;
  return (
    <section aria-labelledby="restore-compare-heading" className="flex flex-col gap-2">
      <h3 id="restore-compare-heading" className="font-semibold">
        What changes
      </h3>
      <DataTable
        caption="Current and restored values"
        rowKey={(row) => `${row.sectionPath ?? ""}/${row.fieldKey}`}
        rows={changed}
        emptyMessage="No field values change."
        columns={[
          {
            key: "field",
            header: "Field",
            isRowHeader: true,
            render: (row) => (
              <span className="flex flex-col gap-1">
                <span>{row.label}</span>
                {row.sectionPath ? <span className="text-xs text-muted">{row.sectionPath}</span> : null}
                <span className="flex flex-wrap gap-1">
                  {row.inCurrentForm ? null : <Badge tone="warning">No longer in the form</Badge>}
                  {row.storedEncrypted ? <Badge tone="neutral">Encrypted</Badge> : null}
                  {row.plaintextAcknowledgementRequired ? <Badge tone="warning">Will not be encrypted</Badge> : null}
                </span>
              </span>
            ),
          },
          { key: "current", header: "Now", render: (row) => <ValueText value={displayValue(row.currentValue, fieldsByKey.get(row.fieldKey))} /> },
          { key: "restored", header: "After restore", render: (row) => <ValueText value={displayValue(row.restoredValue, fieldsByKey.get(row.fieldKey))} /> },
          { key: "change", header: "Change", render: (row) => changeLabel(row.change) },
        ]}
      />
      {unchanged > 0 ? <p className="text-sm text-muted">{unchanged === 1 ? "1 other field keeps its current value." : `${unchanged} other fields keep their current values.`}</p> : null}
    </section>
  );
}

function ValueText({ value }: { value: string }) {
  return value === "Empty" ? <span className="text-muted">Empty</span> : <span className="break-words">{value}</span>;
}

function sectionDetail(added: number, removed: number): string {
  const parts: string[] = [];
  if (added > 0) {
    parts.push(added === 1 ? "1 entry comes back as a new entry" : `${added} entries come back as new entries`);
  }
  if (removed > 0) {
    parts.push(removed === 1 ? "1 current entry is removed" : `${removed} current entries are removed`);
  }
  return parts.length === 0 ? "Entry values change" : parts.join("; ");
}

function blockedTitle(code: string | null | undefined): string {
  switch (code) {
    case "RECORD_NOT_EDITABLE":
      return "Only a draft can be restored";
    case "RESTORE_NO_CHANGES":
      return "Nothing to restore";
    case "RESTORE_VALIDATION_FAILED":
      return "This version doesn't pass the current form's rules";
    case "RESTORE_UNSUPPORTED":
      return "This version can't be restored exactly";
    default:
      return "This version can't be restored";
  }
}

function PreviewError({ error, onRetry }: { error: unknown; onRetry: () => void }) {
  const presented = presentError(error);
  if (presented.status === 404) {
    return <StatusMessage tone="info" title="This version can't be restored">It doesn't exist, or you can't restore this record.</StatusMessage>;
  }
  if (presented.code === "INTEGRITY_VERIFICATION_FAILED" || presented.code === "INTEGRITY_QUARANTINE") {
    return <StatusMessage tone="danger" title="This version could not be verified">Its saved content doesn't match what was accepted, so it can't be restored. The record has been locked for review.</StatusMessage>;
  }
  if (presented.code === "HISTORIC_VERSION_UNVERIFIABLE" || presented.code === "HISTORIC_KEY_UNAVAILABLE" || presented.code === "ENCRYPTION_UNAVAILABLE" || presented.code === "KEY_UNAVAILABLE") {
    return <StatusMessage tone="warning" title="This version can't be restored right now">A key needed to check or decrypt it isn't available. Nothing has been changed.</StatusMessage>;
  }
  return <ErrorPanel error={error} onRetry={onRetry} />;
}

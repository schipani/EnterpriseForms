import type { RecordProjectionResponse, RecordShareSummaryResponse } from "@enterpriseforms/api-client";
import { presentError, useApi } from "@enterpriseforms/app-runtime";
import { Button, Checkbox, DescriptionList, Dialog, legendClass, LoadingBlock, Select, StatusMessage, TextInput, getActiveFormatter, instantToLocalDateTime, localDateTimeToInstant, useFormatter } from "@enterpriseforms/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { ErrorPanel } from "../../support";
import { ShareDocumentSelection, toDocumentRequests, type DocumentSelection } from "./ShareDocumentSelection";

const ROLE_OPTIONS = [
  { value: "Reader", label: "Reader" },
  { value: "Contributor", label: "Contributor" },
  { value: "Manager", label: "Manager" },
];

const pad = (value: number) => String(value).padStart(2, "0");

/** The share's expiry as a `datetime-local` value in the viewer's effective time zone. */
export function toLocalInput(instant: string, timeZone: string): string {
  const local = instantToLocalDateTime(new Date(instant), timeZone);
  return `${local.year}-${pad(local.month)}-${pad(local.day)}T${pad(local.hour)}:${pad(local.minute)}`;
}

/** A `datetime-local` value in the viewer's time zone as an instant; DST gaps and folds are reported, never guessed. */
export function fromLocalInput(value: string, timeZone: string): { instant?: string; problem?: string } {
  const match = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2})$/.exec(value);
  if (!match) {
    return { problem: "Enter a date and time." };
  }
  const [year, month, day, hour, minute] = match.slice(1).map(Number) as [number, number, number, number, number];
  const result = localDateTimeToInstant({ year, month, day, hour, minute }, timeZone);
  if (result.kind === "gap") {
    return { problem: "That time does not exist in your time zone (a daylight saving change). Choose another time." };
  }
  if (result.kind === "ambiguous") {
    return { problem: "That time occurs twice in your time zone (a daylight saving change). Choose another time." };
  }
  return { instant: result.instant.toISOString() };
}

/**
 * PG-022/PG-023 (C10 M3, WP-0 Sections 3.2 and 3.5): edit one share. The dialog first reads the share from the Authority, so the
 * revision it edits — sent as `If-Match: "{recordShareId}:{shareRevision}"` — is the committed one, not the list's projection. A
 * stale revision (412) asks the person to reload; an organization conflict (409) is reported and never retried automatically.
 * The new scope replaces the old one: narrowing invalidates delegated shares that it no longer covers (WP-0 Section 3.4).
 */
export function EditShareDialog({ formRecordId, share, projection, onClose }: { formRecordId: string; share: RecordShareSummaryResponse; projection: RecordProjectionResponse; onClose: () => void }) {
  useFormatter();
  const api = useApi();
  const queryClient = useQueryClient();
  const timeZone = getActiveFormatter().settings.timeZone;
  const details = useQuery({
    queryKey: ["share-details", formRecordId, share.recordShareId],
    queryFn: ({ signal }) => api.shares.get(formRecordId, share.recordShareId, signal).then((r) => r.result),
    refetchOnMount: "always",
    staleTime: 0,
  });
  const shareable = projection.record.values.filter((value) => !value.formRecordGroupId).map((value) => value.fieldKey).filter((key, index, all) => all.indexOf(key) === index).sort();
  const displayNames = new Map(projection.fields.map((field) => [field.fieldKey, field.fieldDisplayName]));
  const [edits, setEdits] = useState<{ role?: string; selected?: Set<string>; documents?: DocumentSelection; noExpiry?: boolean; expiry?: string; stepUp: boolean }>({ stepUp: false });
  const loaded = details.data;
  const role = edits.role ?? loaded?.recipientRole ?? share.recipientRole;
  const selected = edits.selected ?? new Set((loaded?.fieldKeys ?? []).filter((key) => shareable.includes(key)));
  const documents: DocumentSelection = edits.documents ?? new Map((loaded?.documents ?? []).map((d) => [d.fileAttachmentId, { download: d.canDownloadDocument }]));
  const noExpiry = edits.noExpiry ?? !loaded?.shareExpiresOn;
  const expiry = edits.expiry ?? (loaded?.shareExpiresOn ? toLocalInput(loaded.shareExpiresOn, timeZone) : "");
  const parsedExpiry = noExpiry ? undefined : fromLocalInput(expiry, timeZone);
  const unnamedItems = loaded ? loaded.fieldValueItemIds.length - loaded.fieldKeys.length : 0;

  const save = useMutation({
    mutationFn: () => {
      if (!loaded) {
        throw new Error("The share has not been read yet.");
      }
      return api.shares
        .update(formRecordId, share.recordShareId, loaded.shareRevision, {
          recipientRole: role,
          fieldKeys: [...selected],
          ...(documents.size > 0 ? { documents: toDocumentRequests(documents) } : {}),
          shareExpiresOn: noExpiry ? null : (parsedExpiry?.instant ?? null),
          noExpiry: noExpiry ? true : null,
          parentRecordShareId: loaded.parentRecordShareId ?? null,
          useDevelopmentStepUp: edits.stepUp,
        })
        .then((r) => r.result);
    },
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ["shares", formRecordId] });
      await queryClient.invalidateQueries({ queryKey: ["share-details", formRecordId, share.recordShareId] });
      onClose();
    },
  });
  const failure = save.isError ? presentError(save.error) : null;
  const stale = failure?.status === 412;
  const organizationMoved = failure?.status === 409;
  const reload = async () => {
    save.reset();
    setEdits((current) => ({ stepUp: current.stepUp }));
    await details.refetch();
  };
  const toggle = (key: string, checked: boolean) =>
    setEdits((current) => {
      const next = new Set(current.selected ?? selected);
      if (checked) {
        next.add(key);
      } else {
        next.delete(key);
      }
      return { ...current, selected: next };
    });
  const canSave = !!loaded && loaded.shareStatus === "Active" && selected.size + documents.size > 0 && (noExpiry || !!parsedExpiry?.instant) && edits.stepUp && !stale;

  return (
    <Dialog
      open
      onOpenChange={(next) => (next ? undefined : onClose())}
      title="Edit share"
      description="The new role, fields and expiry replace the current ones. Narrowing a share also ends shares that were delegated from it and are no longer covered."
      locked={save.isPending}
      footer={
        <>
          <Button variant="secondary" onClick={onClose} disabled={save.isPending}>
            Cancel
          </Button>
          <Button busy={save.isPending} disabled={!canSave} onClick={() => save.mutate()}>
            Save share
          </Button>
        </>
      }
    >
      <div className="flex flex-col gap-3">
        {details.isPending ? <LoadingBlock label="Reading the share from the authorization authority" /> : null}
        {details.isError ? <ErrorPanel error={details.error} onRetry={() => void details.refetch()} /> : null}
        {loaded ? (
          <>
            <DescriptionList
              items={[
                { term: "Recipient", detail: share.recipientDisplayName ?? loaded.recipientUserId ?? loaded.recipientTeamId ?? "unknown" },
                { term: "State", detail: loaded.shareStatus },
                { term: "Share revision", detail: String(loaded.shareRevision) },
                { term: "Delegation", detail: loaded.parentRecordShareId ? `Delegated (level ${loaded.delegationDepth})` : loaded.legacyLineageUnresolved ? "Granted before delegation was recorded" : "Granted by the owner" },
                { term: "Effective expiry", detail: loaded.effectiveExpiresOn ? getActiveFormatter().instant(loaded.effectiveExpiresOn) : "None" },
              ]}
            />
            {loaded.shareStatus !== "Active" ? <StatusMessage tone="info" title="This share is no longer active and cannot be edited." /> : null}
            <Select label="Role" required value={role} options={ROLE_OPTIONS} onValueChange={(value) => setEdits((current) => ({ ...current, role: value }))} />
            <fieldset className="flex flex-col gap-2 rounded-control border border-line p-3">
              <legend className={`px-1 ${legendClass}`}>Fields to share</legend>
              {shareable.map((key) => (
                <Checkbox key={key} label={displayNames.get(key) ?? key} checked={selected.has(key)} onCheckedChange={(checked) => toggle(key, checked === true)} />
              ))}
              {unnamedItems > 0 ? <p className="text-sm text-muted">This share also covers {unnamedItems} earlier value(s) the record no longer holds; saving keeps only the fields selected here.</p> : null}
            </fieldset>
            <ShareDocumentSelection documents={projection.documents ?? []} selection={documents} onChange={(next) => setEdits((current) => ({ ...current, documents: next }))} />
            <Checkbox label="No expiry" checked={noExpiry} onCheckedChange={(checked) => setEdits((current) => ({ ...current, noExpiry: checked === true }))} />
            {!noExpiry ? (
              <TextInput
                label={`Expires (${timeZone})`}
                type="datetime-local"
                value={expiry}
                required
                error={expiry && parsedExpiry?.problem ? parsedExpiry.problem : undefined}
                helpText="Between one hour and 365 days from now. A delegated share cannot outlast the share it was delegated from."
                onChange={(event) => setEdits((current) => ({ ...current, expiry: event.target.value }))}
              />
            ) : null}
            <Checkbox
              label="Confirm with the development-only step-up"
              checked={edits.stepUp}
              onCheckedChange={(checked) => setEdits((current) => ({ ...current, stepUp: checked === true }))}
              helpText="Sharing changes need a fresh passkey confirmation. This synthetic stand-in exists only in local development and test; elsewhere the authority refuses the change until passkey step-up is available."
            />
          </>
        ) : null}
        {stale ? (
          <StatusMessage tone="warning" title="This share changed since you opened it" actions={<Button size="sm" variant="secondary" onClick={() => void reload()}>Reload share</Button>}>
            Nothing was saved. Reload to see the current role, fields and expiry, then make your change again.
          </StatusMessage>
        ) : null}
        {organizationMoved ? (
          <StatusMessage tone="warning" title="Access in this organization changed while you were editing">
            Nothing was saved and the change was not retried. Review the share and save again if it is still needed.
          </StatusMessage>
        ) : null}
        {save.isError && !stale && !organizationMoved ? <ErrorPanel error={save.error} /> : null}
      </div>
    </Dialog>
  );
}

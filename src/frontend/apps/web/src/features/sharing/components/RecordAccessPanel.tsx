import type { RecordProjectionResponse, RecordShareSummaryResponse } from "@enterpriseforms/api-client";
import { useApi, usePresence } from "@enterpriseforms/app-runtime";
import { Badge, Button, buttonClass, Card, Checkbox, DataTable, type DataTableColumn, DescriptionList, Dialog, legendClass, LoadingBlock, PresenceIndicator, Select, StatusMessage, getActiveFormatter, useFormatter } from "@enterpriseforms/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Link } from "@tanstack/react-router";
import { useState } from "react";
import { ActivityTimeline } from "../../audit";
import { ErrorPanel } from "../../support";
import { contributorViewOnlyReasons } from "./contributorPreview";
import { EditShareDialog } from "./EditShareDialog";
import { RecordSecurityHistoryCard } from "./RecordSecurityHistoryCard";
import { ShareDocumentSelection, toDocumentRequests, type DocumentSelection } from "./ShareDocumentSelection";

/** Role copy follows the Authority's contributor-write switch (Phase C5, D10): never promise an edit that cannot happen. */
const rolesFor = (contributorWritesEnabled: boolean) => [
  { value: "Reader", label: "Reader — view the selected fields" },
  {
    value: "Contributor",
    label: contributorWritesEnabled ? "Contributor — view the selected fields and change their values" : "Contributor — view the selected fields (changing shared values is not enabled)",
  },
  { value: "Manager", label: contributorWritesEnabled ? "Manager — contributor, and can share these fields with others" : "Manager — view and share these fields with others (changing shared values is not enabled)" },
];

/** Audit description the server writes for a share editor's committed change (UpdateFormRecordHandler.SharedEditAuditDescription). */
const SHARED_EDIT_DESCRIPTION = "shared fields changed by a person with shared edit access";

/**
 * PG-022 Access tab (CLAUDE.md §4H, ADR-PB-002 Stage B item 5): the caller's effective permissions as decided by the
 * authority for this request, and — for access managers — the record's explicit field-scoped shares with create and
 * revoke. Every list row is a local projection of an authority command; it confers nothing. New fields, child records
 * and documents are never covered by an existing share.
 */
export function RecordAccessPanel({ formRecordId, projection, verifiedScope }: { formRecordId: string; projection: RecordProjectionResponse; verifiedScope?: readonly (string | number | null)[] }) {
  useFormatter();
  const api = useApi();
  const queryClient = useQueryClient();
  const canManage = projection.permissions.canManageAccess;
  const readOnly = verifiedScope !== undefined;
  // The existing list is record-wide. The standalone page needs the scoped Authority read for delegated managers.
  const canListShares = canManage && (!readOnly || projection.permissions.canTransferOwnership);
  const revision = projection.record.authorizationRevision;
  const shares = useQuery({
    queryKey: verifiedScope ? ["record-sharing", "shares", ...verifiedScope, formRecordId, revision] : ["shares", formRecordId],
    queryFn: ({ signal }) => api.shares.list(formRecordId, signal).then((r) => r.result),
    enabled: canListShares,
    refetchOnMount: readOnly ? "always" : true,
  });
  const [open, setOpen] = useState(false);
  const [revokeTarget, setRevokeTarget] = useState<RecordShareSummaryResponse | null>(null);
  // C10 (independent review F-2): revocation is a human share command and needs the step-up, as edits and transfers do.
  const [revokeStepUp, setRevokeStepUp] = useState(false);
  const [editTarget, setEditTarget] = useState<RecordShareSummaryResponse | null>(null);
  const [revokedNotice, setRevokedNotice] = useState<string | null>(null);
  const writesEnabled = projection.permissions.contributorWritesEnabled;
  // Collaboration history (owners, who can read the record's activity): grants, revocations and shared edits only.
  const history = useQuery({
    queryKey: verifiedScope ? ["record-sharing", "history", ...verifiedScope, formRecordId, revision] : ["audit", "record", formRecordId],
    queryFn: ({ signal }) => api.audit.record(formRecordId, signal).then((r) => r.result),
    enabled: canListShares && projection.permissions.isCompleteProjection,
    refetchOnMount: readOnly ? "always" : true,
  });
  const sharesVerified = shares.isSuccess && shares.isFetchedAfterMount && shares.fetchStatus === "idle";
  const shareRevisionChanged = readOnly && sharesVerified && String(shares.data.authorizationRevision) !== String(revision);
  const shareData = !readOnly || (sharesVerified && !shareRevisionChanged) ? shares.data : undefined;
  const historyData = !readOnly || (history.isSuccess && history.isFetchedAfterMount && history.fetchStatus === "idle") ? history.data : undefined;
  const collaboration = (historyData?.events ?? []).filter((event) => event.eventAction === "RECORD_ACCESS_GRANTED" || event.eventAction === "RECORD_ACCESS_REVOKED" || (event.eventAction === "RECORD_UPDATED" && event.eventDescription === SHARED_EDIT_DESCRIPTION));
  const revoke = useMutation({
    mutationFn: (share: RecordShareSummaryResponse) => api.shares.revoke(formRecordId, share.recordShareId, revokeStepUp).then((r) => r.result),
    onSuccess: async (_result, share) => {
      setRevokeTarget(null);
      setRevokeStepUp(false);
      setRevokedNotice(share.recipientDisplayName ?? share.recipientUserId ?? share.recipientTeamId ?? "the recipient");
      await queryClient.invalidateQueries({ queryKey: ["shares", formRecordId] });
      await queryClient.invalidateQueries({ queryKey: ["audit", "record", formRecordId] });
    },
  });
  const activeShares = shareData?.items.filter((share) => share.shareStatus === "Active") ?? [];
  const sharedEditable = (projection.sharedFieldAccess ?? []).filter((item) => item.editable).length;
  const shareActions: DataTableColumn<RecordShareSummaryResponse>[] = readOnly ? [] : [
    {
      key: "actions",
      header: "Actions",
      isActions: true,
      render: (row) =>
        row.shareStatus === "Active" ? (
          <div className="flex gap-1">
            <Button size="sm" variant="ghost" aria-label={`Edit share for ${row.recipientDisplayName ?? row.recipientUserId ?? row.recipientTeamId ?? "recipient"}`} onClick={() => setEditTarget(row)}>
              Edit
            </Button>
            <Button size="sm" variant="ghost" onClick={() => setRevokeTarget(row)}>Revoke</Button>
          </div>
        ) : null,
    },
  ];

  return (
    <div className="flex flex-col gap-4">
      {canManage && !readOnly ? (
        <div>
          <Link to="/app/records/$formRecordId/sharing" params={{ formRecordId }} className={buttonClass("secondary", "sm")}>Open sharing manager</Link>
          {projection.permissions.canTransferOwnership ? (
            <Link to="/app/records/$formRecordId/ownership" params={{ formRecordId }} className={`ml-2 ${buttonClass("secondary", "sm")}`}>Transfer ownership</Link>
          ) : null}
        </div>
      ) : null}
      <Card title="Your permissions on this record">
        <DescriptionList
          items={[
            { term: "Read", detail: projection.permissions.canRead ? "Yes" : "No" },
            {
              term: "Edit",
              detail: projection.permissions.canEdit
                ? "Yes"
                : !projection.permissions.isCompleteProjection && sharedEditable > 0
                  ? `Shared fields you can change: ${sharedEditable}`
                  : !projection.permissions.isCompleteProjection && !writesEnabled && (projection.sharedFieldAccess ?? []).length > 0
                    ? "No (changing shared fields is not enabled)"
                    : "No",
            },
            { term: "Manage access", detail: projection.permissions.canManageAccess ? "Yes" : "No" },
            { term: "Transfer ownership", detail: projection.permissions.canTransferOwnership ? "Yes" : "No" },
            { term: "View scope", detail: projection.permissions.isCompleteProjection ? "Complete record" : `Shared fields only (${projection.record.values.length})` },
            { term: "Authorization revision", detail: String(projection.record.authorizationRevision) },
            { term: "Committed operation", detail: projection.record.committedOperationId ?? "none" },
          ]}
        />
        <p className="text-sm text-muted">Permissions are decided by the independent authorization authority on every request; this list is informational.</p>
      </Card>
      {canListShares ? (
        <Card
          title={shareData ? `Shares (${activeShares.length} active)` : "Shares"}
          actions={
            <>
              <Button size="sm" variant="secondary" busy={shares.isFetching} onClick={() => void shares.refetch()}>
                Refresh
              </Button>
              {!readOnly ? (
                <Button size="sm" onClick={() => setOpen(true)} disabled={projection.record.values.length === 0}>
                  Share fields
                </Button>
              ) : null}
            </>
          }
        >
          {revokedNotice ? (
            <StatusMessage tone="success" title={`Access revoked for ${revokedNotice}`} actions={<Button size="sm" variant="ghost" onClick={() => setRevokedNotice(null)}>Dismiss</Button>}>
              They lose discovery, reading and editing of the shared fields on their next request. Changes they already saved stay in the record history.
            </StatusMessage>
          ) : null}
          {!shareData && !shares.isError && !shareRevisionChanged ? <LoadingBlock label="Loading shares" /> : null}
          {shareRevisionChanged ? <StatusMessage tone="info" title="Sharing access changed. Refresh access to see the latest shares." /> : null}
          {shares.isError ? <ErrorPanel error={shares.error} onRetry={() => void shares.refetch()} /> : null}
          {shareData ? (
            <DataTable
              caption="Shares of this record"
              rowKey={(row) => row.recordShareId}
              rows={shareData.items}
              emptyMessage="No shares to display."
              columns={[
                { key: "recipient", header: "Recipient", isRowHeader: true, render: (row) => row.recipientDisplayName ?? (row.recipientTeamId ? `Team ${row.recipientTeamId}` : (row.recipientUserId ?? "unknown")) },
                { key: "role", header: "Role", render: (row) => row.recipientRole },
                { key: "changes", header: "Can change values", render: (row) => (row.recipientRole !== "Reader" && writesEnabled ? "Yes" : "No") },
                { key: "fields", header: "Fields", render: (row) => row.fieldKeys.join(", ") },
                { key: "status", header: "Status", render: (row) => <Badge tone={row.shareStatus === "Active" ? "success" : "neutral"}>{row.shareStatus}</Badge> },
                { key: "by", header: "Shared by", render: (row) => row.sharedByDisplayName ?? row.sharedByUserId },
                { key: "created", header: "Created", render: (row) => getActiveFormatter().instant(row.shareCreatedOn) },
                {
                  key: "details",
                  header: "Details",
                  render: (row) => (
                    <Link to="/app/records/$formRecordId/sharing/$recordShareId" params={{ formRecordId, recordShareId: row.recordShareId }} aria-label={`Details of the share for ${row.recipientDisplayName ?? row.recipientUserId ?? row.recipientTeamId ?? "recipient"}`}>
                      Details
                    </Link>
                  ),
                },
                ...shareActions,
              ]}
            />
          ) : null}
          <StatusMessage tone="info" title="What a share covers">
            A share names exactly the fields chosen now. Fields added later, documents, and child records such as addresses are not included and need their own share. A share follows the current value of a field: if you clear a field or change a multiple choice, the new value is not covered until you share it again.
          </StatusMessage>
        </Card>
      ) : null}
      {canListShares && projection.permissions.isCompleteProjection ? (
        <Card
          title="Sharing history"
          aria-label="Sharing history"
          actions={
            <Button size="sm" variant="secondary" busy={history.isFetching} onClick={() => void history.refetch()}>
              Refresh
            </Button>
          }
        >
          {!historyData && !history.isError ? <LoadingBlock label="Loading sharing history" /> : null}
          {history.isError ? <ErrorPanel error={history.error} onRetry={() => void history.refetch()} /> : null}
          {historyData ? (collaboration.length > 0 ? <ActivityTimeline events={collaboration} /> : <p className="text-sm text-muted">No shares, revocations or shared edits yet.</p>) : null}
          <p className="mt-2 text-sm text-muted">Shares granted and revoked, and changes made by people with shared edit access. Values are never shown here; the full audit history is under Activity and history.</p>
        </Card>
      ) : null}
      {canListShares && !readOnly ? <RecordSecurityHistoryCard formRecordId={formRecordId} /> : null}
      {!readOnly && editTarget ? <EditShareDialog formRecordId={formRecordId} share={editTarget} projection={projection} onClose={() => setEditTarget(null)} /> : null}
      {!readOnly && open ? <CreateShareDialog formRecordId={formRecordId} projection={projection} contributorWritesEnabled={writesEnabled} onClose={() => setOpen(false)} /> : null}
      <Dialog
        open={!readOnly && revokeTarget !== null}
        onOpenChange={(next) => {
          if (!next) {
            setRevokeTarget(null);
            setRevokeStepUp(false);
          }
        }}
        title="Revoke this share?"
        description="The authority removes the grant immediately; the recipient loses discovery, reading and editing of the shared fields on their next request. Changes they already saved stay in the record history."
        locked={revoke.isPending}
        footer={
          <>
            <Button variant="secondary" onClick={() => { setRevokeTarget(null); setRevokeStepUp(false); }} disabled={revoke.isPending}>
              Keep
            </Button>
            <Button variant="danger" busy={revoke.isPending} disabled={!revokeStepUp} onClick={() => revokeTarget && revoke.mutate(revokeTarget)}>
              Revoke share
            </Button>
          </>
        }
      >
        {revokeTarget ? (
          <p className="text-sm">
            {revokeTarget.recipientDisplayName ?? revokeTarget.recipientUserId ?? revokeTarget.recipientTeamId} ({revokeTarget.recipientRole}): {revokeTarget.fieldKeys.join(", ")}
          </p>
        ) : null}
        <Checkbox label="Confirm with the development-only step-up" checked={revokeStepUp} onCheckedChange={(checked) => setRevokeStepUp(checked === true)} helpText="Sharing changes need a fresh passkey confirmation. This synthetic stand-in exists only in local development and test; elsewhere the authority refuses the change until passkey step-up is available." />
        {revoke.isError ? <ErrorPanel error={revoke.error} /> : null}
      </Dialog>
    </div>
  );
}

function CreateShareDialog({ formRecordId, projection, contributorWritesEnabled, onClose }: { formRecordId: string; projection: RecordProjectionResponse; contributorWritesEnabled: boolean; onClose: () => void }) {
  const api = useApi();
  const queryClient = useQueryClient();
  const users = useQuery({ queryKey: ["users"], queryFn: () => api.organization.users().then((r) => r.result) });
  const presence = usePresence((users.data ?? []).map((user) => user.userId), !!users.data);
  const [recipientUserId, setRecipientUserId] = useState<string | undefined>(undefined);
  const [role, setRole] = useState("Reader");
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [documents, setDocuments] = useState<DocumentSelection>(new Map());
  // C10 (independent review F-2): creating a share is a human share command and needs the step-up.
  const [stepUp, setStepUp] = useState(false);
  const displayNames = new Map(projection.fields.map((field) => [field.fieldKey, field.fieldDisplayName]));
  // Only fields with a persisted value can be shared (the share binds to accepted manifest items). Fields of sections that
  // allow multiple entries are not shareable on their own in this version (the server refuses them: FIELD_IN_REPEATING_GROUP).
  const shareable = projection.record.values.filter((value) => !value.formRecordGroupId).map((value) => value.fieldKey).filter((key, index, all) => all.indexOf(key) === index).sort();
  const hasEntries = projection.record.values.some((value) => value.formRecordGroupId);
  // Phase C5: for a role that can change values, preview which selected fields would still be view only (advisory).
  const canChange = role !== "Reader" && contributorWritesEnabled;
  const viewOnly = canChange ? contributorViewOnlyReasons(projection.layoutVersion.layoutJson, projection.fields, selected) : new Map<string, string>();
  const create = useMutation({
    mutationFn: () =>
      api.shares
        .create(formRecordId, { recipientUserId: recipientUserId ?? null, recipientTeamId: null, recipientRole: role, fieldKeys: [...selected], useDevelopmentStepUp: stepUp, ...(documents.size > 0 ? { documents: toDocumentRequests(documents) } : {}) })
        .then((r) => r.result),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ["shares", formRecordId] });
      onClose();
    },
  });
  const toggle = (key: string, checked: boolean) =>
    setSelected((current) => {
      const next = new Set(current);
      if (checked) {
        next.add(key);
      } else {
        next.delete(key);
      }
      return next;
    });
  return (
    <Dialog
      open
      onOpenChange={(next) => (next ? undefined : onClose())}
      title="Share fields of this record"
      description="Choose one person, a role and the exact fields or documents. The authority commits the share; nothing outside the selection is exposed."
      locked={create.isPending}
      footer={
        <>
          <Button variant="secondary" onClick={onClose} disabled={create.isPending}>
            Cancel
          </Button>
          <Button busy={create.isPending} disabled={!recipientUserId || selected.size + documents.size === 0 || !stepUp} onClick={() => create.mutate()}>
            Share
          </Button>
        </>
      }
    >
      <div className="flex flex-col gap-3">
        {users.isPending ? <LoadingBlock label="Loading people" /> : null}
        {users.isError ? <ErrorPanel error={users.error} onRetry={() => void users.refetch()} /> : null}
        {users.data ? <Select label="Person" required value={recipientUserId} placeholder="Choose a person" options={users.data.map((user) => ({ value: user.userId, label: user.displayName, trailing: <PresenceIndicator state={presence[user.userId] ?? "Unknown"} /> }))} onValueChange={setRecipientUserId} /> : null}
        <Select label="Role" required value={role} options={rolesFor(contributorWritesEnabled)} onValueChange={setRole} />
        <fieldset className="flex flex-col gap-2 rounded-control border border-line p-3">
          <legend className={`px-1 ${legendClass}`}>Fields to share</legend>
          <div className="flex gap-2">
            <Button size="sm" variant="ghost" onClick={() => setSelected(new Set(shareable))}>
              Select all current fields
            </Button>
            <Button size="sm" variant="ghost" onClick={() => setSelected(new Set())}>
              Clear
            </Button>
          </div>
          {shareable.map((key) => (
            <div key={key} className="flex flex-wrap items-center gap-2">
              <Checkbox label={displayNames.get(key) ?? key} checked={selected.has(key)} onCheckedChange={(checked) => toggle(key, checked === true)} />
              {viewOnly.has(key) ? <Badge tone="neutral">View only: {viewOnly.get(key)}</Badge> : null}
            </div>
          ))}
          <p className="text-sm text-muted">Empty fields can't be shared yet: a share covers the values a record holds now.</p>
          {hasEntries ? <p className="text-sm text-muted">Fields in sections that allow multiple entries cannot be shared on their own.</p> : null}
          {canChange ? <p className="text-sm text-muted">A person with this role can change the values of the selected fields, except those marked View only. They cannot clear values, change multiple choices, entries, documents or related records.</p> : null}
        </fieldset>
        <ShareDocumentSelection documents={projection.documents} selection={documents} onChange={setDocuments} allowReplace={projection.permissions.canTransferOwnership && role !== "Reader"} />
        <Checkbox label="Confirm with the development-only step-up" checked={stepUp} onCheckedChange={(checked) => setStepUp(checked === true)} helpText="Sharing changes need a fresh passkey confirmation. This synthetic stand-in exists only in local development and test; elsewhere the authority refuses the change until passkey step-up is available." />
        {create.isError ? <ErrorPanel error={create.error} /> : null}
      </div>
    </Dialog>
  );
}

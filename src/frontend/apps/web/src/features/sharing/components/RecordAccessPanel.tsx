import type { RecordProjectionResponse, RecordShareSummaryResponse } from "@enterpriseforms/api-client";
import { useApi } from "@enterpriseforms/app-runtime";
import { Badge, Button, Card, Checkbox, DataTable, DescriptionList, Dialog, LoadingBlock, Select, StatusMessage } from "@enterpriseforms/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { ErrorPanel } from "../../support";

const ROLES = [
  { value: "Reader", label: "Reader — view the selected fields" },
  { value: "Contributor", label: "Contributor — view and edit the selected fields" },
  { value: "Manager", label: "Manager — contributor plus share within the same scope" },
];

/**
 * PG-017 Access tab (CLAUDE.md §4H, ADR-PB-002 Stage B item 5): the caller's effective permissions as decided by the
 * authority for this request, and — for access managers — the record's explicit field-scoped shares with create and
 * revoke. Every list row is a local projection of an authority command; it confers nothing. New fields, child records
 * and documents are never covered by an existing share.
 */
export function RecordAccessPanel({ formRecordId, projection }: { formRecordId: string; projection: RecordProjectionResponse }) {
  const api = useApi();
  const queryClient = useQueryClient();
  const canManage = projection.permissions.canManageAccess;
  const shares = useQuery({ queryKey: ["shares", formRecordId], queryFn: () => api.shares.list(formRecordId).then((r) => r.result), enabled: canManage });
  const [open, setOpen] = useState(false);
  const [revokeTarget, setRevokeTarget] = useState<RecordShareSummaryResponse | null>(null);
  const revoke = useMutation({
    mutationFn: (share: RecordShareSummaryResponse) => api.shares.revoke(formRecordId, share.recordShareId).then((r) => r.result),
    onSuccess: async () => {
      setRevokeTarget(null);
      await queryClient.invalidateQueries({ queryKey: ["shares", formRecordId] });
    },
  });
  const activeShares = shares.data?.items.filter((share) => share.shareStatus === "Active") ?? [];

  return (
    <div className="flex flex-col gap-4">
      <Card title="Your permissions on this record">
        <DescriptionList
          items={[
            { term: "Read", detail: projection.permissions.canRead ? "Yes" : "No" },
            { term: "Edit", detail: projection.permissions.canEdit ? "Yes" : "No" },
            { term: "Manage access", detail: projection.permissions.canManageAccess ? "Yes" : "No" },
            { term: "Transfer ownership", detail: projection.permissions.canTransferOwnership ? "Yes" : "No" },
            { term: "View scope", detail: projection.permissions.isCompleteProjection ? "Complete record" : `Shared fields only (${projection.record.values.length})` },
            { term: "Authorization revision", detail: String(projection.record.authorizationRevision) },
            { term: "Committed operation", detail: projection.record.committedOperationId ?? "none" },
          ]}
        />
        <p className="text-sm text-muted">Permissions are decided by the independent authorization authority on every request; this list is informational.</p>
      </Card>
      {canManage ? (
        <Card
          title={`Shares (${activeShares.length} active)`}
          actions={
            <>
              <Button size="sm" variant="secondary" busy={shares.isFetching} onClick={() => void shares.refetch()}>
                Refresh
              </Button>
              <Button size="sm" onClick={() => setOpen(true)} disabled={projection.record.values.length === 0}>
                Share fields
              </Button>
            </>
          }
        >
          {shares.isPending ? <LoadingBlock label="Loading shares" /> : null}
          {shares.isError ? <ErrorPanel error={shares.error} onRetry={() => void shares.refetch()} /> : null}
          {shares.data ? (
            <DataTable
              caption="Shares of this record"
              rowKey={(row) => row.recordShareId}
              rows={shares.data.items}
              emptyMessage="No shares. Only the owner can see this record."
              columns={[
                { key: "recipient", header: "Recipient", isRowHeader: true, render: (row) => row.recipientDisplayName ?? (row.recipientTeamId ? `Team ${row.recipientTeamId}` : (row.recipientUserId ?? "unknown")) },
                { key: "role", header: "Role", render: (row) => row.recipientRole },
                { key: "fields", header: "Fields", render: (row) => row.fieldKeys.join(", ") },
                { key: "status", header: "Status", render: (row) => <Badge tone={row.shareStatus === "Active" ? "success" : "neutral"}>{row.shareStatus}</Badge> },
                { key: "by", header: "Shared by", render: (row) => row.sharedByDisplayName ?? row.sharedByUserId },
                { key: "created", header: "Created", render: (row) => new Date(row.shareCreatedOn).toLocaleString() },
                { key: "actions", header: "Actions", isActions: true, render: (row) => (row.shareStatus === "Active" ? <Button size="sm" variant="ghost" onClick={() => setRevokeTarget(row)}>Revoke</Button> : null) },
              ]}
            />
          ) : null}
          <StatusMessage tone="info" title="What a share covers">
            A share names exactly the fields chosen now. Fields added later, documents, and child records such as addresses are not included and need their own share.
          </StatusMessage>
        </Card>
      ) : null}
      {open ? <CreateShareDialog formRecordId={formRecordId} projection={projection} onClose={() => setOpen(false)} /> : null}
      <Dialog
        open={revokeTarget !== null}
        onOpenChange={(next) => (next ? undefined : setRevokeTarget(null))}
        title="Revoke this share?"
        description="The authority removes the grant immediately; the recipient loses discovery, reading and editing of the shared fields on their next request."
        locked={revoke.isPending}
        footer={
          <>
            <Button variant="secondary" onClick={() => setRevokeTarget(null)} disabled={revoke.isPending}>
              Keep
            </Button>
            <Button variant="danger" busy={revoke.isPending} onClick={() => revokeTarget && revoke.mutate(revokeTarget)}>
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
        {revoke.isError ? <ErrorPanel error={revoke.error} /> : null}
      </Dialog>
    </div>
  );
}

function CreateShareDialog({ formRecordId, projection, onClose }: { formRecordId: string; projection: RecordProjectionResponse; onClose: () => void }) {
  const api = useApi();
  const queryClient = useQueryClient();
  const users = useQuery({ queryKey: ["users"], queryFn: () => api.organization.users().then((r) => r.result) });
  const [recipientUserId, setRecipientUserId] = useState<string | undefined>(undefined);
  const [role, setRole] = useState("Reader");
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const displayNames = new Map(projection.fields.map((field) => [field.fieldKey, field.fieldDisplayName]));
  // Only fields with a persisted value can be shared (the share binds to accepted manifest items). Fields of sections that
  // allow multiple entries are not shareable on their own in this version (the server refuses them: FIELD_IN_REPEATING_GROUP).
  const shareable = projection.record.values.filter((value) => !value.formRecordGroupId).map((value) => value.fieldKey).filter((key, index, all) => all.indexOf(key) === index).sort();
  const hasEntries = projection.record.values.some((value) => value.formRecordGroupId);
  const create = useMutation({
    mutationFn: () => api.shares.create(formRecordId, { recipientUserId: recipientUserId ?? null, recipientTeamId: null, recipientRole: role, fieldKeys: [...selected] }).then((r) => r.result),
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
      description="Choose one person, a role and the exact fields. The authority commits the share; nothing outside the selected fields is exposed."
      locked={create.isPending}
      footer={
        <>
          <Button variant="secondary" onClick={onClose} disabled={create.isPending}>
            Cancel
          </Button>
          <Button busy={create.isPending} disabled={!recipientUserId || selected.size === 0} onClick={() => create.mutate()}>
            Share
          </Button>
        </>
      }
    >
      <div className="flex flex-col gap-3">
        {users.isPending ? <LoadingBlock label="Loading people" /> : null}
        {users.isError ? <ErrorPanel error={users.error} onRetry={() => void users.refetch()} /> : null}
        {users.data ? <Select label="Person" required value={recipientUserId} placeholder="Choose a person" options={users.data.map((user) => ({ value: user.userId, label: user.displayName }))} onValueChange={setRecipientUserId} /> : null}
        <Select label="Role" required value={role} options={ROLES} onValueChange={setRole} />
        <fieldset className="flex flex-col gap-2 rounded border border-line p-3">
          <legend className="px-1 text-sm font-medium">Fields to share</legend>
          <div className="flex gap-2">
            <Button size="sm" variant="ghost" onClick={() => setSelected(new Set(shareable))}>
              Select all current fields
            </Button>
            <Button size="sm" variant="ghost" onClick={() => setSelected(new Set())}>
              Clear
            </Button>
          </div>
          {shareable.map((key) => (
            <Checkbox key={key} label={displayNames.get(key) ?? key} checked={selected.has(key)} onCheckedChange={(checked) => toggle(key, checked === true)} />
          ))}
          {hasEntries ? <p className="text-sm text-muted">Fields in sections that allow multiple entries cannot be shared on their own.</p> : null}
        </fieldset>
        {create.isError ? <ErrorPanel error={create.error} /> : null}
      </div>
    </Dialog>
  );
}

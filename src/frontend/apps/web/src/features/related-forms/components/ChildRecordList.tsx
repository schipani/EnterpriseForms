import type { RelationshipDefinitionResponse } from "@enterpriseforms/api-client";
import { useApi } from "@enterpriseforms/app-runtime";
import { Badge, Button, Card, DataTable, Dialog, LoadingBlock, TextInput } from "@enterpriseforms/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Link } from "@tanstack/react-router";
import { useState } from "react";
import { ErrorPanel } from "../../support";

/**
 * Reusable child-record list (PG-019): each item is an independent FormRecord linked through the relationship.
 * Add navigates to the child editor; view/edit open the child record; unlink keeps the child's data and history.
 */
export function ChildRecordList({ parentFormRecordId, relationship, label, canEdit, summaryFieldKeys }: { parentFormRecordId: string; relationship: RelationshipDefinitionResponse; label: string; canEdit: boolean; summaryFieldKeys: readonly string[] }) {
  const api = useApi();
  const queryClient = useQueryClient();
  const list = useQuery({ queryKey: ["related", parentFormRecordId, relationship.formRelationshipDefinitionId], queryFn: () => api.related.list(parentFormRecordId, relationship.formRelationshipDefinitionId).then((r) => r.result) });
  const [unlinkTarget, setUnlinkTarget] = useState<string | null>(null);
  const [reason, setReason] = useState("");
  const unlink = useMutation({
    mutationFn: (childId: string) => api.related.unlink(parentFormRecordId, childId, { removalReason: reason.trim() }).then((r) => r.result),
    onSuccess: async () => {
      setUnlinkTarget(null);
      setReason("");
      await queryClient.invalidateQueries({ queryKey: ["related", parentFormRecordId] });
    },
  });
  const title = label || relationship.relationshipDisplayName;
  const keys = summaryFieldKeys.length > 0 ? summaryFieldKeys : undefined;
  return (
    <Card
      title={`${title} (${list.data?.items.length ?? 0})`}
      actions={
        <>
          <Button size="sm" variant="secondary" busy={list.isFetching} onClick={() => void list.refetch()}>
            Refresh
          </Button>
          {canEdit && (list.data?.items.length ?? 0) < Number(relationship.maximumChildCount) ? (
            <Link to="/app/records/$formRecordId/related/$formRelationshipDefinitionId/new" params={{ formRecordId: parentFormRecordId, formRelationshipDefinitionId: relationship.formRelationshipDefinitionId }}>
              <Button size="sm">Add {relationship.childFormDefinitionName}</Button>
            </Link>
          ) : null}
        </>
      }
    >
      {list.isPending ? <LoadingBlock label={`Loading ${title}`} /> : null}
      {list.isError ? <ErrorPanel error={list.error} onRetry={() => void list.refetch()} /> : null}
      {list.data ? (
        <DataTable
          caption={title}
          rowKey={(row) => row.formRecordId}
          rows={list.data.items}
          emptyMessage={`No ${relationship.childFormDefinitionName} records yet.`}
          columns={[
            { key: "order", header: "#", render: (row) => String(row.listSortOrder) },
            {
              key: "summary",
              header: relationship.childFormDefinitionName,
              isRowHeader: true,
              render: (row) => {
                const shown = row.summaryValues.filter((value) => !keys || keys.includes(value.fieldKey));
                const text = (shown.length > 0 ? shown : row.summaryValues).map((value) => value.stringValue).filter(Boolean).join(", ");
                return (
                  <Link to="/app/records/$formRecordId" params={{ formRecordId: row.formRecordId }} className="underline-offset-4 hover:underline">
                    {text || `(${relationship.childFormDefinitionName} ${String(row.listSortOrder)})`}
                  </Link>
                );
              },
            },
            { key: "status", header: "Status", render: (row) => <Badge tone="neutral">{row.recordStatus}</Badge> },
            { key: "integrity", header: "Integrity", render: (row) => <Badge tone={row.integrityStatus === "Verified" ? "success" : "danger"}>{row.integrityStatus}</Badge> },
            {
              key: "actions",
              header: "Actions",
              render: (row) => (
                <div className="flex flex-wrap gap-2">
                  <Link to="/app/records/$formRecordId" params={{ formRecordId: row.formRecordId }}>
                    <Button size="sm" variant="secondary">
                      View
                    </Button>
                  </Link>
                  {canEdit ? (
                    <>
                      <Link to="/app/records/$formRecordId/edit" params={{ formRecordId: row.formRecordId }}>
                        <Button size="sm" variant="secondary">
                          Edit
                        </Button>
                      </Link>
                      <Button size="sm" variant="ghost" onClick={() => setUnlinkTarget(row.formRecordId)}>
                        Remove
                      </Button>
                    </>
                  ) : null}
                </div>
              ),
            },
          ]}
        />
      ) : null}
      <Dialog
        open={unlinkTarget !== null}
        onOpenChange={(open) => (open ? undefined : setUnlinkTarget(null))}
        title={`Remove this ${relationship.childFormDefinitionName} from the list`}
        description="The child record and its history are kept; only the link to this parent is ended through the authority."
        locked={unlink.isPending}
        footer={
          <>
            <Button variant="secondary" onClick={() => setUnlinkTarget(null)} disabled={unlink.isPending}>
              Cancel
            </Button>
            <Button variant="danger" busy={unlink.isPending} disabled={reason.trim().length < 3} onClick={() => unlinkTarget && unlink.mutate(unlinkTarget)}>
              Remove from list
            </Button>
          </>
        }
      >
        <TextInput label="Reason" value={reason} required maxLength={500} onChange={(event) => setReason(event.target.value)} />
        {unlink.isError ? <ErrorPanel error={unlink.error} /> : null}
      </Dialog>
    </Card>
  );
}

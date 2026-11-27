import { useApi } from "@enterpriseforms/app-runtime";
import { Badge, Button, buttonClass, Card, Checkbox, LoadingBlock, PageHeader, Select, StatusMessage, TextArea, getActiveFormatter, useFormatter } from "@enterpriseforms/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Link, useParams } from "@tanstack/react-router";
import { useState } from "react";
import { ErrorPanel } from "../../support";

/**
 * PG-053 Ownership transfer workflow (C10 M3, WP-0 Section 3.7): the owner (or a Security Administrator's audited override) moves the
 * record to another person with a reason and a fresh step-up; the authorization authority decides and records it. Shares the former
 * owner granted stay; the former owner keeps only access granted to them separately. The page shows the command's real state: a
 * transfer awaiting projection is pending, not done. The ownership history comes from the authority.
 */
export function OwnershipTransferPage() {
  useFormatter();
  const { formRecordId } = useParams({ from: "/app/records/$formRecordId/ownership" });
  const api = useApi();
  const queryClient = useQueryClient();
  const projection = useQuery({ queryKey: ["ownership", "projection", formRecordId], queryFn: ({ signal }) => api.records.projection(formRecordId, signal).then((r) => r.result), refetchOnMount: "always" });
  const users = useQuery({ queryKey: ["users"], queryFn: ({ signal }) => api.organization.users(signal).then((r) => r.result) });
  const history = useQuery({ queryKey: ["ownership", "history", formRecordId], queryFn: ({ signal }) => api.shares.ownershipHistory(formRecordId, signal).then((r) => r.result), enabled: projection.data?.permissions.canTransferOwnership === true });
  const [newOwner, setNewOwner] = useState<string | undefined>(undefined);
  const [reason, setReason] = useState("");
  const [stepUp, setStepUp] = useState(false);
  const transfer = useMutation({
    // C10 review F4: a pending transfer is replayed under the same idempotency key (bounded) so its audit projection is recorded once it is in effect.
    mutationFn: () => {
      const request = { newOwnerUserId: newOwner ?? null, newOwnerTeamId: null, transferReason: reason.trim(), useDevelopmentStepUp: stepUp };
      return submitUntilEffective((key) => api.shares.transferOwnership(formRecordId, request, key).then((r) => r.result), newTransferIdempotencyKey());
    },
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ["ownership", "history", formRecordId] });
    },
  });
  const back = (
    <Link to="/app/records/$formRecordId" params={{ formRecordId }} search={{ tab: "sharing" }} className={buttonClass("secondary")}>
      Back to record
    </Link>
  );

  if (projection.isPending) {
    return <LoadingBlock label="Verifying your permissions on this record" />;
  }

  if (projection.isError) {
    return (
      <>
        <PageHeader title="Transfer ownership" actions={back} />
        <ErrorPanel error={projection.error} onRetry={() => void projection.refetch()} />
      </>
    );
  }

  if (!projection.data.permissions.canTransferOwnership) {
    return (
      <>
        <PageHeader title="Transfer ownership" actions={back} />
        <StatusMessage tone="info" title="You cannot transfer this record">
          Only the record's owner can transfer it. A Security Administrator can reassign records as an audited override from the member's page.
        </StatusMessage>
      </>
    );
  }

  const outcome = transfer.data?.command;
  const formatter = getActiveFormatter();
  return (
    <>
      <PageHeader title="Transfer ownership" lead={projection.data.formDefinitionName} actions={back} />
      <div className="flex flex-col gap-4">
        <Card title="New owner">
          <div className="flex flex-col gap-3">
            {users.isPending ? <LoadingBlock label="Loading people" /> : null}
            {users.isError ? <ErrorPanel error={users.error} onRetry={() => void users.refetch()} /> : null}
            {users.data ? <Select label="Person" required value={newOwner} placeholder="Choose a person" options={users.data.map((user) => ({ value: user.userId, label: user.displayName }))} onValueChange={setNewOwner} /> : null}
            <TextArea label="Reason" required value={reason} maxLength={1000} helpText="Recorded with the transfer by the authorization authority." onChange={(event) => setReason(event.target.value)} />
            <Checkbox
              label="Confirm with the development-only step-up"
              checked={stepUp}
              onCheckedChange={(checked) => setStepUp(checked === true)}
              helpText="Ownership transfer needs a fresh passkey confirmation. This synthetic stand-in exists only in local development and test; elsewhere the authority refuses the transfer until passkey step-up is available."
            />
            <StatusMessage tone="info" title="What changes">
              The new owner gets full control of the record. Shares you granted stay in place; you keep only access someone else granted you. The record's form design is not affected.
            </StatusMessage>
            <div>
              <Button busy={transfer.isPending} disabled={!newOwner || reason.trim().length < 3 || !stepUp || outcome?.effective === true} onClick={() => transfer.mutate()}>
                Transfer ownership
              </Button>
            </div>
            {outcome ? (
              outcome.effective ? (
                <StatusMessage tone="success" title="Ownership transferred" />
              ) : (
                <StatusMessage tone="info" title="Transfer recorded, not yet in effect">
                  The authorization authority accepted the transfer and is applying it. Refresh the history to see when it takes effect.
                </StatusMessage>
              )
            ) : null}
            {transfer.isError ? <ErrorPanel error={transfer.error} /> : null}
          </div>
        </Card>
        <Card
          title="Ownership history"
          actions={
            <Button size="sm" variant="secondary" busy={history.isFetching} onClick={() => void history.refetch()}>
              Refresh
            </Button>
          }
        >
          {history.isPending ? <LoadingBlock label="Loading ownership history" /> : null}
          {history.isError ? <ErrorPanel error={history.error} onRetry={() => void history.refetch()} /> : null}
          {history.data ? (
            history.data.items.length === 0 ? (
              <p className="text-sm text-muted">No ownership changes yet.</p>
            ) : (
              <ol className="flex flex-col gap-2" aria-label="Ownership changes, newest first">
                {history.data.items.map((item) => (
                  <li key={item.securityCommandId} className="flex flex-wrap items-center gap-2 text-sm">
                    <span>{formatter.instant(item.occurredOn)}</span>
                    <Badge tone={item.commandState === "Effective" ? "success" : "warning"}>{item.commandState === "Effective" ? "In effect" : "Pending"}</Badge>
                    <span>
                      {item.changes.map((change) => `${change.changeKind === "OwnerAdded" ? "New owner" : "Previous owner"}: ${change.subjectDisplayName ?? change.subjectId ?? "unknown"}`).join("; ")}
                    </span>
                  </li>
                ))}
              </ol>
            )
          ) : null}
        </Card>
      </div>
    </>
  );
}

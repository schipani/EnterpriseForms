import type { ReassignRecordsResponse } from "@enterpriseforms/api-client";
import { presentError, useApi } from "@enterpriseforms/app-runtime";
import { Badge, Button, buttonClass, Card, Checkbox, DataTable, LoadingBlock, PageHeader, Select, StatusMessage, TextArea } from "@enterpriseforms/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Link, useParams } from "@tanstack/react-router";
import { useState } from "react";
import { ErrorPanel } from "../../support";

/**
 * PG-053 reassignment of a member's records (C10 M3, WP-0 Section 3.7, D8-4): before a member who owns records is disabled, a
 * Security Administrator or organization owner moves each record the member directly owns. Each record is its own ownership transfer
 * with this reason and step-up, decided by the authorization authority (an administrator can never make themselves or a team they
 * belong to the owner); the page shows every record's own outcome, and pending transfers as pending.
 */
export function MemberReassignPage() {
  const { memberUserId } = useParams({ from: "/app/members/$memberUserId/reassign" });
  const api = useApi();
  const queryClient = useQueryClient();
  const owned = useQuery({ queryKey: ["member-owned-records", memberUserId], queryFn: ({ signal }) => api.shares.memberOwnedRecords(memberUserId, signal).then((r) => r.result), refetchOnMount: "always" });
  const users = useQuery({ queryKey: ["users"], queryFn: ({ signal }) => api.organization.users(signal).then((r) => r.result) });
  const [newOwner, setNewOwner] = useState<string | undefined>(undefined);
  const [reason, setReason] = useState("");
  const [stepUp, setStepUp] = useState(false);
  const reassign = useMutation({
    mutationFn: () => api.shares.reassignRecords(memberUserId, { newOwnerUserId: newOwner ?? null, newOwnerTeamId: null, transferReason: reason.trim(), useDevelopmentStepUp: stepUp }).then((r) => r.result),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ["member-owned-records", memberUserId] });
    },
  });
  const back = (
    <Link to="/app/organization/users" className={buttonClass("secondary")}>
      Back to people
    </Link>
  );
  const member = users.data?.find((user) => user.userId === memberUserId)?.displayName ?? "this member";

  if (owned.isPending) {
    return <LoadingBlock label="Reading the member's records from the authorization authority" />;
  }

  if (owned.isError) {
    return (
      <>
        <PageHeader title="Reassign records" actions={back} />
        {presentError(owned.error).status === 404 ? (
          <StatusMessage tone="info" title="Reassignment is not available">
            Only a Security Administrator can reassign a member's records.
          </StatusMessage>
        ) : (
          <ErrorPanel error={owned.error} onRetry={() => void owned.refetch()} />
        )}
      </>
    );
  }

  const result: ReassignRecordsResponse | undefined = reassign.data;
  return (
    <>
      <PageHeader title="Reassign records" lead={`Records ${member} owns directly: ${owned.data.formRecordIds.length}`} actions={back} />
      <div className="flex flex-col gap-4">
        {owned.data.formRecordIds.length === 0 && !result ? (
          <StatusMessage tone="success" title={`${member} owns no records directly`}>
            Nothing needs to be reassigned before the membership is disabled.
          </StatusMessage>
        ) : (
          <Card title="New owner">
            <div className="flex flex-col gap-3">
              {users.data ? (
                <Select label="Person" required value={newOwner} placeholder="Choose a person" options={users.data.filter((user) => user.userId !== memberUserId).map((user) => ({ value: user.userId, label: user.displayName }))} onValueChange={setNewOwner} />
              ) : (
                <LoadingBlock label="Loading people" />
              )}
              <TextArea label="Reason" required value={reason} maxLength={1000} helpText="Recorded with every transfer by the authorization authority." onChange={(event) => setReason(event.target.value)} />
              <Checkbox
                label="Confirm with the development-only step-up"
                checked={stepUp}
                onCheckedChange={(checked) => setStepUp(checked === true)}
                helpText="Each transfer needs a fresh passkey confirmation. This synthetic stand-in exists only in local development and test."
              />
              <div>
                <Button busy={reassign.isPending} disabled={!newOwner || reason.trim().length < 3 || !stepUp} onClick={() => reassign.mutate()}>
                  Reassign {owned.data.formRecordIds.length} record(s)
                </Button>
              </div>
              {reassign.isError ? <ErrorPanel error={reassign.error} /> : null}
            </div>
          </Card>
        )}
        {result ? (
          <Card title="Outcome">
            <DataTable
              caption="One ownership transfer per record"
              rowKey={(row) => row.formRecordId}
              rows={result.records}
              columns={[
                { key: "record", header: "Record", isRowHeader: true, render: (row) => <Link to="/app/records/$formRecordId" params={{ formRecordId: row.formRecordId }}>{row.formRecordId}</Link> },
                {
                  key: "state",
                  header: "Result",
                  render: (row) =>
                    row.effective ? <Badge tone="success">Transferred</Badge> : row.commandStatus === "Refused" ? <Badge tone="danger">Refused ({row.refusalCode})</Badge> : <Badge tone="warning">Pending</Badge>,
                },
              ]}
            />
          </Card>
        ) : null}
      </div>
    </>
  );
}

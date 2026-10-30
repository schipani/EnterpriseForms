import type { SecurityAdministratorResponse } from "@enterpriseforms/api-client";
import { useApi, useSession } from "@enterpriseforms/app-runtime";
import { ActionIcons, Badge, Button, Card, Checkbox, DataTable, Dialog, LoadingBlock, Select, StatusMessage, TextInput } from "@enterpriseforms/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { ErrorPanel } from "../../support";

const rosterKey = ["organization", "security-administrators"];

/**
 * Security Administrators of the organization (owner message 7). A Security Administrator holds the Authority relation
 * system_administrator (permission administer_security): search approvals and search access today — never record content,
 * Form Designer rights or the organization's encryption policy. Only an existing Security Administrator can appoint or
 * remove one, through Authority security commands with step-up; organization owners see the list read-only. The first
 * Security Administrator of an organization comes from the independently authorized operator process.
 */
export function SecurityAdministratorsCard() {
  const api = useApi();
  const session = useSession();
  const queryClient = useQueryClient();
  const roster = useQuery({ queryKey: rosterKey, queryFn: ({ signal }) => api.organization.securityAdministrators(signal).then((r) => r.result) });
  const [adding, setAdding] = useState(false);
  const [removing, setRemoving] = useState<SecurityAdministratorResponse | null>(null);
  const [userId, setUserId] = useState<string | undefined>(undefined);
  const [reason, setReason] = useState("");
  const [stepUp, setStepUp] = useState(false);
  const close = () => {
    setAdding(false);
    setRemoving(null);
    setUserId(undefined);
    setReason("");
    setStepUp(false);
  };
  const refresh = async () => {
    await queryClient.invalidateQueries({ queryKey: rosterKey });
    await session.refresh();
  };
  const appoint = useMutation({
    mutationFn: () => api.organization.appointSecurityAdministrator({ userId: userId!, reason: reason.trim(), useDevelopmentStepUp: stepUp }).then((r) => r.result),
    onSuccess: async () => {
      close();
      await refresh();
    },
  });
  const remove = useMutation({
    mutationFn: (target: SecurityAdministratorResponse) => api.organization.removeSecurityAdministrator(target.userId, { reason: reason.trim(), useDevelopmentStepUp: stepUp }).then((r) => r.result),
    onSuccess: async () => {
      close();
      await refresh();
    },
  });

  if (roster.isPending) {
    return <LoadingBlock label="Reading Security Administrators from the Authorization Authority" />;
  }
  if (roster.isError) {
    return <ErrorPanel error={roster.error} onRetry={() => void roster.refetch()} />;
  }

  const data = roster.data;
  const last = data.administrators.length <= 1;
  const reasonValid = reason.trim().length >= 3;
  const stepUpReady = !data.appointmentOperational || stepUp;
  const stepUpField = data.appointmentOperational ? (
    <Checkbox
      label="Confirm with the development-only step-up"
      checked={stepUp}
      onCheckedChange={(checked) => setStepUp(checked === true)}
      helpText="A synthetic stand-in for a passkey confirmation that exists only in local development and test. It proves nothing about the person and can never be enabled in staging or production."
    />
  ) : (
    <StatusMessage tone="info" title="Not available in this environment">
      Changing Security Administrators needs a fresh passkey confirmation (step-up), which is not available yet (PB-08). The Authorization Authority refuses every change here.
    </StatusMessage>
  );

  return (
    <Card
      title={`Security Administrators (${data.administrators.length})`}
      actions={
        data.canManage ? (
          <Button size="sm" icon={ActionIcons.add} onClick={() => setAdding(true)} disabled={data.candidates.length === 0}>
            Add administrator
          </Button>
        ) : null
      }
    >
      <p className="m-0 text-sm text-muted">
        Security Administrators approve which plaintext fields can be searched and who may search them. The role gives no access to records, no Form Designer rights and no control over the organization's encryption policy.
      </p>
      {data.administrators.length === 0 ? (
        <StatusMessage tone="warning" title="This organization has no Security Administrator">
          Organization owners cannot appoint themselves or anyone else. The first Security Administrator is appointed through the independently authorized operator process; until then, search approvals cannot be administered.
        </StatusMessage>
      ) : (
        <DataTable
          caption="Security Administrators"
          rowKey={(row) => row.userId}
          rows={[...data.administrators]}
          stickyActions
          columns={[
            { key: "name", header: "Name", isRowHeader: true, render: (row) => (row.isCurrentUser ? `${row.displayName} (you)` : row.displayName) },
            { key: "identity", header: "Verified identity", render: (row) => <span className="break-all text-sm">{`${row.identitySubject} · ${row.identityIssuer}`}</span> },
            { key: "appointed", header: "Appointed", render: (row) => new Date(row.appointedOn).toLocaleDateString() },
            { key: "status", header: "Status", render: () => <Badge tone="success">Active</Badge> },
            ...(data.canManage
              ? [
                  {
                    key: "actions",
                    header: "Actions",
                    isActions: true,
                    render: (row: SecurityAdministratorResponse) => (
                      <Button size="sm" variant="ghost" icon={ActionIcons.delete} aria-label={`Remove ${row.displayName} as Security Administrator`} disabled={last} onClick={() => setRemoving(row)}>
                        Remove
                      </Button>
                    ),
                  },
                ]
              : []),
          ]}
        />
      )}
      {data.canManage && last && data.administrators.length === 1 ? <p className="m-0 text-sm text-muted">The last Security Administrator cannot be removed. Appoint another one first.</p> : null}
      {!data.canManage && data.administrators.length > 0 ? <p className="m-0 text-sm text-muted">Only Security Administrators can appoint or remove Security Administrators.</p> : null}

      <Dialog
        open={adding}
        onOpenChange={(open) => (open ? undefined : close())}
        title="Add a Security Administrator"
        description="Choose a verified member of this organization. The appointment is a recorded Authorization Authority command and takes effect immediately."
        locked={appoint.isPending}
        footer={
          <>
            <Button variant="secondary" onClick={close} disabled={appoint.isPending}>
              Cancel
            </Button>
            <Button busy={appoint.isPending} disabled={!userId || !reasonValid || !stepUpReady || !data.appointmentOperational} onClick={() => appoint.mutate()}>
              Appoint
            </Button>
          </>
        }
      >
        <Select label="Person" value={userId} placeholder="Choose a person" options={data.candidates.map((c) => ({ value: c.userId, label: `${c.displayName} — ${c.identitySubject}` }))} onValueChange={setUserId} />
        <TextInput label="Reason" value={reason} required maxLength={500} helpText="Recorded with the appointment (3 to 500 characters)." onChange={(event) => setReason(event.target.value)} />
        {stepUpField}
        {appoint.isError ? <ErrorPanel error={appoint.error} /> : null}
      </Dialog>

      <Dialog
        open={removing !== null}
        onOpenChange={(open) => (open ? undefined : close())}
        title={`Remove ${removing?.displayName ?? ""} as Security Administrator`}
        description="They lose search-approval administration on their next request. Nothing else about their account changes."
        locked={remove.isPending}
        footer={
          <>
            <Button variant="secondary" onClick={close} disabled={remove.isPending}>
              Keep administrator
            </Button>
            <Button variant="danger" busy={remove.isPending} disabled={!reasonValid || !stepUpReady || !data.appointmentOperational} onClick={() => removing && remove.mutate(removing)}>
              Remove administrator
            </Button>
          </>
        }
      >
        <TextInput label="Reason" value={reason} required maxLength={500} helpText="Recorded with the removal (3 to 500 characters)." onChange={(event) => setReason(event.target.value)} />
        {stepUpField}
        {remove.isError ? <ErrorPanel error={remove.error} /> : null}
      </Dialog>
    </Card>
  );
}

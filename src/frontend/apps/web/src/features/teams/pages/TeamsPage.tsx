import { useApi, useSession } from "@enterpriseforms/app-runtime";
import { ActionIcons, Badge, Button, Card, DataTable, Dialog, LoadingBlock, StatusMessage, TextInput } from "@enterpriseforms/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Link, useNavigate } from "@tanstack/react-router";
import { useState } from "react";
import { OrganizationLayout } from "../../organizations";
import { ErrorPanel } from "../../support";

export const teamDirectoryKey = ["organization", "teams"];

/**
 * PG-048 team directory (Phase C8 M1). Rows are the business projection of Authority team commands with member counts;
 * creating a team is the existing RegisterTeam Authority command (organization owner). A command that the Authority
 * committed but has not made Effective is reported as pending, never as done.
 */
export function TeamsPage() {
  const api = useApi();
  const session = useSession();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const allowed = session.hasCapability("organization.manage") || session.hasCapability("security.administer");
  const directory = useQuery({ queryKey: teamDirectoryKey, queryFn: ({ signal }) => api.teams.directory(signal).then((r) => r.result), enabled: allowed });
  const [creating, setCreating] = useState(false);
  const [teamName, setTeamName] = useState("");
  const [pending, setPending] = useState<string | null>(null);
  const create = useMutation({
    mutationFn: () => api.teams.create({ teamName: teamName.trim() }),
    onSuccess: async (response) => {
      await queryClient.invalidateQueries({ queryKey: teamDirectoryKey });
      setCreating(false);
      setTeamName("");
      if (response.status === 202 || !response.result?.command?.effective) {
        setPending("The Authorization Authority committed the team but has not made it effective yet. It appears once the Authority reports it effective; refresh to check.");
        return;
      }
      await navigate({ to: "/app/organization/teams/$teamId", params: { teamId: response.result.teamId } });
    },
  });

  return (
    <OrganizationLayout
      title="Teams"
      lead="Teams group people for record ownership and form access. Membership that confers access lives only in the Authorization Authority; this directory is its projection."
      actions={
        <div className="flex flex-wrap gap-2">
          <Button size="sm" variant="ghost" icon={ActionIcons.refresh} busy={directory.isFetching} aria-label="Refresh Teams" onClick={() => void directory.refetch()}>
            Refresh
          </Button>
          {directory.data?.canManage ? (
            <Button size="sm" icon={ActionIcons.add} onClick={() => setCreating(true)}>
              Create team
            </Button>
          ) : null}
        </div>
      }
    >
      {directory.isPending ? <LoadingBlock label="Loading teams" /> : null}
      {directory.isError ? <ErrorPanel error={directory.error} onRetry={() => void directory.refetch()} /> : null}
      {pending ? (
        <StatusMessage tone="warning" title="Pending">
          {pending}
        </StatusMessage>
      ) : null}
      {directory.data ? (
        <Card title={`Teams (${directory.data.teams.length})`}>
          <p className="m-0 text-sm text-muted">Source: {directory.data.source}.</p>
          <DataTable
            caption="Teams"
            rowKey={(row) => row.teamId}
            rows={directory.data.teams}
            emptyMessage={directory.data.canManage ? "No team yet. Create one to group people for ownership and form access." : "No team yet."}
            columns={[
              {
                key: "name",
                header: "Team",
                isRowHeader: true,
                render: (row) => (
                  <Link to="/app/organization/teams/$teamId" params={{ teamId: row.teamId }} className="underline underline-offset-4">
                    {row.teamName}
                  </Link>
                ),
              },
              { key: "members", header: "Members", render: (row) => String(row.memberCount) },
              { key: "status", header: "Status", render: (row) => (row.isActive ? <Badge tone="success">Active</Badge> : <Badge tone="neutral">Inactive</Badge>) },
              { key: "created", header: "Created", render: (row) => new Date(row.createdOn).toLocaleDateString() },
            ]}
          />
        </Card>
      ) : null}

      <Dialog
        open={creating}
        onOpenChange={(open) => (open ? undefined : setCreating(false))}
        title="Create team"
        description="Registers the team with the Authorization Authority (a recorded security command). Members are added afterwards from the team page."
        locked={create.isPending}
        footer={
          <>
            <Button variant="secondary" onClick={() => setCreating(false)} disabled={create.isPending}>
              Cancel
            </Button>
            <Button busy={create.isPending} disabled={teamName.trim().length === 0} onClick={() => create.mutate()}>
              Create team
            </Button>
          </>
        }
      >
        <TextInput label="Team name" value={teamName} required maxLength={200} onChange={(event) => setTeamName(event.target.value)} />
        {create.isError ? <ErrorPanel error={create.error} /> : null}
      </Dialog>
    </OrganizationLayout>
  );
}

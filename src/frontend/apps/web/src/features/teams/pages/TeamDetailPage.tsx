import type { TeamMemberResponse } from "@enterpriseforms/api-client";
import { useApi, useSession } from "@enterpriseforms/app-runtime";
import { ActionIcons, Badge, Button, Card, DescriptionList, Dialog, LoadingBlock, Select, StatusMessage, TextInput } from "@enterpriseforms/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Link, useParams } from "@tanstack/react-router";
import { useState } from "react";
import { OrganizationLayout } from "../../organizations";
import { ErrorPanel } from "../../support";
import { TeamRoster } from "../components/TeamRoster";
import { teamDirectoryKey } from "./TeamsPage";

/**
 * PG-049 team detail (Phase C8 M1): roster from the projection, add/remove through the existing AddTeamMember and
 * RemoveTeamMember Authority commands (organization owner or Security Administrator, who is never offered themself: G25),
 * and the display-only rename of D8-8. A committed-but-not-
 * effective command is shown as pending; the roster is re-read from the server rather than updated optimistically.
 */
export function TeamDetailPage() {
  const { teamId } = useParams({ strict: false }) as { teamId: string };
  const api = useApi();
  const session = useSession();
  const queryClient = useQueryClient();
  const allowed = session.hasCapability("organization.manage") || session.hasCapability("security.administer");
  const key = ["organization", "teams", teamId];
  const team = useQuery({ queryKey: key, queryFn: ({ signal }) => api.teams.get(teamId, signal).then((r) => r.result), enabled: allowed });
  const [adding, setAdding] = useState(false);
  const [renaming, setRenaming] = useState(false);
  const [removing, setRemoving] = useState<TeamMemberResponse | null>(null);
  const [userId, setUserId] = useState<string | undefined>(undefined);
  const [name, setName] = useState("");
  const [pending, setPending] = useState<string | null>(null);
  const close = () => {
    setAdding(false);
    setRenaming(false);
    setRemoving(null);
    setUserId(undefined);
  };
  const refresh = async () => {
    await queryClient.invalidateQueries({ queryKey: key });
    await queryClient.invalidateQueries({ queryKey: teamDirectoryKey });
    await queryClient.invalidateQueries({ queryKey: ["organization", "members"] });
  };
  const settle = (status: number, effective: boolean | undefined, what: string) => {
    setPending(status === 202 || !effective ? `The Authorization Authority committed the ${what} but has not made it effective yet. The roster shows it once the Authority reports it effective; refresh to check.` : null);
  };
  const add = useMutation({
    mutationFn: () => api.teams.addMember(teamId, { userId: userId! }),
    onSuccess: async (response) => {
      close();
      settle(response.status, response.result?.command?.effective, "membership");
      await refresh();
    },
  });
  const remove = useMutation({
    mutationFn: (member: TeamMemberResponse) => api.teams.removeMember(teamId, member.userId),
    onSuccess: async (response) => {
      close();
      settle(response.status, response.result?.command?.effective, "removal");
      await refresh();
    },
  });
  const rename = useMutation({
    mutationFn: () => api.teams.rename(teamId, { teamName: name.trim(), expectedModifiedOn: team.data!.modifiedOn }).then((r) => r.result),
    onSuccess: async () => {
      close();
      await refresh();
    },
  });

  const title = team.data?.teamName ?? "Team";
  return (
    <OrganizationLayout
      title={title}
      lead={
        <Link to="/app/organization/teams" className="underline underline-offset-4">
          All teams
        </Link>
      }
      actions={
        <div className="flex flex-wrap gap-2">
          <Button size="sm" variant="ghost" icon={ActionIcons.refresh} busy={team.isFetching} aria-label="Refresh team" onClick={() => void team.refetch()}>
            Refresh
          </Button>
          {team.data?.canManage ? (
            <>
              <Button
                size="sm"
                variant="secondary"
                icon={ActionIcons.edit}
                onClick={() => {
                  setName(team.data?.teamName ?? "");
                  setRenaming(true);
                }}
              >
                Rename
              </Button>
              <Button size="sm" icon={ActionIcons.add} onClick={() => setAdding(true)} disabled={(team.data?.availableMembers.length ?? 0) === 0}>
                Add member
              </Button>
            </>
          ) : null}
        </div>
      }
    >
      {team.isPending ? <LoadingBlock label="Loading the team" /> : null}
      {team.isError ? <ErrorPanel error={team.error} onRetry={() => void team.refetch()} /> : null}
      {pending ? (
        <StatusMessage tone="warning" title="Pending">
          {pending}
        </StatusMessage>
      ) : null}
      {team.data ? (
        <div className="flex flex-col gap-4">
          <Card title="Team">
            <div className="flex flex-wrap gap-2">{team.data.isActive ? <Badge tone="success">Active</Badge> : <Badge tone="neutral">Inactive</Badge>}</div>
            <DescriptionList
              items={[
                { term: "Members", detail: String(team.data.members.length) },
                { term: "Created", detail: new Date(team.data.createdOn).toLocaleDateString() },
                { term: "Source", detail: team.data.source },
              ]}
            />
            <p className="m-0 text-sm text-muted">Members of a team-owned record's owning team hold the record's owner permissions. Adding someone here is an Authority security command; it never grants form design rights.</p>
          </Card>
          <Card title={`Members (${team.data.members.length})`}>
            <TeamRoster members={team.data.members} canManage={team.data.canManage} onRemove={(member) => setRemoving(member)} removingUserId={remove.isPending ? remove.variables?.userId : null} />
            {!team.data.canManage ? <p className="m-0 text-sm text-muted">Only an organization owner or Security Administrator can add or remove members.</p> : null}
          </Card>
        </div>
      ) : null}

      <Dialog
        open={adding}
        onOpenChange={(open) => (open ? undefined : close())}
        title="Add a member"
        description="Choose a person seen in this organization. The membership is a recorded Authorization Authority command and takes effect once the Authority reports it effective."
        locked={add.isPending}
        footer={
          <>
            <Button variant="secondary" onClick={close} disabled={add.isPending}>
              Cancel
            </Button>
            <Button busy={add.isPending} disabled={!userId} onClick={() => add.mutate()}>
              Add member
            </Button>
          </>
        }
      >
        <Select label="Person" value={userId} placeholder="Choose a person" options={(team.data?.availableMembers ?? []).map((u) => ({ value: u.userId, label: u.displayName }))} onValueChange={setUserId} />
        {add.isError ? <ErrorPanel error={add.error} /> : null}
      </Dialog>

      <Dialog
        open={removing !== null}
        onOpenChange={(open) => (open ? undefined : close())}
        title={`Remove ${removing?.displayName ?? ""} from ${title}`}
        description="They lose every permission this team conveys on their next request. Their own grants and ownership are unchanged."
        locked={remove.isPending}
        footer={
          <>
            <Button variant="secondary" onClick={close} disabled={remove.isPending}>
              Keep member
            </Button>
            <Button variant="danger" busy={remove.isPending} onClick={() => removing && remove.mutate(removing)}>
              Remove member
            </Button>
          </>
        }
      >
        {remove.isError ? <ErrorPanel error={remove.error} /> : null}
      </Dialog>

      <Dialog
        open={renaming}
        onOpenChange={(open) => (open ? undefined : close())}
        title="Rename team"
        description="Changes the team's display name only. Membership and permissions in the Authorization Authority are unchanged. The change is audited."
        locked={rename.isPending}
        footer={
          <>
            <Button variant="secondary" onClick={close} disabled={rename.isPending}>
              Cancel
            </Button>
            <Button busy={rename.isPending} disabled={name.trim().length === 0 || name.trim() === team.data?.teamName} onClick={() => rename.mutate()}>
              Save name
            </Button>
          </>
        }
      >
        <TextInput label="Team name" value={name} required maxLength={200} onChange={(event) => setName(event.target.value)} />
        {rename.isError ? <ErrorPanel error={rename.error} /> : null}
      </Dialog>
    </OrganizationLayout>
  );
}

import type { TeamMemberResponse } from "@enterpriseforms/api-client";
import { useApi, useSession } from "@enterpriseforms/app-runtime";
import { ActionIcons, Badge, Button, Card, DescriptionList, Dialog, LoadingBlock, Select, StatusMessage, TextInput } from "@enterpriseforms/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Link, useParams } from "@tanstack/react-router";
import { useEffect, useRef, useState } from "react";
import { OrganizationLayout } from "../../organizations";
import { ErrorPanel } from "../../support";
import { classifyError, classifyResponse, loadPending, MAXIMUM_AUTOMATIC_ATTEMPTS, nextDelayMs, savePending, type MembershipOutcome, type PendingMembershipOperation } from "../components/membershipOutcome";
import { TeamRoster } from "../components/TeamRoster";
import { teamDirectoryKey } from "./TeamsPage";

/**
 * PG-049 team detail (Phase C8 M1): roster from the projection, add/remove through the existing AddTeamMember and
 * RemoveTeamMember Authority commands (organization owner or Security Administrator, who is never offered themself: G25),
 * and the display-only rename of D8-8. A committed-but-not-effective command is shown as Pending and is re-checked
 * automatically with its ORIGINAL idempotency key (C8-M1-01): that addresses the one committed Authority command, never a
 * new one, and the business projection (the roster) is written only once the Authority reports it effective. Pending
 * operations survive a reload of this tab (session storage: team, person, direction and key; nothing secret). The roster
 * is always re-read from the server; it is never updated optimistically.
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
  const [operations, setOperations] = useState<PendingMembershipOperation[]>(() => loadPending(teamId));
  const [notices, setNotices] = useState<{ id: string; outcome: MembershipOutcome; text: string }[]>([]);
  const [exhausted, setExhausted] = useState(false);
  const checking = useRef(false);
  const operationsRef = useRef(operations);
  operationsRef.current = operations;
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
  const describe = (op: PendingMembershipOperation) => `${op.operation === "add" ? "adding" : "removing"} ${op.displayName} ${op.operation === "add" ? "to" : "from"} the team`;
  const finish = async (op: PendingMembershipOperation, outcome: MembershipOutcome) => {
    if (outcome.kind === "pending") {
      return;
    }
    setOperations((current) => {
      const next = current.filter((o) => o.idempotencyKey !== op.idempotencyKey);
      savePending(teamId, next);
      return next;
    });
    const text =
      outcome.kind === "effective"
        ? `The Authorization Authority confirmed ${describe(op)}; the roster below is read from the server.`
        : `${outcome.detail} (${describe(op)}). Nothing was changed.`;
    setNotices((current) => [{ id: op.idempotencyKey, outcome, text }, ...current.filter((n) => n.id !== op.idempotencyKey)].slice(0, 3));
    await refresh();
  };
  /** Sends (or re-sends) the command with the operation's own idempotency key: the Authority returns the one committed command. */
  const send = async (op: PendingMembershipOperation): Promise<MembershipOutcome> => {
    try {
      const response = op.operation === "add" ? await api.teams.addMember(teamId, { userId: op.userId }, op.idempotencyKey) : await api.teams.removeMember(teamId, op.userId, op.idempotencyKey);
      return classifyResponse(response);
    } catch (error) {
      return classifyError(error);
    }
  };
  const recheck = async (op: PendingMembershipOperation) => {
    if (checking.current) {
      return;
    }
    checking.current = true;
    try {
      await finish(op, await send(op));
    } finally {
      checking.current = false;
    }
  };
  const start = async (operation: "add" | "remove", userIdentifier: string, displayName: string) => {
    const op: PendingMembershipOperation = { teamId, userId: userIdentifier, displayName, operation, idempotencyKey: crypto.randomUUID(), startedOn: Date.now() };
    // Recorded before the request so a lost response or a reload still re-checks the same command (never a second one).
    const next = [...operations, op];
    setOperations(next);
    savePending(teamId, next);
    setExhausted(false);
    close();
    await finish(op, await send(op));
  };
  const add = useMutation({
    mutationFn: () => start("add", userId!, team.data?.availableMembers.find((u) => u.userId === userId)?.displayName ?? "the member"),
  });
  const remove = useMutation({
    mutationFn: (member: TeamMemberResponse) => start("remove", member.userId, member.displayName),
  });
  const waiting = operations.length;
  useEffect(() => {
    if (waiting === 0) {
      return undefined;
    }
    let cancelled = false;
    let timer: ReturnType<typeof setTimeout> | undefined;
    let attempt = 0;
    const tick = async () => {
      for (const op of [...operationsRef.current]) {
        if (cancelled) {
          return;
        }
        await recheck(op);
      }
      attempt += 1;
      if (cancelled) {
        return;
      }
      if (attempt >= MAXIMUM_AUTOMATIC_ATTEMPTS) {
        setExhausted(true);
        return;
      }
      timer = setTimeout(() => void tick(), nextDelayMs(attempt));
    };
    timer = setTimeout(() => void tick(), nextDelayMs(0));
    return () => {
      cancelled = true;
      if (timer) {
        clearTimeout(timer);
      }
    };
  }, [waiting, teamId]);
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
      {operations.length > 0 ? (
        <StatusMessage
          tone="warning"
          title="Pending"
          actions={
            exhausted ? (
              <Button size="sm" variant="secondary" onClick={() => operations.forEach((op) => void recheck(op))}>
                Check now
              </Button>
            ) : undefined
          }
        >
          <ul className="m-0 list-none p-0">
            {operations.map((op) => (
              <li key={op.idempotencyKey}>
                The Authorization Authority has not yet confirmed {describe(op)}. {exhausted ? "Automatic checking stopped; use Check now." : "This page checks again automatically."} The roster changes only after the Authority reports it effective.
              </li>
            ))}
          </ul>
        </StatusMessage>
      ) : null}
      {notices.map((n) => (
        <StatusMessage key={n.id} tone={n.outcome.kind === "effective" ? "success" : "danger"} title={n.outcome.kind === "effective" ? "Effective" : n.outcome.kind === "rejected" ? "Rejected" : "Failed"}>
          {n.text}
        </StatusMessage>
      ))}
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
                <></>
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

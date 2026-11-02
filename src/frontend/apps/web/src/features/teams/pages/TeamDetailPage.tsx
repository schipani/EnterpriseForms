import type { TeamMemberResponse, TeamMembershipOperationResponse } from "@enterpriseforms/api-client";
import { useApi, usePresence, useSession } from "@enterpriseforms/app-runtime";
import { ActionIcons, Badge, Button, Card, DescriptionList, Dialog, LoadingBlock, PresenceIndicator, Select, StatusMessage, TextInput } from "@enterpriseforms/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Link, useParams } from "@tanstack/react-router";
import { useEffect, useRef, useState } from "react";
import { OrganizationLayout } from "../../organizations";
import { ErrorPanel } from "../../support";
import { classifyError, classifyOperation, classifyReadError, classifyResponse, loadPending, MAXIMUM_AUTOMATIC_ATTEMPTS, nextDelayMs, savePending, type MembershipOutcome, type PendingMembershipOperation } from "../components/membershipOutcome";
import { TeamRoster } from "../components/TeamRoster";
import { teamDirectoryKey } from "./TeamsPage";

/**
 * PG-049 team detail (Phase C8): roster from the projection, add/remove through the existing AddTeamMember and
 * RemoveTeamMember Authority commands (organization owner or Security Administrator, who is never offered themself: G25),
 * and the display-only rename of D8-8. Each change is sent ONCE. The server records it as a durable operation before
 * submitting it, and its Worker projects the roster from the Authority's confirmed state — also after this page or the
 * browser is closed (C8-M1-01, Design R). This page only reads the operation's status (R-4): it never re-sends a command.
 * The roster is always re-read from the server; it is never updated optimistically. The team's open operations come from
 * the server, so every administrator sees them.
 */
export function TeamDetailPage() {
  const { teamId } = useParams({ strict: false }) as { teamId: string };
  const api = useApi();
  const session = useSession();
  const queryClient = useQueryClient();
  const allowed = session.hasCapability("organization.manage") || session.hasCapability("security.administer");
  const key = ["organization", "teams", teamId];
  const team = useQuery({
    queryKey: key,
    queryFn: ({ signal }) => api.teams.get(teamId, signal).then((r) => r.result),
    enabled: allowed,
    // While the server reports an unfinished operation, re-read the team so every administrator sees Pending turn into the result.
    refetchInterval: (query) => (query.state.data?.membershipOperations.some((o) => !o.completed) ? 3_000 : false),
  });
  const presence = usePresence([...(team.data?.members ?? []).map((m) => m.userId), ...(team.data?.availableMembers ?? []).map((u) => u.userId)], allowed && !!team.data);
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
  const describe = (op: Pick<PendingMembershipOperation, "operation" | "displayName">) => `${op.operation === "add" ? "adding" : "removing"} ${op.displayName} ${op.operation === "add" ? "to" : "from"} the team`;
  const store = (next: PendingMembershipOperation[]) => {
    setOperations(next);
    savePending(teamId, next);
  };
  const finish = async (op: PendingMembershipOperation, outcome: MembershipOutcome) => {
    if (outcome.kind === "pending") {
      return;
    }
    store(operationsRef.current.filter((o) => o.idempotencyKey !== op.idempotencyKey));
    const text =
      outcome.kind === "effective"
        ? `The Authorization Authority confirmed ${describe(op)}; the roster below is read from the server.`
        : outcome.kind === "attention"
          ? `${outcome.detail} (${describe(op)}). An administrator can check it again below.`
          : `${outcome.detail} (${describe(op)}). Nothing was changed.`;
    setNotices((current) => [{ id: op.idempotencyKey, outcome, text }, ...current.filter((n) => n.id !== op.idempotencyKey)].slice(0, 3));
    await refresh();
  };
  const remember = (op: PendingMembershipOperation, operation?: TeamMembershipOperationResponse) => {
    if (operation && op.operationId !== operation.operationId) {
      store(operationsRef.current.map((o) => (o.idempotencyKey === op.idempotencyKey ? { ...o, operationId: operation.operationId } : o)));
    }
  };
  /** Sends the change exactly once. */
  const send = async (op: PendingMembershipOperation): Promise<MembershipOutcome> => {
    try {
      const response = op.operation === "add" ? await api.teams.addMember(teamId, { userId: op.userId }, op.idempotencyKey) : await api.teams.removeMember(teamId, op.userId, op.idempotencyKey);
      remember(op, response.result?.operation);
      return classifyResponse(response);
    } catch (error) {
      return classifyError(error);
    }
  };
  /** Reads the server operation (by id, or by this browser's own key after a lost response). Never re-sends. */
  const read = async (op: PendingMembershipOperation): Promise<MembershipOutcome> => {
    const current = operationsRef.current.find((o) => o.idempotencyKey === op.idempotencyKey) ?? op;
    try {
      const response = current.operationId ? await api.teams.membershipOperation(teamId, current.operationId) : await api.teams.membershipOperationByKey(teamId, current.idempotencyKey);
      remember(current, response.result);
      return classifyOperation(response.result);
    } catch (error) {
      return classifyReadError(error, !current.operationId);
    }
  };
  const recheckAll = async () => {
    if (checking.current) {
      return;
    }
    checking.current = true;
    try {
      for (const op of [...operationsRef.current]) {
        await finish(op, await read(op));
      }
    } finally {
      checking.current = false;
    }
  };
  const start = async (operation: "add" | "remove", userIdentifier: string, displayName: string) => {
    const op: PendingMembershipOperation = { teamId, userId: userIdentifier, displayName, operation, idempotencyKey: crypto.randomUUID(), startedOn: Date.now() };
    // Recorded before the request so a lost response or a reload can still find the same operation on the server.
    store([...operationsRef.current, op]);
    setExhausted(false);
    close();
    await finish(op, await send(op));
    await refresh();
  };
  const add = useMutation({
    mutationFn: () => start("add", userId!, team.data?.availableMembers.find((u) => u.userId === userId)?.displayName ?? "the member"),
  });
  const remove = useMutation({
    mutationFn: (member: TeamMemberResponse) => start("remove", member.userId, member.displayName),
  });
  const recheck = useMutation({
    mutationFn: (operationId: string) => api.teams.recheckMembershipOperation(teamId, operationId).then((r) => r.result),
    onSuccess: refresh,
  });
  const review = useMutation({
    mutationFn: (operationId: string) => api.teams.reviewMembershipOperation(teamId, operationId).then((r) => r.result),
    onSuccess: refresh,
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
      await recheckAll();
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

  const localIds = new Set(operations.map((o) => o.operationId).filter(Boolean));
  const serverOpen = (team.data?.membershipOperations ?? []).filter((o) => !o.completed && o.operationState !== "ReconciliationError" && !localIds.has(o.operationId));
  const attention = (team.data?.membershipOperations ?? []).filter((o) => o.canMarkReviewed);
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
      {operations.length > 0 || serverOpen.length > 0 ? (
        <StatusMessage
          tone="warning"
          title="Pending"
          actions={
            exhausted ? (
              <Button size="sm" variant="secondary" onClick={() => void recheckAll()}>
                Check now
              </Button>
            ) : undefined
          }
        >
          <ul className="m-0 list-none p-0">
            {operations.map((op) => (
              <li key={op.idempotencyKey}>
                The Authorization Authority has not yet confirmed {describe(op)}. {exhausted ? "Automatic status checks stopped; use Check now." : "This page checks the status automatically."} The roster changes only after the Authority reports it effective, and the server completes the change even if you close this page.
              </li>
            ))}
            {serverOpen.map((op) => (
              <li key={op.operationId}>
                The Authorization Authority has not yet confirmed {describe({ operation: op.direction === "add" ? "add" : "remove", displayName: op.memberDisplayName })}
                {op.needsAttention ? " (taking longer than expected)" : ""}. The server completes it without anyone repeating it.
              </li>
            ))}
          </ul>
        </StatusMessage>
      ) : null}
      {notices.map((n) => (
        <StatusMessage key={n.id} tone={n.outcome.kind === "effective" ? "success" : "danger"} title={n.outcome.kind === "effective" ? "Effective" : n.outcome.kind === "rejected" ? "Rejected" : n.outcome.kind === "attention" ? "Needs attention" : "Failed"}>
          {n.text}
        </StatusMessage>
      ))}
      {team.data?.canManage && attention.length > 0 ? (
        <StatusMessage tone="danger" title="Needs attention">
          <ul className="m-0 flex list-none flex-col gap-2 p-0">
            {attention.map((op) => (
              <li key={op.operationId} className="flex flex-wrap items-center gap-2">
                <span>
                  {describe({ operation: op.direction === "add" ? "add" : "remove", displayName: op.memberDisplayName })}: {attentionText(op)}
                </span>
                {op.canRecheck ? (
                  <Button size="sm" variant="secondary" busy={recheck.isPending && recheck.variables === op.operationId} onClick={() => recheck.mutate(op.operationId)}>
                    Check again
                  </Button>
                ) : null}
                <Button size="sm" variant="ghost" busy={review.isPending && review.variables === op.operationId} onClick={() => review.mutate(op.operationId)}>
                  Mark reviewed
                </Button>
              </li>
            ))}
          </ul>
          {recheck.isError ? <ErrorPanel error={recheck.error} /> : null}
          {review.isError ? <ErrorPanel error={review.error} /> : null}
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
            <TeamRoster presence={presence} members={team.data.members} canManage={team.data.canManage} onRemove={(member) => setRemoving(member)} removingUserId={remove.isPending ? remove.variables?.userId : null} />
            <p className="m-0 text-sm text-muted">This roster is the organization's display copy. Access is always decided by the Authorization Authority, and a change made elsewhere can take a moment to appear here.</p>
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
        <Select label="Person" value={userId} placeholder="Choose a person" options={(team.data?.availableMembers ?? []).map((u) => ({ value: u.userId, label: u.displayName, trailing: <PresenceIndicator state={presence[u.userId] ?? "Unknown"} /> }))} onValueChange={setUserId} />
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

function attentionText(op: TeamMembershipOperationResponse): string {
  if (op.lateCommitDetected) {
    return "the Authority accepted it after it was declared not received; the roster may differ from the Authority until the next change of this person.";
  }
  switch (op.operationState) {
    case "AuthorityFailed":
      return "the Authority could not complete it; an operator must investigate.";
    case "NotCommitted":
      return "the Authority never received it; it is not sent again.";
    default:
      return `the server could not confirm it (${op.errorCode ?? "check failed"}); the roster was not changed.`;
  }
}

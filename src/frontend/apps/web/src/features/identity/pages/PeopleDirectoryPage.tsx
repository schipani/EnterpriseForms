import type { OrganizationMemberQuery, OrganizationMemberResponse } from "@enterpriseforms/api-client";
import { presentError, useApi, useSession } from "@enterpriseforms/app-runtime";
import { ActionIcons, Button, Card, LoadingBlock, Pager, Select, StatusMessage, TextInput, useDebouncedValue } from "@enterpriseforms/ui";
import { useQuery } from "@tanstack/react-query";
import { useEffect, useState } from "react";
import { OrganizationLayout } from "../../organizations";
import { ErrorPanel } from "../../support";
import { MemberTable, type MemberAction } from "../components/MemberTable";
import { SecurityAdministratorChangeDialog } from "../components/SecurityAdministratorChangeDialog";

export const membersKey = ["organization", "members"];

/** People are listed 15 per page (server-side), like every other list. */
export const PEOPLE_PAGE_SIZE = 15;

const RESTART_CODES = new Set(["CURSOR_INVALID", "CURSOR_SCOPE_MISMATCH", "LIST_CHANGED", "AUTHORIZATION_STATE_CHANGED"]);

const filterGrid = "grid items-start gap-x-3 gap-y-2 sm:grid-cols-2";

/**
 * PG-050 people directory (C8 M2, within the existing Authority). Server-paged and searchable. Security Administrators
 * see every enabled Authority identity binding, including people who have never signed in; organization owners see people
 * who have used the organization and the Security Administrators. Security Administrator appointment and removal use the
 * existing Authority commands. Adding people, owner/designer roles and disabling membership need the AC-7 Authority
 * commands, which await approval; the page says so instead of offering controls that cannot work.
 */
export function PeopleDirectoryPage() {
  const api = useApi();
  const session = useSession();
  const allowed = session.hasCapability("organization.manage") || session.hasCapability("security.administer");
  const [text, setText] = useState("");
  const [role, setRole] = useState("all");
  const [signIn, setSignIn] = useState("all");
  const [cursor, setCursor] = useState<string | undefined>(undefined);
  const [change, setChange] = useState<{
    member: OrganizationMemberResponse;
    action: MemberAction;
  } | null>(null);
  const [done, setDone] = useState<string | null>(null);
  const settled = useDebouncedValue(text);
  const query: OrganizationMemberQuery = {
    pageSize: PEOPLE_PAGE_SIZE,
    ...(settled.trim() ? { q: settled.trim() } : {}),
    ...(role !== "all" ? { role: role as NonNullable<OrganizationMemberQuery["role"]> } : {}),
    ...(signIn !== "all"
      ? {
          signInStatus: signIn as NonNullable<OrganizationMemberQuery["signInStatus"]>,
        }
      : {}),
  };
  const directory = useQuery({
    queryKey: [...membersKey, query, cursor],
    queryFn: ({ signal }) => api.organization.members({ ...query, ...(cursor ? { continuationToken: cursor } : {}) }, signal).then((r) => r.result),
    enabled: allowed,
    placeholderData: (previous) => previous,
  });
  const errorCode = directory.error ? presentError(directory.error).code : null;
  useEffect(() => {
    if (errorCode && RESTART_CODES.has(errorCode) && cursor) {
      setCursor(undefined);
    }
  }, [errorCode, cursor]);
  const restart = (apply: () => void) => {
    apply();
    setCursor(undefined);
  };
  const data = directory.data;
  const filtered = text !== "" || role !== "all" || signIn !== "all";

  return (
    <OrganizationLayout
      title="People"
      lead="Everyone in this organization, as the Authorization Authority and this application know them."
      actions={
        <>
          <Button size="sm" variant="ghost" icon={ActionIcons.refresh} busy={directory.isFetching} aria-label="Refresh People" onClick={() => void directory.refetch()}>
            Refresh
          </Button>
          <Button size="sm" icon={ActionIcons.add} disabled aria-describedby="people-pending-capabilities">
            Add user
          </Button>
        </>
      }
    >
      {done !== null ? (
        <StatusMessage
          tone="success"
          title="Change recorded"
          actions={
            <Button size="sm" variant="ghost" onClick={() => setDone(null)}>
              Dismiss
            </Button>
          }
        >
          {done}
        </StatusMessage>
      ) : null}
      <div id="people-pending-capabilities">
        <StatusMessage tone="info" title="Adding people and changing owner or designer roles are not available yet">
          Adding a user, granting or removing the organization owner and designer roles, and disabling or re-enabling membership need new Authorization Authority commands (AC-7) that are awaiting
          approval. Until then, new members are added through the operator approval process, and Security Administrators are managed here or on the Security administrators page.
        </StatusMessage>
      </div>
      {directory.isPending && allowed ? <LoadingBlock label="Loading people" /> : null}
      {directory.isError ? <ErrorPanel error={directory.error} onRetry={() => void directory.refetch()} /> : null}
      {data ? (
        <Card title={`People (${Number(data.totalCount)})`}>
          <p className="m-0 text-sm text-muted">{data.coverageStatement}</p>
          {!data.rolesAvailable ? (
            <StatusMessage tone="warning" title="Roles unavailable">
              {data.rolesUnavailableReason}
            </StatusMessage>
          ) : null}
          <TextInput label="Search people" placeholder="Name, identity or team" value={text} autoComplete="off" onChange={(event) => restart(() => setText(event.target.value))} />
          <div className={filterGrid}>
            <Select
              label="Role"
              value={role}
              options={[
                { value: "all", label: "All roles" },
                {
                  value: "SecurityAdministrator",
                  label: "Security Administrator",
                },
                { value: "None", label: "No role shown" },
              ]}
              onValueChange={(value) => restart(() => setRole(value))}
            />
            <Select
              label="Sign-in"
              value={signIn}
              options={[
                { value: "all", label: "Any sign-in state" },
                { value: "SignedIn", label: "Signed in" },
                { value: "NotRecorded", label: "Sign-in not recorded" },
                { value: "NeverSignedIn", label: "Never signed in" },
              ]}
              onValueChange={(value) => restart(() => setSignIn(value))}
            />
          </div>
          {filtered ? (
            <div>
              <Button size="sm" variant="ghost" onClick={() => restart(() => (setText(""), setRole("all"), setSignIn("all")))}>
                Clear filters
              </Button>
            </div>
          ) : null}
          <MemberTable members={[...data.members]} rolesAvailable={data.rolesAvailable} onAction={(member, action) => setChange({ member, action })} />
          {Number(data.totalCount) > 0 ? (
            <Pager
              label="People pages"
              noun="people"
              pageStart={Number(data.pageStart)}
              itemCount={data.members.length}
              totalCount={Number(data.totalCount)}
              pageSize={Number(data.pageSize)}
              hasPrevious={!!data.previousToken}
              hasNext={!!data.nextToken}
              busy={directory.isFetching}
              onPrevious={() => setCursor(data.previousToken ?? undefined)}
              onNext={() => setCursor(data.nextToken ?? undefined)}
            />
          ) : null}
          <p className="m-0 text-xs text-muted">Source: {data.source}. Last sign-in is recorded from this release on.</p>
        </Card>
      ) : null}
      <SecurityAdministratorChangeDialog
        change={change}
        operational={data?.securityAdministratorChangesOperational ?? false}
        unavailableReason={data?.securityAdministratorChangesUnavailableReason}
        onClose={() => setChange(null)}
        onDone={setDone}
      />
    </OrganizationLayout>
  );
}

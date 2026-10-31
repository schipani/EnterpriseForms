import type { OrganizationMemberQuery } from "@enterpriseforms/api-client";
import { presentError, useApi, usePresence, useSession } from "@enterpriseforms/app-runtime";
import { ActionIcons, Button, Card, LoadingBlock, Pager, Select, StatusMessage, TextInput, useDebouncedValue } from "@enterpriseforms/ui";
import { useQuery } from "@tanstack/react-query";
import { useEffect, useState } from "react";
import { OrganizationLayout } from "../../organizations";
import { ErrorPanel } from "../../support";
import type { OrganizationMemberResponse } from "@enterpriseforms/api-client";
import { AddUserDialog } from "../components/AddUserDialog";
import { ApprovalsPanel } from "../components/ApprovalsPanel";
import { EditProfileDialog } from "../components/EditProfileDialog";
import { MemberChangeDialog, type MemberChange } from "../components/MemberChangeDialog";
import { MemberTable } from "../components/MemberTable";

export const membersKey = ["organization", "members"];

/** People are listed 15 per page (server-side), like every other list. */
export const PEOPLE_PAGE_SIZE = 15;

const RESTART_CODES = new Set(["CURSOR_INVALID", "CURSOR_SCOPE_MISMATCH", "LIST_CHANGED", "AUTHORIZATION_STATE_CHANGED"]);

const filterGrid = "grid items-start gap-x-3 gap-y-2 sm:grid-cols-3";

/**
 * PG-050 people directory (C8 M2). The Authorization Authority's complete roster — every member, enabled or disabled,
 * with organization roles — searchable and paged on the server. Organization owners add people (a request two operators
 * must approve), make or remove designers, disable and re-enable members, and propose or approve owner changes (two
 * distinct owners). Security Administrators appoint and remove Security Administrators. The Authority decides every change;
 * the actions shown are its rules mirrored for this person.
 */
export function PeopleDirectoryPage() {
  const api = useApi();
  const session = useSession();
  const allowed = session.hasCapability("organization.manage") || session.hasCapability("security.administer");
  const [text, setText] = useState("");
  const [role, setRole] = useState("all");
  const [signIn, setSignIn] = useState("all");
  const [membership, setMembership] = useState("all");
  const [cursor, setCursor] = useState<string | undefined>(undefined);
  const [change, setChange] = useState<MemberChange | null>(null);
  const [adding, setAdding] = useState(false);
  const [editing, setEditing] = useState<OrganizationMemberResponse | null>(null);
  const [done, setDone] = useState<string | null>(null);
  const settled = useDebouncedValue(text);
  const query: OrganizationMemberQuery = {
    pageSize: PEOPLE_PAGE_SIZE,
    ...(settled.trim() ? { q: settled.trim() } : {}),
    ...(role !== "all" ? { role: role as NonNullable<OrganizationMemberQuery["role"]> } : {}),
    ...(signIn !== "all" ? { signInStatus: signIn as NonNullable<OrganizationMemberQuery["signInStatus"]> } : {}),
    ...(membership !== "all" ? { membershipState: membership as NonNullable<OrganizationMemberQuery["membershipState"]> } : {}),
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
  const presence = usePresence(data?.members.map((m) => m.userId) ?? [], allowed && !!data);
  const filtered = text !== "" || role !== "all" || signIn !== "all" || membership !== "all";
  const canAdd = !!data?.canManageMembers && !!data.organizationIdentityIssuer;
  const operational = data?.canManageMembers ? !!data.membershipCommandsOperational : !!data?.securityAdministratorChangesOperational;

  return (
    <OrganizationLayout
      title="People"
      lead="Everyone in this organization, as the Authorization Authority knows them."
      actions={
        <>
          <Button size="sm" variant="ghost" icon={ActionIcons.refresh} busy={directory.isFetching} aria-label="Refresh People" onClick={() => void directory.refetch()}>
            Refresh
          </Button>
          {data?.canManageMembers ? (
            <Button size="sm" icon={ActionIcons.add} disabled={!canAdd} onClick={() => setAdding(true)}>
              Add user
            </Button>
          ) : null}
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
      {data && data.canManageMembers && !data.membershipCommandsOperational ? (
        <StatusMessage tone="info" title="Changes are not available in this environment">
          Membership and role changes need a fresh passkey confirmation (step-up), which is not available yet (PB-08). The Authorization Authority refuses every change here.
        </StatusMessage>
      ) : null}
      {directory.isPending && allowed ? <LoadingBlock label="Loading people" /> : null}
      {directory.isError ? <ErrorPanel error={directory.error} onRetry={() => void directory.refetch()} /> : null}
      {data ? (
        <div className="flex flex-col gap-6">
          <ApprovalsPanel onApprove={(proposal) => setChange({ kind: "proposal", proposal })} />
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
                  { value: "OrganizationOwner", label: "Organization Owner" },
                  { value: "Designer", label: "Designer" },
                  { value: "SecurityAdministrator", label: "Security Administrator" },
                  { value: "None", label: "No role" },
                ]}
                onValueChange={(value) => restart(() => setRole(value))}
              />
              <Select
                label="Membership"
                value={membership}
                options={[
                  { value: "all", label: "Active and disabled" },
                  { value: "Enabled", label: "Active" },
                  { value: "Disabled", label: "Disabled" },
                ]}
                onValueChange={(value) => restart(() => setMembership(value))}
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
                <Button size="sm" variant="ghost" onClick={() => restart(() => (setText(""), setRole("all"), setSignIn("all"), setMembership("all")))}>
                  Clear filters
                </Button>
              </div>
            ) : null}
            <MemberTable
              members={[...data.members]}
              rolesAvailable={data.rolesAvailable}
              presence={presence}
              onAction={(member, action) => (action === "EditProfile" ? setEditing(member) : setChange({ kind: "member", member, action }))}
            />
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
            <p className="m-0 text-xs text-muted">
              Source: {data.source}. Presence is informational (it never affects access) and refreshes about once a minute. Being a member does not mean someone can sign in: real Microsoft Entra
              sign-in arrives with PB-08.
            </p>
          </Card>
        </div>
      ) : null}
      <MemberChangeDialog
        change={change}
        operational={operational}
        unavailableReason={data?.canManageMembers ? data.membershipCommandsUnavailableReason : data?.securityAdministratorChangesUnavailableReason}
        onClose={() => setChange(null)}
        onDone={setDone}
      />
      <EditProfileDialog member={editing} onClose={() => setEditing(null)} onDone={setDone} />
      <AddUserDialog
        open={adding}
        issuer={data?.organizationIdentityIssuer}
        operational={!!data?.membershipCommandsOperational}
        operatorApprovalOperational={!!data?.operatorApprovalOperational}
        onClose={() => setAdding(false)}
        onDone={setDone}
      />
    </OrganizationLayout>
  );
}

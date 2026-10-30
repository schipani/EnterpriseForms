import { useApi, useSession } from "@enterpriseforms/app-runtime";
import { ActionIcons, Button, Card, LoadingBlock, StatusMessage, TextInput } from "@enterpriseforms/ui";
import { useQuery } from "@tanstack/react-query";
import { useState } from "react";
import { OrganizationLayout } from "../../organizations";
import { ErrorPanel } from "../../support";
import { MemberTable } from "../components/MemberTable";

export const membersKey = ["organization", "members"];

/**
 * PG-050 people directory, read-only (Phase C8 M1). Rows are the business projection of identities the Authorization
 * Authority has bound and that have signed in; the source is stated on the page. Role badges come from the Authority's
 * Security Administrator roster. Enabling, disabling and owner/designer roles arrive with AC-7 (M2) and are not offered.
 * The filter runs on the rows already authorized for this caller; it never queries across organizations.
 */
export function PeopleDirectoryPage() {
  const api = useApi();
  const session = useSession();
  const allowed = session.hasCapability("organization.manage") || session.hasCapability("security.administer");
  const directory = useQuery({ queryKey: membersKey, queryFn: ({ signal }) => api.organization.members(signal).then((r) => r.result), enabled: allowed });
  const [filter, setFilter] = useState("");
  const needle = filter.trim().toLowerCase();
  const rows = (directory.data?.members ?? []).filter((m) => needle.length === 0 || m.displayName.toLowerCase().includes(needle) || m.identitySubject.toLowerCase().includes(needle) || m.teams.some((t) => t.teamName.toLowerCase().includes(needle)));

  return (
    <OrganizationLayout
      title="People"
      lead="Everyone the Authorization Authority has bound to this organization who has signed in. Read-only in this milestone: membership and organization roles are changed through Authority security commands that arrive with the next milestone."
      actions={
        <Button size="sm" variant="ghost" icon={ActionIcons.refresh} busy={directory.isFetching} aria-label="Refresh People" onClick={() => void directory.refetch()}>
          Refresh
        </Button>
      }
    >
      {directory.isPending ? <LoadingBlock label="Loading people" /> : null}
      {directory.isError ? <ErrorPanel error={directory.error} onRetry={() => void directory.refetch()} /> : null}
      {directory.data ? (
        <Card title={`People (${directory.data.members.length})`}>
          <p className="m-0 text-sm text-muted">Source: {directory.data.source}.</p>
          {!directory.data.rolesAvailable ? (
            <StatusMessage tone="warning" title="Roles unavailable">
              {directory.data.rolesUnavailableReason}
            </StatusMessage>
          ) : null}
          <TextInput label="Filter by name, identity or team" value={filter} onChange={(event) => setFilter(event.target.value)} />
          <MemberTable members={rows} rolesAvailable={directory.data.rolesAvailable} />
        </Card>
      ) : null}
    </OrganizationLayout>
  );
}

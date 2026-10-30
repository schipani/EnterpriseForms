import type { OrganizationMemberResponse } from "@enterpriseforms/api-client";
import { Badge, DataTable } from "@enterpriseforms/ui";

const ROLE_LABELS: Record<string, string> = { SecurityAdministrator: "Security Administrator" };

/** Read-only people table (PG-050, Phase C8 M1): names, non-secret identity binding, Authority role badges, teams. No actions until AC-7 (M2). */
export function MemberTable({ members, rolesAvailable }: { members: OrganizationMemberResponse[]; rolesAvailable: boolean }) {
  return (
    <DataTable
      caption="People"
      rowKey={(row) => row.userId}
      rows={members}
      emptyMessage="Nobody has signed in to this organization yet."
      columns={[
        { key: "name", header: "Name", isRowHeader: true, render: (row) => (row.isCurrentUser ? `${row.displayName} (you)` : row.displayName) },
        { key: "identity", header: "Verified identity", render: (row) => <span className="break-all text-sm">{`${row.identitySubject} · ${row.identityIssuer}`}</span> },
        {
          key: "roles",
          header: "Authority roles",
          render: (row) =>
            !rolesAvailable ? (
              <span className="text-muted">Unavailable</span>
            ) : row.authorityRoles.length === 0 ? (
              <span className="text-muted">None shown</span>
            ) : (
              <span className="flex flex-wrap gap-1">
                {row.authorityRoles.map((role) => (
                  <Badge key={role} tone="info">
                    {ROLE_LABELS[role] ?? role}
                  </Badge>
                ))}
              </span>
            ),
        },
        { key: "teams", header: "Teams", render: (row) => (row.teams.length === 0 ? <span className="text-muted">—</span> : row.teams.map((team) => team.teamName).join(", ")) },
        { key: "seen", header: "First seen", render: (row) => new Date(row.firstSeenOn).toLocaleDateString() },
      ]}
    />
  );
}

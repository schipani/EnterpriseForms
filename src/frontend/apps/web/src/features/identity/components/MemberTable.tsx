import type { OrganizationMemberResponse } from "@enterpriseforms/api-client";
import { Badge, Button, DataTable, RowActions } from "@enterpriseforms/ui";

const ROLE_LABELS: Record<string, string> = {
  SecurityAdministrator: "Security Administrator",
};

const SIGN_IN_LABELS: Record<string, { label: string; tone: "success" | "neutral" | "warning" }> = {
  SignedIn: { label: "Signed in", tone: "success" },
  NotRecorded: { label: "Sign-in not recorded", tone: "neutral" },
  NeverSignedIn: { label: "Never signed in", tone: "warning" },
};

export type MemberAction = "AppointSecurityAdministrator" | "RemoveSecurityAdministrator";

/** Name shown for a person; an Authority-bound identity that has never signed in has no name yet. */
export function memberName(member: OrganizationMemberResponse): string {
  return member.displayName || member.identitySubject;
}

/**
 * People table (PG-050, C8 M2): names, non-secret identity binding, Authority role badges, sign-in state, teams and last
 * sign-in. The Actions column appears only when some row offers an action the caller may request (Security Administrator
 * appointment or removal through the existing Authority commands); the Authority still decides every command.
 */
export function MemberTable({
  members,
  rolesAvailable,
  onAction,
}: {
  members: OrganizationMemberResponse[];
  rolesAvailable: boolean;
  onAction?: (member: OrganizationMemberResponse, action: MemberAction) => void;
}) {
  const showActions = onAction !== undefined && members.some((m) => (m.availableActions ?? []).length > 0);
  return (
    <DataTable
      caption="People"
      rowKey={(row) => row.userId}
      rows={members}
      stickyActions={showActions}
      emptyMessage="No people match."
      columns={[
        {
          key: "name",
          header: "Name",
          isRowHeader: true,
          render: (row) =>
            row.displayName ? (
              row.isCurrentUser ? (
                `${row.displayName} (you)`
              ) : (
                row.displayName
              )
            ) : (
              <span>
                <span className="text-muted">Name not known yet</span>
                <span className="sr-only"> ({row.identitySubject})</span>
              </span>
            ),
        },
        {
          key: "identity",
          header: "Verified identity",
          render: (row) => <span className="break-all text-sm">{`${row.identitySubject} · ${row.identityIssuer}`}</span>,
        },
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
        {
          key: "status",
          header: "Sign-in",
          render: (row) => {
            const status = SIGN_IN_LABELS[row.signInStatus ?? "NotRecorded"] ?? SIGN_IN_LABELS["NotRecorded"]!;
            return <Badge tone={status.tone}>{status.label}</Badge>;
          },
        },
        {
          key: "teams",
          header: "Teams",
          render: (row) => (row.teams.length === 0 ? <span className="text-muted">—</span> : row.teams.map((team) => team.teamName).join(", ")),
        },
        {
          key: "last",
          header: "Last sign-in",
          render: (row) => (row.lastSignedInOn ? <span className="whitespace-nowrap">{new Date(row.lastSignedInOn).toLocaleString()}</span> : <span className="text-muted">—</span>),
        },
        ...(showActions
          ? [
              {
                key: "actions",
                header: "Actions",
                isActions: true,
                render: (row: OrganizationMemberResponse) => {
                  const actions = row.availableActions ?? [];
                  return (
                    <RowActions
                      overflowLabel={`More actions for ${memberName(row)}`}
                      primary={
                        actions.includes("AppointSecurityAdministrator") ? (
                          <Button size="sm" variant="secondary" aria-label={`Make Security Administrator: ${memberName(row)}`} onClick={() => onAction?.(row, "AppointSecurityAdministrator")}>
                            Make Security Administrator
                          </Button>
                        ) : actions.includes("RemoveSecurityAdministrator") ? (
                          <Button size="sm" variant="ghost" aria-label={`Remove Security Administrator: ${memberName(row)}`} onClick={() => onAction?.(row, "RemoveSecurityAdministrator")}>
                            Remove Security Administrator
                          </Button>
                        ) : null
                      }
                    />
                  );
                },
              },
            ]
          : []),
      ]}
    />
  );
}

import type { OrganizationMemberResponse } from "@enterpriseforms/api-client";
import { Badge, Button, DataTable, RowActions } from "@enterpriseforms/ui";

const ROLE_LABELS: Record<string, string> = { OrganizationOwner: "Organization Owner", Designer: "Designer", SecurityAdministrator: "Security Administrator" };

const SIGN_IN_LABELS: Record<string, { label: string; tone: "success" | "neutral" | "warning" }> = {
  SignedIn: { label: "Signed in", tone: "success" },
  NotRecorded: { label: "Sign-in not recorded", tone: "neutral" },
  NeverSignedIn: { label: "Never signed in", tone: "warning" },
};

export type MemberAction =
  | "GrantDesigner"
  | "RevokeDesigner"
  | "ProposeOwnerGrant"
  | "ProposeOwnerRevoke"
  | "DisableMembership"
  | "EnableMembership"
  | "ProposeOwnerDisable"
  | "ProposeOwnerEnable"
  | "AppointSecurityAdministrator"
  | "RemoveSecurityAdministrator";

/** Visible labels of the row actions; accessible names start with the same text (WCAG 2.5.3). */
export const MEMBER_ACTION_LABELS: Record<MemberAction, string> = {
  GrantDesigner: "Make designer",
  RevokeDesigner: "Remove designer role",
  ProposeOwnerGrant: "Propose as owner",
  ProposeOwnerRevoke: "Propose owner removal",
  DisableMembership: "Disable membership",
  EnableMembership: "Re-enable membership",
  ProposeOwnerDisable: "Propose disabling",
  ProposeOwnerEnable: "Propose re-enabling",
  AppointSecurityAdministrator: "Make Security Administrator",
  RemoveSecurityAdministrator: "Remove Security Administrator",
};

const isMemberAction = (value: string): value is MemberAction => value in MEMBER_ACTION_LABELS;

/** Name shown for a person; an identity that has never signed in shows the requested display name or its subject. */
export function memberName(member: OrganizationMemberResponse): string {
  return member.displayName || member.identitySubject;
}

/**
 * People table (PG-050, C8 M2): names, verified identity, Authority roles, membership state, sign-in state, teams and last
 * sign-in. Row actions are the server's hints for this caller (the Authority decides every command): the first is shown,
 * the rest are in the row's "…" menu. Without an action handler the table is read-only.
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
  const actionsOf = (m: OrganizationMemberResponse) => (onAction ? (m.availableActions ?? []).filter(isMemberAction) : []);
  const showActions = members.some((m) => actionsOf(m).length > 0);
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
        { key: "identity", header: "Verified identity", render: (row) => <span className="break-all text-sm">{`${row.identitySubject} · ${row.identityIssuer}`}</span> },
        {
          key: "roles",
          header: "Authority roles",
          render: (row) =>
            !rolesAvailable ? (
              <span className="text-muted">Unavailable</span>
            ) : row.authorityRoles.length === 0 ? (
              <span className="text-muted">None</span>
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
          key: "membership",
          header: "Membership",
          render: (row) =>
            row.membershipState === "Disabled" ? (
              <Badge tone="danger">Disabled</Badge>
            ) : row.membershipState === "Enabled" ? (
              <Badge tone="success">Active</Badge>
            ) : (
              <span className="text-muted">Unknown</span>
            ),
        },
        {
          key: "status",
          header: "Sign-in",
          render: (row) => {
            const status = SIGN_IN_LABELS[row.signInStatus] ?? SIGN_IN_LABELS["NotRecorded"]!;
            return <Badge tone={status.tone}>{status.label}</Badge>;
          },
        },
        { key: "teams", header: "Teams", render: (row) => (row.teams.length === 0 ? <span className="text-muted">—</span> : row.teams.map((team) => team.teamName).join(", ")) },
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
                  const [first, ...rest] = actionsOf(row);
                  return (
                    <RowActions
                      overflowLabel={`More actions for ${memberName(row)}`}
                      primary={
                        first ? (
                          <Button size="sm" variant="secondary" aria-label={`${MEMBER_ACTION_LABELS[first]}: ${memberName(row)}`} onClick={() => onAction?.(row, first)}>
                            {MEMBER_ACTION_LABELS[first]}
                          </Button>
                        ) : null
                      }
                      overflow={rest.map((action) => ({
                        key: action,
                        content: (
                          <button type="button" aria-label={`${MEMBER_ACTION_LABELS[action]}: ${memberName(row)}`} onClick={() => onAction?.(row, action)}>
                            {MEMBER_ACTION_LABELS[action]}
                          </button>
                        ),
                      }))}
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

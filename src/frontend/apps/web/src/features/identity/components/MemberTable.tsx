import type { OrganizationMemberResponse } from "@enterpriseforms/api-client";
import { Badge, Button, OverflowMenu } from "@enterpriseforms/ui";

const ROLE_LABELS: Record<string, string> = { OrganizationOwner: "Owner", Designer: "Designer", SecurityAdministrator: "Security administrator" };

const SIGN_IN_LABELS: Record<string, { label: string; tone: "success" | "neutral" | "warning" }> = {
  SignedIn: { label: "Signed in", tone: "success" },
  NotRecorded: { label: "Not recorded", tone: "neutral" },
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

/** Concise provider name for a verified identity's issuer; the full issuer stays in the tooltip. */
export function providerName(issuer: string): string {
  try {
    const host = new URL(issuer).hostname;
    if (host === "login.microsoftonline.com" || host.endsWith(".microsoftonline.com") || host === "sts.windows.net") {
      return "Microsoft Entra ID";
    }
    return host.includes("test-idp") ? "Test identity provider" : host;
  } catch {
    return issuer;
  }
}

/** Desktop column widths (container query); below that each person is a compact card with labelled fields. */
const GRID_WITH_ACTIONS = "@4xl:grid-cols-[minmax(0,2.6fr)_minmax(0,2fr)_5.5rem_8rem_minmax(0,1fr)_7rem_6.5rem]";
const GRID_READ_ONLY = "@4xl:grid-cols-[minmax(0,2.6fr)_minmax(0,2fr)_5.5rem_8rem_minmax(0,1fr)_7rem]";
const CELL = "min-w-0 before:mb-0.5 before:block before:text-help before:text-muted before:content-[attr(data-label)] @4xl:before:hidden";

/**
 * People directory (PG-050, C8 M2): one Person cell (name, then the verified identity as a short muted line whose full value is
 * the tooltip), Authority roles, membership state, sign-in state, teams, last sign-in and one Actions menu. The actions are the
 * server's hints for this caller (the Authority decides every command); without an action handler the table is read-only. On
 * narrow containers the rows become cards. The identity line appears only when the server returned it.
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
  const grid = showActions ? GRID_WITH_ACTIONS : GRID_READ_ONLY;
  const headers = ["Person", "Roles", "Membership", "Sign-in", "Teams", "Last sign-in", ...(showActions ? ["Actions"] : [])];
  return (
    <div className="@container w-full">
      <div role="table" aria-label="People" className="w-full overflow-hidden text-sm @4xl:rounded-control @4xl:border @4xl:border-line">
        <div role="rowgroup" className="sr-only @4xl:not-sr-only">
          <div role="row" className={`grid gap-3 border-b border-line bg-page px-3 py-2 text-label font-semibold ${grid}`}>
            {headers.map((header) => (
              <div key={header} role="columnheader" className={header === "Actions" ? "text-right" : ""}>
                {header}
              </div>
            ))}
          </div>
        </div>
        <div role="rowgroup" className="flex flex-col gap-3 @4xl:gap-0">
          {members.length === 0 ? (
            <div role="row" className="px-3 py-4 text-muted">
              <div role="cell">No people match.</div>
            </div>
          ) : (
            members.map((row) => {
              const rowActions = actionsOf(row);
              const name = memberName(row);
              const identity = row.identitySubject ? `${row.identitySubject} · ${providerName(row.identityIssuer)}` : "";
              const status = SIGN_IN_LABELS[row.signInStatus] ?? SIGN_IN_LABELS["NotRecorded"]!;
              const teams = row.teams.map((team) => team.teamName).join(", ");
              return (
                <div
                  key={row.userId}
                  role="row"
                  className={`grid grid-cols-2 gap-x-3 gap-y-3 rounded-panel @xl:grid-cols-[minmax(0,1.5fr)_auto_auto_minmax(0,1fr)_auto] border border-line bg-surface p-4 @4xl:items-center @4xl:rounded-none @4xl:border-0 @4xl:border-b @4xl:px-3 @4xl:py-2 @4xl:last:border-b-0 ${grid}`}
                >
                  <div role="rowheader" className="col-span-2 min-w-0 font-normal @xl:col-span-4 @4xl:col-span-1">
                    <div className="truncate font-semibold" title={name}>
                      {row.displayName ? row.displayName : <span className="font-normal text-muted">Name not known yet</span>}
                    </div>
                    {identity ? (
                      <div className="truncate text-help text-muted" title={`${row.identitySubject} · ${row.identityIssuer}`}>
                        {identity}
                      </div>
                    ) : null}
                    {row.isCurrentUser ? <div className="text-help text-muted">(you)</div> : null}
                  </div>
                  <div role="cell" data-label="Roles" className={`${CELL} col-span-2 @xl:col-span-1`}>
                    {!rolesAvailable ? (
                      <span className="text-muted">Unavailable</span>
                    ) : row.authorityRoles.length === 0 ? (
                      <span className="text-muted">None</span>
                    ) : (
                      <span className="flex flex-wrap gap-1 whitespace-nowrap">
                        {row.authorityRoles.map((role) => (
                          <Badge key={role} tone="info">
                            {ROLE_LABELS[role] ?? role}
                          </Badge>
                        ))}
                      </span>
                    )}
                  </div>
                  <div role="cell" data-label="Membership" className={`${CELL} whitespace-nowrap`}>
                    {row.membershipState === "Disabled" ? (
                      <Badge tone="danger">Disabled</Badge>
                    ) : row.membershipState === "Enabled" ? (
                      <Badge tone="success">Active</Badge>
                    ) : (
                      <span className="text-muted">Unknown</span>
                    )}
                  </div>
                  <div role="cell" data-label="Sign-in" className={`${CELL} whitespace-nowrap`}>
                    <Badge tone={status.tone}>{status.label}</Badge>
                  </div>
                  <div role="cell" data-label="Teams" className={CELL}>
                    {teams ? (
                      <div className="truncate" title={teams}>
                        {teams}
                      </div>
                    ) : (
                      <span className="text-muted">—</span>
                    )}
                  </div>
                  <div role="cell" data-label="Last sign-in" className={CELL}>
                    {row.lastSignedInOn ? (
                      <time dateTime={row.lastSignedInOn} className="block whitespace-nowrap">
                        {new Date(row.lastSignedInOn).toLocaleDateString()}
                        <span className="block text-help text-muted">{new Date(row.lastSignedInOn).toLocaleTimeString([], { hour: "numeric", minute: "2-digit" })}</span>
                      </time>
                    ) : (
                      <span className="text-muted">—</span>
                    )}
                  </div>
                  {showActions ? (
                    <div
                      role="cell"
                      className="min-w-0 self-end justify-self-end @xl:col-start-5 @xl:row-start-1 @xl:self-start @4xl:col-start-auto @4xl:row-start-auto @4xl:self-center @4xl:justify-self-stretch @4xl:text-right"
                    >
                      {rowActions.length > 0 ? (
                        <OverflowMenu
                          label={`Actions for ${name}`}
                          trigger={
                            <Button size="sm" variant="secondary" aria-label={`Actions for ${name}`}>
                              Actions <span aria-hidden="true">&#9662;</span>
                            </Button>
                          }
                          items={rowActions.map((action) => ({
                            key: action,
                            content: (
                              <button type="button" aria-label={`${MEMBER_ACTION_LABELS[action]}: ${name}`} onClick={() => onAction?.(row, action)}>
                                {MEMBER_ACTION_LABELS[action]}
                              </button>
                            ),
                          }))}
                        />
                      ) : null}
                    </div>
                  ) : null}
                </div>
              );
            })
          )}
        </div>
      </div>
    </div>
  );
}

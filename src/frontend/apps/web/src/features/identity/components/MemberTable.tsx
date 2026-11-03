import type { OrganizationMemberResponse, PresenceState } from "@enterpriseforms/api-client";
import { Badge, Button, OverflowMenu, PresenceIndicator, getActiveFormatter, useFormatter } from "@enterpriseforms/ui";
import { useId, useState } from "react";

const ROLE_LABELS: Record<string, string> = { OrganizationOwner: "Owner", Designer: "Designer", SecurityAdministrator: "Security administrator" };

export type MemberAction =
  | "EditProfile"
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
  EditProfile: "Edit profile",
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

/** "about 12 days", "about 5 hours", "less than a minute": a rounded countdown for display only (the server decides when the safeguard ends). */
export function describeRemaining(untilMilliseconds: number, nowMilliseconds: number): string {
  const minutes = Math.max(0, Math.round((untilMilliseconds - nowMilliseconds) / 60_000));
  if (minutes < 1) return "less than a minute";
  if (minutes < 60) return "about " + minutes + (minutes === 1 ? " minute" : " minutes");
  const hours = Math.round(minutes / 60);
  if (hours < 48) return "about " + hours + (hours === 1 ? " hour" : " hours");
  const days = Math.round(hours / 24);
  return "about " + days + " days";
}

/** Compact disclosure beside the Owner badge while the recovered-owner safeguard is active; hidden regions add no height. */
function RecoveryRestrictions({ until }: { until: string }) {
  useFormatter();
  const [open, setOpen] = useState(false);
  const regionId = useId();
  const end = Date.parse(until);
  if (Number.isNaN(end) || end <= Date.now()) {
    return null;
  }
  return (
    <span className="inline-flex flex-col items-start gap-1 whitespace-normal">
      <Button type="button" size="sm" variant="secondary" aria-expanded={open} aria-controls={regionId} onClick={() => setOpen((value) => !value)}>
        Recovery restrictions
      </Button>
      <span id={regionId} hidden={!open} className="block max-w-prose rounded-control border border-line bg-page p-3 text-help">
        <span className="block">
          Recovery restrictions end {getActiveFormatter().instant(end)} ({describeRemaining(end, Date.now())}).
        </span>
        <span className="mt-1 block">Until then this owner cannot:</span>
        <ul className="list-disc pl-5">
          <li>add other owners recovered by the same case to teams</li>
          <li>grant them the Designer role</li>
          <li>act as their second owner for owner changes</li>
        </ul>
        <span className="mt-1 block">Adding oneself to a team is never allowed for owners. These restrictions are separate from membership status, sign-in and presence. Nothing else is restricted.</span>
      </span>
    </span>
  );
}

/** Desktop column widths (container query); below that each person is a compact card with labelled fields. */
const GRID_WITH_ACTIONS = "@4xl:grid-cols-[minmax(0,2.6fr)_minmax(0,2fr)_5.5rem_7.5rem_minmax(0,1.2fr)_6.5rem]";
const GRID_READ_ONLY = "@4xl:grid-cols-[minmax(0,2.6fr)_minmax(0,2fr)_5.5rem_7.5rem_minmax(0,1.2fr)]";
const CELL = "min-w-0 before:mb-0.5 before:block before:text-help before:text-muted before:content-[attr(data-label)] @4xl:before:hidden";

/**
 * People directory (PG-050, C8 M2): one Person cell (name, then the verified identity as a short muted line whose full value is
 * the tooltip), Authority roles, membership state (Active or Disabled, never presence), informational presence, teams and one Actions menu. The actions are the
 * server's hints for this caller (the Authority decides every command); without an action handler the table is read-only. On
 * narrow containers the rows become cards. The identity line appears only when the server returned it.
 */
export function MemberTable({
  members,
  rolesAvailable,
  presence,
  onAction,
}: {
  members: OrganizationMemberResponse[];
  rolesAvailable: boolean;
  /** Informational presence by user id; absent or missing means Unknown. */
  presence?: Record<string, PresenceState> | undefined;
  onAction?: (member: OrganizationMemberResponse, action: MemberAction) => void;
}) {
  const actionsOf = (m: OrganizationMemberResponse) => (onAction ? (m.availableActions ?? []).filter(isMemberAction) : []);
  const showActions = members.some((m) => actionsOf(m).length > 0);
  const grid = showActions ? GRID_WITH_ACTIONS : GRID_READ_ONLY;
  const headers = ["Person", "Roles", "Membership", "Presence", "Teams", ...(showActions ? ["Actions"] : [])];
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
              const teams = row.teams.map((team) => team.teamName).join(", ");
              return (
                <div
                  key={row.userId}
                  role="row"
                  className={`grid grid-cols-2 gap-x-3 gap-y-3 rounded-panel @xl:grid-cols-[minmax(0,1.5fr)_auto_auto_minmax(0,1fr)] border border-line bg-surface p-4 @4xl:items-center @4xl:rounded-none @4xl:border-0 @4xl:border-b @4xl:px-3 @4xl:py-2 @4xl:last:border-b-0 ${grid}`}
                >
                  <div role="rowheader" className="col-span-2 min-w-0 font-normal @xl:col-span-3 @4xl:col-span-1">
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
                        {row.authorityRoles.includes("OrganizationOwner") && row.recoveryRestrictionsUntil ? <RecoveryRestrictions until={row.recoveryRestrictionsUntil} /> : null}
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
                  <div role="cell" data-label="Presence" className={`${CELL} whitespace-nowrap`}>
                    <PresenceIndicator state={presence?.[row.userId] ?? "Unknown"} />
                  </div>
                  <div role="cell" data-label="Teams" className={`${CELL} col-span-2 @xl:col-span-1`}>
                    {teams ? (
                      <div className="truncate" title={teams}>
                        {teams}
                      </div>
                    ) : (
                      <span className="text-muted">—</span>
                    )}
                  </div>
                  {showActions ? (
                    <div
                      role="cell"
                      className="col-span-2 min-w-0 justify-self-end @xl:col-span-1 @xl:col-start-4 @xl:row-start-1 @xl:self-start @4xl:col-start-auto @4xl:row-start-auto @4xl:self-center @4xl:justify-self-stretch @4xl:text-right"
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

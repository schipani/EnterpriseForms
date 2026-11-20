import type { SecurityCommandTimelineDetailResponse, SecurityCommandTimelineQuery } from "@enterpriseforms/api-client";
import { localDateRange } from "@enterpriseforms/ui";

/**
 * C8 M4-3 security-command timeline (AC-8.2) filters and display helpers. The server (Authorization Authority) validates every
 * filter again and builds every envelope; this model only offers the classified kinds, builds the query and formats values.
 */
export type TimelineFilterState = { fromDate: string; toDate: string; kind: string; state: string };

export const EMPTY_TIMELINE_FILTERS: TimelineFilterState = { fromDate: "", toDate: "", kind: "all", state: "all" };

export const TIMELINE_PAGE_SIZE = 25;

/** Command kinds shown on the timeline (the Authority's classification; excluded kinds never appear). */
export const TIMELINE_COMMAND_KINDS = [
  "GrantSystemRole",
  "RevokeSystemRole",
  "GrantOrganizationRole",
  "RevokeOrganizationRole",
  "ProposeOrganizationOwnerChange",
  "DisableIdentityBinding",
  "EnableIdentityBinding",
  "RequestOrganizationMembership",
  "CancelOrganizationMembershipRequest",
  "ReplaceOrganizationMembershipRequest",
  "RegisterTeam",
  "AddTeamMember",
  "RemoveTeamMember",
  "UpdateFormUsers",
  "UpdateMandatoryPolicy",
  "ReclassifyField",
  "PublishFormSecurityRevision",
  "ApproveFieldSearch",
  "WithdrawFieldSearch",
  "UpdateFieldSearchers",
  "TransferOwnership",
  "CreateShare",
  "RevokeShare",
  "UpdateShareScope",
] as const;

/** Allowlisted audit-stream kinds shown on the timeline. */
export const TIMELINE_AUDIT_KINDS = [
  "AuditAccessAuthorized",
  "FieldSearchExecuted",
  "EnvelopeCapabilityRefused",
  "SecurityAlertRaised",
  "ProtectionReconciliationAttested",
  "SecurityAdministratorFirstAppointed",
  "SecurityAdministratorRecovered",
  "SecurityAdministratorProvisioningRejected",
  "OrganizationMembershipApproved",
  "OrganizationMembershipApprovalRejected",
  "OrganizationMembershipRejected",
  "PlatformOperatorApprovalRecorded",
  "PlatformOperatorApprovalWithdrawn",
  "PlatformOperatorDecisionRefused",
  "PlatformOperatorApprovalsInvalidated",
  "OrganizationInitialized",
  "OwnerRecoveryOpened",
  "OwnerRecoveryObjected",
  "OwnerRecoveryExecuted",
  "OwnerRecoveryInvalidated",
  "PlatformOrganizationDirectoryViewed",
  "PlatformMembershipRequestViewed",
  "PlatformOwnerRecoveryCaseViewed",
  "PlatformOwnerRecoveryMembersViewed",
] as const;

export const TIMELINE_STATES = [
  { value: "Pending", label: "Pending" },
  { value: "CommittedAwaitingProjection", label: "Committed, awaiting projection" },
  { value: "Effective", label: "Effective" },
  { value: "Rejected", label: "Rejected" },
  { value: "FailedReconciliation", label: "Failed reconciliation" },
] as const;

/** "CreateShare" → "Create share"; codes stay codes, this only spaces them for reading. */
export function humanizeKind(kind: string): string {
  const spaced = kind.replace(/([a-z0-9])([A-Z])/g, "$1 $2").toLowerCase();
  return spaced.charAt(0).toUpperCase() + spaced.slice(1);
}

/** "plaintext_search_permitted" → "Plaintext search permitted". */
export function humanizeKey(key: string): string {
  const spaced = key.replace(/_/g, " ");
  return spaced.charAt(0).toUpperCase() + spaced.slice(1);
}

export function stateLabel(state: string | null | undefined): string {
  return TIMELINE_STATES.find((s) => s.value === state)?.label ?? "—";
}

export function validateTimelineFilters(state: TimelineFilterState): { toDate?: string } {
  return state.fromDate !== "" && state.toDate !== "" && state.toDate < state.fromDate ? { toDate: "The end date must not be before the start date." } : {};
}

export function hasTimelineFilters(state: TimelineFilterState): boolean {
  return JSON.stringify(state) !== JSON.stringify(EMPTY_TIMELINE_FILTERS);
}

/** The server query (dates are local calendar days sent as UTC instants; the end day is included). */
export function toTimelineQuery(state: TimelineFilterState, cursor?: string): SecurityCommandTimelineQuery {
  const range = localDateRange(state.fromDate, state.toDate);
  const query: SecurityCommandTimelineQuery = { pageSize: TIMELINE_PAGE_SIZE };
  if (range.from) query.from = range.from;
  if (range.before) query.to = range.before;
  if (state.kind !== "all") query.kind = state.kind;
  if (state.state !== "all") query.state = state.state;
  if (cursor) query.cursor = cursor;
  return query;
}

/** "user:<id>" / "team:<id>" → "User 0199a2b3…" (identifiers are shortened for reading; the full value stays in the title). */
export function formatPrincipal(value: string): string {
  const [kind, id] = value.split(":", 2);
  return `${kind === "team" ? "Team" : "User"} ${id ? id.slice(0, 8) : ""}…`;
}

/** A detail value for display; the full value is kept for the title attribute. */
export function formatDetail(detail: SecurityCommandTimelineDetailResponse): { text: string; full: string } {
  const values = detail.values ?? (detail.value === null || detail.value === undefined ? [] : [detail.value]);
  const full = values.join(", ");
  if (detail.displayName) {
    return { text: detail.displayName, full };
  }

  switch (detail.kind) {
    case "Identifier":
    case "IdentifierList":
      return { text: values.map((v) => `${v.slice(0, 8)}…`).join(", ") || "none", full };
    case "Principal":
    case "PrincipalList":
      return { text: values.map(formatPrincipal).join(", ") || "none", full };
    case "Flag":
      return { text: detail.value === "true" ? "Yes" : "No", full };
    case "Digest":
      return { text: `${(detail.value ?? "").slice(0, 12)}…`, full };
    default:
      return { text: full || "—", full };
  }
}

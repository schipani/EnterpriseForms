import { OperationError } from "@enterpriseforms/app-runtime";
import type { StatusTone } from "@enterpriseforms/ui";

/**
 * Display rules for the Security Home (PG-052, C8 M4-4). Pure functions only, so the wording that limits what a widget claims is
 * unit-tested. Nothing here decides access: every widget asks the server, and the server asks the Authorization Authority.
 */

/** How a failed widget request is presented. `forbidden` is shown as "not available to your role", never as an outage. */
export type WidgetFailure = "forbidden" | "gate-closed" | "unavailable" | "other";

export function widgetFailure(error: unknown): WidgetFailure {
  if (!(error instanceof OperationError)) {
    return "other";
  }

  if (error.status === 403) {
    return "forbidden";
  }

  if (error.errorCode === "AUTHORITY_GATE_CLOSED") {
    return "gate-closed";
  }

  return error.status === 503 || error.status === 0 ? "unavailable" : "other";
}

/** Wording for incidents and search approvals, which keep their live permission check (OD-6). */
export const LIVE_CHECK_UNAVAILABLE =
  "Available to Security Administrators. This widget uses the authorization service's live permission check, which cannot confirm access right now — for example while the organization's authorization gate is closed. It is not shown from cached or committed state.";

export const OVERVIEW_STATEMENT =
  "Operational state read from the Authorization Authority's committed records. It is not independently verified evidence and is not a compliance or certification result.";

const GATE_STATES: Record<string, { label: string; tone: StatusTone; explanation: string }> = {
  Ready: { label: "Open", tone: "success", explanation: "Every committed authorization change is applied and archived; protected access is permitted." },
  AwaitingProjection: { label: "Closed — applying changes", tone: "warning", explanation: "A committed authorization change is not yet applied to the permission service. Protected access is refused until it is." },
  AwaitingArchive: { label: "Closed — awaiting archive", tone: "warning", explanation:
      "The newest authorization change has no currently verified archive receipt: either it is still awaiting delivery to the archive, or archive receipt verification is temporarily unavailable. Protected access is refused until the receipt is verified.",
  },
  Quarantined: { label: "Closed — integrity quarantine", tone: "danger", explanation: "The organization is in integrity quarantine. Protected access is refused until a separately reviewed recovery." },
};

export function gateStateOf(value: string): { label: string; tone: StatusTone; explanation: string } {
  return GATE_STATES[value] ?? { label: value, tone: "warning", explanation: "Unrecognized gate state; treat protected access as unavailable." };
}

const COMMAND_STATES: Record<string, string> = {
  Pending: "Pending",
  CommittedAwaitingProjection: "Committed, awaiting projection",
  FailedReconciliation: "Failed reconciliation",
};

export function commandStateLabel(value: string): string {
  return COMMAND_STATES[value] ?? value;
}

/** "GrantSystemRole" → "Grant system role". Kinds are codes; this only spaces them. */
export function commandKindLabel(value: string): string {
  const spaced = value.replace(/([a-z0-9])([A-Z])/g, "$1 $2").toLowerCase();
  return spaced.charAt(0).toUpperCase() + spaced.slice(1);
}

const STREAMS: Record<string, string> = {
  authorization: "Authorization journal",
  content: "Content journal",
  audit: "Audit journal",
};

export function streamLabel(value: string): string {
  return STREAMS[value] ?? value;
}

const COVERAGE_STATES: Record<string, { label: string; tone: StatusTone | "neutral"; explanation: string }> = {
  Missing: { label: "No attestation", tone: "warning", explanation: "This protection decision has no Authority attestation yet." },
  StaleOrganizationWide: {
    label: "Possibly stale (organization-wide)",
    tone: "warning",
    explanation: "Content somewhere in the organization changed after this attestation. This form may not have changed; the comparison is organization-wide, not a check of this form.",
  },
  NoLaterOrganizationContentChange: {
    label: "No later organization change",
    tone: "neutral",
    explanation: "No content in the organization changed after this attestation. This is not a new verification.",
  },
};

export function coverageStateOf(value: string): { label: string; tone: StatusTone | "neutral"; explanation: string } {
  return COVERAGE_STATES[value] ?? { label: value, tone: "warning", explanation: "Unrecognized coverage state." };
}

/**
 * The completeness badge of a recorded attestation. It describes the attestation when it was taken, never current coverage, so it
 * is never shown in a success tone (a stale attestation must not read as verified now).
 */
export function attestationBadge(attestationComplete: boolean): { label: string; tone: StatusTone | "neutral" } {
  return attestationComplete ? { label: "Complete when attested", tone: "neutral" } : { label: "Incomplete when attested", tone: "warning" };
}

/** Detail for the "complete when attested" count: how many of those attestations are possibly stale (organization-wide). */
export function completeWhenAttestedDetail(decisions: ReadonlyArray<{ attestationState: string; attestation?: { attestationComplete: boolean } | null }>): string {
  const stale = decisions.filter((d) => d.attestation?.attestationComplete === true && d.attestationState === "StaleOrganizationWide").length;
  return stale > 0 ? `${String(stale)} of these possibly stale; not re-verified` : "As recorded; not re-verified";
}

/** A compact age such as "45 s", "12 min", "3 h 5 min" or "2 d 4 h". */
export function formatAge(seconds: number | string | null | undefined): string {
  if (seconds === null || seconds === undefined || seconds === "") {
    return "—";
  }

  const total = Math.max(0, Math.floor(Number(seconds)));
  if (!Number.isFinite(total)) {
    return "—";
  }

  if (total < 60) {
    return `${total} s`;
  }

  const minutes = Math.floor(total / 60);
  if (minutes < 60) {
    return `${minutes} min`;
  }

  const hours = Math.floor(minutes / 60);
  if (hours < 24) {
    const rest = minutes % 60;
    return rest ? `${hours} h ${rest} min` : `${hours} h`;
  }

  const days = Math.floor(hours / 24);
  const restHours = hours % 24;
  return restHours ? `${days} d ${restHours} h` : `${days} d`;
}

/** The Security Home link is shown to either role (D8-70) from the existing capabilities only (OD-5): nothing new is granted. */
export function securityHomeVisible(hasCapability: (capability: string) => boolean): boolean {
  return hasCapability("security.administer") || hasCapability("audit.view");
}

/** The search-approval summary: counts over the forms the existing endpoint returns. */
export function approvalTotals(forms: ReadonlyArray<{ activeApprovalCount: number | string; invalidatedApprovalCount: number | string }>): { active: number; invalidated: number; forms: number } {
  return {
    active: forms.reduce((sum, f) => sum + Number(f.activeApprovalCount), 0),
    invalidated: forms.reduce((sum, f) => sum + Number(f.invalidatedApprovalCount), 0),
    forms: forms.filter((f) => Number(f.activeApprovalCount) > 0).length,
  };
}

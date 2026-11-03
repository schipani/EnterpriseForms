import type { RequestItem } from "../api/client";

/** Display formatting only; nothing here changes what is sent to or decided by the Authority. */
const dateTime = new Intl.DateTimeFormat(undefined, { dateStyle: "medium", timeStyle: "short" });
const timeOnly = new Intl.DateTimeFormat(undefined, { timeStyle: "short" });

export const formatDateTime = (iso: string): string => (Number.isNaN(Date.parse(iso)) ? iso : dateTime.format(new Date(iso)));
export const formatTime = (iso: string): string => (Number.isNaN(Date.parse(iso)) ? iso : timeOnly.format(new Date(iso)));

/** Verified identity summary: subject, with the issuer host beneath. */
export function identitySummary(identity: { issuerUri: string; subjectIdentifier: string } | null | undefined): { subject: string; issuer: string } {
  if (!identity) {
    return { subject: "Unknown", issuer: "" };
  }
  let issuer = identity.issuerUri;
  try {
    issuer = new URL(identity.issuerUri).host;
  } catch {
    // not a URL: show as sent
  }
  return { subject: identity.subjectIdentifier, issuer };
}

export const roleLabel = (role: string | null | undefined): string => role ?? "No role";

export type StateTone = "info" | "success" | "warning" | "danger" | "neutral";

/** Progress chip from the Authority's own numbers; "you approved" and "awaiting another operator" come from the server. */
export function approvalChip(item: RequestItem): { text: string; tone: StateTone } {
  const recorded = Number(item.approvalStatus.recorded);
  const required = Number(item.approvalStatus.required);
  switch (item.requestState) {
    case "Approved":
      return { text: "Approved", tone: "success" };
    case "Rejected":
      return { text: "Rejected", tone: "danger" };
    case "Expired":
      return { text: "Expired", tone: "neutral" };
    case "Cancelled":
      return { text: "Cancelled", tone: "neutral" };
    case "Replaced":
      return { text: "Replaced", tone: "neutral" };
    default:
      if (item.approvalStatus.approvedByMe) {
        return { text: `${recorded} of ${required} · you approved`, tone: "info" };
      }
      return { text: recorded > 0 ? `${recorded} of ${required} · awaiting another operator` : `${recorded} of ${required}`, tone: recorded > 0 ? "warning" : "neutral" };
  }
}

export const statusFilters: Array<{ value: string; label: string }> = [
  { value: "needs_decision", label: "Needs a decision" },
  { value: "awaiting_first_approval", label: "Awaiting first approval" },
  { value: "awaiting_second_approval", label: "Awaiting second approval" },
  { value: "approved", label: "Approved" },
  { value: "rejected", label: "Rejected" },
  { value: "expired", label: "Expired" },
  { value: "cancelled_or_replaced", label: "Cancelled or replaced" },
  { value: "all", label: "All" },
];

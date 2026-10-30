import type { StatusTone } from "@enterpriseforms/ui";

/** Display vocabulary for security incidents. A type describes what was observed, never who or why. */
export const INCIDENT_TYPES = [
  { value: "ConfirmedIntegrityMismatch", label: "Confirmed integrity mismatch" },
  { value: "IntegrityQuarantine", label: "Integrity quarantine" },
  { value: "VerificationServiceUnavailable", label: "Verification service unavailable" },
  { value: "OperationalVerificationFailure", label: "Operational verification failure" },
] as const;

export const INCIDENT_SEVERITIES = [
  { value: "Critical", label: "Critical", tone: "danger" },
  { value: "High", label: "High", tone: "warning" },
  { value: "Medium", label: "Medium", tone: "info" },
  { value: "Low", label: "Low", tone: "neutral" },
] as const;

export const INCIDENT_STATUSES = [
  { value: "Open", label: "Open", tone: "danger" },
  { value: "Investigating", label: "Investigating", tone: "warning" },
  { value: "Resolved", label: "Resolved", tone: "success" },
  { value: "FalsePositive", label: "False positive", tone: "neutral" },
] as const;

export const DETECTION_SOURCES: Record<string, string> = {
  RecordRead: "Record opened",
  RecordVersionRead: "Earlier version opened",
  DocumentVersionDownload: "Document version download",
};

export function incidentTypeLabel(value: string): string {
  return INCIDENT_TYPES.find((t) => t.value === value)?.label ?? value;
}

export function severityOf(value: string): { label: string; tone: StatusTone | "neutral" } {
  return INCIDENT_SEVERITIES.find((s) => s.value === value) ?? { label: value, tone: "neutral" };
}

export function statusOf(value: string): { label: string; tone: StatusTone | "neutral" } {
  return INCIDENT_STATUSES.find((s) => s.value === value) ?? { label: value, tone: "neutral" };
}

/** Closed statuses need a resolution explanation; closing never restores access to the record. */
export function isClosedStatus(value: string): boolean {
  return value === "Resolved" || value === "FalsePositive";
}

/** What the incident search accepts: INC-xxxxxxxx, REC-xxxxxxxx or a full identifier. Null when the text is not one of them. */
export function parseIncidentSearch(text: string): string | null {
  const trimmed = text.trim();
  if (/^(INC|REC)-[0-9a-f]{8}$/i.test(trimmed)) {
    return trimmed.toUpperCase();
  }
  const unbraced = trimmed.replace(/^\{(.*)\}$/, "$1");
  return /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(unbraced) ? unbraced.toLowerCase() : null;
}

export function formatInstant(value: string | null | undefined): string {
  return value ? new Date(value).toLocaleString() : "—";
}

import type { AuditExplorerQuery } from "@enterpriseforms/api-client";
import { getActiveFormatter, localDateRange } from "@enterpriseforms/ui";

/**
 * C8 M4-2 audit explorer filters (PG-054). The server accepts ids, codes and dates only and validates them again; this model
 * only keeps invalid text from being sent and builds the query. It never carries free text.
 */
export type AuditFilterState = {
  fromDate: string;
  toDate: string;
  actorUserId: string;
  action: string;
  category: string;
  outcome: string;
  entityType: string;
  entityId: string;
  formRecordId: string;
  correlationId: string;
  excludeAuditAccess: boolean;
};

export const EMPTY_AUDIT_FILTERS: AuditFilterState = {
  fromDate: "",
  toDate: "",
  actorUserId: "",
  action: "",
  category: "all",
  outcome: "all",
  entityType: "",
  entityId: "",
  formRecordId: "",
  correlationId: "",
  excludeAuditAccess: false,
};

export const AUDIT_CATEGORIES = ["Record", "Form", "Document", "Access", "Relationship", "Security", "Organization", "Preferences"] as const;

/** The values `audit_events.event_outcome` can hold (ck_audit_events_outcome). */
export const AUDIT_OUTCOMES = [
  { value: "Success", label: "Success" },
  { value: "Failed", label: "Failed" },
  { value: "Denied", label: "Denied" },
] as const;

export const AUDIT_PAGE_SIZE = 25;

const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const ACTION = /^[A-Z][A-Z0-9_]{0,63}$/;
const ENTITY_TYPE = /^[a-z][a-z0-9_]{0,63}$/;

export type AuditFilterErrors = Partial<Record<"actorUserId" | "action" | "entityType" | "entityId" | "formRecordId" | "correlationId" | "toDate", string>>;

function normalizedId(value: string): string {
  return value.trim().replace(/^\{(.*)\}$/, "$1").toLowerCase();
}

/** Field errors for the current filters (empty when every filter can be sent). */
export function validateAuditFilters(state: AuditFilterState): AuditFilterErrors {
  const errors: AuditFilterErrors = {};
  const id = (key: "actorUserId" | "entityId" | "formRecordId" | "correlationId", label: string) => {
    const value = state[key].trim();
    if (value !== "" && !UUID.test(normalizedId(value))) {
      errors[key] = `Enter a full ${label} identifier.`;
    }
  };
  id("actorUserId", "user");
  id("entityId", "entity");
  id("formRecordId", "record");
  id("correlationId", "correlation");
  if (state.action.trim() !== "" && !ACTION.test(state.action.trim())) {
    errors.action = "Enter an action code such as RECORD_VIEWED.";
  }
  if (state.entityType.trim() !== "" && !ENTITY_TYPE.test(state.entityType.trim())) {
    errors.entityType = "Enter an entity type code such as form_record.";
  }
  if (state.fromDate !== "" && state.toDate !== "" && state.toDate < state.fromDate) {
    errors.toDate = "The end date must not be before the start date.";
  }
  return errors;
}

export function hasAuditFilters(state: AuditFilterState): boolean {
  return JSON.stringify(state) !== JSON.stringify(EMPTY_AUDIT_FILTERS);
}

/** The server query for valid filters (dates are local calendar days, sent as UTC instants; the end day is included). */
export function toAuditQuery(state: AuditFilterState, cursor?: string): AuditExplorerQuery {
  const range = localDateRange(state.fromDate, state.toDate);
  const text = (value: string) => (value.trim() === "" ? undefined : value.trim());
  const id = (value: string) => (value.trim() === "" ? undefined : normalizedId(value));
  const query: AuditExplorerQuery = { pageSize: AUDIT_PAGE_SIZE };
  const set = <K extends keyof AuditExplorerQuery>(key: K, value: AuditExplorerQuery[K] | undefined) => {
    if (value !== undefined) {
      query[key] = value;
    }
  };
  set("from", range.from);
  set("to", range.before);
  set("actorUserId", id(state.actorUserId));
  set("action", text(state.action));
  set("category", state.category === "all" ? undefined : state.category);
  set("outcome", state.outcome === "all" ? undefined : state.outcome);
  set("entityType", text(state.entityType));
  set("entityId", id(state.entityId));
  set("formRecordId", id(state.formRecordId));
  set("correlationId", id(state.correlationId));
  set("excludeAuditAccess", state.excludeAuditAccess ? true : undefined);
  set("cursor", cursor);
  return query;
}

/** Filters preset from the explorer's own links (`?correlationId=…`, `?actorUserId=…`); anything else is ignored. */
export function filtersFromSearch(search: Record<string, unknown>): AuditFilterState {
  const pick = (key: string) => (typeof search[key] === "string" && UUID.test(normalizedId(search[key] as string)) ? normalizedId(search[key] as string) : "");
  return { ...EMPTY_AUDIT_FILTERS, correlationId: pick("correlationId"), actorUserId: pick("actorUserId") };
}

/** An instant in the viewer's resolved time zone and format (stored values are UTC). */
export function formatInstant(value: string | null | undefined): string {
  return value ? getActiveFormatter().instant(value) : "—";
}

/** A shortened identifier for tables; the full value stays available to copy. */
export function shortId(value: string | null | undefined): string {
  return value ? `${value.slice(0, 8)}…` : "—";
}

/** Keyset pages are forward-only on the server: the client keeps the cursors it has used to go back. */
export type AuditPageTrail = { cursors: Array<string | undefined> };

export const FIRST_PAGE: AuditPageTrail = { cursors: [undefined] };

export function currentCursor(trail: AuditPageTrail): string | undefined {
  return trail.cursors[trail.cursors.length - 1];
}

export function nextPage(trail: AuditPageTrail, nextCursor: string): AuditPageTrail {
  return { cursors: [...trail.cursors, nextCursor] };
}

export function previousPage(trail: AuditPageTrail): AuditPageTrail {
  return trail.cursors.length > 1 ? { cursors: trail.cursors.slice(0, -1) } : trail;
}

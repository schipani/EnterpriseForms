import { describe, expect, it } from "vitest";
import {
  EMPTY_TIMELINE_FILTERS,
  formatDetail,
  hasTimelineFilters,
  humanizeKey,
  humanizeKind,
  stateLabel,
  toTimelineQuery,
  validateTimelineFilters,
} from "./securityCommandTimelineModel";

describe("security-command timeline model", () => {
  it("builds a query of codes and dates only, omitting 'all'", () => {
    expect(toTimelineQuery(EMPTY_TIMELINE_FILTERS)).toEqual({ pageSize: 25 });
    const query = toTimelineQuery({ fromDate: "2026-06-01", toDate: "2026-06-02", kind: "CreateShare", state: "Rejected" }, "cursor-token");
    expect(query.kind).toBe("CreateShare");
    expect(query.state).toBe("Rejected");
    expect(query.cursor).toBe("cursor-token");
    expect(query.from).toBeTruthy();
    expect(query.to).toBeTruthy();
    expect(Object.keys(query).sort()).toEqual(["cursor", "from", "kind", "pageSize", "state", "to"]);
  });

  it("validates the date range and tracks active filters", () => {
    expect(validateTimelineFilters({ ...EMPTY_TIMELINE_FILTERS, fromDate: "2026-06-02", toDate: "2026-06-01" }).toDate).toBeTruthy();
    expect(validateTimelineFilters(EMPTY_TIMELINE_FILTERS)).toEqual({});
    expect(hasTimelineFilters(EMPTY_TIMELINE_FILTERS)).toBe(false);
    expect(hasTimelineFilters({ ...EMPTY_TIMELINE_FILTERS, state: "Rejected" })).toBe(true);
  });

  it("formats labels and detail values without inventing content", () => {
    expect(humanizeKind("TransferOwnership")).toBe("Transfer ownership");
    expect(humanizeKey("plaintext_search_permitted")).toBe("Plaintext search permitted");
    expect(stateLabel("FailedReconciliation")).toBe("Failed reconciliation");
    expect(stateLabel(null)).toBe("—");
    const id = "0199a2b3-c4d5-7e6f-8123-456789abcdef";
    expect(formatDetail({ key: "team_id", kind: "Identifier", value: id, values: null, displayName: null })).toEqual({ text: "0199a2b3…", full: id });
    expect(formatDetail({ key: "target_user_id", kind: "Identifier", value: id, values: null, displayName: "Casey Clerk" }).text).toBe("Casey Clerk");
    expect(formatDetail({ key: "recipient", kind: "Principal", value: `team:${id}`, values: null, displayName: null }).text).toBe("Team 0199a2b3…");
    expect(formatDetail({ key: "added_principals", kind: "PrincipalList", value: null, values: [], displayName: null }).text).toBe("none");
    expect(formatDetail({ key: "plaintext_search_permitted", kind: "Flag", value: "false", values: null, displayName: null }).text).toBe("No");
    expect(formatDetail({ key: "role_name", kind: "Code", value: "security_auditor", values: null, displayName: null }).text).toBe("security_auditor");
  });
});

import { describe, expect, it } from "vitest";
import { EMPTY_AUDIT_FILTERS, FIRST_PAGE, currentCursor, filtersFromSearch, hasAuditFilters, nextPage, previousPage, toAuditQuery, validateAuditFilters } from "./auditExplorerModel";

describe("audit explorer filter model (PG-054)", () => {
  it("sends only the page size when no filter is set", () => {
    expect(toAuditQuery(EMPTY_AUDIT_FILTERS)).toEqual({ pageSize: 25 });
    expect(hasAuditFilters(EMPTY_AUDIT_FILTERS)).toBe(false);
  });

  it("sends ids, codes and dates, normalizing identifiers", () => {
    const query = toAuditQuery(
      {
        ...EMPTY_AUDIT_FILTERS,
        actorUserId: " {0199A2B3-C4D5-7E6F-8123-456789ABCDEF} ",
        action: "RECORD_VIEWED",
        category: "Record",
        outcome: "Denied",
        entityType: "form_record",
        excludeAuditAccess: true,
        fromDate: "2026-11-01",
        toDate: "2026-11-02",
      },
      "cursor-1",
    );
    expect(query.actorUserId).toBe("0199a2b3-c4d5-7e6f-8123-456789abcdef");
    expect(query).toMatchObject({ action: "RECORD_VIEWED", category: "Record", outcome: "Denied", entityType: "form_record", excludeAuditAccess: true, cursor: "cursor-1", pageSize: 25 });
    expect(query.from).toBeDefined();
    expect(new Date(query.to!).getTime()).toBeGreaterThan(new Date(query.from!).getTime());
  });

  it("rejects text that is not an id or a code before it is sent", () => {
    const errors = validateAuditFilters({ ...EMPTY_AUDIT_FILTERS, actorUserId: "alice", action: "record viewed", entityType: "Form Record", correlationId: "1234", fromDate: "2026-11-05", toDate: "2026-11-01" });
    expect(Object.keys(errors).sort()).toEqual(["action", "actorUserId", "correlationId", "entityType", "toDate"]);
    expect(validateAuditFilters({ ...EMPTY_AUDIT_FILTERS, action: "RECORD_VIEWED", correlationId: "0199a2b3-c4d5-7e6f-8123-456789abcdef" })).toEqual({});
  });

  it("presets only identifier filters from the explorer's own links", () => {
    expect(filtersFromSearch({ correlationId: "0199A2B3-C4D5-7E6F-8123-456789ABCDEF", action: "X", actorUserId: "not-an-id" })).toEqual({ ...EMPTY_AUDIT_FILTERS, correlationId: "0199a2b3-c4d5-7e6f-8123-456789abcdef" });
  });

  it("keeps the cursors it used so a keyset list can go back", () => {
    const second = nextPage(FIRST_PAGE, "c2");
    const third = nextPage(second, "c3");
    expect(currentCursor(FIRST_PAGE)).toBeUndefined();
    expect(currentCursor(third)).toBe("c3");
    expect(currentCursor(previousPage(third))).toBe("c2");
    expect(previousPage(FIRST_PAGE)).toBe(FIRST_PAGE);
  });
});

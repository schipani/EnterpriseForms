import { describe, expect, it } from "vitest";
import { incidentTypeLabel, isClosedStatus, parseIncidentSearch, statusOf } from "./incidentModel";

describe("incident search input", () => {
  it("accepts incident and record references and full identifiers only", () => {
    expect(parseIncidentSearch(" inc-0a1b2c3d ")).toBe("INC-0A1B2C3D");
    expect(parseIncidentSearch("rec-017ef770")).toBe("REC-017EF770");
    expect(parseIncidentSearch("{0199A2B3-C4D5-7E6F-8123-456789ABCDEF}")).toBe("0199a2b3-c4d5-7e6f-8123-456789abcdef");
    expect(parseIncidentSearch("salary")).toBeNull();
    expect(parseIncidentSearch("INC-123")).toBeNull();
  });
});

describe("incident vocabulary", () => {
  it("describes what was observed and which statuses close an incident", () => {
    expect(incidentTypeLabel("ConfirmedIntegrityMismatch")).toBe("Confirmed integrity mismatch");
    expect(statusOf("FalsePositive").label).toBe("False positive");
    expect(isClosedStatus("Resolved")).toBe(true);
    expect(isClosedStatus("Investigating")).toBe(false);
  });
});

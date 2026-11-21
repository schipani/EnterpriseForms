import { OperationError } from "@enterpriseforms/app-runtime";
import { describe, expect, it } from "vitest";
import { approvalTotals, attestationBadge, commandKindLabel, completeWhenAttestedDetail, commandStateLabel, coverageStateOf, formatAge, gateStateOf, securityHomeVisible, streamLabel, widgetFailure } from "./securityHomeModel";

const failure = (status: number, errorCode: string) => new OperationError({ type: "failed", operationId: "op", status, errorCode, retryable: false });

describe("Security Home model (C8 M4-4)", () => {
  it("a refusal is 'forbidden', a closed gate and an outage are distinct, anything else is 'other'", () => {
    expect(widgetFailure(failure(403, "SECURITY_ADMINISTRATION_FORBIDDEN"))).toBe("forbidden");
    expect(widgetFailure(failure(503, "AUTHORITY_GATE_CLOSED"))).toBe("gate-closed");
    expect(widgetFailure(failure(503, "AUTHORITY_UNAVAILABLE"))).toBe("unavailable");
    expect(widgetFailure(failure(0, "NETWORK"))).toBe("unavailable");
    expect(widgetFailure(failure(404, "NOT_FOUND"))).toBe("other");
    expect(widgetFailure(new Error("boom"))).toBe("other");
  });

  it("every gate state names protected access as refused unless the gate is open", () => {
    expect(gateStateOf("Ready")).toMatchObject({ label: "Open", tone: "success" });
    for (const closed of ["AwaitingProjection", "AwaitingArchive", "Quarantined"]) {
      expect(gateStateOf(closed).label).toMatch(/^Closed/);
      expect(gateStateOf(closed).explanation).toMatch(/refused/);
    }
    expect(gateStateOf("Quarantined").tone).toBe("danger");
    // C-171: a closed archive gate covers both causes, and neither is shown as open.
    expect(gateStateOf("AwaitingArchive").explanation).toMatch(/awaiting delivery to the archive/);
    expect(gateStateOf("AwaitingArchive").explanation).toMatch(/archive receipt verification is temporarily unavailable/);
    expect(gateStateOf("Something").explanation).toMatch(/unavailable/); // unknown is never shown as open
  });

  it("coverage staleness is worded as an organization-wide comparison, never as a verification of the form", () => {
    expect(coverageStateOf("StaleOrganizationWide").label).toContain("organization-wide");
    expect(coverageStateOf("StaleOrganizationWide").explanation).toMatch(/may not have changed/);
    expect(coverageStateOf("NoLaterOrganizationContentChange").explanation).toMatch(/not a new verification/);
    expect(coverageStateOf("Missing").label).toBe("No attestation");
    for (const state of ["Missing", "StaleOrganizationWide", "NoLaterOrganizationContentChange"]) {
      expect(`${coverageStateOf(state).label} ${coverageStateOf(state).explanation}`).not.toMatch(/verified|compliant|certif/i);
    }
  });

  it("labels codes without inventing meaning", () => {
    expect(commandKindLabel("GrantSystemRole")).toBe("Grant system role");
    expect(commandKindLabel("CreateShare")).toBe("Create share");
    expect(commandStateLabel("CommittedAwaitingProjection")).toBe("Committed, awaiting projection");
    expect(commandStateLabel("Unknown")).toBe("Unknown");
    expect(streamLabel("authorization")).toBe("Authorization journal");
    expect(streamLabel("other")).toBe("other");
  });

  it("formats ages compactly and tolerates missing or string values", () => {
    expect(formatAge(undefined)).toBe("—");
    expect(formatAge(null)).toBe("—");
    expect(formatAge(-5)).toBe("0 s");
    expect(formatAge(45)).toBe("45 s");
    expect(formatAge("720")).toBe("12 min");
    expect(formatAge(3 * 3600)).toBe("3 h");
    expect(formatAge(3 * 3600 + 300)).toBe("3 h 5 min");
    expect(formatAge(2 * 86400 + 4 * 3600)).toBe("2 d 4 h");
  });

  it("the Security Home link uses only the existing capabilities, for either role (OD-5, D8-70)", () => {
    expect(securityHomeVisible((c) => c === "security.administer")).toBe(true);
    expect(securityHomeVisible((c) => c === "audit.view")).toBe(true);
    expect(securityHomeVisible((c) => c === "organization.manage")).toBe(false);
    expect(securityHomeVisible(() => false)).toBe(false);
  });

  it("totals search approvals over the returned forms", () => {
    expect(approvalTotals([])).toEqual({ active: 0, invalidated: 0, forms: 0 });
    expect(approvalTotals([{ activeApprovalCount: "2", invalidatedApprovalCount: 1 }, { activeApprovalCount: 0, invalidatedApprovalCount: "3" }])).toEqual({ active: 2, invalidated: 4, forms: 1 });
  });
  it("a recorded attestation is never shown as current: no success tone, and stale ones are counted explicitly (D-M44-3)", () => {
    expect(attestationBadge(true)).toEqual({ label: "Complete when attested", tone: "neutral" });
    expect(attestationBadge(false)).toEqual({ label: "Incomplete when attested", tone: "warning" });
    expect(completeWhenAttestedDetail([])).toBe("As recorded; not re-verified");
    expect(
      completeWhenAttestedDetail([
        { attestationState: "StaleOrganizationWide", attestation: { attestationComplete: true } },
        { attestationState: "StaleOrganizationWide", attestation: { attestationComplete: false } },
        { attestationState: "NoLaterOrganizationContentChange", attestation: { attestationComplete: true } },
        { attestationState: "Missing", attestation: null },
      ]),
    ).toBe("1 of these possibly stale; not re-verified");
  });
});

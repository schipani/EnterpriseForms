import { OperationError } from "@enterpriseforms/app-runtime";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";

const security = { overview: vi.fn(), incidentSummary: vi.fn() };
const organization = { searchApprovals: vi.fn() };
let capabilities: string[] = [];

vi.mock("@enterpriseforms/app-runtime", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@enterpriseforms/app-runtime")>()),
  useApi: () => ({ security, organization }),
  useSession: () => ({ hasCapability: (c: string) => capabilities.includes(c) }),
}));
vi.mock("@tanstack/react-router", () => ({ Link: ({ to, children, className }: { to: string; children: React.ReactNode; className?: string }) => <a href={to} className={className}>{children}</a> }));

const { SecurityHomePage } = await import("./SecurityHomePage");

const computedOn = "2026-06-01T12:00:00Z";
const forbidden = () => new OperationError({ type: "failed", operationId: "op", status: 403, errorCode: "SECURITY_ADMINISTRATION_FORBIDDEN", retryable: false });
const answers: Record<string, unknown> = {
  gate: { section: "gate", authorizationBasis: "basis", computedOn, gate: { authorizationRevision: 15, projectionAppliedRevision: 12, contentIntegrityRevision: 40, gateState: "AwaitingProjection", integrityQuarantined: false } },
  commands: {
    section: "commands", authorizationBasis: "basis", computedOn,
    commands: { stateCounts: [{ commandState: "Pending", commandCount: 2 }, { commandState: "CommittedAwaitingProjection", commandCount: 0 }, { commandState: "FailedReconciliation", commandCount: 1 }], oldestUnresolvedCreatedOn: computedOn, oldestUnresolvedAgeSeconds: 3600, commands: [{ commandKind: "GrantSystemRole", commandState: "Pending", committedAuthorizationRevision: null, createdOn: computedOn, ageSeconds: 3600 }], listLimit: 20, listStatement: "Unresolved commands only" },
  },
  archive: { section: "archive", authorizationBasis: "basis", computedOn, archive: { streams: [{ streamName: "audit", pendingCount: 1, anchoredCount: 5, verificationFailedCount: 0, oldestPendingCreatedOn: computedOn, oldestPendingAgeSeconds: 300, lastAnchoredOn: computedOn }], archiveStatement: "RR-6: not immutable" } },
  coverage: {
    section: "coverage", authorizationBasis: "basis", computedOn,
    coverage: {
      registeredFormCount: 1, decisionCount: 1, attestedCompleteDecisionCount: 1, missingDecisionCount: 0, staleDecisionCount: 1, organizationContentIntegrityRevision: 40,
      decisions: [{ securityCommandId: "c1", formDefinitionId: "f1", formDisplayName: "Intake Form", fieldDefinitionId: "0199a2b3-field", protectionDirection: "Encrypt", formRegisteredActive: true, attestationState: "StaleOrganizationWide", attestation: { attestationId: "a1", attestationComplete: true, attestationIncompleteReasonCode: null, expectedRecordCount: 7, acceptedRecordCount: 6, compliantRecordCount: 6, pendingCreationRecordCount: 1, quarantinedRecordCount: 0, unverifiedRecordCount: 0, notYetCensusedRecordCount: 0, retiredRecordCount: 2, retainedHistoryRevisionCount: 9, auditJournalRevision: 33, attestedContentIntegrityRevision: 39, attestedOn: computedOn } }],
      coverageStatement: "Not a compliance certification.", stalenessStatement: "Staleness is an organization-wide comparison.", formNameStatement: "Fields are identified by id only.",
    },
  },
};

function renderPage() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <SecurityHomePage />
    </QueryClientProvider>,
  );
}

function card(name: string) {
  return screen.getByRole("heading", { level: 2, name }).closest("section")!;
}

beforeEach(() => {
  vi.clearAllMocks();
  security.overview.mockImplementation((section: string) => Promise.resolve({ result: answers[section] }));
  security.incidentSummary.mockResolvedValue({ result: { openIncidentCount: 3, investigatingIncidentCount: 1, criticalIncidentCount: 0, recentIncidentCount: 4 } });
  organization.searchApprovals.mockResolvedValue({ result: { plaintextSearchPermitted: true, approvalOperational: true, forms: [{ activeApprovalCount: 2, invalidatedApprovalCount: 1 }] } });
});

describe("SecurityHomePage (PG-052, C8 M4-4)", () => {
  it("a Security Administrator sees every section, each from its own request", async () => {
    capabilities = ["security.administer"];
    renderPage();
    expect(await within(card("Authorization gate")).findByText("Closed — applying changes")).toBeInTheDocument();
    expect(await within(card("Unresolved security commands")).findByText("Grant system role")).toBeInTheDocument();
    expect(await within(card("Archive delivery")).findByText("Audit journal")).toBeInTheDocument();
    expect(await within(card("Encryption coverage")).findByText("Possibly stale (organization-wide)")).toBeInTheDocument();
    expect(within(card("Encryption coverage")).getByText("0199a2b3-field")).toBeInTheDocument();
    expect(within(card("Encryption coverage")).getAllByText("Complete when attested")).toHaveLength(2); // the KPI label and the stale row's badge
    expect(within(card("Encryption coverage")).queryByText("Attestation complete")).not.toBeInTheDocument();
    expect(within(card("Encryption coverage")).getByText("1 of these possibly stale; not re-verified")).toBeInTheDocument();
    expect(await within(card("Search approvals")).findByText("Manage search approvals")).toBeInTheDocument();
    expect(await within(card("Security incidents")).findByText("All incidents")).toBeInTheDocument();
    expect(security.overview.mock.calls.map((c) => c[0]).sort()).toEqual(["archive", "commands", "coverage", "gate"]);
    expect(screen.getByText(/not a compliance or certification result/)).toBeInTheDocument();
  });

  it("a Security Auditor reads archive only; the other sections say they are not available, and the live-check widgets request nothing", async () => {
    capabilities = ["audit.view"];
    security.overview.mockImplementation((section: string) => (section === "archive" ? Promise.resolve({ result: answers.archive }) : Promise.reject(forbidden())));
    renderPage();
    expect(await within(card("Archive delivery")).findByText("Audit journal")).toBeInTheDocument();
    for (const name of ["Authorization gate", "Unresolved security commands", "Encryption coverage"]) {
      expect(await within(card(name)).findByText(/Not available to your role/)).toBeInTheDocument();
    }
    expect(within(card("Search approvals")).getByText(/live permission check/)).toBeInTheDocument();
    expect(within(card("Security incidents")).getByText(/live permission check/)).toBeInTheDocument();
    expect(organization.searchApprovals).not.toHaveBeenCalled();
    expect(security.incidentSummary).not.toHaveBeenCalled();
  });

  it("while the gate is closed (no SpiceDB capabilities), the Authority-backed sections still load and the others show unavailable", async () => {
    capabilities = [];
    renderPage();
    expect(await within(card("Authorization gate")).findByText("Closed — applying changes")).toBeInTheDocument();
    expect(await within(card("Encryption coverage")).findByText("Intake Form")).toBeInTheDocument();
    expect(within(card("Search approvals")).getByText(/cannot confirm access right now/)).toBeInTheDocument();
    expect(organization.searchApprovals).not.toHaveBeenCalled();
  });

  it("one section's failure leaves the others intact, and each refresh asks again for that section only", async () => {
    capabilities = ["security.administer"];
    security.overview.mockImplementation((section: string) =>
      section === "commands" ? Promise.reject(new OperationError({ type: "failed", operationId: "op", status: 503, errorCode: "AUTHORITY_UNAVAILABLE", retryable: true })) : Promise.resolve({ result: answers[section] }),
    );
    renderPage();
    expect(await within(card("Unresolved security commands")).findByText("Code AUTHORITY_UNAVAILABLE")).toBeInTheDocument();
    expect(await within(card("Authorization gate")).findByText("Closed — applying changes")).toBeInTheDocument();

    const before = security.overview.mock.calls.filter((c) => c[0] === "gate").length;
    await userEvent.click(within(card("Authorization gate")).getByRole("button", { name: "Refresh authorization gate" }));
    expect(security.overview.mock.calls.filter((c) => c[0] === "gate").length).toBe(before + 1);
    expect(security.overview.mock.calls.filter((c) => c[0] === "archive").length).toBe(1);
  });
});

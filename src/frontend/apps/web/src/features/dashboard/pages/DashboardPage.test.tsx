import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import type { ReactNode } from "react";
import { beforeEach, describe, expect, it, vi } from "vitest";

/**
 * Dashboard composition: every section requests 15 rows from the server and pages independently; a failing section never
 * blanks the others; filters return a section to page 1; there are no per-section Refresh buttons; security monitoring is
 * requested only for sessions with the security.administer capability (the server enforces the same rule).
 */

const page = <T,>(items: T[], extra: Record<string, unknown> = {}) => ({ items, totalCount: items.length, pageStart: items.length ? 1 : 0, pageSize: 15, nextToken: null, previousToken: null, ...extra });
const bucket = (date: string) => ({ bucketStartDate: date, bucketDayCount: 1 });

const api = {
  dashboard: {
    recordStatistics: vi.fn(),
    uploadStatistics: vi.fn(),
    uploads: vi.fn(),
    activity: vi.fn(),
    forms: vi.fn(),
  },
  records: { list: vi.fn() },
  security: { incidentSummary: vi.fn(), incidents: vi.fn() },
};
let capabilities: string[] = [];

vi.mock("@enterpriseforms/app-runtime", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@enterpriseforms/app-runtime")>()),
  useApi: () => api,
  useSession: () => ({ session: { displayName: "Olivia Owner", organizationDisplayName: "Acme" }, hasCapability: (capability: string) => capabilities.includes(capability) }),
}));

vi.mock("@tanstack/react-router", () => ({
  Link: ({ children, to, className }: { children: ReactNode; to: string; className?: string }) => (
    <a href={to} className={className}>
      {children}
    </a>
  ),
}));

const { DashboardPage } = await import("./DashboardPage");

function renderDashboard() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <DashboardPage />
    </QueryClientProvider>,
  );
}

const upload = (n: number) => ({ uploadSessionId: `u-${n}`, formRecordId: `r-${n}`, originalFileName: `file-${n}.pdf`, sessionState: "Committed", statusGroup: "Completed", sessionCreatedOn: "2026-10-30T10:00:00Z", declaredSizeBytes: 2048, failureReasonCode: null });

beforeEach(() => {
  vi.clearAllMocks();
  capabilities = ["records.discover"];
  api.dashboard.recordStatistics.mockResolvedValue({
    result: { accessibleRecordCount: 4, draftRecordCount: 3, submittedRecordCount: 1, archivedRecordCount: 0, otherStatusRecordCount: 0, unavailableRecordCount: 0, periodDays: 30, timeZoneId: "Etc/UTC", activity: [{ ...bucket("2026-10-30"), createdRecordCount: 2, submittedRecordCount: 1 }], authorizationRevision: 3, scopeStatement: "scope" },
  });
  api.dashboard.uploadStatistics.mockResolvedValue({ result: { periodDays: 30, timeZoneId: "Etc/UTC", totalUploadCount: 2, inProgressUploadCount: 0, completedUploadCount: 1, failedUploadCount: 1, cancelledUploadCount: 0, activity: [{ ...bucket("2026-10-30"), inProgressUploadCount: 0, completedUploadCount: 1, failedUploadCount: 1, cancelledUploadCount: 0 }] } });
  api.dashboard.uploads.mockImplementation((query: { continuationToken?: string }) =>
    Promise.resolve({ result: query.continuationToken ? page([upload(16)], { totalCount: 16, pageStart: 16, previousToken: "prev" }) : page(Array.from({ length: 15 }, (_, i) => upload(i + 1)), { totalCount: 16, nextToken: "uploads-page-2" }) }),
  );
  api.dashboard.activity.mockResolvedValue({ result: page([{ auditEventId: "a1", eventOccurredOn: "2026-10-30T09:00:00Z", eventCategory: "Record", eventAction: "RECORD_CREATED", eventOutcome: "Success", actorUserId: null, actorDisplayName: null, eventDescription: "record created", correlationIdentifier: "c", fileAttachmentId: null, targetEntityId: null }], { availableCategories: ["Record"] }) });
  api.dashboard.forms.mockResolvedValue({ result: page([{ formDefinitionId: "f1", formDefinitionName: "Case intake", formDefinitionDescription: "", lifecycleStatus: "Active", currentVersionNumber: 2, currentFormVersionId: "v", hasDraft: false, canDesign: false, canCreateRecord: true, modifiedOn: "2026-10-30T09:00:00Z" }]) });
  api.records.list.mockResolvedValue({ result: { items: [{ formRecordId: "r1", formDefinitionId: "f1", formDefinitionName: "Case intake", recordStatus: "Draft", createdOn: "2026-10-30T09:00:00Z", modifiedOn: "2026-10-30T09:00:00Z", parentFormRecordId: null, projectionVerified: false, recordReference: "REC-0000AAAA", createdByCurrentUser: true, canEdit: true }], authorizationRevision: 3, continuationToken: null, totalCount: 1, pageStart: 1, pageSize: 15, previousToken: null, availableForms: [], unavailableRecordCount: 0, unavailableRecordIds: [], sortBy: "modifiedOn", sortDirection: "desc" } });
  api.security.incidentSummary.mockResolvedValue({ result: { openIncidentCount: 1, investigatingIncidentCount: 0, resolvedIncidentCount: 0, falsePositiveIncidentCount: 0, confirmedIntegrityFailureCount: 1, criticalIncidentCount: 1, highIncidentCount: 0, mediumIncidentCount: 0, lowIncidentCount: 0, recentIncidentCount: 1, periodDays: 30, timeZoneId: "Etc/UTC", detections: [{ ...bucket("2026-10-30"), integrityIncidentCount: 1, operationalIncidentCount: 0 }], coverageStatement: "Incidents are recorded when the application observes a verification problem." } });
  api.security.incidents.mockResolvedValue({ result: page([]) });
});

describe("DashboardPage", () => {
  it("asks every section for exactly 15 rows and shows KPIs that match the server's data", async () => {
    renderDashboard();
    await screen.findByRole("table", { name: "My records list" });
    expect(api.records.list).toHaveBeenCalledWith(expect.objectContaining({ pageSize: 15, sortBy: "modifiedOn", sortDirection: "desc" }), expect.anything());
    for (const call of [api.dashboard.uploads, api.dashboard.forms]) {
      expect(call).toHaveBeenCalledWith(expect.objectContaining({ pageSize: 15 }), expect.anything());
    }
    expect(api.dashboard.activity).toHaveBeenCalledWith(expect.objectContaining({ pageSize: 15 }), expect.anything());
    const accessible = await screen.findByText("Accessible records");
    expect(accessible.closest("div")).toHaveTextContent("4");
    expect(screen.getByText("Failed uploads").closest("div")).toHaveTextContent("1");
    // The donut's legend total equals the KPI total (3 draft + 1 submitted).
    const legend = screen.getByRole("list", { name: "Records by status legend" });
    expect(within(legend).getAllByRole("listitem").map((row) => row.textContent)).toEqual(["Draft375%", "Submitted125%", "Archived00%"]);
  });

  it("has no per-section Refresh buttons", async () => {
    renderDashboard();
    await screen.findByRole("table", { name: "My records list" });
    expect(screen.queryByRole("button", { name: /refresh/i })).not.toBeInTheDocument();
  });

  it("keeps the other sections working when one section fails", async () => {
    api.dashboard.uploads.mockRejectedValue(new Error("upload list unavailable"));
    renderDashboard();
    await screen.findByRole("table", { name: "My records list" });
    await screen.findByRole("table", { name: "Permitted forms list" });
    await screen.findByRole("table", { name: "Recent personal activity list" });
    const uploads = screen.getByRole("region", { name: "Upload activity" });
    await within(uploads).findByText("upload list unavailable");
  });

  it("pages a section on its own and returns to page 1 when its filter changes", async () => {
    renderDashboard();
    const uploads = await screen.findByRole("region", { name: "Upload activity" });
    const pager = await within(uploads).findByRole("navigation", { name: "Upload activity pages" });
    expect(pager).toHaveTextContent("Showing 1–15 of 16 uploads");
    fireEvent.click(within(pager).getByRole("button", { name: "Next" }));
    await waitFor(() => expect(api.dashboard.uploads).toHaveBeenLastCalledWith(expect.objectContaining({ continuationToken: "uploads-page-2" }), expect.anything()));
    await waitFor(() => expect(within(uploads).getByRole("navigation", { name: "Upload activity pages" })).toHaveTextContent("Page 2 of 2"));
    // Other sections did not move.
    expect(api.records.list).not.toHaveBeenCalledWith(expect.objectContaining({ continuationToken: expect.anything() }), expect.anything());

    fireEvent.change(within(uploads).getByLabelText("File name"), { target: { value: "contract" } });
    // One request after the typing pause (debounced), for page 1 of the filtered list.
    await waitFor(() => expect(api.dashboard.uploads).toHaveBeenLastCalledWith(expect.objectContaining({ q: "contract" }), expect.anything()), { timeout: 2000 });
    expect(api.dashboard.uploads.mock.lastCall?.[0]).not.toHaveProperty("continuationToken");
    expect(api.dashboard.uploads.mock.calls.filter(([query]) => (query as { q?: string }).q !== undefined)).toHaveLength(1);
  });

  it("shows security monitoring only to sessions with the security.administer capability", async () => {
    renderDashboard();
    await screen.findByRole("table", { name: "My records list" });
    expect(screen.queryByRole("heading", { name: "Security monitoring" })).not.toBeInTheDocument();
    expect(api.security.incidentSummary).not.toHaveBeenCalled();
    expect(api.security.incidents).not.toHaveBeenCalled();
  });

  it("does not present an empty incident list as proof of security", async () => {
    capabilities = ["records.discover", "security.administer"];
    renderDashboard();
    expect(await screen.findByRole("heading", { name: "Security monitoring" })).toBeInTheDocument();
    expect(await screen.findByText("No incidents recorded")).toBeInTheDocument();
    expect(screen.getByText(/does not mean every record has been verified/)).toBeInTheDocument();
    expect(api.security.incidents).toHaveBeenCalledWith(expect.objectContaining({ pageSize: 15 }), expect.anything());
  });
});

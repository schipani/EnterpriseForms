import type { OwnerRecoveryNoticeResponse } from "@enterpriseforms/api-client";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";

const api = { organization: { ownerRecoveryNotice: vi.fn(), objectToOwnerRecovery: vi.fn() } };

vi.mock("@enterpriseforms/app-runtime", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@enterpriseforms/app-runtime")>()),
  useApi: () => api,
}));
vi.mock("@tanstack/react-router", () => ({ Link: ({ to, children }: { to: string; children: React.ReactNode }) => <a href={to}>{children}</a> }));

const { OwnerRecoveryNotice } = await import("./OwnerRecoveryNotice");

const CASE_ID = "01a194c3-cbb0-70ef-8e74-31d8e5d30001";
const withCase = (canObject: boolean): OwnerRecoveryNoticeResponse => ({
  canObject,
  case: {
    caseId: CASE_ID,
    incidentReference: "INC-4411",
    justification: "Both owners left the company.",
    openedOn: "2026-11-01T10:00:00Z",
    waitingPeriodEndsOn: "2026-11-04T10:00:00Z",
    approvalsRecorded: 2,
    approvalsRequired: 3,
    subjects: [
      { role: "Replacement", authorityUserId: "u-1", displayName: "Robin Replacement", isNewIdentity: true, unavailabilityReason: null },
      { role: "DeclaredUnavailable", authorityUserId: "u-2", displayName: "Lee Leaver", isNewIdentity: false, unavailabilityReason: "LeftOrganization" },
    ],
  },
});

function renderNotice() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <OwnerRecoveryNotice />
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  vi.clearAllMocks();
  vi.stubGlobal("ResizeObserver", class { observe() {} unobserve() {} disconnect() {} });
});

describe("OwnerRecoveryNotice", () => {
  it("renders nothing when the server returns no case", async () => {
    api.organization.ownerRecoveryNotice.mockResolvedValue({ result: { case: null, canObject: false } });
    const { container } = renderNotice();
    await waitFor(() => expect(api.organization.ownerRecoveryNotice).toHaveBeenCalled());
    expect(container).toBeEmptyDOMElement(); // no notice, no spacing wrapper, no gap
  });

  it("shows the case with names only and no objection form when the caller cannot object", async () => {
    api.organization.ownerRecoveryNotice.mockResolvedValue({ result: withCase(false) });
    renderNotice();
    expect(await screen.findByRole("heading", { name: "Owner recovery case in progress" })).toBeInTheDocument();
    // 24px of space below the notice (mb-6) so it never touches the overview cards; it exists only while the notice is shown.
    expect(screen.getByLabelText("Owner recovery case in progress")).toHaveClass("mb-6");
    expect(screen.getByText("INC-4411")).toBeInTheDocument();
    expect(screen.getByText("Both owners left the company.")).toBeInTheDocument();
    expect(screen.getByText("2 of 3")).toBeInTheDocument();
    expect(screen.getByText("Robin Replacement")).toBeInTheDocument();
    expect(screen.getByText("Lee Leaver")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Object to this recovery" })).not.toBeInTheDocument();
  });

  it("confirms the objection only after the API answers", async () => {
    api.organization.ownerRecoveryNotice.mockResolvedValue({ result: withCase(true) });
    let resolve: (value: unknown) => void = () => undefined;
    api.organization.objectToOwnerRecovery.mockImplementation(() => new Promise((r) => (resolve = r)));
    const user = userEvent.setup();
    renderNotice();
    await user.click(await screen.findByRole("button", { name: "Object to this recovery" }));
    const submit = screen.getByRole("button", { name: "Record objection" });
    expect(submit).toBeDisabled();
    await user.type(screen.getByRole("textbox", { name: /Why are you objecting/ }), "We did not request this.");
    expect(submit).toBeDisabled();
    await user.click(screen.getByRole("checkbox", { name: /development-only step-up/ }));
    await user.click(submit);
    await waitFor(() => expect(api.organization.objectToOwnerRecovery).toHaveBeenCalledWith(CASE_ID, { reason: "We did not request this.", useDevelopmentStepUp: true }));
    expect(screen.queryByText(/Your objection was recorded/)).not.toBeInTheDocument();
    resolve({ result: { caseId: CASE_ID, state: "Objected", objectedOn: "2026-11-01T11:00:00Z" } });
    expect(await screen.findByText(/Your objection was recorded. The recovery is blocked./)).toBeInTheDocument();
  });

  it("keeps the confirmation when the refreshed notice no longer has an open case", async () => {
    api.organization.ownerRecoveryNotice.mockResolvedValueOnce({ result: withCase(true) }).mockResolvedValue({ result: { case: null, canObject: false } });
    api.organization.objectToOwnerRecovery.mockResolvedValue({ result: { caseId: CASE_ID, state: "Objected", objectedOn: "2026-11-01T11:00:00Z" } });
    const user = userEvent.setup();
    renderNotice();
    await user.click(await screen.findByRole("button", { name: "Object to this recovery" }));
    await user.type(screen.getByRole("textbox", { name: /Why are you objecting/ }), "We did not request this.");
    await user.click(screen.getByRole("checkbox", { name: /development-only step-up/ }));
    await user.click(screen.getByRole("button", { name: "Record objection" }));
    await waitFor(() => expect(api.organization.ownerRecoveryNotice).toHaveBeenCalledTimes(2));
    expect(await screen.findByText(/Your objection was recorded. The recovery is blocked. \(Case INC-4411\.\)/)).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Object to this recovery" })).not.toBeInTheDocument();
  });

  it("shows the API error and no confirmation when the objection fails", async () => {
    api.organization.ownerRecoveryNotice.mockResolvedValue({ result: withCase(true) });
    api.organization.objectToOwnerRecovery.mockRejectedValue(new Error("refused"));
    const user = userEvent.setup();
    renderNotice();
    await user.click(await screen.findByRole("button", { name: "Object to this recovery" }));
    await user.type(screen.getByRole("textbox", { name: /Why are you objecting/ }), "We did not request this.");
    await user.click(screen.getByRole("checkbox", { name: /development-only step-up/ }));
    await user.click(screen.getByRole("button", { name: "Record objection" }));
    expect(await screen.findByRole("alert")).toBeInTheDocument();
    expect(screen.queryByText(/Your objection was recorded/)).not.toBeInTheDocument();
  });
});

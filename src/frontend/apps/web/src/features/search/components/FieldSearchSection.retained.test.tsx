import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";

/**
 * C-188 (D-H9, D-H18): a field that the latest published version no longer places is shown "No longer in the form"; a security
 * administrator approves searching its latest retained values with the explicit RetainedField scope, only where the environment
 * enables it; an approved retained field says so. Fields of the current version keep the normal approval (no scope sent).
 */
const forms = { fieldSearch: vi.fn(), approveFieldSearch: vi.fn(), withdrawFieldSearch: vi.fn() };

vi.mock("@enterpriseforms/app-runtime", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@enterpriseforms/app-runtime")>()),
  useApi: () => ({ forms }),
}));

const { FieldSearchSection } = await import("./FieldSearchSection");

const formId = "0199a2b3-c4d5-7e6f-8123-456789abc001";
const phone = "0199a2b3-c4d5-7e6f-8123-456789abc002";
const first = "0199a2b3-c4d5-7e6f-8123-456789abc003";

function status(overrides: Record<string, unknown>) {
  return {
    fieldDefinitionId: phone,
    fieldLabel: "Phone",
    fieldDataType: "String",
    fieldClassification: "Internal",
    protectionMode: "Standard",
    eligibility: "Eligible",
    approvalState: "Invalidated",
    invalidationReason: "FORM_VERSION_CHANGED",
    approvedOperators: [],
    permittedOperators: ["equals", "startsWith"],
    inCurrentVersion: false,
    lastPlacedVersionNumber: 1,
    approvalScope: "CurrentVersion",
    ...overrides,
  };
}

function configuration(retainedEnabled: boolean, fields: unknown[]) {
  return { formDefinitionId: formId, plaintextSearchPermitted: true, searchPolicyRevision: 4, canApprove: true, approvalOperational: true, approvalUnavailableReason: "", registeredFormVersionNumber: 2, fields, fieldSearchers: null, retainedFieldApprovalsEnabled: retainedEnabled };
}

function renderSection(fieldDefinitionId: string) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <FieldSearchSection formDefinitionId={formId} fieldDefinitionId={fieldDefinitionId} />
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  vi.clearAllMocks();
  forms.approveFieldSearch.mockResolvedValue({ result: configuration(true, []) });
});

describe("FieldSearchSection for a field that is no longer in the form (C-188)", () => {
  it("marks the field, explains that only the latest retained value is searched, and approves with the RetainedField scope", async () => {
    forms.fieldSearch.mockResolvedValue({ result: configuration(true, [status({})]) });
    renderSection(phone);
    expect(await screen.findByText("No longer in the form")).toBeInTheDocument();
    expect(screen.getByText(/Last included in published version 1/)).toBeInTheDocument();
    expect(screen.getByText(/never earlier revisions/)).toBeInTheDocument();
    expect(screen.getByRole("group", { name: "Approve searching retained values" })).toBeInTheDocument();

    const user = userEvent.setup();
    await user.type(screen.getByLabelText(/Approval reference/), "GOV-C188");
    await user.click(screen.getByRole("checkbox", { name: /development-only step-up/ }));
    await user.click(screen.getByRole("button", { name: "Approve retained values" }));
    await waitFor(() => expect(forms.approveFieldSearch).toHaveBeenCalledTimes(1));
    expect(forms.approveFieldSearch).toHaveBeenCalledWith(formId, phone, { operators: ["equals", "startsWith"], approvalReference: "GOV-C188", useDevelopmentStepUp: true, approvalScope: "RetainedField" });
  });

  it("offers no retained approval where the environment disables it, and says why", async () => {
    forms.fieldSearch.mockResolvedValue({ result: configuration(false, [status({})]) });
    renderSection(phone);
    expect(await screen.findByText("Searching removed fields is not enabled here")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Approve retained values" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Approve for search" })).not.toBeInTheDocument();
  });

  it("describes an active retained approval as approved for its latest retained values", async () => {
    forms.fieldSearch.mockResolvedValue({ result: configuration(true, [status({ approvalState: "Active", invalidationReason: null, approvedOperators: ["equals"], approvalScope: "RetainedField" })]) });
    renderSection(phone);
    expect(await screen.findByText("Approved for its latest retained values: Is exactly.")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Withdraw search approval" })).toBeInTheDocument();
  });

  it("keeps the normal approval for a field of the current version (no scope sent, no retained notice)", async () => {
    forms.fieldSearch.mockResolvedValue({ result: configuration(true, [status({ fieldDefinitionId: first, fieldLabel: "First name", approvalState: "None", invalidationReason: null, inCurrentVersion: true, lastPlacedVersionNumber: 2, approvalScope: null })]) });
    renderSection(first);
    expect(await screen.findByRole("group", { name: "Approve for search" })).toBeInTheDocument();
    expect(screen.queryByText("No longer in the form")).not.toBeInTheDocument();
    const user = userEvent.setup();
    await user.type(screen.getByLabelText(/Approval reference/), "GOV-1");
    await user.click(screen.getByRole("checkbox", { name: /development-only step-up/ }));
    await user.click(screen.getByRole("button", { name: "Approve for search" }));
    await waitFor(() => expect(forms.approveFieldSearch).toHaveBeenCalledTimes(1));
    expect(forms.approveFieldSearch.mock.calls[0]![2]).toMatchObject({ approvalScope: null });
  });
});

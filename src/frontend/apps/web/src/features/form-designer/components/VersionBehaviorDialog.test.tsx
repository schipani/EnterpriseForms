import { OperationError } from "@enterpriseforms/work-queue";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { beforeEach, describe, expect, it, vi } from "vitest";

/**
 * Phase C9 record-layout dialog: three plain-language choices with the current one marked, an impact summary for the
 * chosen target counted only over records the designer can see, a reason checked like the server (3–500, no control
 * characters), the revision the dialog was opened with, and a conflict that keeps the dialog open and refreshes the form.
 */

const api = { forms: { changeVersionBehavior: vi.fn(), versionBehaviorImpact: vi.fn() } };

vi.mock("@enterpriseforms/app-runtime", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@enterpriseforms/app-runtime")>()),
  useApi: () => api,
}));

vi.mock("@tanstack/react-router", () => ({
  Link: ({ children, to }: { children: ReactNode; to: string }) => <a href={to}>{children}</a>,
}));

const { VersionBehaviorDialog, versionBehaviorReasonProblem } = await import("./VersionBehaviorDialog");

const target = { formDefinitionId: "f1", formDefinitionName: "Case intake", formDefinitionRevision: 4, versionBehavior: "Latest" as const };

const impact = {
  formDefinitionId: "f1",
  currentVersionBehavior: "Latest",
  targetVersionBehavior: "Historical",
  currentVersionNumber: 3,
  visibleRecordCount: 5,
  viewLayoutChangeCount: 4,
  editLayoutChangeCount: 3,
  unavailableRecordCount: 1,
  recordsByCreatedVersion: [
    { versionNumber: 1, recordCount: 4 },
    { versionNumber: 3, recordCount: 1 },
  ],
  formDefinitionRevision: 4,
  authorizationRevision: 9,
  countsAreApproximate: true,
  scopeStatement: "Counts cover only records you can discover.",
};

function renderDialog(onChanged = vi.fn(), onClose = vi.fn()) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  const invalidate = vi.spyOn(client, "invalidateQueries");
  render(
    <QueryClientProvider client={client}>
      <VersionBehaviorDialog target={target} onClose={onClose} onChanged={onChanged} />
    </QueryClientProvider>,
  );
  return { onChanged, onClose, invalidate };
}

beforeEach(() => {
  vi.clearAllMocks();
  api.forms.versionBehaviorImpact.mockResolvedValue({ result: impact });
});

describe("versionBehaviorReasonProblem", () => {
  it("mirrors the server rule", () => {
    expect(versionBehaviorReasonProblem(" ab ")).toMatch(/at least 3/);
    expect(versionBehaviorReasonProblem("x".repeat(501))).toMatch(/at most 500/);
    expect(versionBehaviorReasonProblem("two\nlines")).toMatch(/control characters/);
    expect(versionBehaviorReasonProblem("  keep records on their version ")).toBeNull();
  });
});

describe("VersionBehaviorDialog", () => {
  it("marks the current setting, explains every choice and asks for nothing until another is chosen", () => {
    renderDialog();
    expect(screen.getByRole("radio", { name: /Current published version \(current setting\)/ })).toBeChecked();
    expect(screen.getByRole("radio", { name: /Version a record was created with/ })).not.toBeChecked();
    expect(screen.getByText(/shown with the version it was created with, and edited with the current published version/)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Change record layout" })).toBeDisabled();
    expect(api.forms.versionBehaviorImpact).not.toHaveBeenCalled();
  });

  it("shows the approximate impact over visible records for the chosen target", async () => {
    renderDialog();
    fireEvent.click(screen.getByRole("radio", { name: /Version a record was created with/ }));
    expect(await screen.findByText("5 records of this form that you can see.")).toBeInTheDocument();
    expect(screen.getByText("4 records would be shown with a different layout.")).toBeInTheDocument();
    expect(screen.getByText("3 records would be edited with a different layout.")).toBeInTheDocument();
    expect(screen.getByText("Created with v1: 4 records")).toBeInTheDocument();
    expect(screen.getByText(/1 record you can access couldn't be described/)).toBeInTheDocument();
    expect(screen.getByText("Counts cover only records you can discover.")).toBeInTheDocument();
    expect(api.forms.versionBehaviorImpact).toHaveBeenCalledWith("f1", "Historical", expect.anything());
  });

  it("refuses a short reason without calling the server", async () => {
    renderDialog();
    fireEvent.click(screen.getByRole("radio", { name: /Show original, edit with current/ }));
    fireEvent.change(screen.getByLabelText(/Reason for the change/), { target: { value: "ab" } });
    fireEvent.click(screen.getByRole("button", { name: "Change record layout" }));
    expect(await screen.findByText(/at least 3 characters/)).toBeInTheDocument();
    expect(api.forms.changeVersionBehavior).not.toHaveBeenCalled();
  });

  it("sends the behaviour, the opened revision and the trimmed reason, then refreshes forms and records", async () => {
    api.forms.changeVersionBehavior.mockResolvedValue({ result: { summary: { versionBehavior: "Hybrid" } } });
    const { onChanged, onClose, invalidate } = renderDialog();
    fireEvent.click(screen.getByRole("radio", { name: /Show original, edit with current/ }));
    fireEvent.change(screen.getByLabelText(/Reason for the change/), { target: { value: "  edit with the newest fields " } });
    fireEvent.click(screen.getByRole("button", { name: "Change record layout" }));
    await waitFor(() => expect(onChanged).toHaveBeenCalledWith("Hybrid"));
    expect(api.forms.changeVersionBehavior).toHaveBeenCalledWith("f1", { versionBehavior: "Hybrid", expectedFormDefinitionRevision: 4, changeReason: "edit with the newest fields" });
    expect(onClose).toHaveBeenCalled();
    await waitFor(() => expect(invalidate).toHaveBeenCalledWith({ queryKey: ["records"] }));
    expect(invalidate).toHaveBeenCalledWith({ queryKey: ["forms"] });
  });

  it("keeps the dialog open and refreshes the form after a conflicting change", async () => {
    api.forms.changeVersionBehavior.mockRejectedValue(new OperationError({ type: "failed", operationId: "op", status: 409, errorCode: "FORM_DEFINITION_MODIFIED_CONCURRENTLY", retryable: false, problem: { title: "conflict", detail: "the form's settings were changed by someone else; reload and retry" } }));
    const { onChanged, onClose, invalidate } = renderDialog();
    fireEvent.click(screen.getByRole("radio", { name: /Version a record was created with/ }));
    fireEvent.change(screen.getByLabelText(/Reason for the change/), { target: { value: "keep records on their version" } });
    fireEvent.click(screen.getByRole("button", { name: "Change record layout" }));
    await waitFor(() => expect(invalidate).toHaveBeenCalledWith({ queryKey: ["forms"] }));
    expect(onChanged).not.toHaveBeenCalled();
    expect(onClose).not.toHaveBeenCalled();
  });
});

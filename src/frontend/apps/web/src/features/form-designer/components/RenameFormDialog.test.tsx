import { OperationError } from "@enterpriseforms/work-queue";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { beforeEach, describe, expect, it, vi } from "vitest";

/**
 * Rename dialog: shows the current name, validates like the server (empty, control characters), sends the revision the
 * dialog was opened with, reports success to the caller and refreshes every list that shows form names, and keeps the
 * dialog open with the server's explanation on a conflict.
 */

const api = { forms: { rename: vi.fn() } };

vi.mock("@enterpriseforms/app-runtime", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@enterpriseforms/app-runtime")>()),
  useApi: () => api,
}));

vi.mock("@tanstack/react-router", () => ({
  Link: ({ children, to }: { children: ReactNode; to: string }) => <a href={to}>{children}</a>,
}));

const { RenameFormDialog, formNameProblem } = await import("./RenameFormDialog");

const target = { formDefinitionId: "f1", formDefinitionName: "Case intake", formDefinitionRevision: 4 };

function renderDialog(onRenamed = vi.fn(), onClose = vi.fn()) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  const invalidate = vi.spyOn(client, "invalidateQueries");
  render(
    <QueryClientProvider client={client}>
      <RenameFormDialog target={target} onClose={onClose} onRenamed={onRenamed} />
    </QueryClientProvider>,
  );
  return { onRenamed, onClose, invalidate };
}

beforeEach(() => {
  vi.clearAllMocks();
});

describe("formNameProblem", () => {
  it("mirrors the server rule", () => {
    expect(formNameProblem("  ")).toMatch(/Enter a form name/);
    expect(formNameProblem("a\tb")).toMatch(/control characters/);
    expect(formNameProblem("x".repeat(201))).toMatch(/at most 200/);
    expect(formNameProblem("  Witness statement ")).toBeNull();
  });
});

describe("RenameFormDialog", () => {
  it("shows the current name and keeps Save disabled until the name changes", () => {
    renderDialog();
    expect(screen.getByText("Case intake", { selector: "span" })).toBeInTheDocument();
    expect(screen.getByLabelText(/New name/)).toHaveValue("Case intake");
    expect(screen.getByRole("button", { name: "Save name" })).toBeDisabled();
  });

  it("refuses an empty name without calling the server", () => {
    renderDialog();
    fireEvent.change(screen.getByLabelText(/New name/), { target: { value: "   " } });
    fireEvent.click(screen.getByRole("button", { name: "Save name" }));
    expect(screen.getByText("Enter a form name.")).toBeInTheDocument();
    expect(api.forms.rename).not.toHaveBeenCalled();
  });

  it("saves the trimmed name with the opened revision and refreshes form names everywhere", async () => {
    api.forms.rename.mockResolvedValue({ result: { summary: { formDefinitionName: "Witness intake" } } });
    const { onRenamed, onClose, invalidate } = renderDialog();
    fireEvent.change(screen.getByLabelText(/New name/), { target: { value: "  Witness intake " } });
    fireEvent.click(screen.getByRole("button", { name: "Save name" }));
    await waitFor(() => expect(onRenamed).toHaveBeenCalledWith("Witness intake"));
    expect(api.forms.rename).toHaveBeenCalledWith("f1", { formDefinitionName: "Witness intake", expectedFormDefinitionRevision: 4 });
    expect(onClose).toHaveBeenCalled();
    await waitFor(() => expect(invalidate).toHaveBeenCalledWith({ queryKey: ["records"] }));
    expect(invalidate).toHaveBeenCalledWith({ queryKey: ["forms"] });
    expect(invalidate).toHaveBeenCalledWith({ queryKey: ["dashboard"] });
  });

  it("keeps the dialog open and explains a conflicting rename", async () => {
    api.forms.rename.mockRejectedValue(new OperationError({ type: "failed", operationId: "op", status: 409, errorCode: "FORM_DEFINITION_MODIFIED_CONCURRENTLY", retryable: false, problem: { title: "conflict", detail: "the form was renamed by someone else; reload and retry" } }));
    const { onRenamed, onClose, invalidate } = renderDialog();
    fireEvent.change(screen.getByLabelText(/New name/), { target: { value: "Other" } });
    fireEvent.click(screen.getByRole("button", { name: "Save name" }));
    expect(await screen.findByText("Someone else renamed this form")).toBeInTheDocument();
    expect(onRenamed).not.toHaveBeenCalled();
    expect(onClose).not.toHaveBeenCalled();
    await waitFor(() => expect(invalidate).toHaveBeenCalledWith({ queryKey: ["forms"] }));
  });
});

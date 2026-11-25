import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

/**
 * C-188 BF-1 designer notice: nothing while the current version is registered (or nothing is published); a warning with
 * "Retry registration" while a registration is pending (field search on the form is unavailable); a warning without the
 * action for a version that can only be registered by publishing again; the recovery outcome replaces the warning.
 */

const api = { forms: { authorityRegistration: vi.fn(), recoverAuthorityRegistration: vi.fn() } };

vi.mock("@enterpriseforms/app-runtime", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@enterpriseforms/app-runtime")>()),
  useApi: () => api,
}));

const { AuthorityRegistrationNotice } = await import("./AuthorityRegistrationNotice");

const status = (registrationState: string, extra: Record<string, unknown> = {}) => ({
  result: { formDefinitionId: "f1", currentVersionNumber: 2, registeredVersionNumber: 1, pendingVersionNumber: registrationState === "Pending" ? 2 : null, registrationState, outcome: null, failureReason: null, ...extra },
});

function renderNotice() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <AuthorityRegistrationNotice formDefinitionId="f1" />
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  vi.clearAllMocks();
});

describe("AuthorityRegistrationNotice", () => {
  it("shows nothing when the current version is registered or nothing is published", async () => {
    api.forms.authorityRegistration.mockResolvedValueOnce(status("Registered"));
    const { container } = renderNotice();
    await waitFor(() => expect(api.forms.authorityRegistration).toHaveBeenCalledWith("f1", expect.anything()));
    expect(container).toBeEmptyDOMElement();
  });

  it("explains a pending registration and completes it without publishing again", async () => {
    api.forms.authorityRegistration.mockResolvedValueOnce(status("Pending"));
    api.forms.recoverAuthorityRegistration.mockResolvedValueOnce(status("Registered", { registeredVersionNumber: 2, outcome: "Registered" }));
    renderNotice();
    expect(await screen.findByText("This version is not yet registered with the Authority")).toBeInTheDocument();
    expect(screen.getByText(/Field search on this form is unavailable/)).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Retry registration" }));
    expect(await screen.findByText("Registration complete")).toBeInTheDocument();
    expect(screen.getByText(/now registered/)).toBeInTheDocument();
    expect(api.forms.recoverAuthorityRegistration).toHaveBeenCalledWith("f1");
  });

  it("reports a refused recovery and keeps the warning", async () => {
    api.forms.authorityRegistration.mockResolvedValueOnce(status("Pending"));
    api.forms.recoverAuthorityRegistration.mockResolvedValueOnce(status("Pending", { outcome: "Refused", failureReason: "ABANDONMENT_ATTESTATION_UNAVAILABLE" }));
    renderNotice();
    fireEvent.click(await screen.findByRole("button", { name: "Retry registration" }));
    expect(await screen.findByText(/refused the registration \(ABANDONMENT_ATTESTATION_UNAVAILABLE\)/)).toBeInTheDocument();
    expect(screen.getByText("This version is not yet registered with the Authority")).toBeInTheDocument();
  });

  it("reports a blocked recovery without offering a retry (C-191)", async () => {
    api.forms.authorityRegistration.mockResolvedValueOnce(status("Pending"));
    api.forms.recoverAuthorityRegistration.mockResolvedValueOnce(status("RecoveryBlocked", { pendingVersionNumber: 2, outcome: "RecoveryBlocked", failureReason: "STAGED_VERSION_MISMATCH" }));
    renderNotice();
    fireEvent.click(await screen.findByRole("button", { name: "Retry registration" }));
    expect(await screen.findByText("Registration of this version is blocked")).toBeInTheDocument();
    expect(screen.getByText(/separately authorized repair/)).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Retry registration" })).not.toBeInTheDocument();
  });

  it("shows the blocked state on load", async () => {
    api.forms.authorityRegistration.mockResolvedValueOnce(status("RecoveryBlocked", { pendingVersionNumber: 2 }));
    renderNotice();
    expect(await screen.findByText("Registration of this version is blocked")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Retry registration" })).not.toBeInTheDocument();
  });

  it("offers no retry for a version that can only be registered by publishing again", async () => {
    api.forms.authorityRegistration.mockResolvedValueOnce(status("NotRegistered"));
    renderNotice();
    expect(await screen.findByText("This version was never registered with the Authority")).toBeInTheDocument();
    expect(screen.getByText(/Publish the form again/)).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Retry registration" })).not.toBeInTheDocument();
  });
});

import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { OrganizationMemberResponse } from "@enterpriseforms/api-client";

const reasonCodes = {
  appointment: [
    { reasonCode: "OVERSIGHT_ROLE", reasonLabel: "Oversight role" },
    { reasonCode: "COVER", reasonLabel: "Cover for absence" },
  ],
  removal: [{ reasonCode: "ROLE_ENDED", reasonLabel: "Role ended" }],
};
const api = {
  organization: {
    securityAuditorReasonCodes: vi.fn(),
    appointSecurityAuditor: vi.fn(),
    removeSecurityAuditor: vi.fn(),
  },
};
const refresh = vi.fn();

vi.mock("@enterpriseforms/app-runtime", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@enterpriseforms/app-runtime")>()),
  useApi: () => api,
  useSession: () => ({ refresh }),
}));

const { MemberChangeDialog } = await import("./MemberChangeDialog");

const member = {
  userId: "u-9",
  displayName: "Sam Person",
  identityIssuer: "https://idp.test",
  identitySubject: "sub-9",
  firstSeenOn: null,
  teams: [],
  isCurrentUser: false,
  lastSignedInOn: null,
  disabledOn: null,
  authorityRoles: [],
  signInStatus: "SignedIn",
  availableActions: [],
  membershipState: "Enabled",
} as OrganizationMemberResponse;

function renderDialog(action: "AppointSecurityAuditor" | "RemoveSecurityAuditor") {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <MemberChangeDialog change={{ kind: "member", member, action }} operational unavailableReason={null} onClose={vi.fn()} onDone={vi.fn()} />
    </QueryClientProvider>,
  );
}


describe("MemberChangeDialog: Security Auditor", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    api.organization.securityAuditorReasonCodes.mockResolvedValue({ result: reasonCodes, status: 200 });
    api.organization.appointSecurityAuditor.mockResolvedValue(undefined);
    api.organization.removeSecurityAuditor.mockResolvedValue(undefined);
  });

  it("requires a reason from the appointment list and sends only reasonCode (no reason, no note: D8-72)", async () => {
    const user = userEvent.setup();
    renderDialog("AppointSecurityAuditor");
    const dialog = await screen.findByRole("dialog", { name: /Make Sam Person a Security Auditor/ });
    const confirm = within(dialog).getByRole("button", { name: "Appoint" });
    await user.click(within(dialog).getByLabelText(/development-only step-up/i));
    expect(confirm).toBeDisabled(); // no reason chosen yet
    const select = within(dialog).getByLabelText(/Reason/) as HTMLSelectElement;
    expect(select.value).toBe("");
    await waitFor(() => expect(within(select).getAllByRole("option").map((o) => o.textContent)).toEqual(["Select a reason", "Oversight role", "Cover for absence"]));
    await user.selectOptions(select, "COVER");
    expect(confirm).toBeEnabled();
    await user.click(confirm);
    await waitFor(() => expect(api.organization.appointSecurityAuditor).toHaveBeenCalledTimes(1));
    const body = api.organization.appointSecurityAuditor.mock.calls[0]![0];
    expect(body).toEqual({ userId: "u-9", reasonCode: "COVER", useDevelopmentStepUp: true });
    expect(body).not.toHaveProperty("reason");
    expect(body).not.toHaveProperty("reasonNote");
  });

  it("offers no note field while notes are refused (D8-72)", async () => {
    renderDialog("AppointSecurityAuditor");
    const dialog = await screen.findByRole("dialog");
    expect(within(dialog).queryByLabelText(/Note/)).toBeNull();
    expect(within(dialog).queryByRole("textbox")).toBeNull();
  });

  it("uses the removal list for a removal and sends no reason key", async () => {
    const user = userEvent.setup();
    renderDialog("RemoveSecurityAuditor");
    const dialog = await screen.findByRole("dialog", { name: /Remove Sam Person as Security Auditor/ });
    const select = within(dialog).getByLabelText(/Reason/) as HTMLSelectElement;
    await waitFor(() => expect(within(select).getAllByRole("option").map((o) => o.textContent)).toEqual(["Select a reason", "Role ended"]));
    await user.selectOptions(select, "ROLE_ENDED");
    await user.click(within(dialog).getByLabelText(/development-only step-up/i));
    await user.click(within(dialog).getByRole("button", { name: "Remove auditor" }));
    await waitFor(() => expect(api.organization.removeSecurityAuditor).toHaveBeenCalledTimes(1));
    const [userId, body] = api.organization.removeSecurityAuditor.mock.calls[0]!;
    expect(userId).toBe("u-9");
    expect(body).toEqual({ reasonCode: "ROLE_ENDED", useDevelopmentStepUp: true });
    expect(body).not.toHaveProperty("reason");
  });
});

import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
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

const HELPER = "Kept with the command by the Authorization Authority. Never shown to Security Auditors or stored in this application's audit log.";

describe("MemberChangeDialog: Security Auditor", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    api.organization.securityAuditorReasonCodes.mockResolvedValue({ result: reasonCodes, status: 200 });
    api.organization.appointSecurityAuditor.mockResolvedValue(undefined);
    api.organization.removeSecurityAuditor.mockResolvedValue(undefined);
  });

  it("requires a reason from the appointment list and sends reasonCode and a trimmed note, never reason", async () => {
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
    fireEvent.change(within(dialog).getByLabelText(/Note \(optional\)/), { target: { value: "  covering Q4  " } });
    await user.click(confirm);
    await waitFor(() => expect(api.organization.appointSecurityAuditor).toHaveBeenCalledTimes(1));
    const body = api.organization.appointSecurityAuditor.mock.calls[0]![0];
    expect(body).toEqual({ userId: "u-9", reasonCode: "COVER", reasonNote: "covering Q4", useDevelopmentStepUp: true });
    expect(body).not.toHaveProperty("reason");
  });

  it("limits the note to 500 characters with a visible counter and the exact helper text; an empty note is sent as null", async () => {
    const user = userEvent.setup();
    renderDialog("AppointSecurityAuditor");
    const dialog = await screen.findByRole("dialog");
    const note = within(dialog).getByLabelText(/Note \(optional\)/) as HTMLTextAreaElement;
    expect(note).toHaveAttribute("maxlength", "500");
    expect(within(dialog).getByText(HELPER)).toBeInTheDocument();
    expect(dialog).toHaveTextContent("0 / 500");
    fireEvent.change(note, { target: { value: "x".repeat(600) } });
    expect(note.value).toHaveLength(500);
    expect(dialog).toHaveTextContent("500 / 500");
    fireEvent.change(note, { target: { value: "   " } });
    await user.click(within(dialog).getByLabelText(/development-only step-up/i));
    await waitFor(() => expect(within(dialog).getAllByRole("option").length).toBe(3));
    await user.selectOptions(within(dialog).getByLabelText(/Reason/), "OVERSIGHT_ROLE");
    await user.click(within(dialog).getByRole("button", { name: "Appoint" }));
    await waitFor(() => expect(api.organization.appointSecurityAuditor).toHaveBeenCalled());
    expect(api.organization.appointSecurityAuditor.mock.calls[0]![0]).toMatchObject({ reasonCode: "OVERSIGHT_ROLE", reasonNote: null });
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
    expect(body).toEqual({ reasonCode: "ROLE_ENDED", reasonNote: null, useDevelopmentStepUp: true });
    expect(body).not.toHaveProperty("reason");
  });
});

import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { ReactNode } from "react";
import { beforeEach, describe, expect, it, vi } from "vitest";

/**
 * People page (C8 M2): 15-row server pages; owners can add users (a request, never a member), change designers, disable
 * and re-enable, propose owner changes and approve another owner's proposal — each with a reason and the development
 * step-up, each through the Authority. Pending proposals and requests are shown with their state; nothing claims that a
 * requested person can sign in.
 */

const person = (n: number, extra: Record<string, unknown> = {}) => ({
  userId: `u-${n}`,
  displayName: `Person ${String(n).padStart(2, "0")}`,
  identityIssuer: "https://idp.test",
  identitySubject: `sub-${n}`,
  firstSeenOn: "2026-10-01T10:00:00Z",
  authorityRoles: [],
  teams: [],
  isCurrentUser: false,
  lastSignedInOn: null,
  signInStatus: "NotRecorded",
  availableActions: ["GrantDesigner", "ProposeOwnerGrant", "DisableMembership"],
  membershipState: "Enabled",
  disabledOn: null,
  ...extra,
});

const directory = (members: unknown[], extra: Record<string, unknown> = {}) => ({
  source: "Authorization Authority roster",
  members,
  rolesAvailable: true,
  rolesUnavailableReason: null,
  totalCount: members.length,
  pageStart: members.length ? 1 : 0,
  pageSize: 15,
  nextToken: null,
  previousToken: null,
  includesMembersWhoNeverSignedIn: true,
  coverageStatement: "Lists every identity the Authorization Authority has bound to this organization, enabled or disabled, with its organization roles.",
  securityAdministratorChangesOperational: false,
  securityAdministratorChangesUnavailableReason: null,
  canManageMembers: true,
  membershipCommandsOperational: true,
  membershipCommandsUnavailableReason: null,
  operatorApprovalOperational: true,
  organizationIdentityIssuer: "https://idp.test",
  ownerProposals: [
    {
      proposalId: "p-1",
      targetUserId: "u-9",
      targetDisplayName: "Person 09",
      proposedChange: "GrantOwner",
      proposedByUserId: "u-8",
      proposedByDisplayName: "Person 08",
      reason: "board",
      proposedOn: "2026-10-31T08:00:00Z",
      expiresOn: "2026-11-01T08:00:00Z",
      proposalState: "Pending",
      canApprove: true,
    },
  ],
  membershipRequests: [
    {
      requestId: "r-1",
      requestedUserId: "u-new",
      issuerUri: "https://idp.test",
      subjectIdentifier: "dev-nia",
      displayNameHint: "Nia Newcomer",
      initialRoleName: "Designer",
      requestedByUserId: "u-8",
      requestedByDisplayName: "Person 08",
      reason: "joins",
      requestedOn: "2026-10-31T08:00:00Z",
      expiresOn: "2026-11-07T08:00:00Z",
      requestState: "Pending",
      decidedOn: null,
      decisionReason: null,
    },
  ],
  ...extra,
});

const api = {
  organization: {
    members: vi.fn(),
    grantMemberRole: vi.fn(),
    revokeMemberRole: vi.fn(),
    proposeOwnerChange: vi.fn(),
    approveOwnerProposal: vi.fn(),
    disableMember: vi.fn(),
    enableMember: vi.fn(),
    requestMembership: vi.fn(),
    appointSecurityAdministrator: vi.fn(),
    removeSecurityAdministrator: vi.fn(),
  },
};
const refresh = vi.fn();

vi.mock("@enterpriseforms/app-runtime", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@enterpriseforms/app-runtime")>()),
  useApi: () => api,
  useSession: () => ({ hasCapability: (capability: string) => capability === "organization.manage", refresh }),
}));

vi.mock("../../organizations", () => ({
  OrganizationLayout: ({ title, actions, children }: { title: string; actions?: ReactNode; children: ReactNode }) => (
    <main>
      <h1>{title}</h1>
      {actions}
      {children}
    </main>
  ),
}));

vi.mock("@tanstack/react-router", () => ({
  Link: ({ children, to }: { children: ReactNode; to: string }) => <a href={to}>{children}</a>,
}));

const { PeopleDirectoryPage } = await import("./PeopleDirectoryPage");

function renderPage() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <PeopleDirectoryPage />
    </QueryClientProvider>,
  );
}

async function confirmChange(dialogName: RegExp, button: string) {
  const dialog = await screen.findByRole("dialog", { name: dialogName });
  const confirm = within(dialog).getByRole("button", { name: button });
  expect(confirm).toBeDisabled();
  fireEvent.change(within(dialog).getByLabelText(/Reason/), { target: { value: "unit decision" } });
  expect(confirm).toBeDisabled();
  fireEvent.click(within(dialog).getByRole("checkbox", { name: /development-only step-up/ }));
  fireEvent.click(confirm);
}

beforeEach(() => {
  vi.clearAllMocks();
  api.organization.members.mockImplementation((query: { continuationToken?: string }) =>
    Promise.resolve({
      result: query.continuationToken
        ? directory([person(16)], { totalCount: 16, pageStart: 16, previousToken: "back" })
        : directory(
            Array.from({ length: 15 }, (_, i) => person(i + 1)),
            { totalCount: 16, nextToken: "page-2" },
          ),
    }),
  );
  for (const fn of [api.organization.grantMemberRole, api.organization.disableMember, api.organization.approveOwnerProposal]) {
    fn.mockResolvedValue({ result: { commandId: "c", commandStatus: "Effective", authorizationRevision: 4 } });
  }
  api.organization.proposeOwnerChange.mockResolvedValue({ result: { commandId: "c", commandStatus: "Effective", authorizationRevision: 4, proposalId: "p-2", expiresOn: "2026-11-01T09:00:00Z" } });
  api.organization.requestMembership.mockResolvedValue({ result: { commandId: "r-2", commandStatus: "Effective", authorizationRevision: 4, requestId: "r-2", expiresOn: "2026-11-07T09:00:00Z" } });
});

describe("PeopleDirectoryPage", () => {
  it("requests 15 rows and pages with server cursors", async () => {
    renderPage();
    expect(await screen.findByText("Person 01")).toBeInTheDocument();
    expect(api.organization.members).toHaveBeenCalledWith({ pageSize: 15 }, expect.anything());
    fireEvent.click(screen.getByRole("button", { name: /Next/ }));
    expect(await screen.findByText("Person 16")).toBeInTheDocument();
    expect(api.organization.members).toHaveBeenLastCalledWith({ pageSize: 15, continuationToken: "page-2" }, expect.anything());
  });

  it("makes a designer through the Authority with a reason and step-up", async () => {
    renderPage();
    await screen.findByText("Person 01");
    await userEvent.setup().click(screen.getByRole("button", { name: "Actions for Person 03" }));
    await userEvent.setup().click(screen.getByRole("menuitem", { name: "Make designer: Person 03" }));
    await confirmChange(/Make Person 03 a designer/, "Make designer");
    await waitFor(() => expect(api.organization.grantMemberRole).toHaveBeenCalledWith("u-3", { roleName: "Designer", reason: "unit decision", useDevelopmentStepUp: true }));
    expect(await screen.findByText("Person 03 is now a designer.")).toBeInTheDocument();
  });

  it("lets a second owner approve a pending owner proposal", async () => {
    renderPage();
    await screen.findByText("Person 01");
    expect(screen.getByRole("table", { name: "Owner proposals list" })).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: /Approve: Make Organization Owner for Person 09/ }));
    await confirmChange(/Approve/, "Approve and apply");
    await waitFor(() => expect(api.organization.approveOwnerProposal).toHaveBeenCalledWith("p-1", { reason: "unit decision", useDevelopmentStepUp: true }));
  });

  it("adds a user as a request that needs operator approval and never claims sign-in", async () => {
    renderPage();
    await screen.findByText("Person 01");
    expect(screen.getByText("Waiting for operator approval")).toBeInTheDocument();
    expect(screen.getAllByText(/Needs Entra integration/).length).toBeGreaterThan(0);
    fireEvent.click(screen.getByRole("button", { name: "Add user" }));
    const dialog = await screen.findByRole("dialog", { name: "Add user" });
    expect(within(dialog).getByLabelText(/Identity provider/)).toHaveValue("https://idp.test");
    expect(within(dialog).getByText("Membership is not sign-in")).toBeInTheDocument();
    fireEvent.change(within(dialog).getByLabelText(/Object ID/), { target: { value: "nia@contoso.example" } });
    fireEvent.click(within(dialog).getByRole("button", { name: "Request membership" }));
    expect(within(dialog).getByText(/not an email address/)).toBeInTheDocument();
    fireEvent.change(within(dialog).getByLabelText(/Object ID/), { target: { value: "dev-nia-2" } });
    fireEvent.change(within(dialog).getByLabelText(/^Name/), { target: { value: "Nia Two" } });
    fireEvent.change(within(dialog).getByLabelText(/Reason/), { target: { value: "joins intake" } });
    fireEvent.click(within(dialog).getByRole("checkbox", { name: /development-only step-up/ }));
    fireEvent.click(within(dialog).getByRole("button", { name: "Request membership" }));
    await waitFor(() =>
      expect(api.organization.requestMembership).toHaveBeenCalledWith({
        subjectIdentifier: "dev-nia-2",
        displayNameHint: "Nia Two",
        initialRoleName: null,
        reason: "joins intake",
        useDevelopmentStepUp: true,
      }),
    );
    expect(await screen.findByText(/Request recorded for Nia Two/)).toBeInTheDocument();
  });

  it("does not show Add user to someone who cannot manage members", async () => {
    api.organization.members.mockResolvedValue({ result: directory([person(1, { availableActions: [] })], { canManageMembers: false, ownerProposals: [], membershipRequests: [] }) });
    renderPage();
    await screen.findByText("Person 01");
    expect(screen.queryByRole("button", { name: "Add user" })).toBeNull();
  });
});

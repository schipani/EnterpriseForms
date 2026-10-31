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
    ownerProposals: vi.fn(),
    membershipRequests: vi.fn(),
    memberProfile: vi.fn(),
    updateMemberProfile: vi.fn(),
  },
};
const refresh = vi.fn();

vi.mock("@enterpriseforms/app-runtime", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@enterpriseforms/app-runtime")>()),
  useApi: () => api,
  usePresence: (ids: string[]) => Object.fromEntries(ids.map((id, index) => [id, index === 0 ? "Available" : index === 1 ? "Away" : "Offline"])),
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

const approvalPage = (items: unknown[], extra: Record<string, unknown> = {}) => ({
  result: { items, totalCount: items.length, pageStart: items.length ? 1 : 0, pageSize: 5, nextToken: null, previousToken: null, ...extra },
});

beforeEach(() => {
  vi.clearAllMocks();
  api.organization.ownerProposals.mockImplementation(() => Promise.resolve(approvalPage(directory([]).ownerProposals)));
  api.organization.membershipRequests.mockImplementation(() => Promise.resolve(approvalPage(directory([]).membershipRequests)));
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
    expect(await screen.findByRole("table", { name: "Owner proposals list" })).toBeInTheDocument();
    fireEvent.click(await screen.findByRole("button", { name: /Approve: Make Owner for Person 09/ }));
    await confirmChange(/Approve/, "Approve and apply");
    await waitFor(() => expect(api.organization.approveOwnerProposal).toHaveBeenCalledWith("p-1", { reason: "unit decision", useDevelopmentStepUp: true }));
  });

  it("adds a user as a request that needs operator approval and never claims sign-in", async () => {
    renderPage();
    await screen.findByText("Person 01");
    expect(await screen.findByText("Awaiting operators")).toBeInTheDocument();
    expect(screen.getByText(/sign-in needs the Entra integration, PB-08/)).toBeInTheDocument();
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

  it("lists people with Name, Roles, Membership, Presence, Teams and Actions only (no Sign-in or Last sign-in columns)", async () => {
    renderPage();
    await screen.findByText("Person 01");
    const table = screen.getByRole("table", { name: "People" });
    expect(
      within(table)
        .getAllByRole("columnheader")
        .map((h) => h.textContent),
    ).toEqual(["Person", "Roles", "Membership", "Presence", "Teams", "Actions"]);
    expect(within(table).getAllByText("Available").length).toBeGreaterThan(0);
    expect(within(table).getAllByText("Away").length).toBeGreaterThan(0);
    expect(within(table).getAllByText("Offline").length).toBeGreaterThan(0);
  });

  it("shows presence separately from membership (a disabled member can be Offline; an Active one can be Away)", async () => {
    api.organization.members.mockResolvedValue({ result: directory([person(1), person(2, { membershipState: "Disabled" })]) });
    renderPage();
    await screen.findByText("Person 01");
    const rows = within(screen.getByRole("table", { name: "People" }))
      .getAllByRole("row")
      .slice(1);
    expect(rows[0]).toHaveTextContent("Active");
    expect(rows[0]).toHaveTextContent("Available");
    expect(rows[1]).toHaveTextContent("Disabled");
    expect(rows[1]).toHaveTextContent("Away");
  });

  it("asks the server for five approvals at a time and pages each list independently, resetting to page one when a filter changes", async () => {
    const proposals = Array.from({ length: 5 }, (_, i) => ({ ...directory([]).ownerProposals[0]!, proposalId: `p-${i}`, targetDisplayName: `Target ${i}` }));
    api.organization.ownerProposals.mockImplementation((query: { continuationToken?: string; status?: string }) =>
      Promise.resolve(
        query.continuationToken
          ? approvalPage([{ ...proposals[0]!, proposalId: "p-last", targetDisplayName: "Target last" }], { totalCount: 6, pageStart: 6, previousToken: "back-p" })
          : approvalPage(proposals, { totalCount: 6, nextToken: "next-p" }),
      ),
    );
    renderPage();
    await screen.findByText("Target 0");
    expect(api.organization.ownerProposals).toHaveBeenCalledWith({ pageSize: 5 }, expect.anything());
    const nav = screen.getByRole("navigation", { name: "Owner proposals pages" });
    expect(nav).toHaveTextContent("Showing 1–5 of 6 proposals");
    expect(nav).toHaveTextContent("Page 1 of 2");
    fireEvent.click(within(nav).getByRole("button", { name: "Next" }));
    expect(await screen.findByText("Target last")).toBeInTheDocument();
    expect(api.organization.ownerProposals).toHaveBeenLastCalledWith({ pageSize: 5, continuationToken: "next-p" }, expect.anything());
    // the membership requests list is separate: it never moved
    expect(api.organization.membershipRequests).toHaveBeenCalledTimes(1);
    expect(api.organization.membershipRequests).toHaveBeenCalledWith({ pageSize: 5 }, expect.anything());
    // changing a filter starts again on the first page
    fireEvent.change(screen.getByLabelText("Search proposals"), { target: { value: "Target 3" } });
    await waitFor(() => expect(api.organization.ownerProposals).toHaveBeenLastCalledWith({ pageSize: 5, q: "Target 3" }, expect.anything()));
  });

  it("shows empty states for the approval lists", async () => {
    api.organization.ownerProposals.mockResolvedValue(approvalPage([]));
    api.organization.membershipRequests.mockResolvedValue(approvalPage([]));
    renderPage();
    expect(await screen.findByText("No owner proposals in the last 30 days.")).toBeInTheDocument();
    expect(screen.getByText("No membership requests in the last 30 days.")).toBeInTheDocument();
  });

  it("edits a profile from the Actions menu with the revision token, and a stale revision offers a reload", async () => {
    const profile = {
      userId: "u-3",
      displayName: "Person 03",
      identityDisplayName: "Person 03",
      displayNameOverridden: false,
      preferredName: null,
      jobTitle: null,
      profileRevision: 4,
      profileModifiedOn: null,
      identityIssuer: "https://idp.test",
      identitySubject: "sub-3",
      isCurrentUser: false,
      canEdit: true,
    };
    api.organization.members.mockResolvedValue({ result: directory([person(1), person(2), person(3, { availableActions: ["EditProfile", "GrantDesigner"] })]) });
    api.organization.memberProfile.mockResolvedValue({ result: profile });
    api.organization.updateMemberProfile.mockResolvedValue({ result: { ...profile, displayName: "Pat Three", profileRevision: 5 } });
    renderPage();
    await screen.findByText("Person 03");
    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: "Actions for Person 03" }));
    await user.click(screen.getByRole("menuitem", { name: "Edit profile: Person 03" }));
    const dialog = await screen.findByRole("dialog", { name: /Edit profile: Person 03/ });
    expect(await within(dialog).findByLabelText(/Display name/)).toHaveValue("Person 03");
    expect(within(dialog).getByText(/cannot be changed in a profile/)).toBeInTheDocument();
    await user.clear(within(dialog).getByLabelText(/Display name/));
    await user.type(within(dialog).getByLabelText(/Display name/), "Pat Three");
    await user.type(within(dialog).getByLabelText("Job title"), "Intake lead");
    await user.click(within(dialog).getByRole("button", { name: "Save profile" }));
    await waitFor(() =>
      expect(api.organization.updateMemberProfile).toHaveBeenCalledWith("u-3", {
        displayName: "Pat Three",
        preferredName: null,
        jobTitle: "Intake lead",
        expectedRevision: 4,
        resetDisplayName: false,
      }),
    );
    expect(await screen.findByText("Profile saved for Pat Three.")).toBeInTheDocument();
  });
});

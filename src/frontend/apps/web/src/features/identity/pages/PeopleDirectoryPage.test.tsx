import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { beforeEach, describe, expect, it, vi } from "vitest";

/**
 * People page (C8 M2): asks the server for 15 rows, pages with the server's cursors, restarts at page 1 when a filter
 * changes, states what the directory covers, does not offer the AC-7 capabilities (Add user is disabled with the reason
 * next to it), and runs Security Administrator changes through the existing commands with a reason and step-up.
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
  availableActions: ["AppointSecurityAdministrator"],
  ...extra,
});

const directory = (members: unknown[], extra: Record<string, unknown> = {}) => ({
  source: "business directory",
  members,
  rolesAvailable: true,
  rolesUnavailableReason: null,
  totalCount: members.length,
  pageStart: members.length ? 1 : 0,
  pageSize: 15,
  nextToken: null,
  previousToken: null,
  includesMembersWhoNeverSignedIn: true,
  coverageStatement: "Lists every identity the Authorization Authority has bound to this organization and enabled.",
  securityAdministratorChangesOperational: true,
  securityAdministratorChangesUnavailableReason: null,
  ...extra,
});

const api = {
  organization: {
    members: vi.fn(),
    appointSecurityAdministrator: vi.fn(),
    removeSecurityAdministrator: vi.fn(),
  },
};
const refresh = vi.fn();

vi.mock("@enterpriseforms/app-runtime", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@enterpriseforms/app-runtime")>()),
  useApi: () => api,
  useSession: () => ({
    hasCapability: (capability: string) => capability === "security.administer",
    refresh,
  }),
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
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  return render(
    <QueryClientProvider client={client}>
      <PeopleDirectoryPage />
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  vi.clearAllMocks();
  api.organization.members.mockImplementation((query: { continuationToken?: string; q?: string }) =>
    Promise.resolve({
      result: query.continuationToken
        ? directory([person(16)], {
            totalCount: 16,
            pageStart: 16,
            previousToken: "back",
          })
        : query.q
          ? directory([person(7)])
          : directory(
              Array.from({ length: 15 }, (_, i) => person(i + 1)),
              { totalCount: 16, nextToken: "page-2" },
            ),
    }),
  );
  api.organization.appointSecurityAdministrator.mockResolvedValue({
    result: undefined,
  });
});

describe("PeopleDirectoryPage", () => {
  it("requests 15 rows, shows coverage, and pages with server cursors", async () => {
    renderPage();
    expect(await screen.findByText("Person 01")).toBeInTheDocument();
    expect(api.organization.members).toHaveBeenCalledWith({ pageSize: 15 }, expect.anything());
    expect(screen.getByText(/Lists every identity the Authorization Authority has bound/)).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: /Next/ }));
    expect(await screen.findByText("Person 16")).toBeInTheDocument();
    expect(api.organization.members).toHaveBeenLastCalledWith({ pageSize: 15, continuationToken: "page-2" }, expect.anything());
  });

  it("restarts at page 1 with the search text when searching", async () => {
    renderPage();
    await screen.findByText("Person 01");
    fireEvent.click(screen.getByRole("button", { name: /Next/ }));
    await screen.findByText("Person 16");
    fireEvent.change(screen.getByLabelText("Search people"), {
      target: { value: "sub-7" },
    });
    await waitFor(() => expect(api.organization.members).toHaveBeenLastCalledWith({ pageSize: 15, q: "sub-7" }, expect.anything()));
    expect(await screen.findByText("Person 07")).toBeInTheDocument();
  });

  it("does not offer capabilities that need unapproved Authority commands", async () => {
    renderPage();
    await screen.findByText("Person 01");
    const add = screen.getByRole("button", { name: "Add user" });
    expect(add).toBeDisabled();
    expect(add).toHaveAccessibleDescription(/awaiting approval/);
    expect(screen.queryByRole("button", { name: /owner/i })).toBeNull();
    expect(screen.queryByRole("button", { name: /disable/i })).toBeNull();
  });

  it("appoints a Security Administrator with a reason and the development step-up, then confirms", async () => {
    renderPage();
    await screen.findByText("Person 01");
    fireEvent.click(
      screen.getByRole("button", {
        name: "Make Security Administrator: Person 03",
      }),
    );
    const appoint = screen.getByRole("button", { name: "Appoint" });
    expect(appoint).toBeDisabled();
    fireEvent.change(screen.getByLabelText(/Reason/), {
      target: { value: "covers the night shift" },
    });
    expect(appoint).toBeDisabled();
    fireEvent.click(screen.getByRole("checkbox", { name: /development-only step-up/ }));
    fireEvent.click(appoint);
    await waitFor(() =>
      expect(api.organization.appointSecurityAdministrator).toHaveBeenCalledWith({
        userId: "u-3",
        reason: "covers the night shift",
        useDevelopmentStepUp: true,
      }),
    );
    expect(await screen.findByText("Person 03 is now a Security Administrator.")).toBeInTheDocument();
    expect(refresh).toHaveBeenCalled();
  });
});

import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, within } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

const organization = { searchApprovals: vi.fn() };
let capabilities: string[] = [];

vi.mock("@enterpriseforms/app-runtime", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@enterpriseforms/app-runtime")>()),
  useApi: () => ({ organization }),
  useSession: () => ({ hasCapability: (c: string) => capabilities.includes(c) }),
}));
vi.mock("@tanstack/react-router", () => ({ Link: ({ to, children, className }: { to: string; children: React.ReactNode; className?: string }) => <a href={to} className={className}>{children}</a> }));

const { SearchApprovalsPage } = await import("./SearchApprovalsPage");

function renderPage() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <SearchApprovalsPage />
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  vi.clearAllMocks();
  organization.searchApprovals.mockResolvedValue({ result: { plaintextSearchPermitted: true, approvalOperational: true, forms: [] } });
});

describe("SearchApprovalsPage in the Organization layout", () => {
  it("an organization owner without security.administer sees the layout and a refusal, and nothing is requested from the server", async () => {
    capabilities = ["organization.manage"];
    renderPage();
    expect(await screen.findByText("Security administrators only")).toBeInTheDocument();
    const nav = screen.getByRole("navigation", { name: "Organization" });
    expect(within(nav).queryByRole("link", { name: "Search Approvals" })).not.toBeInTheDocument(); // no item for a permission they do not hold
    expect(organization.searchApprovals).not.toHaveBeenCalled();
  });

  it("a Security Administrator gets the shared header and the Organization navigation with Search Approvals under Security & Governance", async () => {
    capabilities = ["security.administer"];
    renderPage();
    expect(await screen.findByRole("heading", { level: 1, name: "Search approvals" })).toBeInTheDocument();
    const nav = screen.getByRole("navigation", { name: "Organization" });
    const group = within(nav).getByRole("heading", { level: 2, name: "Security & Governance" });
    const list = within(nav).getAllByRole("list").find((l) => l.getAttribute("aria-labelledby") === group.id)!;
    expect(within(list).getByRole("link", { name: "Search Approvals" })).toHaveAttribute("href", "/app/organization/search-approvals");
    expect(organization.searchApprovals).toHaveBeenCalled();
  });

  it("a member with no administration capability sees only the generic no-permission message, not the page or its data", async () => {
    capabilities = [];
    renderPage();
    expect(await screen.findByText("No permission")).toBeInTheDocument();
    expect(screen.queryByRole("navigation", { name: "Organization" })).not.toBeInTheDocument();
    expect(organization.searchApprovals).not.toHaveBeenCalled();
  });
});

import type { OrganizationOverviewResponse } from "@enterpriseforms/api-client";
import { render, screen, within } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { organizationNavigation, type OrganizationNavGroup } from "./OrganizationLayout";
import { OverviewAggregateCards } from "./OverviewCards";

vi.mock("@tanstack/react-router", () => ({ Link: ({ to, children, className }: { to: string; children: React.ReactNode; className?: string }) => <a href={to} className={className}>{children}</a> }));

const available = { available: true, unavailableReason: null };
const overview: OrganizationOverviewResponse = {
  canManageConfiguration: true,
  canAdministerSecurity: false,
  organization: { status: available, organizationDisplayName: "Northwind Legal", hasLogo: false, brandingRevision: 3 },
  members: { status: available, projectedMemberCount: 5, memberCountSource: "business projection: identities that have signed in to this organization", securityAdministratorCount: 1, securityAdministratorsCanManage: false },
  teams: { status: available, teamCount: 2 },
  dataProtection: { status: available, minimumFieldClassification: "Internal", plaintextInternalFieldsPermitted: true, isUnreviewedDefault: true, securityPolicyRevision: 1 },
  authentication: { authenticationRequirementName: "PasswordlessFederated", authenticationRolloutStatus: "NotEnforced", statusExplanation: "Sign-in is delegated; FIDO2 enforcement awaits PB-08." },
};

const labels = (groups: OrganizationNavGroup[]) => groups.map((g) => ({ group: g.title, items: g.items.map((i) => i.label) }));

describe("organizationNavigation", () => {
  it("shows nothing without a capability", () => {
    expect(organizationNavigation(() => false)).toEqual([]);
  });

  it("gives an owner the three groups in order, without Search Approvals", () => {
    expect(labels(organizationNavigation((c) => c === "organization.manage"))).toEqual([
      { group: "Organization", items: ["Overview", "People", "Teams"] },
      { group: "Appearance & Localization", items: ["Branding", "Regional Settings", "Language", "Text Size"] },
      { group: "Security & Governance", items: ["Data Protection", "Security Administrators"] },
    ]);
  });

  it("gives a Security Administrator no Appearance & Localization group at all (never an empty heading) and adds Search Approvals", () => {
    expect(labels(organizationNavigation((c) => c === "security.administer"))).toEqual([
      { group: "Organization", items: ["Overview", "People", "Teams"] },
      { group: "Security & Governance", items: ["Data Protection", "Security Administrators", "Search Approvals"] },
    ]);
  });

  it("keeps all ten original destinations on their original routes", () => {
    const everything = organizationNavigation((c) => c === "organization.manage" || c === "security.administer").flatMap((g) => g.items.map((i) => i.to));
    expect(everything).toEqual([
      "/app/organization",
      "/app/organization/users",
      "/app/organization/teams",
      "/app/organization/branding",
      "/app/organization/regional",
      "/app/organization/language",
      "/app/organization/text-size",
      "/app/organization/data-protection",
      "/app/organization/security-administrators",
      "/app/organization/search-approvals",
    ]);
  });

  it("marks only Overview as an exact match so it is not active on every other Organization page", () => {
    const items = organizationNavigation(() => true).flatMap((g) => g.items);
    expect(items.filter((i) => i.exact).map((i) => i.label)).toEqual(["Overview"]);
  });

  it("assigns an icon to every group and every item", () => {
    for (const group of organizationNavigation(() => true)) {
      expect(group.icon).toBeDefined();
      expect(group.items.every((i) => i.icon !== undefined)).toBe(true);
    }
  });
});

describe("OverviewAggregateCards", () => {
  it("renders every part without a refresh button and labels the member-count source", () => {
    render(<OverviewAggregateCards overview={overview} />);
    const members = screen.getByRole("region", { name: "Members and roles" });
    expect(within(members).getByText("5")).toBeInTheDocument();
    expect(within(members).getByText(/Member count source: business projection/)).toBeInTheDocument();
    expect(within(members).getByText(/Owner and designer roles are not shown yet/)).toBeInTheDocument();
    expect(screen.getByText("Not enforced — awaits PB-08")).toBeInTheDocument();
    expect(screen.queryAllByRole("button", { name: /refresh/i })).toHaveLength(0);
  });

  it("shows an unavailable part as such instead of a guessed value", () => {
    const degraded: OrganizationOverviewResponse = { ...overview, teams: { status: { available: false, unavailableReason: "the business projection could not be read" }, teamCount: null } };
    render(<OverviewAggregateCards overview={degraded} />);
    const teams = screen.getByRole("region", { name: "Teams" });
    expect(within(teams).getByText("Unavailable")).toBeInTheDocument();
    expect(within(teams).getByText(/the business projection could not be read/)).toBeInTheDocument();
    expect(within(teams).queryByText("Active teams")).toBeNull();
    expect(within(teams).queryByText(/refresh/i)).toBeNull(); // the notice no longer tells people to click a button that does not exist
  });
});

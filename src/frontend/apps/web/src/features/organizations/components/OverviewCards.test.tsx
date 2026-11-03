import type { OrganizationOverviewResponse } from "@enterpriseforms/api-client";
import { render, screen, within } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { organizationNavigation } from "./OrganizationLayout";
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

describe("organizationNavigation", () => {
  it("offers nothing to a member without administration capabilities", () => {
    expect(organizationNavigation(() => false)).toEqual([]);
  });

  it("gives an owner every page except Search approvals, and a Security Administrator everything except Branding", () => {
    const owner = organizationNavigation((c) => c === "organization.manage").map((i) => i.label);
    expect(owner).toEqual(["Overview", "People", "Teams", "Branding", "Regional settings", "Data protection", "Security administrators"]);
    const security = organizationNavigation((c) => c === "security.administer").map((i) => i.label);
    expect(security).toEqual(["Overview", "People", "Teams", "Data protection", "Security administrators", "Search approvals"]);
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

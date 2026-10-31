import type { OrganizationMemberResponse } from "@enterpriseforms/api-client";
import { fireEvent, render, screen, within } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { subjectProblem } from "./AddUserDialog";
import { MemberTable } from "./MemberTable";

const base = { identityIssuer: "https://idp.test", firstSeenOn: "2026-10-01T10:00:00Z", teams: [], isCurrentUser: false, lastSignedInOn: null, disabledOn: null };

const members: OrganizationMemberResponse[] = [
  {
    ...base,
    userId: "u1",
    displayName: "Avery Owner",
    identitySubject: "dev-owner",
    authorityRoles: ["OrganizationOwner"],
    teams: [{ teamId: "t1", teamName: "Intake" }],
    isCurrentUser: true,
    lastSignedInOn: "2026-10-30T10:00:00Z",
    signInStatus: "SignedIn",
    availableActions: [],
    membershipState: "Enabled",
  },
  {
    ...base,
    userId: "u2",
    displayName: "Sam Security",
    identitySubject: "dev-security",
    authorityRoles: ["SecurityAdministrator"],
    signInStatus: "NotRecorded",
    availableActions: ["GrantDesigner", "ProposeOwnerGrant", "DisableMembership"],
    membershipState: "Enabled",
  },
  {
    ...base,
    userId: "u3",
    displayName: "",
    identitySubject: "dev-new",
    firstSeenOn: null,
    authorityRoles: [],
    signInStatus: "NeverSignedIn",
    availableActions: ["EnableMembership"],
    membershipState: "Disabled",
  },
];

describe("MemberTable", () => {
  it("shows roles, membership, sign-in, teams and last sign-in", () => {
    render(<MemberTable members={members} rolesAvailable />);
    const table = screen.getByRole("table", { name: "People" });
    expect(within(table).getByRole("rowheader", { name: "Avery Owner (you)" })).toBeInTheDocument();
    expect(within(table).getByText("Organization Owner")).toBeInTheDocument();
    expect(within(table).getByText("Security Administrator")).toBeInTheDocument();
    expect(within(table).getByText("Disabled")).toBeInTheDocument();
    expect(within(table).getAllByText("Active")).toHaveLength(2);
    expect(within(table).getByText("Never signed in")).toBeInTheDocument();
    expect(within(table).getByText("Intake")).toBeInTheDocument();
    expect(within(table).getByRole("rowheader", { name: /Name not known yet/ })).toBeInTheDocument();
  });

  it("is read-only without an action handler", () => {
    render(<MemberTable members={members} rolesAvailable />);
    expect(screen.queryByRole("columnheader", { name: "Actions" })).toBeNull();
    expect(screen.queryByRole("button")).toBeNull();
  });

  it("shows the first hinted action and puts the rest in the row menu, with names that start with the visible text", () => {
    const onAction = vi.fn();
    render(<MemberTable members={members} rolesAvailable onAction={onAction} />);
    fireEvent.click(screen.getByRole("button", { name: "Make designer: Sam Security" }));
    expect(onAction).toHaveBeenCalledWith(members[1], "GrantDesigner");
    fireEvent.click(screen.getByRole("button", { name: "Re-enable membership: dev-new" }));
    expect(onAction).toHaveBeenCalledWith(members[2], "EnableMembership");
    expect(screen.getByRole("button", { name: "More actions for Sam Security" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /Avery Owner/ })).toBeNull();
  });

  it("says roles are unavailable when the Authority roster did not answer", () => {
    render(<MemberTable members={members} rolesAvailable={false} />);
    expect(screen.getAllByText("Unavailable")).toHaveLength(3);
  });
});

describe("subjectProblem", () => {
  it("accepts only immutable subjects, never email addresses", () => {
    const entra = "https://login.microsoftonline.com/11111111-2222-3333-4444-555555555555/v2.0";
    expect(subjectProblem(entra, "0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0")).toBeNull();
    expect(subjectProblem(entra, "nia-newcomer")).toMatch(/object ID/);
    expect(subjectProblem(entra, "nia@contoso.example")).toMatch(/not an email/);
    expect(subjectProblem("https://test-idp.enterpriseforms.invalid", "dev-newcomer")).toBeNull();
    expect(subjectProblem("https://test-idp.enterpriseforms.invalid", " ")).toMatch(/Enter/);
  });
});

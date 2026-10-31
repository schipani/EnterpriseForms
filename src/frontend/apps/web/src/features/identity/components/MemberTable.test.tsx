import type { OrganizationMemberResponse } from "@enterpriseforms/api-client";
import { render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import { subjectProblem } from "./AddUserDialog";
import { MemberTable, providerName } from "./MemberTable";

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
  it("shows one Person cell with the identity beneath the name, then roles, membership, presence and teams", () => {
    render(<MemberTable members={members} rolesAvailable />);
    const table = screen.getByRole("table", { name: "People" });
    expect(
      within(table)
        .getAllByRole("columnheader")
        .map((h) => h.textContent),
    ).toEqual(["Person", "Roles", "Membership", "Presence", "Teams"]);
    const owner = within(table).getByRole("rowheader", { name: /Avery Owner/ });
    expect(owner).toHaveTextContent("Avery Owner");
    expect(owner).toHaveTextContent("dev-owner · idp.test");
    expect(owner).toHaveTextContent("(you)");
    expect(within(owner).getByTitle("dev-owner · https://idp.test")).toBeInTheDocument(); // full value for the tooltip
    expect(within(table).getByText("Owner")).toBeInTheDocument();
    expect(within(table).getByText("Security administrator")).toBeInTheDocument();
    expect(within(table).getByText("Disabled")).toBeInTheDocument();
    expect(within(table).getAllByText("Active")).toHaveLength(2);
    expect(within(table).queryByText("Never signed in")).toBeNull(); // sign-in state is no longer a column
    expect(within(table).getAllByText("Presence unknown").length).toBe(3); // no presence supplied: Unknown, never Offline
    expect(within(table).getByText("Intake")).toBeInTheDocument();
    expect(within(table).getByRole("rowheader", { name: /Name not known yet/ })).toBeInTheDocument();
  });

  it("shows the supplied presence for each person, separate from membership", () => {
    render(<MemberTable members={members} rolesAvailable presence={{ u1: "Available", u2: "Away", u3: "Offline" }} />);
    const rows = within(screen.getByRole("table", { name: "People" }))
      .getAllByRole("row")
      .slice(1);
    expect(rows[0]).toHaveTextContent("Available");
    expect(rows[1]).toHaveTextContent("Away");
    expect(rows[2]).toHaveTextContent("Offline");
    expect(rows[2]).toHaveTextContent("Disabled"); // membership is a different column: a disabled person is Offline, an Active one may be Away
    expect(rows[1]).toHaveTextContent("Active");
  });

  it("offers Edit profile first in the Actions menu when the server hints it", async () => {
    const user = userEvent.setup();
    const onAction = vi.fn();
    render(<MemberTable members={[{ ...members[1]!, availableActions: ["EditProfile", "GrantDesigner"] }]} rolesAvailable onAction={onAction} />);
    await user.click(screen.getByRole("button", { name: "Actions for Sam Security" }));
    expect(screen.getAllByRole("menuitem").map((i) => i.textContent)).toEqual(["Edit profile", "Make designer"]);
    await user.click(screen.getByRole("menuitem", { name: "Edit profile: Sam Security" }));
    expect(onAction).toHaveBeenCalledWith(expect.objectContaining({ userId: "u2" }), "EditProfile");
  });

  it("shows no identity line when the server did not return an identity", () => {
    render(<MemberTable members={[{ ...members[1]!, identitySubject: "", identityIssuer: "" }]} rolesAvailable />);
    expect(screen.getByRole("rowheader")).toHaveTextContent(/^Sam Security$/);
  });

  it("is read-only without an action handler", () => {
    render(<MemberTable members={members} rolesAvailable />);
    expect(screen.queryByRole("columnheader", { name: "Actions" })).toBeNull();
    expect(screen.queryByRole("button")).toBeNull();
  });

  it("offers one Actions menu per row, only for rows with hinted actions, with names that start with the visible text", async () => {
    const onAction = vi.fn();
    const user = userEvent.setup();
    render(<MemberTable members={members} rolesAvailable onAction={onAction} />);
    expect(screen.getByRole("columnheader", { name: "Actions" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /Actions for Avery Owner/ })).toBeNull(); // no authorized action for that row
    await user.click(screen.getByRole("button", { name: "Actions for Sam Security" }));
    expect(screen.getAllByRole("menuitem").map((i) => i.textContent)).toEqual(["Make designer", "Propose as owner", "Disable membership"]);
    await user.click(screen.getByRole("menuitem", { name: "Make designer: Sam Security" }));
    expect(onAction).toHaveBeenCalledWith(members[1], "GrantDesigner");
    await user.click(screen.getByRole("button", { name: "Actions for dev-new" }));
    await user.click(screen.getByRole("menuitem", { name: "Re-enable membership: dev-new" }));
    expect(onAction).toHaveBeenCalledWith(members[2], "EnableMembership");
  });

  it("opens the Actions menu from the keyboard", async () => {
    const user = userEvent.setup();
    render(<MemberTable members={members} rolesAvailable onAction={vi.fn()} />);
    screen.getByRole("button", { name: "Actions for Sam Security" }).focus();
    await user.keyboard("{Enter}");
    expect(screen.getByRole("menuitem", { name: /Make designer/ })).toBeInTheDocument();
    await user.keyboard("{Escape}");
    expect(screen.queryByRole("menuitem")).toBeNull();
  });

  it("says roles are unavailable when the Authority roster did not answer", () => {
    render(<MemberTable members={members} rolesAvailable={false} />);
    expect(screen.getAllByText("Unavailable")).toHaveLength(3);
  });
});

describe("providerName", () => {
  it("shortens issuers to a provider name", () => {
    expect(providerName("https://login.microsoftonline.com/11111111-2222-3333-4444-555555555555/v2.0")).toBe("Microsoft Entra ID");
    expect(providerName("https://test-idp.enterpriseforms.invalid")).toBe("Test identity provider");
    expect(providerName("https://idp.test")).toBe("idp.test");
    expect(providerName("not a url")).toBe("not a url");
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

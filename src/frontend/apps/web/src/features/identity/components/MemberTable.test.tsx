import type { OrganizationMemberResponse } from "@enterpriseforms/api-client";
import { fireEvent, render, screen, within } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { MemberTable } from "./MemberTable";

const members: OrganizationMemberResponse[] = [
  {
    userId: "0199a2b3-c4d5-7e6f-8123-456789abcde1",
    displayName: "Avery Owner",
    identityIssuer: "https://idp.test",
    identitySubject: "dev-owner",
    firstSeenOn: "2026-10-01T10:00:00Z",
    authorityRoles: [],
    teams: [{ teamId: "0199a2b3-c4d5-7e6f-8123-456789abcdf1", teamName: "Intake" }],
    isCurrentUser: true,
    lastSignedInOn: "2026-10-30T10:00:00Z",
    signInStatus: "SignedIn",
    availableActions: [],
  },
  {
    userId: "0199a2b3-c4d5-7e6f-8123-456789abcde2",
    displayName: "Sam Security",
    identityIssuer: "https://idp.test",
    identitySubject: "dev-security",
    firstSeenOn: "2026-10-02T10:00:00Z",
    authorityRoles: ["SecurityAdministrator"],
    teams: [],
    isCurrentUser: false,
    lastSignedInOn: null,
    signInStatus: "NotRecorded",
    availableActions: ["RemoveSecurityAdministrator"],
  },
  {
    userId: "0199a2b3-c4d5-7e6f-8123-456789abcde3",
    displayName: "",
    identityIssuer: "https://idp.test",
    identitySubject: "dev-new",
    firstSeenOn: null,
    authorityRoles: [],
    teams: [],
    isCurrentUser: false,
    lastSignedInOn: null,
    signInStatus: "NeverSignedIn",
    availableActions: ["AppointSecurityAdministrator"],
  },
];

describe("MemberTable", () => {
  it("shows names, identity, Authority role badges, sign-in state, teams and last sign-in", () => {
    render(<MemberTable members={members} rolesAvailable />);
    const table = screen.getByRole("table", { name: "People" });
    expect(within(table).getByRole("rowheader", { name: "Avery Owner (you)" })).toBeInTheDocument();
    expect(within(table).getByText("dev-security · https://idp.test")).toBeInTheDocument();
    expect(within(table).getByText("Security Administrator")).toBeInTheDocument();
    expect(within(table).getByText("Intake")).toBeInTheDocument();
    expect(within(table).getByText("Signed in")).toBeInTheDocument();
    expect(within(table).getByText("Sign-in not recorded")).toBeInTheDocument();
    expect(within(table).getByText("Never signed in")).toBeInTheDocument();
    expect(within(table).getByRole("rowheader", { name: /Name not known yet/ })).toBeInTheDocument();
  });

  it("has no action column without an action handler, even when rows carry hints", () => {
    render(<MemberTable members={members} rolesAvailable />);
    expect(screen.queryByRole("columnheader", { name: "Actions" })).toBeNull();
    expect(screen.queryByRole("button")).toBeNull();
  });

  it("offers only the Security Administrator action the server hinted for each row", () => {
    const onAction = vi.fn();
    render(<MemberTable members={members} rolesAvailable onAction={onAction} />);
    expect(screen.getByRole("columnheader", { name: "Actions" })).toBeInTheDocument();
    fireEvent.click(
      screen.getByRole("button", {
        name: "Make Security Administrator: dev-new",
      }),
    );
    expect(onAction).toHaveBeenCalledWith(members[2], "AppointSecurityAdministrator");
    fireEvent.click(
      screen.getByRole("button", {
        name: "Remove Security Administrator: Sam Security",
      }),
    );
    expect(onAction).toHaveBeenCalledWith(members[1], "RemoveSecurityAdministrator");
    expect(screen.queryByRole("button", { name: /Avery Owner/ })).toBeNull();
  });

  it("says roles are unavailable when the Authority roster did not answer, instead of showing none", () => {
    render(<MemberTable members={members} rolesAvailable={false} />);
    expect(screen.getAllByText("Unavailable")).toHaveLength(3);
    expect(screen.queryByText("Security Administrator")).toBeNull();
  });
});

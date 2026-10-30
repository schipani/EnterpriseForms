import type { OrganizationMemberResponse } from "@enterpriseforms/api-client";
import { render, screen, within } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { MemberTable } from "./MemberTable";

const members: OrganizationMemberResponse[] = [
  { userId: "0199a2b3-c4d5-7e6f-8123-456789abcde1", displayName: "Avery Owner", identityIssuer: "https://idp.test", identitySubject: "dev-owner", firstSeenOn: "2026-10-01T10:00:00Z", authorityRoles: [], teams: [{ teamId: "0199a2b3-c4d5-7e6f-8123-456789abcdf1", teamName: "Intake" }], isCurrentUser: true },
  { userId: "0199a2b3-c4d5-7e6f-8123-456789abcde2", displayName: "Sam Security", identityIssuer: "https://idp.test", identitySubject: "dev-security", firstSeenOn: "2026-10-02T10:00:00Z", authorityRoles: ["SecurityAdministrator"], teams: [], isCurrentUser: false },
];

describe("MemberTable", () => {
  it("is read-only: names, non-secret identity, Authority role badges and teams, with no action column", () => {
    render(<MemberTable members={members} rolesAvailable />);
    const table = screen.getByRole("table", { name: "People" });
    expect(within(table).getByRole("rowheader", { name: "Avery Owner (you)" })).toBeInTheDocument();
    expect(within(table).getByText("dev-security · https://idp.test")).toBeInTheDocument();
    expect(within(table).getByText("Security Administrator")).toBeInTheDocument();
    expect(within(table).getByText("Intake")).toBeInTheDocument();
    expect(within(table).queryByRole("columnheader", { name: "Actions" })).toBeNull();
    expect(within(table).queryByRole("button")).toBeNull();
  });

  it("says roles are unavailable when the Authority roster did not answer, instead of showing none", () => {
    render(<MemberTable members={members} rolesAvailable={false} />);
    expect(screen.getAllByText("Unavailable")).toHaveLength(2);
    expect(screen.queryByText("Security Administrator")).toBeNull();
  });
});

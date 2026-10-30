import type { TeamMemberResponse } from "@enterpriseforms/api-client";
import { render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import { TeamRoster } from "./TeamRoster";

const members: TeamMemberResponse[] = [
  { userId: "0199a2b3-c4d5-7e6f-8123-456789abcde1", displayName: "Casey Clerk", memberSince: "2026-10-03T10:00:00Z", authorityCommandId: "0199a2b3-c4d5-7e6f-8123-456789abcdf1" },
  { userId: "0199a2b3-c4d5-7e6f-8123-456789abcde2", displayName: "Forged Row", memberSince: "2026-10-03T10:00:00Z", authorityCommandId: null },
];

describe("TeamRoster", () => {
  it("marks a row without an Authority command as an unverified projection row", () => {
    render(<TeamRoster members={members} canManage={false} />);
    const table = screen.getByRole("table", { name: "Team members" });
    expect(within(within(table).getByRole("row", { name: /Casey Clerk/ })).getByText("Effective")).toBeInTheDocument();
    expect(within(within(table).getByRole("row", { name: /Forged Row/ })).getByText("Unverified projection row")).toBeInTheDocument();
    expect(within(table).queryByRole("button")).toBeNull();
  });

  it("offers Remove only when the server said the caller may manage the team", async () => {
    const onRemove = vi.fn();
    render(<TeamRoster members={members} canManage onRemove={onRemove} />);
    await userEvent.click(screen.getByRole("button", { name: "Remove Casey Clerk from the team" }));
    expect(onRemove).toHaveBeenCalledWith(members[0]);
  });
});

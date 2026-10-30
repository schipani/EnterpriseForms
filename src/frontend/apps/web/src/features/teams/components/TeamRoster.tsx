import type { TeamMemberResponse } from "@enterpriseforms/api-client";
import { ActionIcons, Badge, Button, DataTable } from "@enterpriseforms/ui";

/**
 * Team roster (PG-049, Phase C8 M1) from the membership projection. Each row names the Authority command that made the
 * membership effective; a row without one is shown as "unverified" because a forged projection row confers nothing.
 * Remove is offered only when the server said the caller may manage teams.
 */
export function TeamRoster({ members, canManage, onRemove, removingUserId }: { members: TeamMemberResponse[]; canManage: boolean; onRemove?: ((member: TeamMemberResponse) => void) | undefined; removingUserId?: string | null | undefined }) {
  return (
    <DataTable
      caption="Team members"
      rowKey={(row) => row.userId}
      rows={members}
      emptyMessage="This team has no members yet."
      stickyActions
      columns={[
        { key: "name", header: "Name", isRowHeader: true, render: (row) => row.displayName },
        { key: "since", header: "Member since", render: (row) => new Date(row.memberSince).toLocaleDateString() },
        { key: "command", header: "Authority command", render: (row) => (row.authorityCommandId ? <Badge tone="success">Effective</Badge> : <Badge tone="warning">Unverified projection row</Badge>) },
        ...(canManage && onRemove
          ? [
              {
                key: "actions",
                header: "Actions",
                isActions: true,
                render: (row: TeamMemberResponse) => (
                  <Button size="sm" variant="ghost" icon={ActionIcons.delete} aria-label={`Remove ${row.displayName} from the team`} busy={removingUserId === row.userId} onClick={() => onRemove(row)}>
                    Remove
                  </Button>
                ),
              },
            ]
          : []),
      ]}
    />
  );
}

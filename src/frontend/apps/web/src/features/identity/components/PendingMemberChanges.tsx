import type { MembershipRequestResponse, OwnerProposalResponse } from "@enterpriseforms/api-client";
import { Badge, Button, Card, DataTable } from "@enterpriseforms/ui";

const PROPOSAL_LABELS: Record<string, string> = {
  GrantOwner: "Make Organization Owner",
  RevokeOwner: "Remove Organization Owner",
  DisableOwner: "Disable owner's membership",
  EnableOwner: "Re-enable owner's membership",
};

const stateTone = (state: string) => (state === "Pending" ? "warning" : state === "Approved" || state === "Consumed" ? "success" : state === "Rejected" ? "danger" : "neutral");

const stateLabel = (state: string) => (state === "Consumed" ? "Approved" : state === "Pending" ? "Waiting for approval" : state);

/**
 * Two-owner proposals and membership requests from the Authority (last 30 days). A proposal can be approved by another
 * owner than the proposer and the person; membership requests are decided by two operators, never from this page.
 */
export function PendingMemberChanges({
  proposals,
  requests,
  onApprove,
}: {
  proposals: OwnerProposalResponse[];
  requests: MembershipRequestResponse[];
  onApprove: (proposal: OwnerProposalResponse) => void;
}) {
  if (proposals.length === 0 && requests.length === 0) {
    return null;
  }
  return (
    <Card title="Owner proposals and membership requests">
      <p className="m-0 text-sm text-muted">
        Changes to an Organization Owner need a second owner within 24 hours. Membership requests need two operator approvals within 7 days; until then the person is not a member and cannot sign in.
      </p>
      {proposals.length > 0 ? (
        <DataTable<OwnerProposalResponse>
          caption="Owner proposals list"
          rows={proposals}
          rowKey={(row) => row.proposalId}
          stickyActions
          columns={[
            { key: "person", header: "Person", isRowHeader: true, render: (row) => row.targetDisplayName },
            { key: "change", header: "Change", render: (row) => PROPOSAL_LABELS[row.proposedChange] ?? row.proposedChange },
            { key: "by", header: "Proposed by", render: (row) => row.proposedByDisplayName },
            { key: "reason", header: "Reason", render: (row) => <span className="break-words">{row.reason}</span> },
            { key: "expires", header: "Expires", render: (row) => <span className="whitespace-nowrap">{new Date(row.expiresOn).toLocaleString()}</span> },
            { key: "state", header: "Status", render: (row) => <Badge tone={stateTone(row.proposalState)}>{stateLabel(row.proposalState)}</Badge> },
            {
              key: "actions",
              header: "Actions",
              isActions: true,
              render: (row) =>
                row.canApprove ? (
                  <Button
                    size="sm"
                    variant="secondary"
                    aria-label={`Approve: ${PROPOSAL_LABELS[row.proposedChange] ?? row.proposedChange} for ${row.targetDisplayName}`}
                    onClick={() => onApprove(row)}
                  >
                    Approve
                  </Button>
                ) : row.proposalState === "Pending" ? (
                  <span className="text-sm text-muted">Another owner approves</span>
                ) : null,
            },
          ]}
        />
      ) : null}
      {requests.length > 0 ? (
        <DataTable<MembershipRequestResponse>
          caption="Membership requests list"
          rows={requests}
          rowKey={(row) => row.requestId}
          columns={[
            { key: "name", header: "Name", isRowHeader: true, render: (row) => row.displayNameHint },
            { key: "identity", header: "Object ID", render: (row) => <span className="break-all text-sm">{row.subjectIdentifier}</span> },
            { key: "role", header: "Initial role", render: (row) => row.initialRoleName ?? <span className="text-muted">None</span> },
            { key: "by", header: "Requested by", render: (row) => row.requestedByDisplayName },
            { key: "expires", header: "Expires", render: (row) => <span className="whitespace-nowrap">{new Date(row.expiresOn).toLocaleDateString()}</span> },
            {
              key: "state",
              header: "Status",
              render: (row) => <Badge tone={stateTone(row.requestState)}>{row.requestState === "Pending" ? "Waiting for operator approval" : row.requestState}</Badge>,
            },
            { key: "signin", header: "Sign-in", render: () => <span className="text-sm text-muted">Needs Entra integration (PB-08)</span> },
          ]}
        />
      ) : null}
    </Card>
  );
}

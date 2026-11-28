import type { RecordSecurityHistoryItemResponse } from "@enterpriseforms/api-client";
import { useApi } from "@enterpriseforms/app-runtime";
import { Badge, Button, Card, LoadingBlock, StatusMessage, getActiveFormatter, useFormatter } from "@enterpriseforms/ui";
import { useQuery } from "@tanstack/react-query";
import { ErrorPanel } from "../../support";

const COMMAND_LABELS: Record<string, string> = {
  CreateShare: "Share created",
  UpdateShareScope: "Share changed",
  RevokeShare: "Share revoked",
  ExpireShare: "Share expired (scheduled when it was granted)",
  TransferOwnership: "Ownership transferred",
  RemoveTeamMember: "Team member removed",
  DisableIdentityBinding: "Member disabled",
  RegisterRecord: "Record created",
};

const REASONS: Record<string, string> = {
  UpstreamRevoked: "the share it was delegated from was revoked",
  UpstreamNarrowed: "the share it was delegated from no longer covers it",
  UpstreamExpired: "the share it was delegated from expired",
  GrantorPathLost: "the person who delegated it lost the access it came from",
};

/** Only an Effective command is in force; every other state is shown as such (A11). */
function StateBadge({ state }: { state: string }) {
  if (state === "Effective") {
    return <Badge tone="success">In effect</Badge>;
  }
  if (state === "CommittedAwaitingProjection" || state === "Pending") {
    return <Badge tone="warning">Pending</Badge>;
  }
  return <Badge tone="neutral">{state}</Badge>;
}

function describe(item: RecordSecurityHistoryItemResponse): string[] {
  return item.changes.map((change) => {
    const subject = change.subjectDisplayName ?? change.subjectId ?? "unknown";
    switch (change.changeKind) {
      case "OwnerAdded":
        return `New owner: ${change.subjectKind === "Team" ? `team ${subject}` : subject}`;
      case "OwnerRemoved":
        return `Previous owner: ${change.subjectKind === "Team" ? `team ${subject}` : subject}`;
      case "ShareCreated":
        return `Share ${change.recordShareId ?? ""} created`;
      case "ShareScopeChanged":
        return `Share ${change.recordShareId ?? ""} changed${change.shareExpiresOn ? `, expires ${getActiveFormatter().instant(change.shareExpiresOn)}` : ""}`;
      case "ShareEnded":
        return change.shareEndState === "Invalidated"
          ? `Share ${change.recordShareId ?? ""} ended because ${REASONS[change.invalidationReasonCode ?? ""] ?? "its delegation was no longer valid"}`
          : `Share ${change.recordShareId ?? ""} ${change.shareEndState === "Expired" ? "expired" : "revoked"}`;
      default:
        return change.changeKind;
    }
  });
}

/**
 * C10 M3 (WP-0 Section 3.5, D10-11): the record's sharing and ownership changes as the authorization authority committed them,
 * newest first. A change awaiting projection is labelled Pending and never presented as in effect.
 */
export function RecordSecurityHistoryCard({ formRecordId }: { formRecordId: string }) {
  useFormatter();
  const api = useApi();
  const history = useQuery({
    queryKey: ["shares", formRecordId, "authority-history"],
    queryFn: ({ signal }) => api.shares.history(formRecordId, signal).then((r) => r.result),
  });
  return (
    <Card
      title="Sharing and ownership changes"
      aria-label="Sharing and ownership changes"
      actions={
        <Button size="sm" variant="secondary" busy={history.isFetching} onClick={() => void history.refetch()}>
          Refresh
        </Button>
      }
    >
      {history.isPending ? <LoadingBlock label="Loading sharing and ownership changes" /> : null}
      {history.isError ? <ErrorPanel error={history.error} onRetry={() => void history.refetch()} /> : null}
      {history.data ? (
        history.data.items.length === 0 ? (
          <p className="text-sm text-muted">No sharing or ownership changes yet.</p>
        ) : (
          <ol className="flex flex-col gap-3" aria-label="Changes, newest first">
            {history.data.items.map((item) => (
              <li key={item.securityCommandId} className="rounded-control border border-line p-3">
                <div className="flex flex-wrap items-center gap-2">
                  <span className="font-medium">{COMMAND_LABELS[item.commandKind] ?? item.commandKind}</span>
                  <StateBadge state={item.commandState} />
                  <span className="text-sm text-muted">{getActiveFormatter().instant(item.occurredOn)}</span>
                  {item.actorDisplayName ? <span className="text-sm text-muted">by {item.actorDisplayName}</span> : null}
                </div>
                <ul className="mt-1 list-disc pl-5 text-sm">
                  {describe(item).map((line, index) => (
                    <li key={index}>{line}</li>
                  ))}
                </ul>
              </li>
            ))}
          </ol>
        )
      ) : null}
      {history.data?.truncated ? <StatusMessage tone="info" title="Only the most recent changes are shown." /> : null}
      <p className="mt-2 text-sm text-muted">From the independent authorization authority. Pending changes are not yet in effect.</p>
    </Card>
  );
}

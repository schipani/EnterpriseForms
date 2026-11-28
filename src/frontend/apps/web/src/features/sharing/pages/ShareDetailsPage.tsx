import { presentError, useApi } from "@enterpriseforms/app-runtime";
import { Badge, Button, buttonClass, Card, DataTable, DescriptionList, LoadingBlock, PageHeader, StatusMessage, getActiveFormatter, useFormatter } from "@enterpriseforms/ui";
import { useQuery } from "@tanstack/react-query";
import { Link, useParams } from "@tanstack/react-router";
import { ErrorPanel } from "../../support";

const STATUS_TEXT: Record<string, string> = {
  Active: "In effect",
  Expired: "Expired: gives no access",
  Invalid: "No longer valid: gives no access",
  LegacyUnresolved: "Granted before delegation was recorded",
};

/**
 * PG-023 Share details and effective permissions (C10 M3, WP-0 Section 3.5): one share as the authorization authority committed it, and
 * what its recipient can do through it now (derived scope, upstream chain, expiry, latest revision). Readers are the record's owner and a
 * Manager on the share's lineage; anyone else sees the same "not available" answer as for an unknown share.
 */
export function ShareDetailsPage() {
  useFormatter();
  const { formRecordId, recordShareId } = useParams({ from: "/app/records/$formRecordId/sharing/$recordShareId" });
  const api = useApi();
  const details = useQuery({ queryKey: ["share-details", formRecordId, recordShareId], queryFn: ({ signal }) => api.shares.get(formRecordId, recordShareId, signal).then((r) => r.result), refetchOnMount: "always" });
  const scope = useQuery({ queryKey: ["share-effective-scope", formRecordId, recordShareId], queryFn: ({ signal }) => api.shares.effectiveScope(formRecordId, recordShareId, signal).then((r) => r.result), refetchOnMount: "always" });
  const back = (
    <Link to="/app/records/$formRecordId/sharing" params={{ formRecordId }} className={buttonClass("secondary")}>
      Back to sharing
    </Link>
  );

  if (details.isPending) {
    return <LoadingBlock label="Reading the share from the authorization authority" />;
  }

  if (details.isError) {
    const notAvailable = presentError(details.error).status === 404;
    return (
      <>
        <PageHeader title="Share details" actions={back} />
        {notAvailable ? (
          <StatusMessage tone="info" title="This share is not available to you">
            Only the record's owner and a Manager the share was delegated through can see a share's details.
          </StatusMessage>
        ) : (
          <ErrorPanel error={details.error} onRetry={() => void details.refetch()} />
        )}
      </>
    );
  }

  const share = details.data;
  const formatter = getActiveFormatter();
  return (
    <>
      <PageHeader
        title="Share details"
        lead={share.recipientDisplayName ?? share.recipientUserId ?? share.recipientTeamId ?? "Share"}
        actions={
          <>
            {back}
            <Button variant="secondary" busy={details.isFetching || scope.isFetching} onClick={() => void Promise.all([details.refetch(), scope.refetch()])}>
              Refresh
            </Button>
          </>
        }
      />
      <div className="flex flex-col gap-4">
        <Card title="Share">
          <DescriptionList
            items={[
              { term: "State", detail: <Badge tone={share.shareStatus === "Active" ? "success" : "neutral"}>{share.shareStatus}</Badge> },
              { term: "Recipient", detail: share.recipientDisplayName ?? share.recipientUserId ?? (share.recipientTeamId ? `Team ${share.recipientTeamId}` : "None") },
              { term: "Role", detail: share.recipientRole ?? "None" },
              { term: "Granted by", detail: share.grantorDisplayName ?? share.grantorUserId ?? "Unknown" },
              { term: "Delegation", detail: share.parentRecordShareId ? `Delegated (level ${share.delegationDepth})` : share.legacyLineageUnresolved ? "Granted before delegation was recorded" : "Granted by the owner" },
              { term: "Expires", detail: share.shareExpiresOn ? formatter.instant(share.shareExpiresOn) : "No expiry" },
              { term: "Effective expiry", detail: share.effectiveExpiresOn ? formatter.instant(share.effectiveExpiresOn) : "None" },
              { term: "Share revision", detail: String(share.shareRevision) },
              { term: "Fields", detail: share.fieldKeys.length > 0 ? share.fieldKeys.join(", ") : "None" },
            ]}
          />
          {share.invalidationReasonCode ? <StatusMessage tone="info" title={`Ended: ${share.invalidationReasonCode}`} /> : null}
        </Card>
        {share.documents.length > 0 ? (
          <Card title="Documents">
            <DataTable
              caption="Documents in this share"
              rowKey={(row) => row.fileAttachmentId}
              rows={share.documents}
              columns={[
                { key: "document", header: "Document", isRowHeader: true, render: (row) => row.fileAttachmentId },
                { key: "download", header: "Download", render: (row) => (row.canDownloadDocument ? "Allowed" : "View only") },
                { key: "versions", header: "Versions covered", render: (row) => `Up to version ${row.sharedThroughVersionNumber}` },
              ]}
            />
          </Card>
        ) : null}
        <Card title="What the recipient can do now">
          {scope.isPending ? <LoadingBlock label="Reading the effective scope" /> : null}
          {scope.isError ? <ErrorPanel error={scope.error} onRetry={() => void scope.refetch()} /> : null}
          {scope.data ? (
            <>
              <DescriptionList
                items={[
                  { term: "Status", detail: <Badge tone={scope.data.status === "Active" ? "success" : "neutral"}>{STATUS_TEXT[scope.data.status] ?? scope.data.status}</Badge> },
                  { term: "Shared values", detail: String(scope.data.fieldValueItemIds.length) },
                  { term: "Documents", detail: `${scope.data.documents.length} (${scope.data.documents.filter((d) => d.canDownloadDocument).length} downloadable)` },
                  { term: "Can share further", detail: scope.data.canDelegate ? "Yes" : "No" },
                ]}
              />
              <h3 className="mt-3 text-sm font-semibold">Delegation chain</h3>
              <ol className="list-decimal pl-5 text-sm" aria-label="Delegation chain, this share first">
                {scope.data.lineage.map((step) => (
                  <li key={step.recordShareId}>
                    Level {step.delegationDepth}
                    {step.shareExpiresOn ? `, expires ${formatter.instant(step.shareExpiresOn)}` : ", no expiry"}
                    {step.recordShareId === recordShareId ? " (this share)" : ""}
                  </li>
                ))}
              </ol>
            </>
          ) : null}
          <p className="mt-2 text-sm text-muted">Decided by the independent authorization authority now. An expired or ended share gives no access, whatever this page last showed.</p>
        </Card>
      </div>
    </>
  );
}

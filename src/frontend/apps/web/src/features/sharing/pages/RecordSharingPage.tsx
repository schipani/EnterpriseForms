import { useApi } from "@enterpriseforms/app-runtime";
import { Button, buttonClass, LoadingBlock, PageHeader, StatusMessage } from "@enterpriseforms/ui";
import { useQuery } from "@tanstack/react-query";
import { Link, useParams } from "@tanstack/react-router";
import { RecordAccessPanel } from "../components/RecordAccessPanel";
import { ErrorPanel } from "../../support";

/**
 * PG-022: display the record's sharing data after the Authority has verified the current caller's access.
 */
export function RecordSharingPage() {
  const { formRecordId } = useParams({ from: "/app/records/$formRecordId/sharing" });
  const api = useApi();

  const projection = useQuery({
    queryKey: ["records", "projection", formRecordId],
    queryFn: ({ signal }) => api.records.projection(formRecordId, signal).then((r) => r.result),
    refetchOnMount: "always",
  });

  if (projection.isPending || (projection.isFetching && !projection.isFetchedAfterMount)) {
    return <LoadingBlock label="Verifying record and sharing permissions" />;
  }

  if (projection.isError) {
    return (
      <>
        <PageHeader title="Sharing" lead="Access denied or unavailable" />
        <ErrorPanel error={projection.error} onRetry={() => void projection.refetch()} />
      </>
    );
  }

  const data = projection.data;
  const backLink = (
    <Link to="/app/records/$formRecordId" params={{ formRecordId }} search={{ tab: "sharing" }} className={buttonClass()}>
      Back to record
    </Link>
  );
  if (!data.permissions.canManageAccess) {
    return (
      <>
        <PageHeader title="Sharing" actions={backLink} />
        <StatusMessage tone="info" title="You cannot manage sharing for this record">
          Only the owner or a delegated Share Manager can view and manage sharing. You can see your own permissions on the record's Sharing and access tab.
        </StatusMessage>
      </>
    );
  }

  return (
    <>
      <PageHeader
        title="Sharing"
        lead={data.formDefinitionName}
        actions={
          <>
            {backLink}
            <Button variant="secondary" aria-label="Refresh sharing data" busy={projection.isFetching} onClick={() => void projection.refetch()}>
              Refresh
            </Button>
          </>
        }
      />
      <RecordAccessPanel formRecordId={formRecordId} projection={data} readOnly />
    </>
  );
}

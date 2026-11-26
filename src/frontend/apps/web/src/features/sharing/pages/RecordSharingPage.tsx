import { useApi, useSession } from "@enterpriseforms/app-runtime";
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
  const { status, session, bootstrap } = useSession();

  if (status !== "authenticated" || !session?.authenticated || !session.organizationId || !session.userId || !bootstrap) {
    return <StatusMessage tone="info" title="Sign in to verify sharing access" />;
  }

  const scope = [session.organizationId, session.userId, session.expiresOn, bootstrap.authorizationRevision] as const;
  return <VerifiedRecordSharingPage key={JSON.stringify([formRecordId, ...scope])} formRecordId={formRecordId} scope={scope} />;
}

function VerifiedRecordSharingPage({ formRecordId, scope }: { formRecordId: string; scope: readonly (string | number | null)[] }) {
  const api = useApi();

  const projection = useQuery({
    queryKey: ["record-sharing", "projection", ...scope, formRecordId],
    queryFn: ({ signal }) => api.records.projection(formRecordId, signal).then((r) => r.result),
    refetchOnMount: "always",
  });

  if (projection.fetchStatus === "paused") {
    return <StatusMessage tone="info" title="Waiting for a connection to verify sharing access" />;
  }

  if (projection.fetchStatus === "fetching" || projection.isPending || (!projection.isFetchedAfterMount && !projection.isError)) {
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
            <Button variant="secondary" onClick={() => void projection.refetch()}>
              Refresh access
            </Button>
          </>
        }
      />
      <RecordAccessPanel formRecordId={formRecordId} projection={data} verifiedScope={scope} />
    </>
  );
}

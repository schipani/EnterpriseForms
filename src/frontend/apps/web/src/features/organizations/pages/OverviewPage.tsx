import { useApi, useSession } from "@enterpriseforms/app-runtime";
import { LoadingBlock } from "@enterpriseforms/ui";
import { useQuery } from "@tanstack/react-query";
import { ErrorPanel } from "../../support";
import { OrganizationLayout } from "../components/OrganizationLayout";
import { DataProtectionOverviewCard, OverviewAggregateCards, SecurityAdministratorsOverviewCard } from "../components/OverviewCards";

export const overviewKey = ["organization", "overview"];

/**
 * PG-041 organization overview (Phase C8 M1). The aggregate endpoint returns independently computed parts, so one lagging
 * projection or an unreachable Authority marks a card "Unavailable" instead of failing the page. The data-protection and
 * Security Administrators cards use their own queries and refresh on their own. Nothing here shows record content.
 */
export function OverviewPage() {
  const api = useApi();
  const session = useSession();
  const allowed = session.hasCapability("organization.manage") || session.hasCapability("security.administer");
  const overview = useQuery({ queryKey: overviewKey, queryFn: ({ signal }) => api.organization.overview(signal).then((r) => r.result), enabled: allowed });
  const policy = useQuery({ queryKey: ["organization", "security-policy"], queryFn: ({ signal }) => api.organization.securityPolicy(signal).then((r) => r.result), enabled: allowed });
  const roster = useQuery({ queryKey: ["organization", "security-administrators"], queryFn: ({ signal }) => api.organization.securityAdministrators(signal).then((r) => r.result), enabled: allowed });
  const unavailable = (query: { isError: boolean }) => (query.isError ? "This card could not be read" : null);

  return (
    <OrganizationLayout title="Organization overview" lead="What this organization is, who administers it and how its data is protected. Every number is decided or projected server-side; nothing here opens a record.">
      {overview.isPending ? <LoadingBlock label="Loading the organization overview" /> : null}
      {overview.isError ? <ErrorPanel error={overview.error} onRetry={() => void overview.refetch()} /> : null}
      <div className="grid grid-cols-1 gap-4 xl:grid-cols-2">
        {overview.data ? <OverviewAggregateCards overview={overview.data} refreshing={overview.isFetching} onRefresh={() => void overview.refetch()} /> : null}
        {overview.data ? (
          <>
            <DataProtectionOverviewCard policy={policy.data} refreshing={policy.isFetching} onRefresh={() => void policy.refetch()} unavailableReason={unavailable(policy)} />
            <SecurityAdministratorsOverviewCard roster={roster.data} refreshing={roster.isFetching} onRefresh={() => void roster.refetch()} unavailableReason={unavailable(roster)} />
          </>
        ) : null}
      </div>
    </OrganizationLayout>
  );
}

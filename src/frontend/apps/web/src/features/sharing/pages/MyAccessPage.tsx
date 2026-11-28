import type { MyAccessEntryResponse } from "@enterpriseforms/api-client";
import { useApi } from "@enterpriseforms/app-runtime";
import { Badge, Button, DataTable, EmptyState, LoadingBlock, PageHeader, getActiveFormatter, useFormatter } from "@enterpriseforms/ui";
import { useQuery } from "@tanstack/react-query";
import { Link } from "@tanstack/react-router";
import { ErrorPanel } from "../../support";

const SOURCE_TEXT: Record<string, string> = {
  Owner: "You own it",
  OwningTeam: "Your team owns it",
  DirectShare: "Shared with you",
  TeamShare: "Shared with your team",
};

/**
 * PG-066 My access and shared-with-me (C10 M3, WP-0 Section 3.5): the records you can reach and why, as the authorization authority
 * decides now. Expired shares are not listed; shares expiring within the authority's warning window are marked. No other person's
 * access is shown.
 */
export function MyAccessPage() {
  useFormatter();
  const api = useApi();
  const access = useQuery({ queryKey: ["me", "access"], queryFn: ({ signal }) => api.shares.mine(signal).then((r) => r.result), refetchOnMount: "always" });
  const formatter = getActiveFormatter();
  return (
    <>
      <PageHeader
        title="My access"
        lead="Records you own and records shared with you or your teams"
        actions={
          <Button variant="secondary" busy={access.isFetching} onClick={() => void access.refetch()}>
            Refresh
          </Button>
        }
      />
      {access.isPending ? <LoadingBlock label="Loading your access" /> : null}
      {access.isError ? <ErrorPanel error={access.error} onRetry={() => void access.refetch()} /> : null}
      {access.data ? (
        access.data.entries.length === 0 ? (
          <EmptyState title="No records yet">Records you create and records shared with you appear here.</EmptyState>
        ) : (
          <DataTable<MyAccessEntryResponse>
            caption="Your access, as decided by the authorization authority"
            rowKey={(row) => `${row.formRecordId}:${row.accessSource}:${row.recordShareId ?? ""}:${row.teamId ?? ""}`}
            rows={access.data.entries}
            columns={[
              {
                key: "record",
                header: "Record",
                isRowHeader: true,
                render: (row) => (
                  <Link to="/app/records/$formRecordId" params={{ formRecordId: row.formRecordId }}>
                    {row.formDefinitionName ?? "Record"}
                  </Link>
                ),
              },
              { key: "source", header: "Why", render: (row) => SOURCE_TEXT[row.accessSource] ?? row.accessSource },
              { key: "role", header: "Role", render: (row) => row.recipientRole ?? "Owner" },
              {
                key: "expires",
                header: "Expires",
                render: (row) =>
                  row.effectiveExpiresOn ? (
                    <span className="flex flex-wrap items-center gap-2">
                      {formatter.instant(row.effectiveExpiresOn)}
                      {row.expiringSoon ? <Badge tone="warning">Expiring soon</Badge> : null}
                    </span>
                  ) : (
                    "No expiry"
                  ),
              },
            ]}
          />
        )
      ) : null}
      {access.data?.truncated ? <p className="mt-2 text-sm text-muted">Only the first entries are shown.</p> : null}
    </>
  );
}

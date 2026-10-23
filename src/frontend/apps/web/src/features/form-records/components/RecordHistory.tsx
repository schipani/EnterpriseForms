import { useApi } from "@enterpriseforms/app-runtime";
import type { RecordVersionItemResponse, RecordVersionListResponse } from "@enterpriseforms/api-client";
import { Badge, Button, Card, DataTable, EmptyState, LoadingBlock } from "@enterpriseforms/ui";
import { useInfiniteQuery } from "@tanstack/react-query";
import { Link } from "@tanstack/react-router";
import { ErrorPanel } from "../../support";
import { actorText, changeText } from "./historyModel";
import { formatDateTime } from "./recordList";

/**
 * Phase C6 M2 — History tab of the record workspace (owners and anyone else who can read the whole record). The list of saved
 * versions comes from the Authorization Authority, oldest first; opening a version checks it again before it is shown.
 */
export function RecordHistory({ formRecordId }: { formRecordId: string }) {
  const api = useApi();
  const history = useInfiniteQuery<RecordVersionListResponse>({
    queryKey: ["records", "versions", formRecordId],
    initialPageParam: undefined as number | undefined,
    queryFn: ({ pageParam, signal }) => api.records.versions(formRecordId, pageParam as number | undefined, signal).then((r) => r.result),
    getNextPageParam: (last) => (last.nextAfterRevision === null || last.nextAfterRevision === undefined ? undefined : Number(last.nextAfterRevision)),
  });
  const rows = (history.data?.pages ?? []).flatMap((page) => page.versions);
  const current = Number(history.data?.pages[0]?.currentContentRevision ?? 0);

  return (
    <Card
      title="Version history"
      actions={
        <Button variant="secondary" size="sm" aria-label="Refresh version history" busy={history.isFetching} onClick={() => void history.refetch()}>
          Refresh
        </Button>
      }
    >
      <p className="text-sm text-muted">Every saved version of this record, oldest first. Versions are read-only; a version is checked again each time you open it.</p>
      {history.isPending ? <LoadingBlock label="Loading version history" /> : null}
      {history.isError ? <ErrorPanel error={history.error} onRetry={() => void history.refetch()} /> : null}
      {history.data && rows.length === 0 ? <EmptyState title="No saved versions yet">Versions appear here once the record has been saved.</EmptyState> : null}
      {rows.length > 0 ? (
        <>
          <DataTable
            caption="Saved versions of this record"
            rowKey={(row: RecordVersionItemResponse) => String(row.contentRevision)}
            rows={rows}
            columns={[
              {
                key: "version",
                header: "Version",
                isRowHeader: true,
                render: (row) => (
                  <span className="flex flex-wrap items-center gap-2">
                    <span>Version {String(row.contentRevision)}</span>
                    {row.isCurrent ? <Badge tone="success">Current</Badge> : null}
                  </span>
                ),
              },
              { key: "saved", header: "Saved", render: (row) => formatDateTime(row.acceptedOn) },
              { key: "by", header: "By", render: (row) => actorText(row) },
              {
                key: "change",
                header: "Change",
                render: (row) => {
                  const change = changeText(row);
                  return (
                    <span className="flex flex-col">
                      <span className={change.text === "Not recorded" ? "text-muted" : undefined}>{change.text}</span>
                      {change.note ? <span className="text-xs text-muted">{change.note}</span> : null}
                    </span>
                  );
                },
              },
              {
                key: "actions",
                header: "Actions",
                isActions: true,
                render: (row) =>
                  row.isCurrent ? (
                    <span className="text-sm text-muted">Shown on the record</span>
                  ) : !row.verifiableNow ? (
                    <span className="text-sm text-muted">Can't be opened now</span>
                  ) : (
                    <Link to="/app/records/$formRecordId/versions/$contentRevision" params={{ formRecordId, contentRevision: String(row.contentRevision) }} aria-label={`View version ${String(row.contentRevision)} of ${current}`}>
                      <Button size="sm" variant="secondary">
                        View
                      </Button>
                    </Link>
                  ),
              },
            ]}
          />
          {history.hasNextPage ? (
            <div className="mt-3">
              <Button variant="secondary" size="sm" busy={history.isFetchingNextPage} onClick={() => void history.fetchNextPage()}>
                Show more versions
              </Button>
            </div>
          ) : null}
        </>
      ) : null}
    </Card>
  );
}

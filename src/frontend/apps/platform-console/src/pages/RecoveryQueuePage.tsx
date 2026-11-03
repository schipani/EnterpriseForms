import { Badge, Button, Card, DataTable, EmptyState, LoadingBlock, Pager, Select, StatusMessage, TextInput, useDebouncedValue, type DataTableColumn } from "@enterpriseforms/ui";
import { keepPreviousData, useQuery } from "@tanstack/react-query";
import { useEffect, useRef, useState } from "react";
import { consoleApi, type RecoveryItem } from "../api/client";
import { describeError } from "../app/errors";
import { formatDateTime, type StateTone as StatusTone } from "../app/format";
import { navigate, organizationPath, recoveryPath } from "../app/router";
import { useConsoleSession } from "../app/session";
import { PAGE_SIZE, useNarrowViewport } from "./ApprovalsPage";

export const recoveryStatusFilters: Array<{ value: string; label: string }> = [
  { value: "all", label: "All" },
  { value: "open", label: "Open" },
  { value: "awaiting_approvals", label: "Awaiting approvals" },
  { value: "waiting_period", label: "Waiting period" },
  { value: "ready", label: "Ready for recovery" },
  { value: "objected", label: "Blocked by objection" },
  { value: "recovered", label: "Recovered" },
  { value: "closed", label: "Expired or cancelled" },
];

export const recoveryStateLabel: Record<string, string> = {
  AwaitingApprovals: "Awaiting approvals",
  WaitingPeriod: "Waiting period",
  ReadyForRecovery: "Ready for recovery",
  BlockedByObjection: "Blocked by objection",
  Recovering: "Recovering",
  Recovered: "Recovered",
  Cancelled: "Cancelled",
  Expired: "Expired",
  Invalidated: "Invalidated",
};

export const recoveryStateTone = (state: string): StatusTone =>
  state === "Recovered" ? "success" : state === "BlockedByObjection" ? "danger" : state === "Cancelled" || state === "Expired" || state === "Invalidated" ? "neutral" : state === "ReadyForRecovery" || state === "Recovering" ? "info" : "warning";

/** Progress in words from the Authority's own numbers; the colour only repeats it. */
export function recoveryChip(item: Pick<RecoveryItem, "displayState" | "approvalsRecorded" | "approvalsRequired" | "approvedByMe">): { text: string; tone: StatusTone } {
  if (item.displayState === "AwaitingApprovals") {
    const text = `Approvals ${Number(item.approvalsRecorded)} of ${Number(item.approvalsRequired)}`;
    return { text: item.approvedByMe ? `${text} · you approved` : text, tone: Number(item.approvalsRecorded) > 0 ? "warning" : "neutral" };
  }
  return { text: recoveryStateLabel[item.displayState] ?? item.displayState, tone: recoveryStateTone(item.displayState) };
}

function RecoveryCard({ r }: { r: RecoveryItem }) {
  const chip = recoveryChip(r);
  return (
    <li>
      <Card as="article" aria-label={`${r.organizationDisplayName} ${r.incidentReference}`}>
        <div className="flex flex-wrap items-start justify-between gap-2">
          <p className="min-w-0 break-words font-semibold">{r.organizationDisplayName}</p>
          <Badge tone={chip.tone}>{chip.text}</Badge>
        </div>
        <dl className="grid grid-cols-[max-content_minmax(0,1fr)] gap-x-4 gap-y-1 text-sm">
          <dt className="text-muted">Incident</dt>
          <dd className="break-words">{r.incidentReference}</dd>
          <dt className="text-muted">Replacements</dt>
          <dd>{Number(r.replacementCount)}</dd>
          <dt className="text-muted">Opened by</dt>
          <dd className="break-words">{r.openedByOperatorDisplayName}</dd>
          <dt className="text-muted">Opened</dt>
          <dd>{formatDateTime(r.openedOn)}</dd>
          <dt className="text-muted">Waiting period ends</dt>
          <dd>{formatDateTime(r.waitingPeriodEndsOn)}</dd>
        </dl>
        <Button variant="secondary" size="sm" className="self-start" aria-label={`Review ${r.organizationDisplayName} ${r.incidentReference}`} onClick={() => navigate(recoveryPath(r.caseId))}>
          Review
        </Button>
      </Card>
    </li>
  );
}

/** PG-075 Owner recovery: cases and the organizations that need one. Paging, search and filtering are the Authority's (server-side, cursor-based). */
export function RecoveryQueuePage() {
  const { touch, handleError } = useConsoleSession();
  const initial = new URLSearchParams(window.location.search);
  const [search, setSearch] = useState(initial.get("q") ?? "");
  const [status, setStatus] = useState(recoveryStatusFilters.some((f) => f.value === initial.get("status")) ? initial.get("status")! : "all");
  const [cursors, setCursors] = useState<Array<string | undefined>>([undefined]);
  const q = useDebouncedValue(search.trim());
  const headingRef = useRef<HTMLHeadingElement>(null);
  const narrow = useNarrowViewport();

  useEffect(() => headingRef.current?.focus(), []);
  useEffect(() => {
    const params = new URLSearchParams();
    if (q) params.set("q", q);
    if (status !== "all") params.set("status", status);
    window.history.replaceState(null, "", window.location.pathname + (params.size ? `?${params.toString()}` : ""));
    setCursors([undefined]);
  }, [q, status]);

  const cursor = cursors[cursors.length - 1];
  const queue = useQuery({
    queryKey: ["recovery", q, status, cursor ?? ""],
    queryFn: () => consoleApi.recoveryQueue({ q, status, cursor, limit: PAGE_SIZE }),
    placeholderData: keepPreviousData,
  });
  const needing = useQuery({ queryKey: ["recovery-needing"], queryFn: () => consoleApi.organizations({ q: "", filter: "needs_owners", limit: 10 }) });

  useEffect(() => {
    if (queue.isSuccess && !queue.isPlaceholderData) {
      touch();
    }
  }, [queue.isSuccess, queue.isPlaceholderData, queue.dataUpdatedAt, touch]);
  useEffect(() => {
    if (queue.isError) {
      handleError(queue.error);
    }
  }, [queue.isError, queue.error, handleError]);
  useEffect(() => {
    if (needing.isError) {
      handleError(needing.error);
    }
  }, [needing.isError, needing.error, handleError]);

  const columns: Array<DataTableColumn<RecoveryItem>> = [
    { key: "organization", header: "Organization", isRowHeader: true, render: (r) => r.organizationDisplayName },
    { key: "incident", header: "Incident", render: (r) => r.incidentReference },
    { key: "replacements", header: "Replacements", render: (r) => Number(r.replacementCount) },
    { key: "openedBy", header: "Opened by", render: (r) => r.openedByOperatorDisplayName },
    { key: "opened", header: "Opened", render: (r) => formatDateTime(r.openedOn) },
    { key: "waiting", header: "Waiting period ends", render: (r) => formatDateTime(r.waitingPeriodEndsOn) },
    {
      key: "progress",
      header: "Progress",
      render: (r) => {
        const chip = recoveryChip(r);
        return <Badge tone={chip.tone}>{chip.text}</Badge>;
      },
    },
    {
      key: "actions",
      header: "Actions",
      isActions: true,
      render: (r) => (
        <Button variant="secondary" size="sm" aria-label={`Review ${r.organizationDisplayName} ${r.incidentReference}`} onClick={() => navigate(recoveryPath(r.caseId))}>
          Review
        </Button>
      ),
    },
  ];

  const page = queue.data;
  const total = page ? Number(page.totalCount) : 0;
  const error = queue.isError ? describeError(queue.error) : null;
  const needingError = needing.isError ? describeError(needing.error) : null;
  const pageStart = (cursors.length - 1) * PAGE_SIZE + 1;
  return (
    <div className="flex flex-col gap-4">
      <div>
        <h1 ref={headingRef} tabIndex={-1} className="text-2xl font-semibold">
          Owner recovery
        </h1>
        <p className="mt-1 text-sm text-muted">
          Restores owners to an organization that has lost them. Each case needs approval from three different operators and a waiting period during which an owner can object.
        </p>
      </div>
      <Card title="Organizations requiring recovery">
        {needing.isLoading ? <LoadingBlock label="Loading organizations requiring recovery" /> : null}
        {needingError ? (
          <StatusMessage tone={needingError.tone} title={needingError.title}>
            {needingError.message}
            {needingError.reference ? ` Reference ${needingError.reference}.` : ""}
          </StatusMessage>
        ) : null}
        {needing.data ? (
          needing.data.items.length === 0 ? (
            <p className="text-sm text-muted">No organization currently has fewer than two enabled owners.</p>
          ) : (
            <ul aria-label="Organizations requiring recovery" className="flex list-none flex-col gap-2 p-0">
              {needing.data.items.map((o) => (
                <li key={o.organizationId} className="flex flex-wrap items-center justify-between gap-2 rounded-control border border-line p-3">
                  <div className="min-w-0">
                    <p className="break-words font-medium">{o.organizationDisplayName}</p>
                    <p className="text-help text-muted">
                      {Number(o.enabledOwnerCount)} enabled {Number(o.enabledOwnerCount) === 1 ? "owner" : "owners"} · {Number(o.memberCount)} members
                    </p>
                  </div>
                  <div className="flex flex-wrap gap-2">
                    <Button variant="ghost" size="sm" aria-label={`Details for ${o.organizationDisplayName}`} onClick={() => navigate(organizationPath(o.organizationId))}>
                      Details
                    </Button>
                    <Button variant="secondary" size="sm" aria-label={`Create recovery case for ${o.organizationDisplayName}`} onClick={() => navigate(`/platform/recovery/new?organizationId=${o.organizationId}`)}>
                      Create recovery case
                    </Button>
                  </div>
                </li>
              ))}
            </ul>
          )
        ) : null}
      </Card>
      <div className="grid gap-3 rounded-panel border border-line bg-surface p-4 sm:grid-cols-[minmax(0,2fr)_minmax(0,1fr)]">
        <TextInput label="Search" type="search" value={search} onChange={(event) => setSearch(event.target.value)} placeholder="Organization name or incident" maxLength={200} autoComplete="off" />
        <Select label="Status" value={status} onValueChange={setStatus} options={recoveryStatusFilters} />
      </div>
      {error ? (
        <StatusMessage
          tone={error.tone}
          title={error.title}
          actions={
            error.retryable || error.reload ? (
              <Button variant="secondary" size="sm" onClick={() => void queue.refetch()}>
                Try again
              </Button>
            ) : undefined
          }
        >
          {error.message}
          {error.reference ? ` Reference ${error.reference}.` : ""}
        </StatusMessage>
      ) : null}
      {queue.isLoading ? <LoadingBlock label="Loading recovery cases" /> : null}
      {page ? (
        <div className="flex flex-col gap-3" aria-busy={queue.isFetching}>
          {page.items.length === 0 ? (
            <EmptyState title="No recovery cases match">{status === "all" && !q ? "Create a case from an organization that needs owners." : "Change the search or status filter."}</EmptyState>
          ) : narrow ? (
            <ul aria-label="Recovery cases" className="flex list-none flex-col gap-3 p-0">
              {page.items.map((r) => (
                <RecoveryCard key={r.caseId} r={r} />
              ))}
            </ul>
          ) : (
            <DataTable caption="Recovery cases" columns={columns} rows={page.items} rowKey={(r) => r.caseId} />
          )}
          <Pager
            label="Recovery case pages"
            noun="cases"
            pageStart={page.items.length === 0 ? 0 : pageStart}
            itemCount={page.items.length}
            totalCount={total}
            pageSize={PAGE_SIZE}
            hasPrevious={cursors.length > 1}
            hasNext={Boolean(page.nextCursor)}
            busy={queue.isFetching}
            onPrevious={() => setCursors((c) => c.slice(0, -1))}
            onNext={() => setCursors((c) => (page.nextCursor ? [...c, page.nextCursor] : c))}
          />
        </div>
      ) : null}
    </div>
  );
}

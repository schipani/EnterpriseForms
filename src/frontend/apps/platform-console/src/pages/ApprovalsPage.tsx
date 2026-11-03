import { Badge, Button, Card, DataTable, EmptyState, LoadingBlock, Pager, Select, StatusMessage, TextInput, useDebouncedValue, type DataTableColumn } from "@enterpriseforms/ui";
import { keepPreviousData, useQuery } from "@tanstack/react-query";
import { useEffect, useRef, useState, useSyncExternalStore } from "react";
import { consoleApi, type RequestItem } from "../api/client";
import { describeError } from "../app/errors";
import { approvalChip, formatDateTime, identitySummary, roleLabel, statusFilters } from "../app/format";
import { navigate, reviewPath } from "../app/router";
import { useConsoleSession } from "../app/session";

export const PAGE_SIZE = 15;

/** Below this width the queue is shown as cards instead of a table (no horizontal scrolling table on phones). */
export const CARD_BREAKPOINT = 768;
const narrowQuery = `(max-width: ${CARD_BREAKPOINT - 1}px)`;
export function useNarrowViewport(): boolean {
  return useSyncExternalStore(
    (notify) => {
      if (typeof window.matchMedia !== "function") return () => undefined;
      const media = window.matchMedia(narrowQuery);
      media.addEventListener("change", notify);
      return () => media.removeEventListener("change", notify);
    },
    () => (typeof window.matchMedia === "function" ? window.matchMedia(narrowQuery).matches : false),
    () => false,
  );
}

/** One request as a card: the same facts and the same Review details action as a table row, in reading order. */
function RequestCard({ r }: { r: RequestItem }) {
  const identity = identitySummary(r.target);
  const chip = approvalChip(r);
  return (
    <li>
      <Card as="article" aria-label={`${r.displayNameHint}, ${r.organizationDisplayName}`}>
        <div className="flex flex-wrap items-start justify-between gap-2">
          <div className="min-w-0">
            <p className="break-words font-semibold">{r.displayNameHint}</p>
            <p className="break-words text-help text-muted">
              {identity.subject}
              {identity.issuer ? ` · ${identity.issuer}` : ""}
            </p>
          </div>
          <Badge tone={chip.tone}>{chip.text}</Badge>
        </div>
        <dl className="grid grid-cols-[max-content_minmax(0,1fr)] gap-x-4 gap-y-1 text-sm">
          <dt className="text-muted">Organization</dt>
          <dd className="break-words">{r.organizationDisplayName}</dd>
          <dt className="text-muted">Requested role</dt>
          <dd>{roleLabel(r.initialRoleName)}</dd>
          <dt className="text-muted">Requested</dt>
          <dd>{formatDateTime(r.requestedOn)}</dd>
          <dt className="text-muted">Expires</dt>
          <dd>{formatDateTime(r.expiresOn)}</dd>
        </dl>
        <Button variant="secondary" size="sm" className="self-start" aria-label={`Review details for ${r.displayNameHint} in ${r.organizationDisplayName}`} onClick={() => navigate(reviewPath(r.organizationId, r.requestId))}>
          Review details
        </Button>
      </Card>
    </li>
  );
}

/** PG-070 Operator approvals: the cross-organization membership-request queue. Paging, search and filtering are the Authority's (server-side, cursor-based, newest first). */
export function ApprovalsPage() {
  const { touch, handleError } = useConsoleSession();
  const initial = new URLSearchParams(window.location.search);
  const [search, setSearch] = useState(initial.get("q") ?? "");
  const [status, setStatus] = useState(statusFilters.some((f) => f.value === initial.get("status")) ? initial.get("status")! : "needs_decision");
  // Cursor-based paging: `cursors[i]` is the cursor that fetched page i (page 0 has none), so Previous is a pop, not a client-side slice.
  const [cursors, setCursors] = useState<Array<string | undefined>>([undefined]);
  const q = useDebouncedValue(search.trim());
  const headingRef = useRef<HTMLHeadingElement>(null);
  const narrow = useNarrowViewport();

  useEffect(() => {
    const params = new URLSearchParams();
    if (q) params.set("q", q);
    if (status !== "needs_decision") params.set("status", status);
    window.history.replaceState(null, "", window.location.pathname + (params.size ? `?${params.toString()}` : ""));
    setCursors([undefined]);
  }, [q, status]);

  const cursor = cursors[cursors.length - 1];
  const queue = useQuery({
    queryKey: ["queue", q, status, cursor ?? ""],
    queryFn: () => consoleApi.queue({ q, status, cursor, limit: PAGE_SIZE }),
    placeholderData: keepPreviousData,
  });

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

  const columns: Array<DataTableColumn<RequestItem>> = [
    { key: "organization", header: "Organization", isRowHeader: true, render: (r) => r.organizationDisplayName },
    {
      key: "person",
      header: "Requested person",
      render: (r) => {
        const identity = identitySummary(r.target);
        return (
          <div className="min-w-0">
            <p className="font-medium">{r.displayNameHint}</p>
            <p className="text-help text-muted">
              {identity.subject}
              {identity.issuer ? ` · ${identity.issuer}` : ""}
            </p>
          </div>
        );
      },
    },
    { key: "role", header: "Requested role", render: (r) => roleLabel(r.initialRoleName) },
    { key: "requested", header: "Requested", render: (r) => formatDateTime(r.requestedOn) },
    { key: "expires", header: "Expires", render: (r) => formatDateTime(r.expiresOn) },
    {
      key: "progress",
      header: "Approval progress",
      render: (r) => {
        const chip = approvalChip(r);
        return <Badge tone={chip.tone}>{chip.text}</Badge>;
      },
    },
    {
      key: "actions",
      header: "Actions",
      isActions: true,
      render: (r) => (
        <Button variant="secondary" size="sm" aria-label={`Review details for ${r.displayNameHint} in ${r.organizationDisplayName}`} onClick={() => navigate(reviewPath(r.organizationId, r.requestId))}>
          Review details
        </Button>
      ),
    },
  ];

  const page = queue.data;
  const total = page ? Number(page.totalCount) : 0;
  const error = queue.isError ? describeError(queue.error) : null;
  const pageStart = (cursors.length - 1) * PAGE_SIZE + 1;
  return (
    <div className="flex flex-col gap-4">
      <div>
        <h1 ref={headingRef} className="text-2xl font-semibold">
          Operator approvals
        </h1>
        <p className="mt-1 text-sm text-muted">Membership requests from every organization. Each request needs approval from two different Platform Operators.</p>
      </div>
      <div className="grid gap-3 rounded-panel border border-line bg-surface p-4 sm:grid-cols-[minmax(0,2fr)_minmax(0,1fr)]">
        <TextInput label="Search" type="search" value={search} onChange={(event) => setSearch(event.target.value)} placeholder="Organization, person or identity" maxLength={200} autoComplete="off" />
        <Select label="Status" value={status} onValueChange={setStatus} options={statusFilters} />
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
      {queue.isLoading ? <LoadingBlock label="Loading requests" /> : null}
      {page ? (
        <div className="flex flex-col gap-3" aria-busy={queue.isFetching}>
          {page.items.length === 0 ? (
            <EmptyState title={status === "needs_decision" && !q ? "No requests need a decision" : "No requests match"}>
              {status === "needs_decision" && !q ? "New membership requests appear here when an organization owner submits them." : "Change the search or status filter."}
            </EmptyState>
          ) : (
            narrow ? (
              <ul aria-label="Membership requests" className="flex list-none flex-col gap-3 p-0">
                {page.items.map((r) => (
                  <RequestCard key={r.requestId} r={r} />
                ))}
              </ul>
            ) : (
              <DataTable caption="Membership requests" columns={columns} rows={page.items} rowKey={(r) => r.requestId} />
            )
          )}
          <Pager
            label="Membership request pages"
            noun="requests"
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

import { Badge, Button, Card, DataTable, EmptyState, LoadingBlock, Pager, Select, StatusMessage, TextInput, useDebouncedValue, type DataTableColumn } from "@enterpriseforms/ui";
import { keepPreviousData, useQuery } from "@tanstack/react-query";
import { useEffect, useRef, useState } from "react";
import { consoleApi, type OnboardingItem } from "../api/client";
import { describeError } from "../app/errors";
import { formatDateTime, type StateTone as StatusTone } from "../app/format";
import { navigate, onboardingPath } from "../app/router";
import { useConsoleSession } from "../app/session";
import { PAGE_SIZE, useNarrowViewport } from "./ApprovalsPage";

export const onboardingStatusFilters: Array<{ value: string; label: string }> = [
  { value: "all", label: "All" },
  { value: "needs_approval", label: "Needs approval" },
  { value: "awaiting_key_custody", label: "Awaiting key provisioning" },
  { value: "initialized", label: "Initialized" },
  { value: "closed", label: "Closed" },
];

export const displayStateLabel: Record<string, string> = {
  AwaitingApproval: "Awaiting approval",
  AwaitingKeyProvisioning: "Awaiting key provisioning",
  Activating: "Activating",
  Active: "Active",
  Rejected: "Rejected",
  Cancelled: "Cancelled",
  Expired: "Expired",
};

export const displayStateTone = (state: string): StatusTone =>
  state === "Active" ? "success" : state === "Rejected" ? "danger" : state === "Cancelled" || state === "Expired" ? "neutral" : state === "Activating" ? "info" : "warning";

/** Progress in words from the Authority's own numbers; the colour only repeats it. */
export function onboardingChip(item: Pick<OnboardingItem, "displayState" | "approvalsRecorded" | "approvalsRequired" | "approvedByMe">): { text: string; tone: StatusTone } {
  if (item.displayState === "AwaitingApproval") {
    const text = `Approvals ${Number(item.approvalsRecorded)} of ${Number(item.approvalsRequired)}`;
    return { text: item.approvedByMe ? `${text} · you approved` : text, tone: Number(item.approvalsRecorded) > 0 ? "warning" : "neutral" };
  }
  return { text: displayStateLabel[item.displayState] ?? item.displayState, tone: displayStateTone(item.displayState) };
}

function OnboardingCard({ r }: { r: OnboardingItem }) {
  const chip = onboardingChip(r);
  return (
    <li>
      <Card as="article" aria-label={r.organizationDisplayName}>
        <div className="flex flex-wrap items-start justify-between gap-2">
          <p className="min-w-0 break-words font-semibold">{r.organizationDisplayName}</p>
          <Badge tone={chip.tone}>{chip.text}</Badge>
        </div>
        <dl className="grid grid-cols-[max-content_minmax(0,1fr)] gap-x-4 gap-y-1 text-sm">
          <dt className="text-muted">Owners</dt>
          <dd>{Number(r.ownerCount)}</dd>
          <dt className="text-muted">Created by</dt>
          <dd className="break-words">{r.createdByOperatorDisplayName}</dd>
          <dt className="text-muted">Created</dt>
          <dd>{formatDateTime(r.createdOn)}</dd>
          <dt className="text-muted">Expires</dt>
          <dd>{formatDateTime(r.expiresOn)}</dd>
        </dl>
        <Button variant="secondary" size="sm" className="self-start" aria-label={`Review ${r.organizationDisplayName}`} onClick={() => navigate(onboardingPath(r.requestId))}>
          Review
        </Button>
      </Card>
    </li>
  );
}

/** PG-074 Onboarding: organization onboarding requests. Paging, search and filtering are the Authority's (server-side, cursor-based). */
export function OnboardingQueuePage() {
  const { touch, handleError } = useConsoleSession();
  const initial = new URLSearchParams(window.location.search);
  const [search, setSearch] = useState(initial.get("q") ?? "");
  const [status, setStatus] = useState(onboardingStatusFilters.some((f) => f.value === initial.get("status")) ? initial.get("status")! : "all");
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
    queryKey: ["onboarding", q, status, cursor ?? ""],
    queryFn: () => consoleApi.onboardingQueue({ q, status, cursor, limit: PAGE_SIZE }),
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

  const columns: Array<DataTableColumn<OnboardingItem>> = [
    { key: "organization", header: "Organization", isRowHeader: true, render: (r) => r.organizationDisplayName },
    { key: "owners", header: "Owners", render: (r) => Number(r.ownerCount) },
    { key: "createdBy", header: "Created by", render: (r) => r.createdByOperatorDisplayName },
    { key: "created", header: "Created", render: (r) => formatDateTime(r.createdOn) },
    { key: "expires", header: "Expires", render: (r) => formatDateTime(r.expiresOn) },
    {
      key: "progress",
      header: "Progress",
      render: (r) => {
        const chip = onboardingChip(r);
        return <Badge tone={chip.tone}>{chip.text}</Badge>;
      },
    },
    {
      key: "actions",
      header: "Actions",
      isActions: true,
      render: (r) => (
        <Button variant="secondary" size="sm" aria-label={`Review ${r.organizationDisplayName}`} onClick={() => navigate(onboardingPath(r.requestId))}>
          Review
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
      <div className="flex flex-wrap items-end justify-between gap-3">
        <div>
          <h1 ref={headingRef} tabIndex={-1} className="text-2xl font-semibold">
            Organization onboarding
          </h1>
          <p className="mt-1 text-sm text-muted">New organizations proposed by Platform Operators. Each request needs approval from two different operators before it can be initialized.</p>
        </div>
        <Button variant="primary" onClick={() => navigate("/platform/onboarding/new")}>
          New organization
        </Button>
      </div>
      <div className="grid gap-3 rounded-panel border border-line bg-surface p-4 sm:grid-cols-[minmax(0,2fr)_minmax(0,1fr)]">
        <TextInput label="Search" type="search" value={search} onChange={(event) => setSearch(event.target.value)} placeholder="Organization name" maxLength={200} autoComplete="off" />
        <Select label="Status" value={status} onValueChange={setStatus} options={onboardingStatusFilters} />
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
      {queue.isLoading ? <LoadingBlock label="Loading onboarding requests" /> : null}
      {page ? (
        <div className="flex flex-col gap-3" aria-busy={queue.isFetching}>
          {page.items.length === 0 ? (
            <EmptyState title="No onboarding requests match">{status === "all" && !q ? "Create a request with New organization." : "Change the search or status filter."}</EmptyState>
          ) : narrow ? (
            <ul aria-label="Onboarding requests" className="flex list-none flex-col gap-3 p-0">
              {page.items.map((r) => (
                <OnboardingCard key={r.requestId} r={r} />
              ))}
            </ul>
          ) : (
            <DataTable caption="Onboarding requests" columns={columns} rows={page.items} rowKey={(r) => r.requestId} />
          )}
          <Pager
            label="Onboarding request pages"
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

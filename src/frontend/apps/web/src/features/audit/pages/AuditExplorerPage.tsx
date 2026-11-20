import { useApi, useSession } from "@enterpriseforms/app-runtime";
import type { AuditExplorerEventResponse } from "@enterpriseforms/api-client";
import { ActionIcons, Badge, Button, Card, Checkbox, DataTable, EmptyState, LoadingBlock, PageHeader, Select, StatusMessage, TextInput, useFormatter } from "@enterpriseforms/ui";
import { useQuery } from "@tanstack/react-query";
import { Link, useSearch } from "@tanstack/react-router";
import { useState } from "react";
import { formatInstant } from "../components/auditExplorerModel";
import { ErrorPanel } from "../../support";
import {
  AUDIT_CATEGORIES,
  AUDIT_OUTCOMES,
  EMPTY_AUDIT_FILTERS,
  FIRST_PAGE,
  currentCursor,
  filtersFromSearch,
  hasAuditFilters,
  nextPage,
  previousPage,
  shortId,
  toAuditQuery,
  validateAuditFilters,
  type AuditFilterState,
  type AuditPageTrail,
} from "../components/auditExplorerModel";

/**
 * PG-054 audit explorer (C8 M4-2). Security Auditors only: navigation is a hint, and every page is authorized and recorded by
 * the Authorization Authority before the server reads any row, so a revoked auditor's next page is refused. Rows are the
 * application's audit projection, not independently verified, and show envelopes only (no record content, free text, field
 * keys or labels). Newest first by event time; this is not commit order.
 */
export function AuditExplorerPage() {
  useFormatter();
  const session = useSession();
  const api = useApi();
  const search = useSearch({ strict: false }) as Record<string, unknown>;
  const [filters, setFilters] = useState<AuditFilterState>(() => filtersFromSearch(search));
  const [trail, setTrail] = useState<AuditPageTrail>(FIRST_PAGE);
  const errors = validateAuditFilters(filters);
  const valid = Object.keys(errors).length === 0;
  const query = toAuditQuery(filters, currentCursor(trail));
  const allowed = session.hasCapability("audit.view");
  const events = useQuery({
    queryKey: ["audit", "events", query],
    queryFn: ({ signal }) => api.audit.events(query, signal).then((r) => r.result),
    enabled: allowed && valid,
    placeholderData: (previous) => previous,
    staleTime: 0,
  });

  if (!allowed) {
    return (
      <>
        <PageHeader title="Audit explorer" />
        <StatusMessage tone="info" title="No permission">
          The audit explorer is available to Security Auditors. Access is decided, and recorded, by the independent Authorization Authority.
        </StatusMessage>
      </>
    );
  }

  // Every filter change starts again at the first page (a cursor belongs to the filters it was issued for).
  const change = (patch: Partial<AuditFilterState>) => {
    setFilters((current) => ({ ...current, ...patch }));
    setTrail(FIRST_PAGE);
  };
  const data = events.data;
  const pageNumber = trail.cursors.length;
  return (
    <>
      <PageHeader
        title="Audit explorer"
        lead="Activity and changes recorded in this organization's audit log."
        actions={
          <Button variant="secondary" size="sm" icon={ActionIcons.refresh} disabled={events.isFetching || !valid} onClick={() => void events.refetch()}>
            Refresh
          </Button>
        }
      />
      <div className="flex flex-col gap-4">
        <StatusMessage tone="info" title="What this log can and cannot tell you">
          These events are held in the application database and are not independently verified: a database administrator could add, change or delete them, and they have no archive receipt yet. Each page you open is authorized and recorded by the Authorization Authority. Events are newest first by the time they were recorded, which is not necessarily the order in which changes were committed.
        </StatusMessage>
        <Card title="Filters">
          <div role="search" aria-label="Filter audit events" className="grid items-start gap-x-4 gap-y-3 sm:grid-cols-2 lg:grid-cols-3">
            <TextInput label="From" type="date" value={filters.fromDate} max={filters.toDate || undefined} onChange={(event) => change({ fromDate: event.target.value })} />
            <TextInput label="To" type="date" value={filters.toDate} min={filters.fromDate || undefined} error={errors.toDate} onChange={(event) => change({ toDate: event.target.value })} />
            <Select label="Category" value={filters.category} options={[{ value: "all", label: "All categories" }, ...AUDIT_CATEGORIES.map((c) => ({ value: c, label: c }))]} onValueChange={(value) => change({ category: value })} />
            <Select label="Outcome" value={filters.outcome} options={[{ value: "all", label: "All outcomes" }, ...AUDIT_OUTCOMES.map((o) => ({ value: o.value, label: o.label }))]} onValueChange={(value) => change({ outcome: value })} />
            <TextInput label="Action code" placeholder="RECORD_VIEWED" value={filters.action} autoComplete="off" spellCheck={false} error={errors.action} onChange={(event) => change({ action: event.target.value })} />
            <TextInput label="Actor (user ID)" value={filters.actorUserId} autoComplete="off" spellCheck={false} error={errors.actorUserId} onChange={(event) => change({ actorUserId: event.target.value })} />
            <TextInput label="Record ID" value={filters.formRecordId} autoComplete="off" spellCheck={false} error={errors.formRecordId} onChange={(event) => change({ formRecordId: event.target.value })} />
            <TextInput label="Entity type" placeholder="form_record" value={filters.entityType} autoComplete="off" spellCheck={false} error={errors.entityType} onChange={(event) => change({ entityType: event.target.value })} />
            <TextInput label="Entity ID" value={filters.entityId} autoComplete="off" spellCheck={false} error={errors.entityId} onChange={(event) => change({ entityId: event.target.value })} />
            <TextInput label="Correlation ID" value={filters.correlationId} autoComplete="off" spellCheck={false} error={errors.correlationId} onChange={(event) => change({ correlationId: event.target.value })} />
            <div className="sm:col-span-2 lg:col-span-3">
              <Checkbox label="Hide audit explorer access (this page's own records)" checked={filters.excludeAuditAccess} onCheckedChange={(checked) => change({ excludeAuditAccess: checked })} />
            </div>
          </div>
          {hasAuditFilters(filters) ? (
            <div className="mt-3">
              <Button variant="ghost" size="sm" icon={ActionIcons.reset} onClick={() => change(EMPTY_AUDIT_FILTERS)}>
                Clear filters
              </Button>
            </div>
          ) : null}
        </Card>
        <Card title="Events">
          <div className="flex flex-col gap-3">
            {events.isPending && valid ? <LoadingBlock label="Loading audit events" /> : null}
            {events.isError ? <ErrorPanel error={events.error} onRetry={() => void events.refetch()} /> : null}
            {data && data.items.length === 0 ? (
              <EmptyState title={pageNumber > 1 ? "No more events" : "No events match"}>{hasAuditFilters(filters) ? "Try a wider period or fewer filters." : "No audit events are recorded for this organization yet."}</EmptyState>
            ) : null}
            {data && data.items.length > 0 ? (
              <>
                <DataTable<AuditExplorerEventResponse> caption="Audit events" rows={[...data.items]} rowKey={(row) => row.auditEventId} stickyActions columns={eventColumns()} />
                <nav aria-label="Audit event pages" className="flex flex-wrap items-center justify-between gap-3">
                  <p className="text-help text-muted" aria-live="polite">
                    Page {pageNumber} · up to {String(data.pageSize)} events per page · access recorded by the Authority (audit-stream revision {String(data.accessEvidence.authorityJournalRevision)})
                  </p>
                  <div className="flex gap-2">
                    <Button variant="secondary" size="sm" disabled={pageNumber === 1 || events.isFetching} onClick={() => setTrail((t) => previousPage(t))}>
                      Previous
                    </Button>
                    <Button variant="secondary" size="sm" disabled={!data.nextCursor || events.isFetching} onClick={() => data.nextCursor && setTrail((t) => nextPage(t, data.nextCursor!))}>
                      Next
                    </Button>
                  </div>
                </nav>
              </>
            ) : null}
            <p className="text-help text-muted">{data?.orderingStatement}</p>
          </div>
        </Card>
      </div>
    </>
  );
}

function eventColumns() {
  return [
    {
      key: "when",
      header: "Recorded (event time)",
      isRowHeader: true,
      render: (row: AuditExplorerEventResponse) => (
        <Link to="/app/security/audit/$auditEventId" params={{ auditEventId: row.auditEventId }} className="whitespace-nowrap underline-offset-4 hover:underline">
          {formatInstant(row.occurredOn)}
        </Link>
      ),
    },
    {
      key: "action",
      header: "Action",
      render: (row: AuditExplorerEventResponse) => (
        <span className="flex flex-col">
          <span>{row.actionLabel}</span>
          <span className="font-mono text-help text-muted">{row.action}</span>
          {!row.recognized ? <Badge tone="warning">Not in catalog</Badge> : null}
        </span>
      ),
    },
    { key: "outcome", header: "Outcome", render: (row: AuditExplorerEventResponse) => <Badge tone={row.outcome === "Success" ? "success" : row.outcome === "Denied" ? "warning" : "neutral"}>{row.outcome}</Badge> },
    { key: "actor", header: "Actor", render: (row: AuditExplorerEventResponse) => row.actor.displayName ?? <span className="text-muted">{row.actor.actorType}</span> },
    { key: "form", header: "Form", render: (row: AuditExplorerEventResponse) => row.formDisplayName ?? <span className="text-muted">—</span> },
    { key: "record", header: "Record ID", render: (row: AuditExplorerEventResponse) => <span className="font-mono" title={row.formRecordId ?? undefined}>{shortId(row.formRecordId)}</span> },
    { key: "category", header: "Category", render: (row: AuditExplorerEventResponse) => row.category },
    {
      key: "actions",
      header: "Actions",
      isActions: true,
      render: (row: AuditExplorerEventResponse) => (
        <Link to="/app/security/audit/$auditEventId" params={{ auditEventId: row.auditEventId }} className="underline underline-offset-4" aria-label={`Open event ${row.actionLabel} recorded ${formatInstant(row.occurredOn)}`}>
          Open
        </Link>
      ),
    },
  ];
}

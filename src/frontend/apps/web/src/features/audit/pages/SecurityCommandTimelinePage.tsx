import { useApi, useSession } from "@enterpriseforms/app-runtime";
import type { SecurityCommandTimelineItemResponse } from "@enterpriseforms/api-client";
import { ActionIcons, Badge, Button, Card, DataTable, EmptyState, LoadingBlock, PageHeader, Select, StatusMessage, TextInput, useFormatter } from "@enterpriseforms/ui";
import { useQuery } from "@tanstack/react-query";
import { useState } from "react";
import { ErrorPanel } from "../../support";
import { AuditViewTabs } from "../components/AuditViewTabs";
import { SecurityHomeHint, isGateClosed } from "../components/SecurityHomeHint";
import { FIRST_PAGE, currentCursor, formatInstant, nextPage, previousPage, shortId, type AuditPageTrail } from "../components/auditExplorerModel";
import {
  EMPTY_TIMELINE_FILTERS,
  TIMELINE_AUDIT_KINDS,
  TIMELINE_COMMAND_KINDS,
  TIMELINE_STATES,
  formatDetail,
  hasTimelineFilters,
  humanizeKey,
  humanizeKind,
  stateLabel,
  toTimelineQuery,
  validateTimelineFilters,
  type TimelineFilterState,
} from "../components/securityCommandTimelineModel";

/**
 * Security-command timeline (C8 M4-3, AC-8.2; a tab of PG-054). Security Auditors only. Every page is read and served by the
 * Authorization Authority from its own database after it recorded the access; receipt by the browser is not attested. Envelopes
 * carry identifiers and codes only: free text, notes, step-up evidence, share scopes and counts are withheld (named, never shown).
 * A command item's fields are read from the Authority's command row, which is not cryptographically verified. "Journal-backed"
 * means a journal entry naming the command was found; the row's fields are not checked against it. "Command row only" items
 * (most rejected commands) have no such entry, which is expected and is not a sign of tampering.
 */
export function SecurityCommandTimelinePage() {
  useFormatter();
  const session = useSession();
  const api = useApi();
  const [filters, setFilters] = useState<TimelineFilterState>(EMPTY_TIMELINE_FILTERS);
  const [trail, setTrail] = useState<AuditPageTrail>(FIRST_PAGE);
  const errors = validateTimelineFilters(filters);
  const valid = Object.keys(errors).length === 0;
  const query = toTimelineQuery(filters, currentCursor(trail));
  const allowed = session.hasCapability("audit.view");
  const timeline = useQuery({
    queryKey: ["audit", "security-commands", query],
    queryFn: ({ signal }) => api.audit.securityCommands(query, signal).then((r) => r.result),
    enabled: allowed && valid,
    placeholderData: (previous) => previous,
    staleTime: 0,
  });

  if (!allowed) {
    return (
      <>
        <PageHeader title="Security commands" />
        <StatusMessage tone="info" title="No permission">
          The security-command timeline is available to Security Auditors. Access is decided, and recorded, by the independent Authorization Authority.
        </StatusMessage>
        <SecurityHomeHint />
      </>
    );
  }

  const change = (patch: Partial<TimelineFilterState>) => {
    setFilters((current) => ({ ...current, ...patch }));
    setTrail(FIRST_PAGE);
  };
  const data = timeline.data;
  const pageNumber = trail.cursors.length;
  return (
    <>
      <PageHeader
        title="Security commands"
        lead="Security and governance commands and allowlisted Authority audit entries for this organization."
        actions={
          <Button variant="secondary" size="sm" icon={ActionIcons.refresh} disabled={timeline.isFetching || !valid} onClick={() => void timeline.refetch()}>
            Refresh
          </Button>
        }
      />
      <div className="flex flex-col gap-4">
        <AuditViewTabs current="commands" />
        <StatusMessage tone="info" title="What this timeline can and cannot tell you">
          This timeline is read by the Authorization Authority from its own database each time you open a page, and the Authority records that you opened it; it does not record that your browser received it. A command's kind, state and details are read from the Authority's command row, which is not cryptographically verified. "Journal-backed" means a journal entry naming the command was found; the command row's fields are not checked against that entry, and this page does not re-verify the journal. Items marked "Command row only", such as most rejected commands, have no such entry: this is expected and is not a sign of tampering. Free-text reasons, notes, step-up evidence, share scopes and search counts are never shown.
        </StatusMessage>
        <Card title="Filters">
          <div role="search" aria-label="Filter security commands" className="grid items-start gap-x-4 gap-y-3 sm:grid-cols-2 lg:grid-cols-4">
            <TextInput label="From" type="date" value={filters.fromDate} max={filters.toDate || undefined} onChange={(event) => change({ fromDate: event.target.value })} />
            <TextInput label="To" type="date" value={filters.toDate} min={filters.fromDate || undefined} error={errors.toDate} onChange={(event) => change({ toDate: event.target.value })} />
            <Select
              label="Kind"
              value={filters.kind}
              options={[
                { value: "all", label: "All kinds" },
                ...TIMELINE_COMMAND_KINDS.map((k) => ({ value: k, label: `Command: ${humanizeKind(k)}` })),
                ...TIMELINE_AUDIT_KINDS.map((k) => ({ value: k, label: `Audit entry: ${humanizeKind(k)}` })),
              ]}
              onValueChange={(value) => change({ kind: value })}
            />
            <Select label="Command state" value={filters.state} options={[{ value: "all", label: "All states" }, ...TIMELINE_STATES.map((s) => ({ value: s.value, label: s.label }))]} onValueChange={(value) => change({ state: value })} />
          </div>
          {filters.state !== "all" ? <p className="mt-2 text-help text-muted">A state filter shows command rows only; audit entries have no state.</p> : null}
          {hasTimelineFilters(filters) ? (
            <div className="mt-3">
              <Button variant="ghost" size="sm" icon={ActionIcons.reset} onClick={() => change(EMPTY_TIMELINE_FILTERS)}>
                Clear filters
              </Button>
            </div>
          ) : null}
        </Card>
        <Card title="Timeline">
          <div className="flex flex-col gap-3">
            {timeline.isPending && valid ? <LoadingBlock label="Loading security commands" /> : null}
            {timeline.isError ? <ErrorPanel error={timeline.error} onRetry={() => void timeline.refetch()} /> : null}
            {timeline.isError && isGateClosed(timeline.error) ? <SecurityHomeHint /> : null}
            {data && data.items.length === 0 ? (
              <EmptyState title={pageNumber > 1 ? "No more items" : "No items match"}>{hasTimelineFilters(filters) ? "Try a wider period or fewer filters." : "No security commands are recorded for this organization yet."}</EmptyState>
            ) : null}
            {data && data.items.length > 0 ? (
              <>
                <DataTable<SecurityCommandTimelineItemResponse> caption="Security commands and audit entries" rows={[...data.items]} rowKey={rowKey} columns={timelineColumns()} />
                <nav aria-label="Security command pages" className="flex flex-wrap items-center justify-between gap-3">
                  <p className="text-help text-muted" aria-live="polite">
                    Page {pageNumber} · up to {String(data.pageSize)} items per page · access recorded by the Authority (audit-stream revision {String(data.accessEvidence.authorityJournalRevision)})
                  </p>
                  <div className="flex gap-2">
                    <Button variant="secondary" size="sm" disabled={pageNumber === 1 || timeline.isFetching} onClick={() => setTrail((t) => previousPage(t))}>
                      Previous
                    </Button>
                    <Button variant="secondary" size="sm" disabled={!data.nextCursor || timeline.isFetching} onClick={() => data.nextCursor && setTrail((t) => nextPage(t, data.nextCursor!))}>
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

function rowKey(row: SecurityCommandTimelineItemResponse): string {
  return row.commandId ?? `${row.evidence?.stream ?? "audit"}:${String(row.evidence?.streamRevision ?? row.occurredOn)}`;
}

function timelineColumns() {
  return [
    { key: "when", header: "Time", isRowHeader: true, render: (row: SecurityCommandTimelineItemResponse) => <span className="whitespace-nowrap">{formatInstant(row.occurredOn)}</span> },
    {
      key: "kind",
      header: "Kind",
      render: (row: SecurityCommandTimelineItemResponse) => (
        <span className="flex flex-col">
          <span>{humanizeKind(row.kind)}</span>
          <span className="font-mono text-help text-muted">{row.kind}</span>
          <span className="text-help text-muted">{row.source === "Command" ? "Command" : "Audit entry"}</span>
        </span>
      ),
    },
    {
      key: "state",
      header: "State",
      render: (row: SecurityCommandTimelineItemResponse) =>
        row.commandState ? (
          <span className="flex flex-col gap-1">
            <Badge tone={row.commandState === "Effective" ? "success" : row.commandState === "Rejected" || row.commandState === "FailedReconciliation" ? "warning" : "neutral"}>{stateLabel(row.commandState)}</Badge>
            {row.rejectionCode ? <span className="font-mono text-help text-muted">{row.rejectionCode}</span> : null}
          </span>
        ) : (
          <span className="text-muted">—</span>
        ),
    },
    {
      key: "actor",
      header: "Actor",
      render: (row: SecurityCommandTimelineItemResponse) => (row.actorUserId ? <span title={row.actorUserId}>{row.actorDisplayName ?? shortId(row.actorUserId)}</span> : <span className="text-muted">—</span>),
    },
    {
      key: "evidence",
      header: "Evidence",
      render: (row: SecurityCommandTimelineItemResponse) => (
        <span className="flex flex-col gap-1">
          {row.evidenceBasis === "JournalBacked" ? <Badge tone="info">Journal-backed</Badge> : <Badge tone="neutral">Command row only</Badge>}
          {row.evidence ? (
            <span className="text-help text-muted" title={row.evidence.entryDigestHex}>
              {row.evidence.stream} #{String(row.evidence.streamRevision)} · archive {row.evidence.archiveState}
            </span>
          ) : null}
          {row.reportedBy === "BusinessRuntime" ? <Badge tone="warning">Reported by the business runtime</Badge> : null}
        </span>
      ),
    },
    {
      key: "details",
      header: "Details",
      render: (row: SecurityCommandTimelineItemResponse) => (
        <span className="flex flex-col gap-1">
          {row.details.length > 0 ? (
            <dl className="grid grid-cols-[auto_1fr] gap-x-2 text-help">
              {row.details.map((detail) => {
                const shown = formatDetail(detail);
                return (
                  <div key={detail.key} className="contents">
                    <dt className="text-muted">{humanizeKey(detail.key)}</dt>
                    <dd className="font-mono" title={shown.full}>
                      {shown.text}
                    </dd>
                  </div>
                );
              })}
            </dl>
          ) : (
            <span className="text-muted">—</span>
          )}
          {row.withheldFields.length > 0 ? <span className="text-help text-muted">Withheld by policy: {row.withheldFields.map(humanizeKey).join(", ")}</span> : null}
        </span>
      ),
    },
  ];
}

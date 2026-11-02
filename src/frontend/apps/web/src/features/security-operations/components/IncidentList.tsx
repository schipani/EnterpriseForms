import { useApi } from "@enterpriseforms/app-runtime";
import type { SecurityIncidentListItemResponse, SecurityIncidentQuery } from "@enterpriseforms/api-client";
import { ActionIcons, Badge, Button, DataTable, EmptyState, LoadingBlock, Pager, Select, TextInput, localDateRange, useDebouncedValue } from "@enterpriseforms/ui";
import { useQuery } from "@tanstack/react-query";
import { Link } from "@tanstack/react-router";
import { useState } from "react";
import { ErrorPanel } from "../../support";
import { INCIDENT_SEVERITIES, INCIDENT_STATUSES, INCIDENT_TYPES, formatInstant, incidentTypeLabel, parseIncidentSearch, severityOf, statusOf } from "./incidentModel";

const PAGE_SIZE = 15;

/**
 * Security incidents, searched and filtered on the server, 15 per page. Each instance keeps its own filter and page state;
 * any filter change returns to the first page. Record references appear only when the server says the viewer may
 * discover the record.
 */
export function IncidentList({ caption = "Security incidents", compact = false }: { caption?: string; compact?: boolean }) {
  const api = useApi();
  const [text, setText] = useState("");
  const [type, setType] = useState("all");
  const [severity, setSeverity] = useState("all");
  const [status, setStatus] = useState("all");
  const [fromDate, setFromDate] = useState("");
  const [toDate, setToDate] = useState("");
  const [cursor, setCursor] = useState<string | undefined>(undefined);
  const settledText = useDebouncedValue(text);
  const parsed = settledText.trim() === "" ? "" : parseIncidentSearch(settledText);
  const searchError = parsed === null ? "Enter an incident reference (INC-…), a record reference (REC-…) or a full identifier." : undefined;

  const query: SecurityIncidentQuery = {
    pageSize: PAGE_SIZE,
    ...(parsed ? { q: parsed } : {}),
    ...(type !== "all" ? { type } : {}),
    ...(severity !== "all" ? { severity } : {}),
    ...(status !== "all" ? { status } : {}),
    ...localDateRange(fromDate, toDate),
    ...(cursor ? { continuationToken: cursor } : {}),
  };
  const incidents = useQuery({
    queryKey: ["security", "incidents", { ...query }],
    queryFn: ({ signal }) => api.security.incidents(query, signal).then((r) => r.result),
    enabled: parsed !== null,
    placeholderData: (previous) => previous,
  });
  // Every filter change starts again at page 1 (the cursor belongs to the previous filters).
  const change = (apply: () => void) => {
    apply();
    setCursor(undefined);
  };
  const filtered = text !== "" || type !== "all" || severity !== "all" || status !== "all" || fromDate !== "" || toDate !== "";
  const clear = () =>
    change(() => {
      setText("");
      setType("all");
      setSeverity("all");
      setStatus("all");
      setFromDate("");
      setToDate("");
    });
  const data = incidents.data;
  return (
    <div className="flex flex-col gap-3">
      <div role="search" aria-label={`Search ${caption.toLowerCase()}`} className={`grid items-start gap-x-4 gap-y-3 ${compact ? "sm:grid-cols-2 xl:grid-cols-4" : "sm:grid-cols-2 lg:grid-cols-3"}`}>
        <TextInput label="Search" placeholder="INC-…, REC-… or ID" value={text} autoComplete="off" spellCheck={false} error={searchError} onChange={(event) => change(() => setText(event.target.value))} />
        <Select label="Type" value={type} options={[{ value: "all", label: "All types" }, ...INCIDENT_TYPES.map((t) => ({ value: t.value, label: t.label }))]} onValueChange={(value) => change(() => setType(value))} />
        <Select label="Severity" value={severity} options={[{ value: "all", label: "All severities" }, ...INCIDENT_SEVERITIES.map((s) => ({ value: s.value, label: s.label }))]} onValueChange={(value) => change(() => setSeverity(value))} />
        <Select label="Status" value={status} options={[{ value: "all", label: "All statuses" }, ...INCIDENT_STATUSES.map((s) => ({ value: s.value, label: s.label }))]} onValueChange={(value) => change(() => setStatus(value))} />
        <TextInput label="Detected from" type="date" value={fromDate} max={toDate || undefined} onChange={(event) => change(() => setFromDate(event.target.value))} />
        <TextInput label="Detected to" type="date" value={toDate} min={fromDate || undefined} onChange={(event) => change(() => setToDate(event.target.value))} />
      </div>
      {filtered ? (
        <div>
          <Button variant="ghost" size="sm" icon={ActionIcons.reset} onClick={clear}>
            Clear filters
          </Button>
        </div>
      ) : null}
      {incidents.isPending && parsed !== null ? <LoadingBlock label="Loading incidents" /> : null}
      {incidents.isError ? <ErrorPanel error={incidents.error} onRetry={() => void incidents.refetch()} /> : null}
      {data && Number(data.totalCount) === 0 ? (
        filtered ? (
          <EmptyState title="No incidents match these filters">Try another type, severity, status or date range.</EmptyState>
        ) : (
          <EmptyState title="No incidents recorded">No verification problem has been observed. This does not mean every record has been verified.</EmptyState>
        )
      ) : null}
      {data && Number(data.totalCount) > 0 ? (
        <>
          <DataTable<SecurityIncidentListItemResponse> caption={caption} rows={[...data.items]} rowKey={(row) => row.securityIncidentId} stickyActions columns={incidentColumns()} />
          <Pager
            label={`${caption} pages`}
            noun="incidents"
            pageStart={Number(data.pageStart)}
            itemCount={data.items.length}
            totalCount={Number(data.totalCount)}
            pageSize={Number(data.pageSize)}
            hasPrevious={Boolean(data.previousToken)}
            hasNext={Boolean(data.nextToken)}
            busy={incidents.isFetching}
            onPrevious={() => setCursor(data.previousToken ?? undefined)}
            onNext={() => setCursor(data.nextToken ?? undefined)}
          />
        </>
      ) : null}
    </div>
  );
}

function incidentColumns() {
  return [
    {
      key: "incident",
      header: "Incident",
      isRowHeader: true,
      render: (row: SecurityIncidentListItemResponse) => (
        <Link to="/app/security/incidents/$securityIncidentId" params={{ securityIncidentId: row.securityIncidentId }} className="font-mono underline-offset-4 hover:underline">
          {row.incidentReference}
        </Link>
      ),
    },
    { key: "detected", header: "Last detected", render: (row: SecurityIncidentListItemResponse) => <span className="whitespace-nowrap">{formatInstant(row.lastDetectedOn)}</span> },
    { key: "type", header: "Type", render: (row: SecurityIncidentListItemResponse) => incidentTypeLabel(row.incidentTypeName) },
    {
      key: "severity",
      header: "Severity",
      render: (row: SecurityIncidentListItemResponse) => {
        const severity = severityOf(row.incidentSeverityName);
        return <Badge tone={severity.tone}>{severity.label}</Badge>;
      },
    },
    {
      key: "record",
      header: "Record",
      render: (row: SecurityIncidentListItemResponse) =>
        !row.recordScoped ? (
          <span className="text-muted">Not record-specific</span>
        ) : row.recordWithheld || !row.recordReference ? (
          <span className="text-muted">Withheld (no access)</span>
        ) : (
          <span className="font-mono">{row.recordReference}</span>
        ),
    },
    {
      key: "status",
      header: "Status",
      render: (row: SecurityIncidentListItemResponse) => {
        const status = statusOf(row.incidentStatusName);
        return <Badge tone={status.tone}>{status.label}</Badge>;
      },
    },
    { key: "assignee", header: "Investigator", render: (row: SecurityIncidentListItemResponse) => row.assignedToDisplayName ?? <span className="text-muted">Unassigned</span> },
    { key: "occurrences", header: "Detections", render: (row: SecurityIncidentListItemResponse) => <span className="tabular-nums">{String(row.occurrenceCount)}</span> },
    {
      key: "actions",
      header: "Actions",
      isActions: true,
      render: (row: SecurityIncidentListItemResponse) => (
        <Link to="/app/security/incidents/$securityIncidentId" params={{ securityIncidentId: row.securityIncidentId }} className="underline underline-offset-4" aria-label={`Open incident ${row.incidentReference}`}>
          Open
        </Link>
      ),
    },
  ];
}

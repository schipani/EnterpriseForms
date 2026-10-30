import { useApi, useSession } from "@enterpriseforms/app-runtime";
import type { AuditEventResponse, DashboardUploadItemResponse, DashboardUploadQuery, FormDefinitionSummaryResponse, RecordListItemResponse } from "@enterpriseforms/api-client";
import { Badge, DataTable, EmptyState, Select, TextInput, localDateRange, useDebouncedValue } from "@enterpriseforms/ui";
import { useQuery } from "@tanstack/react-query";
import { Link } from "@tanstack/react-router";
import { useState } from "react";
import { RECORD_STATUSES, parseLookupInput, recordStatusLabel } from "../../form-records";
import { DashboardSection, useCursorRestart, usePageCursor } from "./DashboardSection";

/** Every dashboard list shows 15 rows per page by default (server-side). */
export const DASHBOARD_PAGE_SIZE = 15;

const formatInstant = (value: string) => new Date(value).toLocaleString();

const filterGrid = "grid items-start gap-x-3 gap-y-2 sm:grid-cols-2";

const recordStatusTone = (status: string) => (status === "Submitted" ? "success" : status === "Archived" ? "neutral" : "info");

/** My Records (exact reference/ID lookup, status and form filters). The full search lives on the My Records page. */
export function RecordsSection() {
  const api = useApi();
  const [text, setText] = useState("");
  const [status, setStatus] = useState("all");
  const [form, setForm] = useState("all");
  const settled = useDebouncedValue(text);
  const parsed = settled.trim() === "" ? null : parseLookupInput(settled);
  const invalidSearch = settled.trim() !== "" && parsed === null;
  const query = {
    pageSize: DASHBOARD_PAGE_SIZE,
    sortBy: "modifiedOn" as const,
    sortDirection: "desc" as const,
    ...(parsed ? { q: parsed.value } : {}),
    ...(status !== "all" ? { recordStatus: status } : {}),
    ...(form !== "all" ? { formDefinitionId: form } : {}),
  };
  const paging = usePageCursor();
  const records = useQuery({
    queryKey: ["dashboard", "records", query, paging.cursor],
    queryFn: ({ signal }) => api.records.list({ ...query, ...(paging.cursor ? { continuationToken: paging.cursor } : {}) }, signal).then((r) => r.result),
    enabled: !invalidSearch,
    placeholderData: (previous) => previous,
  });
  useCursorRestart(records.error, paging);
  const data = records.data;
  const filtered = text !== "" || status !== "all" || form !== "all";
  const formOptions = data?.availableForms ?? [];
  return (
    <DashboardSection
      id="dashboard-records"
      title="My records"
      description="Records you own or that are shared with you, most recently changed first."
      fullPage={{ to: "/app/records", label: "Advanced search in My Records" }}
      filtered={filtered}
      onClear={() => paging.change(() => (setText(""), setStatus("all"), setForm("all")))}
      filters={
        <>
          <TextInput
            label="Find a record"
            placeholder="REC-1A2B3C4D or record ID"
            value={text}
            autoComplete="off"
            spellCheck={false}
            error={invalidSearch ? "Enter a record reference such as REC-1A2B3C4D or a full record ID." : undefined}
            onChange={(event) => paging.change(() => setText(event.target.value))}
          />
          <div className={filterGrid}>
            <Select label="Status" value={status} options={[{ value: "all", label: "All statuses" }, ...RECORD_STATUSES.map((s) => ({ value: s, label: recordStatusLabel(s) }))]} onValueChange={(value) => paging.change(() => setStatus(value))} />
            <Select
              label="Form"
              value={form}
              options={[{ value: "all", label: "All forms" }, ...(form !== "all" && !formOptions.some((f) => f.formDefinitionId === form) ? [{ value: form, label: "Selected form" }] : []), ...formOptions.map((f) => ({ value: f.formDefinitionId, label: f.formDefinitionName || "Unnamed form" }))]}
              onValueChange={(value) => paging.change(() => setForm(value))}
            />
          </div>
        </>
      }
      loading={records.isPending && !invalidSearch}
      error={records.error}
      onRetry={() => void records.refetch()}
      page={data ? { totalCount: data.totalCount, pageStart: data.pageStart, pageSize: data.pageSize, nextToken: data.continuationToken ?? null, previousToken: data.previousToken ?? null } : undefined}
      itemCount={data?.items.length ?? 0}
      noun="records"
      busy={records.isFetching}
      onPage={paging.setCursor}
    >
      {data && Number(data.totalCount) === 0 ? (
        <EmptyState title={filtered ? "No records match" : "No records yet"}>{filtered ? "Check the reference, or try another status or form." : "Records you create, and records shared with you, appear here."}</EmptyState>
      ) : null}
      {data && Number(data.totalCount) > 0 ? (
        <DataTable<RecordListItemResponse>
          caption="My records list"
          rows={[...data.items]}
          rowKey={(row) => row.formRecordId}
          columns={[
            {
              key: "reference",
              header: "Record",
              isRowHeader: true,
              render: (row) => (
                <Link to="/app/records/$formRecordId" params={{ formRecordId: row.formRecordId }} className="font-mono underline-offset-4 hover:underline">
                  {row.recordReference}
                </Link>
              ),
            },
            { key: "form", header: "Form", render: (row) => row.formDefinitionName || "Unnamed form" },
            { key: "status", header: "Status", render: (row) => <Badge tone={recordStatusTone(row.recordStatus)}>{recordStatusLabel(row.recordStatus)}</Badge> },
            { key: "changed", header: "Last changed", render: (row) => <span className="whitespace-nowrap">{formatInstant(row.modifiedOn)}</span> },
          ]}
        />
      ) : null}
    </DashboardSection>
  );
}

const UPLOAD_GROUPS = [
  { value: "InProgress", label: "In progress", tone: "info" },
  { value: "Completed", label: "Completed", tone: "success" },
  { value: "Failed", label: "Failed", tone: "danger" },
  { value: "Cancelled", label: "Cancelled or expired", tone: "neutral" },
] as const;

const formatBytes = (bytes: number) => (bytes < 1024 ? `${bytes} B` : bytes < 1024 * 1024 ? `${(bytes / 1024).toFixed(1)} KB` : `${(bytes / (1024 * 1024)).toFixed(1)} MB`);

/** The caller's own uploads: file name search, status and date filters. */
export function UploadsSection() {
  const api = useApi();
  const [text, setText] = useState("");
  const [status, setStatus] = useState("all");
  const [fromDate, setFromDate] = useState("");
  const [toDate, setToDate] = useState("");
  const settled = useDebouncedValue(text).trim();
  const paging = usePageCursor();
  const query: DashboardUploadQuery = {
    pageSize: DASHBOARD_PAGE_SIZE,
    ...(settled ? { q: settled } : {}),
    ...(status !== "all" ? { status: status as NonNullable<DashboardUploadQuery["status"]> } : {}),
    ...localDateRange(fromDate, toDate),
  };
  const uploads = useQuery({
    queryKey: ["dashboard", "uploads", query, paging.cursor],
    queryFn: ({ signal }) => api.dashboard.uploads({ ...query, ...(paging.cursor ? { continuationToken: paging.cursor } : {}) }, signal).then((r) => r.result),
    placeholderData: (previous) => previous,
  });
  useCursorRestart(uploads.error, paging);
  const data = uploads.data;
  const filtered = text !== "" || status !== "all" || fromDate !== "" || toDate !== "";
  return (
    <DashboardSection
      id="dashboard-uploads"
      title="Upload activity"
      description="Your own uploads and their processing outcome, newest first."
      fullPage={{ to: "/app/uploads", label: "Upload Center" }}
      filtered={filtered}
      onClear={() => paging.change(() => (setText(""), setStatus("all"), setFromDate(""), setToDate("")))}
      filters={
        <>
          <div className={filterGrid}>
            <TextInput label="File name" placeholder="Search file names" value={text} autoComplete="off" onChange={(event) => paging.change(() => setText(event.target.value))} />
            <Select label="Status" value={status} options={[{ value: "all", label: "All statuses" }, ...UPLOAD_GROUPS.map((g) => ({ value: g.value, label: g.label }))]} onValueChange={(value) => paging.change(() => setStatus(value))} />
          </div>
          <div className={filterGrid}>
            <TextInput label="From" type="date" value={fromDate} max={toDate || undefined} onChange={(event) => paging.change(() => setFromDate(event.target.value))} />
            <TextInput label="To" type="date" value={toDate} min={fromDate || undefined} onChange={(event) => paging.change(() => setToDate(event.target.value))} />
          </div>
        </>
      }
      loading={uploads.isPending}
      error={uploads.error}
      onRetry={() => void uploads.refetch()}
      page={data}
      itemCount={data?.items.length ?? 0}
      noun="uploads"
      busy={uploads.isFetching}
      onPage={paging.setCursor}
    >
      {data && Number(data.totalCount) === 0 ? <EmptyState title={filtered ? "No uploads match" : "No uploads yet"}>{filtered ? "Try another file name, status or date range." : "Documents you upload to records appear here."}</EmptyState> : null}
      {data && Number(data.totalCount) > 0 ? (
        <DataTable<DashboardUploadItemResponse>
          caption="Upload activity list"
          rows={[...data.items]}
          rowKey={(row) => row.uploadSessionId}
          columns={[
            {
              key: "file",
              header: "File",
              isRowHeader: true,
              render: (row) => (
                <Link to="/app/records/$formRecordId" params={{ formRecordId: row.formRecordId }} className="break-all underline-offset-4 hover:underline">
                  {row.originalFileName}
                </Link>
              ),
            },
            {
              key: "status",
              header: "Status",
              render: (row) => {
                const group = UPLOAD_GROUPS.find((g) => g.value === row.statusGroup);
                return <Badge tone={group?.tone ?? "neutral"}>{group?.label ?? row.statusGroup}</Badge>;
              },
            },
            { key: "started", header: "Started", render: (row) => <span className="whitespace-nowrap">{formatInstant(row.sessionCreatedOn)}</span> },
            { key: "size", header: "Size", render: (row) => <span className="tabular-nums">{formatBytes(Number(row.declaredSizeBytes))}</span> },
          ]}
        />
      ) : null}
    </DashboardSection>
  );
}

const OUTCOMES = [
  { value: "Success", label: "Succeeded", tone: "success" },
  { value: "Failed", label: "Failed", tone: "danger" },
  { value: "Denied", label: "Denied", tone: "warning" },
] as const;

/** The caller's own activity (audit events): type, outcome and date filters. */
export function ActivitySection() {
  const api = useApi();
  const [category, setCategory] = useState("all");
  const [outcome, setOutcome] = useState("all");
  const [fromDate, setFromDate] = useState("");
  const [toDate, setToDate] = useState("");
  const paging = usePageCursor();
  const query = {
    pageSize: DASHBOARD_PAGE_SIZE,
    ...(category !== "all" ? { category } : {}),
    ...(outcome !== "all" ? { outcome } : {}),
    ...localDateRange(fromDate, toDate),
  };
  const activity = useQuery({
    queryKey: ["dashboard", "activity", query, paging.cursor],
    queryFn: ({ signal }) => api.dashboard.activity({ ...query, ...(paging.cursor ? { continuationToken: paging.cursor } : {}) }, signal).then((r) => r.result),
    placeholderData: (previous) => previous,
  });
  useCursorRestart(activity.error, paging);
  const data = activity.data;
  const filtered = category !== "all" || outcome !== "all" || fromDate !== "" || toDate !== "";
  const categories = data?.availableCategories ?? [];
  return (
    <DashboardSection
      id="dashboard-activity"
      title="Recent personal activity"
      description="What you did in the application, newest first."
      fullPage={{ to: "/app/activity", label: "My activity" }}
      filtered={filtered}
      onClear={() => paging.change(() => (setCategory("all"), setOutcome("all"), setFromDate(""), setToDate("")))}
      filters={
        <>
          <div className={filterGrid}>
            <Select
              label="Activity type"
              value={category}
              options={[{ value: "all", label: "All types" }, ...(category !== "all" && !categories.includes(category) ? [{ value: category, label: category }] : []), ...categories.map((c) => ({ value: c, label: c }))]}
              onValueChange={(value) => paging.change(() => setCategory(value))}
            />
            <Select label="Outcome" value={outcome} options={[{ value: "all", label: "All outcomes" }, ...OUTCOMES.map((o) => ({ value: o.value, label: o.label }))]} onValueChange={(value) => paging.change(() => setOutcome(value))} />
          </div>
          <div className={filterGrid}>
            <TextInput label="From" type="date" value={fromDate} max={toDate || undefined} onChange={(event) => paging.change(() => setFromDate(event.target.value))} />
            <TextInput label="To" type="date" value={toDate} min={fromDate || undefined} onChange={(event) => paging.change(() => setToDate(event.target.value))} />
          </div>
        </>
      }
      loading={activity.isPending}
      error={activity.error}
      onRetry={() => void activity.refetch()}
      page={data}
      itemCount={data?.items.length ?? 0}
      noun="events"
      busy={activity.isFetching}
      onPage={paging.setCursor}
    >
      {data && Number(data.totalCount) === 0 ? <EmptyState title={filtered ? "No activity matches" : "No activity yet"}>{filtered ? "Try another type, outcome or date range." : "Your actions in the application appear here."}</EmptyState> : null}
      {data && Number(data.totalCount) > 0 ? (
        <DataTable<AuditEventResponse>
          caption="Recent personal activity list"
          rows={[...data.items]}
          rowKey={(row) => row.auditEventId}
          columns={[
            { key: "when", header: "When", isRowHeader: true, render: (row) => <span className="whitespace-nowrap">{formatInstant(row.eventOccurredOn)}</span> },
            { key: "what", header: "Activity", render: (row) => row.eventDescription },
            { key: "type", header: "Type", render: (row) => row.eventCategory },
            {
              key: "outcome",
              header: "Outcome",
              render: (row) => {
                const known = OUTCOMES.find((o) => o.value === row.eventOutcome);
                return <Badge tone={known?.tone ?? "neutral"}>{known?.label ?? row.eventOutcome}</Badge>;
              },
            },
          ]}
        />
      ) : null}
    </DashboardSection>
  );
}

/** Forms the caller may use (create records) or design, by name. */
export function FormsSection() {
  const api = useApi();
  const session = useSession();
  const designer = session.hasCapability("forms.design");
  const [text, setText] = useState("");
  const [access, setAccess] = useState("all");
  const [status, setStatus] = useState("all");
  const settled = useDebouncedValue(text).trim();
  const paging = usePageCursor();
  const query = {
    pageSize: DASHBOARD_PAGE_SIZE,
    ...(settled ? { q: settled } : {}),
    ...(access !== "all" ? { access: access as "create" | "design" } : {}),
    ...(status !== "all" ? { status: status as "published" | "unpublished" } : {}),
  };
  const forms = useQuery({
    queryKey: ["dashboard", "forms", query, paging.cursor],
    queryFn: ({ signal }) => api.dashboard.forms({ ...query, ...(paging.cursor ? { continuationToken: paging.cursor } : {}) }, signal).then((r) => r.result),
    placeholderData: (previous) => previous,
  });
  useCursorRestart(forms.error, paging);
  const data = forms.data;
  const filtered = text !== "" || access !== "all" || status !== "all";
  return (
    <DashboardSection
      id="dashboard-forms"
      title="Permitted forms"
      description="Forms you can use to create records, or design, by name."
      fullPage={{ to: "/app/forms", label: "All forms" }}
      filtered={filtered}
      onClear={() => paging.change(() => (setText(""), setAccess("all"), setStatus("all")))}
      filters={
        <>
          <TextInput label="Form name" placeholder="Search forms" value={text} autoComplete="off" onChange={(event) => paging.change(() => setText(event.target.value))} />
          <div className={filterGrid}>
            <Select
              label="Access"
              value={access}
              options={[{ value: "all", label: "Any access" }, { value: "create", label: "Can create records" }, ...(designer ? [{ value: "design", label: "Can design" }] : [])]}
              onValueChange={(value) => paging.change(() => setAccess(value))}
            />
            <Select
              label="Status"
              value={status}
              options={[
                { value: "all", label: "All statuses" },
                { value: "published", label: "Published" },
                { value: "unpublished", label: "Not published" },
              ]}
              onValueChange={(value) => paging.change(() => setStatus(value))}
            />
          </div>
        </>
      }
      loading={forms.isPending}
      error={forms.error}
      onRetry={() => void forms.refetch()}
      page={data}
      itemCount={data?.items.length ?? 0}
      noun="forms"
      busy={forms.isFetching}
      onPage={paging.setCursor}
    >
      {data && Number(data.totalCount) === 0 ? <EmptyState title={filtered ? "No forms match" : "No forms available"}>{filtered ? "Try another name, access or status." : "Forms you are allowed to use or design appear here."}</EmptyState> : null}
      {data && Number(data.totalCount) > 0 ? (
        <DataTable<FormDefinitionSummaryResponse>
          caption="Permitted forms list"
          rows={[...data.items]}
          rowKey={(row) => row.formDefinitionId}
          stickyActions
          columns={[
            { key: "name", header: "Form", isRowHeader: true, render: (row) => row.formDefinitionName || "Unnamed form" },
            { key: "version", header: "Version", render: (row) => (row.currentVersionNumber === null || row.currentVersionNumber === undefined ? <Badge tone="neutral">Not published</Badge> : `v${String(row.currentVersionNumber)}`) },
            { key: "access", header: "Access", render: (row) => [row.canCreateRecord ? "Create records" : null, row.canDesign ? "Design" : null].filter(Boolean).join(" · ") },
            {
              key: "actions",
              header: "Actions",
              isActions: true,
              render: (row) => (
                <span className="flex justify-end gap-3">
                  {row.canCreateRecord && row.currentVersionNumber !== null && row.currentVersionNumber !== undefined ? (
                    <Link to="/app/forms/$formDefinitionId/records/new" params={{ formDefinitionId: row.formDefinitionId }} className="underline underline-offset-4" aria-label={`New ${row.formDefinitionName} record`}>
                      New record
                    </Link>
                  ) : null}
                  {row.canDesign ? (
                    <Link to="/app/forms/$formDefinitionId/design" params={{ formDefinitionId: row.formDefinitionId }} className="underline underline-offset-4" aria-label={`Design ${row.formDefinitionName}`}>
                      Design
                    </Link>
                  ) : null}
                </span>
              ),
            },
          ]}
        />
      ) : null}
    </DashboardSection>
  );
}

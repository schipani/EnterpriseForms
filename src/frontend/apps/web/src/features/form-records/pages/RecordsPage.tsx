import { presentError, useApi } from "@enterpriseforms/app-runtime";
import type { FieldSearchRequest, FieldSearchResponse, RecordListItemResponse, RecordListQuery, RecordListResponse } from "@enterpriseforms/api-client";
import { ActionIcons, Badge, Button, Card, Checkbox, DataTable, EmptyState, LoadingBlock, OverflowMenu, PageHeader, RowActions, Select, StatusMessage, TextInput, type DataTableColumn } from "@enterpriseforms/ui";
import { useMutation, useQuery } from "@tanstack/react-query";
import { Link, useNavigate, useSearch } from "@tanstack/react-router";
import { useEffect, useState, type FormEvent } from "react";
import { AdvancedFieldSearch, presentSearchFailure, toFieldSearchRequest, type ConditionDraft, type SearchFailure } from "../../search";
import { ErrorPanel } from "../../support";
import { formatDateTime, parseLookupInput, recordStatusLabel, referenceFor, RECORD_STATUSES, SORT_CHOICES, sortChoiceFor, type RecordsSearch } from "../components/recordList";

/** Errors that mean "this page position is no longer meaningful": the list restarts from the first page. */
const RESTART_CODES = new Set(["LIST_CHANGED", "CURSOR_INVALID", "CURSOR_SCOPE_MISMATCH"]);

/**
 * PG-015 My Records (C-29). The server lists every record the authority lets the user discover (all of them, at one
 * authorization revision), then filters, sorts and pages that set; this page only renders what it returns. Row details
 * are application-database information and are checked when a record is opened.
 */
export function RecordsPage() {
  const api = useApi();
  const navigate = useNavigate();
  const search = useSearch({ from: "/app/records" });
  const [cursor, setCursor] = useState<string | undefined>(undefined);
  const [restarted, setRestarted] = useState(false);
  // The search box holds what the person is typing; only Enter or Search turns it into the filter in the address (search.q).
  const [searchText, setSearchText] = useState(search.q ?? "");
  const [searchError, setSearchError] = useState<string | null>(null);
  useEffect(() => {
    setSearchText(search.q ?? "");
    setSearchError(null);
  }, [search.q]);
  const sort = sortChoiceFor(search.sort);
  const pageSize = search.size ?? 25;
  const query: RecordListQuery = {
    ...(search.form ? { formDefinitionId: search.form } : {}),
    ...(search.status ? { recordStatus: search.status } : {}),
    ...(search.children ? { includeChildren: true } : {}),
    ...(search.archived ? { includeArchived: true } : {}),
    ...(search.q ? { q: search.q } : {}),
    sortBy: sort.sortBy,
    sortDirection: sort.sortDirection,
    pageSize,
    ...(cursor ? { continuationToken: cursor } : {}),
  };
  const records = useQuery<RecordListResponse>({
    queryKey: ["records", "list", query],
    queryFn: ({ signal }) => api.records.list(query, signal).then((r) => r.result),
    // Keep showing the current page while the next one loads (no flash of an empty table).
    placeholderData: (previous) => previous,
  });
  // Forms this user may create records with (the authority decides; the list is complete even for large catalogs, C-27).
  const forms = useQuery({ queryKey: ["forms"], queryFn: () => api.forms.list().then((r) => r.result) });
  const creatable = (forms.data ?? []).filter((form) => form.canCreateRecord && form.currentVersionNumber !== null);
  // Phase C7 Advanced Search: only the server says which fields of the selected form can be searched (M0: none — trusted
  // execution is not enabled). Nothing is inferred from field types and nothing is read from the form's design.
  const capabilities = useQuery({
    queryKey: ["search", "capabilities", search.form],
    queryFn: ({ signal }) => api.search.capabilities(search.form!, signal).then((r) => r.result),
    enabled: Boolean(search.form),
  });
  // A field-search result (or failure) replaces the list in the same table until cleared. Predicate values stay in memory
  // and in the POST body only — never in the address bar.
  const [fieldSearch, setFieldSearch] = useState<{ result?: FieldSearchResponse; failure?: SearchFailure; request: FieldSearchRequest } | null>(null);
  const runFieldSearch = useMutation({
    mutationFn: (request: FieldSearchRequest) => api.search.fieldSearch(request).then((r) => r.result),
    onSuccess: (result, request) => setFieldSearch({ result, request }),
    onError: (error, request) => setFieldSearch({ failure: presentSearchFailure(error), request }),
  });
  const pageFieldSearch = useMutation({
    mutationFn: (cursor: string) => api.search.page({ cursor }).then((r) => r.result),
    onSuccess: (result) => setFieldSearch((current) => (current ? { result, request: current.request } : current)),
    onError: (error) => setFieldSearch((current) => (current ? { failure: presentSearchFailure(error), request: current.request } : current)),
  });
  const fieldSearchAvailable = Boolean(search.form) && capabilities.data?.fieldValueSearchAvailable === true;
  const startFieldSearch = (conditions: ConditionDraft[]) => {
    if (search.form && capabilities.data) {
      runFieldSearch.mutate(toFieldSearchRequest(search.form, conditions, capabilities.data, { recordStatus: search.status, includeArchived: search.archived, includeChildren: search.children, sortBy: sort.sortBy, sortDirection: sort.sortDirection, pageSize }));
    }
  };

  const errorCode = records.isError ? presentError(records.error).code : null;
  useEffect(() => {
    if (errorCode && RESTART_CODES.has(errorCode) && cursor) {
      setCursor(undefined);
      setRestarted(true);
    }
  }, [errorCode, cursor]);

  const update = (next: Partial<RecordsSearch>) => {
    setCursor(undefined);
    setRestarted(false);
    // Any filter change ends an advanced search: its result was verified for the previous form and filters.
    setFieldSearch(null);
    void navigate({ to: "/app/records", search: (current: RecordsSearch) => clean({ ...current, ...next }) });
  };
  const filtered = Boolean(search.form || search.status || search.children || search.archived || search.q);
  const data = records.data;
  const rows = data?.items ?? [];
  const formOptions = data?.availableForms ?? [];
  const selectedFormMissing = search.form && !formOptions.some((form) => form.formDefinitionId === search.form);
  const total = Number(data?.totalCount ?? 0);
  const unavailable = Number(data?.unavailableRecordCount ?? 0);
  const submitSearch = (event: FormEvent) => {
    event.preventDefault();
    if (searchText.trim() === "") {
      // An empty box means "no search": it behaves like Clear for the search filter only.
      setSearchError(null);
      update({ q: undefined });
      return;
    }
    const parsed = parseLookupInput(searchText);
    if (parsed === null) {
      setSearchError("Enter a record reference such as REC-1A2B3C4D or a full record ID.");
      return;
    }
    setSearchError(null);
    setSearchText(parsed.value);
    update({ q: parsed.value });
  };
  const clearSearch = () => {
    setSearchText("");
    setSearchError(null);
    update({ q: undefined });
  };
  const clearAll = () => {
    setSearchText("");
    setSearchError(null);
    update({ q: undefined, form: undefined, status: undefined, children: undefined, archived: undefined, sort: undefined, size: undefined });
  };
  const first = total > 0 ? Number(data!.pageStart) : 0;
  const last = total > 0 ? first + rows.length - 1 : 0;

  return (
    <>
      <PageHeader
        title="My Records"
        lead="Records you own and records shared with you. Opening a record checks its signed content."
        actions={
          <>
            <Button variant="secondary" aria-label="Refresh My Records" busy={records.isFetching} onClick={() => void records.refetch()}>
              Refresh
            </Button>
            {creatable.length > 0 ? (
              <OverflowMenu
                label="New record"
                trigger={<Button icon={ActionIcons.add}>New record</Button>}
                items={creatable.map((form) => ({
                  key: form.formDefinitionId,
                  content: (
                    <Link to="/app/forms/$formDefinitionId/records/new" params={{ formDefinitionId: form.formDefinitionId }}>
                      {form.formDefinitionName}
                    </Link>
                  ),
                }))}
              />
            ) : null}
          </>
        }
      />
      <div className="records-search">
        <div className="records-search__panels">
      <Card title="Find and filter records" aria-label="Find and filter records" className="records-search__panel">
        <form role="search" aria-label="Search records by reference or ID" onSubmit={submitSearch} noValidate>
          <TextInput
            label="Search records"
            helpText="Enter an exact record reference (for example REC-017EF770) or a full record ID. This finds one record; it does not search inside records."
            value={searchText}
            autoComplete="off"
            spellCheck={false}
            onChange={(event) => {
              setSearchText(event.target.value);
              setSearchError(null);
            }}
            error={searchError ?? undefined}
            actions={
              <>
                <Button type="submit" icon={ActionIcons.search} busy={records.isFetching && Boolean(search.q)}>
                  Search
                </Button>
                {search.q || searchText ? (
                  <Button type="button" variant="secondary" icon={ActionIcons.reset} onClick={clearSearch}>
                    Clear
                  </Button>
                ) : null}
              </>
            }
          />
        </form>
        <div className="flex flex-col gap-3" role="group" aria-label="Filter and sort records">
          <div className="grid items-start gap-x-4 gap-y-3 sm:grid-cols-2">
            <Select
              label="Form"
              value={search.form ?? "all"}
              options={[{ value: "all", label: "All forms" }, ...(selectedFormMissing ? [{ value: search.form!, label: "Selected form (no records)" }] : []), ...formOptions.map((form) => ({ value: form.formDefinitionId, label: form.formDefinitionName || "Unnamed form" }))]}
              onValueChange={(value) => update({ form: value === "all" ? undefined : value })}
            />
            <Select label="Status" value={search.status ?? "all"} options={[{ value: "all", label: "All statuses" }, ...RECORD_STATUSES.map((status) => ({ value: status, label: recordStatusLabel(status) }))]} onValueChange={(value) => update({ status: value === "all" ? undefined : value })} />
          </div>
          <div className="grid items-start gap-x-4 gap-y-3 sm:grid-cols-2">
            <Select label="Sort by" value={sort.key} options={SORT_CHOICES.map((choice) => ({ value: choice.key, label: choice.label }))} onValueChange={(value) => update({ sort: value === SORT_CHOICES[0]!.key ? undefined : value })} />
            <Select label="Rows per page" value={String(pageSize)} options={["10", "25", "50", "100"].map((size) => ({ value: size, label: size }))} onValueChange={(value) => update({ size: value === "25" ? undefined : Number(value) })} />
          </div>
        </div>
        <div className="flex flex-col gap-2">
          <div className="flex flex-wrap items-center justify-between gap-3">
            <div className="flex flex-wrap gap-x-6 gap-y-1">
              <Checkbox label="Include related (child) records" checked={Boolean(search.children)} onCheckedChange={(checked) => update({ children: checked ? true : undefined })} />
              <Checkbox label="Include archived records" checked={Boolean(search.archived) || search.status === "Archived"} disabled={search.status === "Archived"} onCheckedChange={(checked) => update({ archived: checked ? true : undefined })} />
            </div>
            {filtered || search.sort || search.size ? (
              <Button variant="ghost" size="sm" icon={ActionIcons.reset} onClick={clearAll}>
                Clear filters
              </Button>
            ) : null}
          </div>
          <p className="text-sm text-muted">Archived records are hidden unless you include them or choose the Archived status. An exact search also finds archived and related records; the Form and Status filters still narrow it.</p>
        </div>
      </Card>
      <Card as="div" className="records-search__panel">
        <AdvancedFieldSearch
          collapsible={false}
          formSelected={Boolean(search.form)}
          capabilities={capabilities.data}
          loading={capabilities.isPending && Boolean(search.form)}
          loadFailed={capabilities.isError}
          onRetryLoad={() => void capabilities.refetch()}
          onSearch={fieldSearchAvailable ? startFieldSearch : undefined}
          searching={runFieldSearch.isPending}
        />
      </Card>
        </div>
      </div>

      <Card className="records-results">
        {fieldSearch ? (
          <FieldSearchResults
            state={fieldSearch}
            busy={runFieldSearch.isPending || pageFieldSearch.isPending}
            onRetry={() => runFieldSearch.mutate(fieldSearch.request)}
            onPage={(cursor) => pageFieldSearch.mutate(cursor)}
            onClear={() => setFieldSearch(null)}
          />
        ) : (
          <>
            {restarted ? (
              <StatusMessage tone="info" title="The list was reloaded">
                Access to records changed while you were browsing, so the list starts again from the first page.
              </StatusMessage>
            ) : null}
            {records.isPending ? <LoadingBlock label="Loading your records" /> : null}
            {records.isError && !(errorCode && RESTART_CODES.has(errorCode) && cursor) ? <ErrorPanel error={records.error} onRetry={() => void records.refetch()} /> : null}
            {data && unavailable > 0 ? (
              <StatusMessage tone="warning" title={`${unavailable} of your records could not be described`}>
                You have access to {unavailable === 1 ? "a record" : "records"} that the application database holds no information for, so {unavailable === 1 ? "it is" : "they are"} not in the table or the filters. Open {unavailable === 1 ? "it" : "them"} to check:{" "}
                {(data.unavailableRecordIds ?? []).map((id, index) => (
                  <span key={id}>
                    {index > 0 ? ", " : null}
                    <Link to="/app/records/$formRecordId" params={{ formRecordId: id }} className="underline underline-offset-4">
                      {referenceFor(id)}
                    </Link>
                  </span>
                ))}
                {unavailable > (data.unavailableRecordIds?.length ?? 0) ? " …" : null}
              </StatusMessage>
            ) : null}
            {data && total === 0 ? (
              filtered ? (
                <EmptyState title={search.q ? "No record found" : "No records match these filters"} actions={<Button variant="secondary" icon={ActionIcons.reset} onClick={clearAll}>Clear filters</Button>}>
                  {search.q ? `No record ${search.q.startsWith("REC-") ? "with reference" : "with ID"} ${search.q} that you can access matches the selected filters. Check the ${search.q.startsWith("REC-") ? "reference" : "ID"}, the Form and Status filters, or ask the record's owner to share it with you.` : "Try another form or status, or include related records."}
                </EmptyState>
              ) : (
                <EmptyState
                  title="You have no records yet"
                  actions={creatable.length === 1 ? (
                    <Link to="/app/forms/$formDefinitionId/records/new" params={{ formDefinitionId: creatable[0]!.formDefinitionId }}>
                      <Button>New {creatable[0]!.formDefinitionName}</Button>
                    </Link>
                  ) : null}
                >
                  Records you create, and records other people share with you, appear here.
                  {creatable.length > 1 ? " Use New record to start one." : null}
                </EmptyState>
              )
            ) : null}
            {data && total > 0 ? (
              <>
                <p className="mb-2 text-sm text-muted">Record details shown here come from the application database and are checked when you open a record.</p>
                <DataTable
                  caption="My Records"
                  rowKey={(row) => row.formRecordId}
                  rows={rows}
                  stickyActions
                  columns={recordColumns()}
                />
                <nav aria-label="Record pages" className="mt-3 flex flex-wrap items-center justify-between gap-3">
                  <p className="text-sm" aria-live="polite">
                    Showing {first}–{last} of {total} {total === 1 ? "record" : "records"}
                  </p>
                  <div className="flex gap-2">
                    <Button variant="secondary" size="sm" disabled={!data.previousToken || records.isFetching} onClick={() => setCursor(data.previousToken ?? undefined)}>
                      Previous page
                    </Button>
                    <Button variant="secondary" size="sm" disabled={!data.continuationToken || records.isFetching} onClick={() => setCursor(data.continuationToken ?? undefined)}>
                      Next page
                    </Button>
                  </div>
                </nav>
              </>
            ) : null}
          </>
        )}
      </Card>
    </>
  );
}

/**
 * Advanced-search outcome in the My Records table (Phase C7). A result is shown only when the server returned a complete,
 * verified answer; a failure is explained and is never presented as "no records match".
 */
function FieldSearchResults({ state, busy, onRetry, onPage, onClear }: { state: { result?: FieldSearchResponse; failure?: SearchFailure }; busy: boolean; onRetry: () => void; onPage: (cursor: string) => void; onClear: () => void }) {
  const clear = (
    <Button variant="secondary" size="sm" icon={ActionIcons.reset} onClick={onClear}>
      Clear advanced search
    </Button>
  );
  if (state.failure) {
    return (
      <StatusMessage
        tone={state.failure.code === "SEARCH_VERIFICATION_UNAVAILABLE" && !state.failure.canRetry ? "info" : "warning"}
        title={state.failure.title}
        actions={
          <>
            {state.failure.canRetry ? (
              <Button size="sm" icon={ActionIcons.search} busy={busy} onClick={onRetry}>
                Run search again
              </Button>
            ) : null}
            {clear}
          </>
        }
      >
        {state.failure.message}
      </StatusMessage>
    );
  }
  const result = state.result!;
  const total = Number(result.totalCount);
  const first = total > 0 ? Number(result.pageStart) : 0;
  const last = total > 0 ? first + result.items.length - 1 : 0;
  return (
    <>
      <div className="mb-2 flex flex-wrap items-start justify-between gap-3">
        <p className="text-sm text-muted">{result.scopeStatement}</p>
        {clear}
      </div>
      {total === 0 ? (
        <EmptyState title="No records match these conditions">The search was complete and verified for the records you can access.</EmptyState>
      ) : (
        <>
          <DataTable caption="My Records" rowKey={(row) => row.formRecordId} rows={[...result.items]} stickyActions columns={recordColumns()} />
          <nav aria-label="Search result pages" className="mt-3 flex flex-wrap items-center justify-between gap-3">
            <p className="text-sm" aria-live="polite">
              Showing {first}–{last} of {total} matching {total === 1 ? "record" : "records"}
            </p>
            <div className="flex gap-2">
              <Button variant="secondary" size="sm" disabled={!result.previousToken || busy} onClick={() => onPage(result.previousToken!)}>
                Previous page
              </Button>
              <Button variant="secondary" size="sm" disabled={!result.continuationToken || busy} onClick={() => onPage(result.continuationToken!)}>
                Next page
              </Button>
            </div>
          </nav>
        </>
      )}
    </>
  );
}

/** The My Records columns (C-29); advanced-search results reuse them so there is one results table. */
function recordColumns(): DataTableColumn<RecordListItemResponse>[] {
  return [
    {
      key: "record",
      header: "Record",
      isRowHeader: true,
      render: (row) => (
        <span className="flex flex-col">
          <Link to="/app/records/$formRecordId" params={{ formRecordId: row.formRecordId }} className="underline-offset-4 hover:underline">
            {row.formDefinitionName || "Unnamed form"}
          </Link>
          <span className="text-xs font-normal text-muted">
            {row.recordReference}
            {row.parentFormRecordId ? <> · related to {referenceFor(row.parentFormRecordId)}</> : null}
          </span>
        </span>
      ),
    },
    { key: "status", header: "Status", render: (row) => <Badge tone="neutral">{recordStatusLabel(row.recordStatus)}</Badge> },
    { key: "created", header: "Created", render: (row) => <CreatedCell row={row} /> },
    { key: "modified", header: "Last changed", render: (row) => formatDateTime(row.modifiedOn) },
    {
      key: "actions",
      header: "Actions",
      isActions: true,
      render: (row) => (
        <RowActions
          overflowLabel={`More actions for ${row.formDefinitionName} ${row.recordReference}`}
          primary={
            <>
              <Link to="/app/records/$formRecordId" params={{ formRecordId: row.formRecordId }} aria-label={`Open ${row.formDefinitionName} ${row.recordReference}`}>
                <Button size="sm" variant="secondary">
                  Open
                </Button>
              </Link>
              {row.canEdit && row.recordStatus === "Draft" ? (
                <Link to="/app/records/$formRecordId/edit" params={{ formRecordId: row.formRecordId }} aria-label={`Edit ${row.formDefinitionName} ${row.recordReference}`}>
                  <Button size="sm" variant="secondary" icon={ActionIcons.edit}>
                    Edit
                  </Button>
                </Link>
              ) : null}
            </>
          }
          overflow={row.parentFormRecordId ? [{ key: "parent", content: <Link to="/app/records/$formRecordId" params={{ formRecordId: row.parentFormRecordId }}>Open parent record</Link> }] : []}
        />
      ),
    },
  ];
}

function CreatedCell({ row }: { row: RecordListItemResponse }) {
  return (
    <span className="flex flex-col">
      <span>{formatDateTime(row.createdOn)}</span>
      {row.createdByCurrentUser ? <span className="text-xs text-muted">by you</span> : row.createdByDisplayName ? <span className="text-xs text-muted">by {row.createdByDisplayName}</span> : null}
    </span>
  );
}

function clean(search: RecordsSearch): RecordsSearch {
  return Object.fromEntries(Object.entries(search).filter(([, value]) => value !== undefined)) as RecordsSearch;
}

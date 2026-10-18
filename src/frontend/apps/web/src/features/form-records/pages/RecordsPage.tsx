import { useApi } from "@enterpriseforms/app-runtime";
import { Badge, Button, Card, DataTable, LoadingBlock, PageHeader, Select } from "@enterpriseforms/ui";
import { useInfiniteQuery, useQuery } from "@tanstack/react-query";
import { Link, useNavigate, useSearch } from "@tanstack/react-router";
import { ErrorPanel } from "../../support";

/** PG-010 records list: authority-filtered discovery; rows are unverified pointers until opened (B-07). */
export function RecordsPage() {
  const api = useApi();
  const navigate = useNavigate();
  const search = useSearch({ from: "/app/records" });
  const forms = useQuery({ queryKey: ["forms"], queryFn: () => api.forms.list().then((r) => r.result) });
  // Opaque, authority-issued continuation tokens (ADR-PB-006): each page is a fresh discover lookup; "Load more" appends.
  const records = useInfiniteQuery({
    queryKey: ["records", search.form ?? "all"],
    initialPageParam: undefined as string | undefined,
    queryFn: ({ pageParam }) => api.records.list({ ...(search.form ? { formDefinitionId: search.form } : {}), pageSize: 50, ...(pageParam ? { continuationToken: pageParam } : {}) }).then((r) => r.result),
    getNextPageParam: (last) => last.continuationToken ?? undefined,
  });
  const rows = records.data?.pages.flatMap((page) => page.items) ?? [];
  const selectedForm = forms.data?.find((form) => form.formDefinitionId === search.form);
  return (
    <>
      <PageHeader
        title="Records"
        lead="Only records you own or were explicitly granted are listed. Opening a record verifies its signed content."
        actions={
          <>
            <Button variant="secondary" busy={records.isFetching} onClick={() => void records.refetch()}>
              Refresh
            </Button>
            {selectedForm?.canCreateRecord && selectedForm.currentVersionNumber !== null ? (
              <Link to="/app/forms/$formDefinitionId/records/new" params={{ formDefinitionId: selectedForm.formDefinitionId }}>
                <Button>New {selectedForm.formDefinitionName}</Button>
              </Link>
            ) : null}
          </>
        }
      />
      <Card>
        <div className="max-w-md">
          <Select label="Form" value={search.form ?? "all"} options={[{ value: "all", label: "All forms" }, ...(forms.data ?? []).map((form) => ({ value: form.formDefinitionId, label: form.formDefinitionName }))]} onValueChange={(value) => void navigate({ to: "/app/records", search: value === "all" ? {} : { form: value } })} />
        </div>
        {records.isPending ? <LoadingBlock label="Loading records" /> : null}
        {records.isError ? <ErrorPanel error={records.error} onRetry={() => void records.refetch()} /> : null}
        {records.data ? (
          <DataTable
            caption="Records"
            rowKey={(row) => row.formRecordId}
            rows={rows}
            emptyMessage="No records are visible to you."
            columns={[
              { key: "form", header: "Form", isRowHeader: true, render: (row) => <Link to="/app/records/$formRecordId" params={{ formRecordId: row.formRecordId }} className="underline-offset-4 hover:underline">{row.formDefinitionName}</Link> },
              { key: "id", header: "Record", render: (row) => <code className="text-xs">{row.formRecordId}</code> },
              { key: "status", header: "Status", render: (row) => <Badge tone="neutral">{row.recordStatus}</Badge> },
              { key: "created", header: "Created", render: (row) => new Date(row.createdOn).toLocaleString() },
              { key: "modified", header: "Modified", render: (row) => new Date(row.modifiedOn).toLocaleString() },
              { key: "verified", header: "Integrity", render: (row) => (row.projectionVerified ? <Badge tone="success">Verified</Badge> : <Badge tone="neutral">Verified on open</Badge>) },
            ]}
          />
        ) : null}
        {records.hasNextPage ? (
          <div className="mt-3 flex items-center gap-3">
            <Button variant="secondary" busy={records.isFetchingNextPage} onClick={() => void records.fetchNextPage()}>
              Load more
            </Button>
            <span className="text-sm text-muted">{rows.length} shown</span>
          </div>
        ) : null}
      </Card>
    </>
  );
}

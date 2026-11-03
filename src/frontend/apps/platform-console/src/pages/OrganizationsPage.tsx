import { Badge, Button, Card, DataTable, DescriptionList, EmptyState, LoadingBlock, Pager, Select, StatusMessage, TextInput, useDebouncedValue, type DataTableColumn } from "@enterpriseforms/ui";
import { keepPreviousData, useQuery } from "@tanstack/react-query";
import { useEffect, useRef, useState } from "react";
import { consoleApi, type DirectoryItem } from "../api/client";
import { describeError } from "../app/errors";
import { identitySummary, type StateTone as StatusTone } from "../app/format";
import { navigate, onboardingPath, organizationPath } from "../app/router";
import { useConsoleSession } from "../app/session";
import { PAGE_SIZE, useNarrowViewport } from "./ApprovalsPage";

export const organizationFilters: Array<{ value: string; label: string }> = [
  { value: "all", label: "All" },
  { value: "needs_owners", label: "Needs owners" },
];

export const METADATA_ONLY_NOTICE = "Platform Operators see organization metadata and owner identities only — no records, documents, teams or shares.";

const lifecycleTone = (lifecycle: string): StatusTone => (lifecycle === "Active" ? "success" : lifecycle === "Activating" ? "info" : "danger");

/** Owner coverage in words: the warning and the advisory never rely on colour alone. */
export function OwnerCoverage({ item }: { item: DirectoryItem }) {
  const owners = Number(item.enabledOwnerCount);
  return (
    <div>
      <p>{owners}</p>
      {item.needsOwners ? <Badge tone="danger">Fewer than two owners</Badge> : owners === 2 ? <span className="text-help text-muted">Two owners — three recommended</span> : null}
    </div>
  );
}

function OrganizationCard({ o }: { o: DirectoryItem }) {
  return (
    <li>
      <Card as="article" aria-label={o.organizationDisplayName}>
        <div className="flex flex-wrap items-start justify-between gap-2">
          <p className="min-w-0 break-words font-semibold">{o.organizationDisplayName}</p>
          <Badge tone={lifecycleTone(o.lifecycle)}>{o.lifecycle}</Badge>
        </div>
        <dl className="grid grid-cols-[max-content_minmax(0,1fr)] gap-x-4 gap-y-1 text-sm">
          <dt className="text-muted">Enabled owners</dt>
          <dd>
            <OwnerCoverage item={o} />
          </dd>
          <dt className="text-muted">Security Administrators</dt>
          <dd>{Number(o.securityAdministratorCount)}</dd>
          <dt className="text-muted">Members</dt>
          <dd>{Number(o.memberCount)}</dd>
        </dl>
        <Button variant="secondary" size="sm" className="self-start" aria-label={`Details for ${o.organizationDisplayName}`} onClick={() => navigate(organizationPath(o.organizationId))}>
          Details
        </Button>
      </Card>
    </li>
  );
}

/** PG-072 Organizations: the platform directory. Metadata and owner identities only. */
export function OrganizationsPage() {
  const { touch, handleError } = useConsoleSession();
  const initial = new URLSearchParams(window.location.search);
  const [search, setSearch] = useState(initial.get("q") ?? "");
  const [filter, setFilter] = useState(organizationFilters.some((f) => f.value === initial.get("filter")) ? initial.get("filter")! : "all");
  const [cursors, setCursors] = useState<Array<string | undefined>>([undefined]);
  const q = useDebouncedValue(search.trim());
  const headingRef = useRef<HTMLHeadingElement>(null);
  const narrow = useNarrowViewport();

  useEffect(() => headingRef.current?.focus(), []);
  useEffect(() => {
    const params = new URLSearchParams();
    if (q) params.set("q", q);
    if (filter !== "all") params.set("filter", filter);
    window.history.replaceState(null, "", window.location.pathname + (params.size ? `?${params.toString()}` : ""));
    setCursors([undefined]);
  }, [q, filter]);

  const cursor = cursors[cursors.length - 1];
  const directory = useQuery({
    queryKey: ["organizations", q, filter, cursor ?? ""],
    queryFn: () => consoleApi.organizations({ q, filter, cursor, limit: PAGE_SIZE }),
    placeholderData: keepPreviousData,
  });

  useEffect(() => {
    if (directory.isSuccess && !directory.isPlaceholderData) {
      touch();
    }
  }, [directory.isSuccess, directory.isPlaceholderData, directory.dataUpdatedAt, touch]);
  useEffect(() => {
    if (directory.isError) {
      handleError(directory.error);
    }
  }, [directory.isError, directory.error, handleError]);

  const columns: Array<DataTableColumn<DirectoryItem>> = [
    { key: "name", header: "Organization", isRowHeader: true, render: (o) => o.organizationDisplayName },
    { key: "lifecycle", header: "Lifecycle", render: (o) => <Badge tone={lifecycleTone(o.lifecycle)}>{o.lifecycle}</Badge> },
    { key: "owners", header: "Enabled owners", render: (o) => <OwnerCoverage item={o} /> },
    { key: "admins", header: "Security Administrators", render: (o) => Number(o.securityAdministratorCount) },
    { key: "members", header: "Members", render: (o) => Number(o.memberCount) },
    {
      key: "actions",
      header: "Actions",
      isActions: true,
      render: (o) => (
        <Button variant="secondary" size="sm" aria-label={`Details for ${o.organizationDisplayName}`} onClick={() => navigate(organizationPath(o.organizationId))}>
          Details
        </Button>
      ),
    },
  ];

  const page = directory.data;
  const total = page ? Number(page.totalCount) : 0;
  const error = directory.isError ? describeError(directory.error) : null;
  const pageStart = (cursors.length - 1) * PAGE_SIZE + 1;
  return (
    <div className="flex flex-col gap-4">
      <div>
        <h1 ref={headingRef} tabIndex={-1} className="text-2xl font-semibold">
          Organizations
        </h1>
        <p className="mt-1 text-sm text-muted">{METADATA_ONLY_NOTICE}</p>
      </div>
      <div className="grid gap-3 rounded-panel border border-line bg-surface p-4 sm:grid-cols-[minmax(0,2fr)_minmax(0,1fr)]">
        <TextInput label="Search" type="search" value={search} onChange={(event) => setSearch(event.target.value)} placeholder="Organization name" maxLength={200} autoComplete="off" />
        <Select label="Show" value={filter} onValueChange={setFilter} options={organizationFilters} />
      </div>
      {error ? (
        <StatusMessage
          tone={error.tone}
          title={error.title}
          actions={
            error.retryable || error.reload ? (
              <Button variant="secondary" size="sm" onClick={() => void directory.refetch()}>
                Try again
              </Button>
            ) : undefined
          }
        >
          {error.message}
          {error.reference ? ` Reference ${error.reference}.` : ""}
        </StatusMessage>
      ) : null}
      {directory.isLoading ? <LoadingBlock label="Loading organizations" /> : null}
      {page ? (
        <div className="flex flex-col gap-3" aria-busy={directory.isFetching}>
          {page.items.length === 0 ? (
            <EmptyState title="No organizations match">Change the search or filter.</EmptyState>
          ) : narrow ? (
            <ul aria-label="Organizations" className="flex list-none flex-col gap-3 p-0">
              {page.items.map((o) => (
                <OrganizationCard key={o.organizationId} o={o} />
              ))}
            </ul>
          ) : (
            <DataTable caption="Organizations" columns={columns} rows={page.items} rowKey={(o) => o.organizationId} />
          )}
          <Pager
            label="Organization pages"
            noun="organizations"
            pageStart={page.items.length === 0 ? 0 : pageStart}
            itemCount={page.items.length}
            totalCount={total}
            pageSize={PAGE_SIZE}
            hasPrevious={cursors.length > 1}
            hasNext={Boolean(page.nextCursor)}
            busy={directory.isFetching}
            onPrevious={() => setCursors((c) => c.slice(0, -1))}
            onNext={() => setCursors((c) => (page.nextCursor ? [...c, page.nextCursor] : c))}
          />
        </div>
      ) : null}
    </div>
  );
}

/** PG-072 details. The Authority audits each view, so the page reads once per visit and again only on an explicit refresh. */
export function OrganizationDetailPage({ organizationId }: { organizationId: string }) {
  const { touch, handleError } = useConsoleSession();
  const detail = useQuery({ queryKey: ["organization", organizationId], queryFn: () => consoleApi.organization(organizationId), staleTime: Infinity, gcTime: 0 });
  const headingRef = useRef<HTMLHeadingElement>(null);
  useEffect(() => headingRef.current?.focus(), []);
  useEffect(() => {
    if (detail.isSuccess) {
      touch();
    }
  }, [detail.isSuccess, detail.dataUpdatedAt, touch]);
  useEffect(() => {
    if (detail.isError) {
      handleError(detail.error);
    }
  }, [detail.isError, detail.error, handleError]);

  const data = detail.data;
  const failure = detail.isError ? describeError(detail.error) : null;
  const summary = data?.summary;
  return (
    <div className="flex flex-col gap-4">
      <div>
        <Button variant="ghost" size="sm" onClick={() => navigate("/platform/organizations")}>
          ← Back to organizations
        </Button>
        <h1 ref={headingRef} tabIndex={-1} className="mt-1 text-2xl font-semibold">
          {summary ? summary.organizationDisplayName : "Organization"}
        </h1>
        <p className="mt-1 text-sm text-muted">{METADATA_ONLY_NOTICE}</p>
      </div>
      {detail.isLoading ? <LoadingBlock label="Loading organization" /> : null}
      {failure ? (
        <StatusMessage
          tone={failure.tone}
          title={failure.title}
          actions={
            failure.retryable || failure.reload ? (
              <Button variant="secondary" size="sm" onClick={() => void detail.refetch()}>
                Reload organization
              </Button>
            ) : undefined
          }
        >
          {failure.message}
          {failure.reference ? ` Reference ${failure.reference}.` : ""}
        </StatusMessage>
      ) : null}
      {data && summary ? (
        <>
          <div className="grid gap-4 lg:grid-cols-2">
            <Card
              title="Summary"
              actions={
                <Button variant="secondary" size="sm" busy={detail.isFetching} onClick={() => void detail.refetch()}>
                  Refresh
                </Button>
              }
            >
              <DescriptionList
                items={[
                  { term: "Lifecycle", detail: <Badge tone={lifecycleTone(summary.lifecycle)}>{summary.lifecycle}</Badge> },
                  { term: "Enabled owners", detail: <OwnerCoverage item={summary} /> },
                  { term: "Security Administrators", detail: Number(summary.securityAdministratorCount) },
                  { term: "Members", detail: Number(summary.memberCount) },
                  { term: "Organization ID", detail: <code className="break-all text-help">{summary.organizationId}</code> },
                ]}
              />
              {summary.needsOwners ? <p className="text-help text-muted">Owner recovery is not available yet.</p> : null}
            </Card>
            <Card title="Onboarding provenance">
              {summary.onboardingRequestId ? (
                <>
                  <p className="text-sm">
                    Created through Platform Console onboarding
                    {data.onboardingApprovedBy.length > 0 ? `, approved by ${data.onboardingApprovedBy.join(" and ")}` : ""}.
                  </p>
                  <Button variant="secondary" size="sm" className="self-start" onClick={() => navigate(onboardingPath(summary.onboardingRequestId!))}>
                    View onboarding request
                  </Button>
                </>
              ) : (
                <p className="text-sm">Created by a development fixture.</p>
              )}
            </Card>
          </div>
          <Card title="Owners">
            {data.owners.length === 0 ? (
              <p className="text-sm text-muted">This organization has no owners.</p>
            ) : (
              <ul aria-label="Owners" className="flex list-none flex-col gap-2 p-0">
                {data.owners.map((owner) => {
                  const identity = identitySummary(owner);
                  return (
                    <li key={owner.authorityUserId} className="flex flex-wrap items-center justify-between gap-2 rounded-control border border-line p-3">
                      <div className="min-w-0">
                        <p className="break-words font-medium">{identity.subject}</p>
                        <p className="break-words text-help text-muted">{identity.issuer}</p>
                      </div>
                      <Badge tone={owner.enabled ? "success" : "neutral"}>{owner.enabled ? "Enabled" : "Disabled"}</Badge>
                    </li>
                  );
                })}
              </ul>
            )}
          </Card>
          <details className="rounded-panel border border-line bg-surface p-4">
            <summary className="cursor-pointer text-sm font-medium">Technical details</summary>
            <div className="mt-3">
              <DescriptionList
                items={[
                  { term: "Authorization revision", detail: String(data.authorizationRevision) },
                  { term: "Projection applied revision", detail: String(data.projectionAppliedRevision) },
                ]}
              />
            </div>
          </details>
        </>
      ) : null}
    </div>
  );
}

import { useApi, useSession } from "@enterpriseforms/app-runtime";
import { Badge, Button, Card, LoadingBlock, PageHeader } from "@enterpriseforms/ui";
import { useQuery } from "@tanstack/react-query";
import { Link } from "@tanstack/react-router";
import type { ReactNode } from "react";
import { ErrorPanel } from "../../support";

/** PG-006 role-based dashboard: widgets come from the server; each card loads and refreshes independently (ADR-PB-007). */
export function DashboardPage() {
  const api = useApi();
  const session = useSession();
  const dashboard = useQuery({ queryKey: ["dashboard"], queryFn: () => api.dashboard.get().then((r) => r.result) });
  return (
    <>
      <PageHeader title={`Welcome, ${session.session?.displayName ?? ""}`} lead="Each card loads on its own; a failing card never blocks the others." />
      {dashboard.isPending ? <LoadingBlock label="Loading dashboard" /> : null}
      {dashboard.isError ? <ErrorPanel error={dashboard.error} onRetry={() => void dashboard.refetch()} /> : null}
      <div className="grid grid-cols-1 gap-4 md:grid-cols-2 xl:grid-cols-4">
        {(dashboard.data?.widgets ?? []).map((widget) => (
          <div key={widget.widgetIdentifier} className={Number(widget.minimumColumnSpan) >= 2 ? "md:col-span-2" : ""}>
            <DashboardCard identifier={widget.widgetIdentifier} title={widget.widgetTitle} />
          </div>
        ))}
      </div>
    </>
  );
}

function DashboardCard({ identifier, title }: { identifier: string; title: string }) {
  const api = useApi();
  const count = useQuery({ queryKey: ["dashboard", "count", identifier], queryFn: () => api.dashboard.count(identifier).then((r) => r.result), enabled: identifier === "my-records" });
  const body = useQuery({
    queryKey: ["dashboard", "widget", identifier],
    queryFn: async (): Promise<ReactNode> => {
      switch (identifier) {
        case "my-records": {
          const { result } = await api.records.list({ pageSize: 5 });
          return result.items.length === 0 ? <p className="text-muted">No records visible to you yet.</p> : <List items={result.items.map((item) => ({ key: item.formRecordId, to: `/app/records/${item.formRecordId}`, label: `${item.formDefinitionName} · ${new Date(item.modifiedOn).toLocaleString()}`, badge: item.recordStatus }))} />;
        }
        case "upload-activity": {
          const { result } = await api.uploads.mine();
          return result.length === 0 ? <p className="text-muted">No upload sessions in progress.</p> : <List items={result.map((item) => ({ key: item.uploadSessionId, to: "/app/uploads", label: item.originalFileName, badge: item.sessionState }))} />;
        }
        case "recent-activity": {
          const { result } = await api.audit.mine(8);
          return result.length === 0 ? <p className="text-muted">No recent activity.</p> : <List items={result.map((item) => ({ key: item.auditEventId, label: `${new Date(item.eventOccurredOn).toLocaleString()} · ${item.eventDescription}`, badge: item.eventOutcome }))} />;
        }
        case "permitted-forms":
        case "my-designs": {
          const { result } = await api.forms.list();
          const forms = identifier === "my-designs" ? result.filter((form) => form.canDesign) : result.filter((form) => form.canCreateRecord && form.currentVersionNumber !== null);
          return forms.length === 0 ? <p className="text-muted">{identifier === "my-designs" ? "You do not design any forms yet." : "No published forms you can use."}</p> : <List items={forms.map((form) => ({ key: form.formDefinitionId, to: identifier === "my-designs" ? `/app/forms/${form.formDefinitionId}/design` : `/app/forms/${form.formDefinitionId}/records/new`, label: form.formDefinitionName, badge: form.currentVersionNumber === null ? "Unpublished" : `v${form.currentVersionNumber}` }))} />;
        }
        case "organization-branding": {
          const { result } = await api.organization.branding();
          return <List items={[{ key: "branding", to: "/app/organization/branding", label: `${result.organizationDisplayName} · revision ${result.brandingRevision}`, badge: result.hasLogo ? "Logo set" : "No logo" }]} />;
        }
        default:
          return <p className="text-muted">Unknown widget.</p>;
      }
    },
  });
  return (
    <Card
      title={
        <span className="flex items-center gap-2">
          {title}
          {count.data ? <Badge tone="neutral">{String(count.data.count)}</Badge> : null}
        </span>
      }
      actions={
        <Button size="sm" variant="secondary" aria-label={`Refresh ${title}`} busy={body.isFetching} onClick={() => void Promise.all([body.refetch(), count.refetch()])}>
          Refresh
        </Button>
      }
      aria-busy={body.isFetching}
    >
      {body.isPending ? <LoadingBlock label={`Loading ${title}`} /> : body.isError ? <ErrorPanel error={body.error} onRetry={() => void body.refetch()} /> : body.data}
    </Card>
  );
}

function List({ items }: { items: Array<{ key: string; to?: string; label: string; badge?: string }> }) {
  return (
    <ul className="flex flex-col divide-y divide-line">
      {items.map((item) => (
        <li key={item.key} className="flex items-center justify-between gap-2 py-2">
          {item.to ? (
            <Link to={item.to} className="min-w-0 truncate underline-offset-4 hover:underline">
              {item.label}
            </Link>
          ) : (
            <span className="min-w-0 truncate">{item.label}</span>
          )}
          {item.badge ? <Badge tone="neutral">{item.badge}</Badge> : null}
        </li>
      ))}
    </ul>
  );
}

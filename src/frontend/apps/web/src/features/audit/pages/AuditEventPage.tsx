import { useApi, useSession } from "@enterpriseforms/app-runtime";
import { Badge, Button, Card, DescriptionList, LoadingBlock, PageHeader, StatusMessage, useFormatter } from "@enterpriseforms/ui";
import { useQuery } from "@tanstack/react-query";
import { Link, useParams } from "@tanstack/react-router";
import { useState } from "react";
import { formatInstant } from "../components/auditExplorerModel";
import { ErrorPanel } from "../../support";

/**
 * PG-055 audit event detail (C8 M4-2). The server renders the event through its redaction catalog: allowlisted identifiers,
 * codes, counts and directory names only; free text, field keys and labels, option text, design diffs and record scopes are
 * listed as withheld (label only). Opening the event is authorized and recorded by the Authority; it grants nothing on the
 * record it names — the record link is authorized separately.
 */
export function AuditEventPage() {
  useFormatter();
  const { auditEventId } = useParams({ strict: false }) as { auditEventId: string };
  const api = useApi();
  const session = useSession();
  const allowed = session.hasCapability("audit.view");
  const detail = useQuery({ queryKey: ["audit", "event", auditEventId], queryFn: ({ signal }) => api.audit.event(auditEventId, signal).then((r) => r.result), enabled: allowed, staleTime: 0 });
  const [copied, setCopied] = useState(false);

  const back = (
    <Link to="/app/security/audit" className="inline-flex min-h-control items-center underline underline-offset-4">
      All audit events
    </Link>
  );
  if (!allowed) {
    return (
      <>
        <PageHeader title="Audit event" />
        <StatusMessage tone="info" title="No permission">
          Audit events are available to Security Auditors.
        </StatusMessage>
      </>
    );
  }
  if (detail.isPending) {
    return <LoadingBlock label="Loading audit event" />;
  }
  if (detail.isError) {
    return (
      <>
        <PageHeader title="Audit event" actions={back} />
        <ErrorPanel error={detail.error} onRetry={() => void detail.refetch()} />
      </>
    );
  }
  const data = detail.data;
  const e = data.event;
  const copy = () => {
    void navigator.clipboard?.writeText(e.auditEventId).then(() => setCopied(true));
  };
  return (
    <>
      <PageHeader title={e.actionLabel} lead={formatInstant(e.occurredOn)} actions={back} />
      <div className="flex flex-col gap-4">
        <StatusMessage tone="warning" title="Not independently verified">
          {data.evidenceStatement}
        </StatusMessage>
        {!e.recognized ? (
          <StatusMessage tone="warning" title="Action not in the redaction catalog">
            Only the event envelope is shown. The action may have been forged or written by a newer version of the application.
          </StatusMessage>
        ) : null}
        <div className="grid grid-cols-1 gap-4 xl:grid-cols-[minmax(0,3fr)_minmax(0,2fr)]">
          <Card title="Event">
            <div className="flex flex-col gap-3">
              <p>{data.description}</p>
              <DescriptionList
                items={[
                  { term: "Action", detail: <span className="font-mono">{e.action}</span> },
                  { term: "Outcome", detail: <Badge tone={e.outcome === "Success" ? "success" : "neutral"}>{e.outcome}</Badge> },
                  { term: "Category", detail: e.category },
                  { term: "Recorded (local)", detail: formatInstant(e.occurredOn) },
                  { term: "Recorded (UTC)", detail: <span className="font-mono">{e.occurredOn}</span> },
                  { term: "Sequence", detail: <span className="font-mono">{String(e.sequence)}</span> },
                  { term: "Actor", detail: e.actor.displayName ? `${e.actor.displayName} (${e.actor.actorType})` : e.actor.actorType },
                  { term: "Actor user ID", detail: <span className="font-mono break-all">{e.actor.userId ?? "—"}</span> },
                  { term: "Target", detail: e.target.entityType ? <span className="font-mono break-all">{`${e.target.entityType} ${e.target.entityId ?? ""}`}</span> : "—" },
                  { term: "Form", detail: e.formDisplayName ?? (e.formDefinitionId ? <span className="font-mono break-all">{e.formDefinitionId}</span> : "—") },
                  { term: "Record ID", detail: <span className="font-mono break-all">{e.formRecordId ?? "—"}</span> },
                  { term: "Field ID", detail: <span className="font-mono break-all">{e.fieldDefinitionId ?? "—"}</span> },
                  { term: "Correlation ID", detail: <span className="font-mono break-all">{e.correlationId}</span> },
                  { term: "Event schema", detail: String(e.schemaVersion) },
                  {
                    term: "Event ID",
                    detail: (
                      <span className="flex flex-wrap items-center gap-2">
                        <span className="font-mono break-all">{e.auditEventId}</span>
                        <Button variant="ghost" size="sm" onClick={copy}>
                          {copied ? "Copied" : "Copy ID"}
                        </Button>
                      </span>
                    ),
                  },
                ]}
              />
            </div>
          </Card>
          <div className="flex flex-col gap-4">
            <Card title="Details">
              {data.metadata.length === 0 ? (
                <p className="text-muted">No further details are shown for this event.</p>
              ) : (
                <DescriptionList items={data.metadata.map((m) => ({ term: m.label, detail: <span className={m.kind === "DirectoryName" ? "" : "font-mono break-all"}>{m.value}</span> }))} />
              )}
            </Card>
            <Card title="Withheld by policy">
              {data.withheldMetadata.length === 0 ? (
                <p className="text-muted">Nothing in this event is withheld.</p>
              ) : (
                <ul className="flex flex-col gap-1">
                  {data.withheldMetadata.map((w, index) => (
                    <li key={`${w.label}-${index}`}>
                      <span className="font-medium">{w.label}</span> — <span className="text-muted">{w.policy}</span>
                    </li>
                  ))}
                </ul>
              )}
            </Card>
            <Card title="Evidence">
              <DescriptionList
                items={[
                  { term: "This view", detail: `${data.accessEvidence.statement} Audit-stream revision ${String(data.accessEvidence.authorityJournalRevision)}.` },
                  { term: "Archive receipt", detail: `${data.receipt.state} (${data.receipt.reason})` },
                  { term: "Ordering", detail: data.orderingStatement },
                ]}
              />
            </Card>
            <Card title="Related">
              <ul className="flex flex-col gap-1">
                {data.links.map((l) => (
                  <li key={l.relation}>
                    <a href={l.href} className="underline underline-offset-4">
                      {l.label}
                    </a>
                  </li>
                ))}
              </ul>
            </Card>
          </div>
        </div>
      </div>
    </>
  );
}

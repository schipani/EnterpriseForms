import { presentError, useApi, useSession } from "@enterpriseforms/app-runtime";
import type { SecurityIncidentDetailResponse, UpdateSecurityIncidentRequest } from "@enterpriseforms/api-client";
import { Badge, Button, Card, DataTable, DescriptionList, LoadingBlock, PageHeader, Select, StatusMessage, TextArea } from "@enterpriseforms/ui";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Link, useParams } from "@tanstack/react-router";
import { useEffect, useState } from "react";
import { ErrorPanel } from "../../support";
import { DETECTION_SOURCES, INCIDENT_STATUSES, formatInstant, incidentTypeLabel, isClosedStatus, severityOf, statusOf } from "../components/incidentModel";

const UNASSIGNED = "unassigned";

/**
 * Incident detail (PG-056): metadata, detection history, safe technical evidence, notes, investigator, status and audit
 * trail. No record content is ever shown, and no action here repairs, re-signs or releases a record.
 */
export function SecurityIncidentPage() {
  const { securityIncidentId } = useParams({ strict: false }) as { securityIncidentId: string };
  const api = useApi();
  const session = useSession();
  const client = useQueryClient();
  const detail = useQuery({ queryKey: ["security", "incident", securityIncidentId], queryFn: ({ signal }) => api.security.incident(securityIncidentId, signal).then((r) => r.result), enabled: session.hasCapability("security.administer") });
  const store = (result: SecurityIncidentDetailResponse) => {
    client.setQueryData(["security", "incident", securityIncidentId], result);
    void client.invalidateQueries({ queryKey: ["security", "incidents"] });
    void client.invalidateQueries({ queryKey: ["security", "incident-summary"] });
  };

  if (!session.hasCapability("security.administer")) {
    return (
      <>
        <PageHeader title="Security incident" />
        <StatusMessage tone="info" title="No permission">
          Security incidents are available to Security Administrators.
        </StatusMessage>
      </>
    );
  }
  if (detail.isPending) {
    return <LoadingBlock label="Loading incident" />;
  }
  if (detail.isError) {
    return (
      <>
        <PageHeader title="Security incident" />
        <ErrorPanel error={detail.error} onRetry={() => void detail.refetch()} />
      </>
    );
  }
  const data = detail.data;
  const incident = data.incident;
  const severity = severityOf(incident.incidentSeverityName);
  const status = statusOf(incident.incidentStatusName);
  return (
    <>
      <PageHeader
        title={`Incident ${incident.incidentReference}`}
        lead={incidentTypeLabel(incident.incidentTypeName)}
        actions={
          <Link to="/app/security/incidents" className="inline-flex min-h-control items-center underline underline-offset-4">
            All incidents
          </Link>
        }
      />
      <div className="flex flex-col gap-4">
        <StatusMessage tone="info" title="Evidence only">
          {data.evidenceStatement}
        </StatusMessage>
        <div className="grid grid-cols-1 gap-4 xl:grid-cols-[minmax(0,3fr)_minmax(0,2fr)]">
          <Card title="Incident details">
            <DescriptionList
              items={[
                { term: "Status", detail: <Badge tone={status.tone}>{status.label}</Badge> },
                { term: "Severity", detail: <Badge tone={severity.tone}>{severity.label}</Badge> },
                { term: "Type", detail: incidentTypeLabel(incident.incidentTypeName) },
                {
                  term: "Record",
                  detail: !incident.recordScoped ? (
                    "Not record-specific"
                  ) : incident.recordWithheld || !incident.formRecordId ? (
                    <span className="text-muted">Withheld — you cannot discover this record</span>
                  ) : (
                    <span className="flex flex-col">
                      <span className="font-mono">{incident.recordReference}</span>
                      <span className="text-help text-muted">The record stays quarantined; opening it fails closed until it is recovered.</span>
                    </span>
                  ),
                },
                { term: "First detected", detail: formatInstant(incident.firstDetectedOn) },
                { term: "Last detected", detail: formatInstant(incident.lastDetectedOn) },
                { term: "Detections", detail: String(incident.occurrenceCount) },
                { term: "Detected by", detail: DETECTION_SOURCES[data.detectionSourceName] ?? data.detectionSourceName },
                { term: "Verification code", detail: <code className="break-all">{data.verificationErrorCode || "—"}</code> },
                { term: "Correlation ID", detail: <code className="break-all">{data.firstCorrelationIdentifier}</code> },
                { term: "Investigator", detail: incident.assignedToDisplayName ?? <span className="text-muted">Unassigned</span> },
                ...(data.resolvedOn ? [{ term: "Closed", detail: `${formatInstant(data.resolvedOn)} by ${data.resolvedByDisplayName ?? "unknown"}` }, { term: "Resolution", detail: <p className="whitespace-pre-wrap">{data.resolutionExplanationText}</p> }] : []),
              ]}
            />
          </Card>
          <InvestigationCard key={String(data.incidentRevision)} data={data} onSaved={store} update={(request) => api.security.updateIncident(securityIncidentId, request).then((r) => r.result)} />
        </div>
        <NotesCard data={data} onSaved={store} add={(note) => api.security.addIncidentNote(securityIncidentId, { investigationNote: note }).then((r) => r.result)} />
        <Card title="Detection history">
          <p className="text-help text-muted">Each row is one detection (repeats within a minute are counted together). Times are when the application observed the problem.</p>
          <DataTable
            caption="Detection history"
            rows={[...data.occurrences]}
            rowKey={(row) => row.securityIncidentOccurrenceId}
            emptyMessage="No detection history."
            columns={[
              { key: "when", header: "Detected", isRowHeader: true, render: (row) => <span className="whitespace-nowrap">{formatInstant(row.occurrenceDetectedOn)}</span> },
              { key: "type", header: "Observation", render: (row) => incidentTypeLabel(row.observedIncidentTypeName) },
              { key: "source", header: "Source", render: (row) => DETECTION_SOURCES[row.detectionSourceName] ?? row.detectionSourceName },
              { key: "code", header: "Code", render: (row) => <code>{row.verificationErrorCode || "—"}</code> },
              { key: "revision", header: "Revision", render: (row) => (row.committedContentRevision === null || row.committedContentRevision === undefined ? "—" : String(row.committedContentRevision)) },
              { key: "by", header: "Observed during", render: (row) => (row.observedByDisplayName ? `${row.observedByDisplayName}'s request` : "—") },
              { key: "correlation", header: "Correlation ID", render: (row) => <code className="break-all text-help">{row.requestCorrelationIdentifier}</code> },
            ]}
          />
        </Card>
        <Card title="Audit trail">
          <DataTable
            caption="Incident audit trail"
            rows={[...data.auditTrail]}
            rowKey={(row) => row.auditEventId}
            emptyMessage="No audit events."
            columns={[
              { key: "when", header: "When", isRowHeader: true, render: (row) => <span className="whitespace-nowrap">{formatInstant(row.eventOccurredOn)}</span> },
              { key: "action", header: "Action", render: (row) => <code className="text-help">{row.eventAction}</code> },
              { key: "description", header: "Description", render: (row) => row.eventDescription },
              { key: "actor", header: "By", render: (row) => row.actorDisplayName ?? "System" },
            ]}
          />
        </Card>
      </div>
    </>
  );
}

function InvestigationCard({ data, update, onSaved }: { data: SecurityIncidentDetailResponse; update: (request: UpdateSecurityIncidentRequest) => Promise<SecurityIncidentDetailResponse>; onSaved: (result: SecurityIncidentDetailResponse) => void }) {
  const incident = data.incident;
  const [status, setStatus] = useState(incident.incidentStatusName);
  const [assignee, setAssignee] = useState(incident.assignedToUserId ?? UNASSIGNED);
  const [explanation, setExplanation] = useState("");
  const [validation, setValidation] = useState<string | null>(null);
  const [saved, setSaved] = useState(false);
  useEffect(() => setSaved(false), [status, assignee, explanation]);
  const closing = status !== incident.incidentStatusName && isClosedStatus(status);
  const save = useMutation({
    mutationFn: () => {
      const request: UpdateSecurityIncidentRequest = { expectedIncidentRevision: data.incidentRevision, clearAssignment: false };
      if (status !== incident.incidentStatusName) {
        request.incidentStatusName = status;
      }
      if (closing) {
        request.resolutionExplanation = explanation.trim();
      }
      const current = incident.assignedToUserId ?? UNASSIGNED;
      if (assignee !== current) {
        if (assignee === UNASSIGNED) {
          request.clearAssignment = true;
        } else {
          request.assignedToUserId = assignee;
        }
      }
      return update(request);
    },
    onSuccess: (result) => {
      setSaved(true);
      onSaved(result);
    },
  });
  const changed = status !== incident.incidentStatusName || assignee !== (incident.assignedToUserId ?? UNASSIGNED);
  const submit = () => {
    if (closing && explanation.trim().length === 0) {
      setValidation("Explain the resolution before closing the incident.");
      return;
    }
    setValidation(null);
    save.mutate();
  };
  const conflict = save.isError && presentError(save.error).code === "INCIDENT_MODIFIED_CONCURRENTLY";
  return (
    <Card title="Investigation">
      <div className="flex flex-col gap-3">
        <Select label="Status" value={status} options={INCIDENT_STATUSES.map((s) => ({ value: s.value, label: s.label }))} onValueChange={setStatus} />
        {closing ? (
          <TextArea
            label="Resolution explanation"
            required
            maxLength={2000}
            helpText="Required to close. Describe what was found and done. Do not include record content or personal information."
            value={explanation}
            error={validation ?? undefined}
            onChange={(event) => setExplanation(event.target.value)}
          />
        ) : null}
        <Select
          label="Investigator"
          value={assignee}
          helpText="Only Security Administrators can be assigned."
          options={[{ value: UNASSIGNED, label: "Unassigned" }, ...data.assignableInvestigators.map((i) => ({ value: i.userId, label: i.displayName }))]}
          onValueChange={setAssignee}
        />
        <p className="text-help text-muted">Changing the status never restores access to a quarantined record or changes verification.</p>
        {conflict ? (
          <StatusMessage tone="warning" title="Someone else changed this incident">
            Reload the page to see their change, then try again.
          </StatusMessage>
        ) : save.isError ? (
          <ErrorPanel error={save.error} />
        ) : null}
        {saved && !changed ? <StatusMessage tone="success" title="Incident updated" /> : null}
        <div>
          <Button onClick={submit} busy={save.isPending} disabled={!changed}>
            Save changes
          </Button>
        </div>
      </div>
    </Card>
  );
}

function NotesCard({ data, add, onSaved }: { data: SecurityIncidentDetailResponse; add: (note: string) => Promise<SecurityIncidentDetailResponse>; onSaved: (result: SecurityIncidentDetailResponse) => void }) {
  const [note, setNote] = useState("");
  const [validation, setValidation] = useState<string | null>(null);
  const save = useMutation({
    mutationFn: () => add(note.trim()),
    onSuccess: (result) => {
      setNote("");
      onSaved(result);
    },
  });
  return (
    <Card title="Investigation notes">
      {data.notes.length === 0 ? <p className="text-help text-muted">No notes yet.</p> : null}
      <ol className="flex flex-col gap-3">
        {data.notes.map((n) => (
          <li key={n.securityIncidentNoteId} className="rounded-control border border-line bg-page p-3">
            <p className="text-help text-muted">
              {n.authorDisplayName} · {formatInstant(n.noteCreatedOn)}
            </p>
            <p className="mt-1 whitespace-pre-wrap break-words">{n.investigationNoteText}</p>
          </li>
        ))}
      </ol>
      <form
        className="flex flex-col gap-2"
        onSubmit={(event) => {
          event.preventDefault();
          if (note.trim().length === 0) {
            setValidation("Write a note first.");
            return;
          }
          setValidation(null);
          save.mutate();
        }}
      >
        <TextArea label="Add a note" maxLength={2000} helpText="Notes are kept with the incident and cannot be edited. Do not include record content or personal information." value={note} error={validation ?? undefined} onChange={(event) => setNote(event.target.value)} />
        {save.isError ? <ErrorPanel error={save.error} /> : null}
        <div>
          <Button type="submit" variant="secondary" busy={save.isPending}>
            Add note
          </Button>
        </div>
      </form>
    </Card>
  );
}

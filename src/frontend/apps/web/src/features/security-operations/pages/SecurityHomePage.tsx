import { useApi, useSession } from "@enterpriseforms/app-runtime";
import type { SecurityOverviewSection, SecurityOverviewSectionResponse } from "@enterpriseforms/api-client";
import { ActionIcons, Badge, Button, Card, DescriptionList, EmptyState, KpiTile, LoadingBlock, PageHeader, StatusMessage, getActiveFormatter, useFormatter } from "@enterpriseforms/ui";
import { useQuery, type UseQueryResult } from "@tanstack/react-query";
import { Link } from "@tanstack/react-router";
import type { ReactNode } from "react";
import { ErrorPanel } from "../../support";
import {
  LIVE_CHECK_UNAVAILABLE,
  OVERVIEW_STATEMENT,
  approvalTotals,
  attestationBadge,
  commandKindLabel,
  commandStateLabel,
  completeWhenAttestedDetail,
  coverageStateOf,
  formatAge,
  gateStateOf,
  streamLabel,
  widgetFailure,
} from "../components/securityHomeModel";

/**
 * Security Home (PG-052, C8 M4-4; AC-8.4, AC-8.5). Six widgets, each authorized by the server on its own and refreshed on its own
 * (PB-07); none depends on another and no permission is cached here.
 * <ul>
 *   <li>Gate, commands, archive and coverage come from the Authorization Authority's committed state under its Effective-edge rule,
 *   so they work while the organization gate is closed. Gate, commands and coverage are for Security Administrators; archive also
 *   for Security Auditors (D8-70). A refusal is shown as "not available to your role".</li>
 *   <li>Search approvals and incidents keep their existing live permission check (OD-6). Without that capability, or when the check
 *   cannot run, they show an unavailable state and request nothing.</li>
 * </ul>
 * Counts, codes, kinds, revisions and times only. Nothing on this page is independently verified evidence or a compliance result.
 */
export function SecurityHomePage() {
  useFormatter();
  return (
    <>
      <PageHeader title="Security home" lead="Authorization gate, unresolved security commands, archive delivery and encryption coverage for this organization." />
      <div className="flex flex-col gap-4">
        <StatusMessage tone="info" title="What this page shows">
          {OVERVIEW_STATEMENT} Each section is authorized separately; a section you are not authorized for says so.
        </StatusMessage>
        <div className="grid grid-cols-1 gap-4 lg:grid-cols-2">
          <GateWidget />
          <ArchiveWidget />
        </div>
        <CommandsWidget />
        <CoverageWidget />
        <div className="grid grid-cols-1 gap-4 lg:grid-cols-2">
          <SearchApprovalsWidget />
          <IncidentsWidget />
        </div>
      </div>
    </>
  );
}

function useSection(section: SecurityOverviewSection) {
  const api = useApi();
  return useQuery({
    queryKey: ["security", "overview", section],
    queryFn: ({ signal }) => api.security.overview(section, signal).then((r) => r.result),
    staleTime: 0,
  });
}

function RefreshButton({ label, query }: { label: string; query: Pick<UseQueryResult, "isFetching" | "refetch"> }) {
  return (
    <Button variant="secondary" size="sm" icon={ActionIcons.refresh} disabled={query.isFetching} aria-label={`Refresh ${label}`} onClick={() => void query.refetch()}>
      Refresh
    </Button>
  );
}

/** Loading, refusal and failure states shared by the four Authority-backed sections. */
function SectionWidget({ title, query, children }: { title: string; query: UseQueryResult<SecurityOverviewSectionResponse>; children: (data: SecurityOverviewSectionResponse) => ReactNode }) {
  const failure = query.isError ? widgetFailure(query.error) : null;
  return (
    <Card title={title} actions={<RefreshButton label={title.toLowerCase()} query={query} />} aria-busy={query.isFetching || undefined}>
      {query.isPending ? (
        <LoadingBlock label={`Loading ${title.toLowerCase()}`} />
      ) : failure === "forbidden" ? (
        <p className="text-sm text-muted">Not available to your role. The Authorization Authority decides access to each section.</p>
      ) : query.isError ? (
        <ErrorPanel error={query.error} onRetry={() => void query.refetch()} />
      ) : (
        <div className="flex flex-col gap-3">
          {children(query.data)}
          <p className="text-xs text-muted">As of {getActiveFormatter().instant(query.data.computedOn)}.</p>
        </div>
      )}
    </Card>
  );
}

function GateWidget() {
  const query = useSection("gate");
  return (
    <SectionWidget title="Authorization gate" query={query}>
      {(data) => {
        const gate = data.gate!;
        const state = gateStateOf(gate.gateState);
        return (
          <>
            <div className="flex flex-wrap items-center gap-2">
              <Badge tone={state.tone}>{state.label}</Badge>
              {gate.integrityQuarantined ? <Badge tone="danger">Integrity quarantine</Badge> : null}
            </div>
            <p className="text-sm">{state.explanation}</p>
            <DescriptionList
              items={[
                { term: "Authorization revision", detail: String(gate.authorizationRevision) },
                { term: "Applied to permission service", detail: String(gate.projectionAppliedRevision) },
                { term: "Content-integrity revision", detail: String(gate.contentIntegrityRevision) },
              ]}
            />
          </>
        );
      }}
    </SectionWidget>
  );
}

function CommandsWidget() {
  const query = useSection("commands");
  return (
    <SectionWidget title="Unresolved security commands" query={query}>
      {(data) => {
        const commands = data.commands!;
        return (
          <>
            <div className="grid grid-cols-1 gap-3 sm:grid-cols-3">
              {commands.stateCounts.map((s) => (
                <KpiTile key={s.commandState} label={commandStateLabel(s.commandState)} value={getActiveFormatter().number(s.commandCount)} />
              ))}
            </div>
            <p className="text-sm">Oldest unresolved: {commands.oldestUnresolvedCreatedOn ? `${formatAge(commands.oldestUnresolvedAgeSeconds)} old` : "none"}</p>
            {commands.commands.length === 0 ? (
              <EmptyState title="No unresolved commands" />
            ) : (
              <div className="overflow-x-auto">
                <table className="w-full text-left text-sm">
                  <caption className="sr-only">Oldest unresolved security commands, at most {String(commands.listLimit)}</caption>
                  <thead>
                    <tr className="border-b border-input">
                      <th scope="col" className="py-2 pr-3 font-semibold">Kind</th>
                      <th scope="col" className="py-2 pr-3 font-semibold">State</th>
                      <th scope="col" className="py-2 pr-3 font-semibold">Revision</th>
                      <th scope="col" className="py-2 pr-3 font-semibold">Created</th>
                      <th scope="col" className="py-2 font-semibold">Age</th>
                    </tr>
                  </thead>
                  <tbody>
                    {commands.commands.map((c, index) => (
                      <tr key={`${c.createdOn}-${index}`} className="border-b border-input last:border-0">
                        <td className="py-2 pr-3">{commandKindLabel(c.commandKind)}</td>
                        <td className="py-2 pr-3">{commandStateLabel(c.commandState)}</td>
                        <td className="py-2 pr-3">{c.committedAuthorizationRevision === null || c.committedAuthorizationRevision === undefined ? "—" : String(c.committedAuthorizationRevision)}</td>
                        <td className="py-2 pr-3">{getActiveFormatter().instant(c.createdOn)}</td>
                        <td className="py-2">{formatAge(c.ageSeconds)}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
            <p className="text-xs text-muted">{commands.listStatement}</p>
          </>
        );
      }}
    </SectionWidget>
  );
}

function ArchiveWidget() {
  const query = useSection("archive");
  return (
    <SectionWidget title="Archive delivery" query={query}>
      {(data) => {
        const archive = data.archive!;
        return (
          <>
            <ul className="flex flex-col gap-3">
              {archive.streams.map((s) => (
                <li key={s.streamName} className="flex flex-col gap-1">
                  <div className="flex flex-wrap items-center gap-2">
                    <span className="font-semibold">{streamLabel(s.streamName)}</span>
                    {Number(s.verificationFailedCount) > 0 ? <Badge tone="danger">{`${String(s.verificationFailedCount)} verification failed`}</Badge> : null}
                    {Number(s.pendingCount) > 0 ? <Badge tone="warning">{`${String(s.pendingCount)} pending`}</Badge> : <Badge tone="neutral">None pending</Badge>}
                  </div>
                  <p className="text-sm text-muted">
                    {String(s.anchoredCount)} anchored · last anchored {s.lastAnchoredOn ? getActiveFormatter().instant(s.lastAnchoredOn) : "never"}
                    {s.oldestPendingCreatedOn ? ` · oldest pending ${formatAge(s.oldestPendingAgeSeconds)} old` : ""}
                  </p>
                </li>
              ))}
            </ul>
            <p className="text-xs text-muted">{archive.archiveStatement}</p>
          </>
        );
      }}
    </SectionWidget>
  );
}

function CoverageWidget() {
  const query = useSection("coverage");
  return (
    <SectionWidget title="Encryption coverage" query={query}>
      {(data) => {
        const coverage = data.coverage!;
        return (
          <>
            <div className="grid grid-cols-2 gap-3 md:grid-cols-4">
              <KpiTile label="Protection decisions" value={getActiveFormatter().number(coverage.decisionCount)} />
              <KpiTile label="Complete when attested" value={getActiveFormatter().number(coverage.attestedCompleteDecisionCount)} detail={completeWhenAttestedDetail(coverage.decisions)} />
              <KpiTile label="No attestation" value={getActiveFormatter().number(coverage.missingDecisionCount)} />
              <KpiTile label="Possibly stale" value={getActiveFormatter().number(coverage.staleDecisionCount)} detail="Organization-wide comparison" />
            </div>
            <StatusMessage tone="info" title="How to read staleness">
              {coverage.stalenessStatement}
            </StatusMessage>
            {coverage.decisions.length === 0 ? (
              <EmptyState title="No current protection decisions" />
            ) : (
              <ul className="flex flex-col divide-y divide-input">
                {coverage.decisions.map((d) => {
                  const state = coverageStateOf(d.attestationState);
                  const a = d.attestation;
                  return (
                    <li key={d.securityCommandId} className="flex flex-col gap-1 py-3">
                      <div className="flex flex-wrap items-center gap-2">
                        <span className="font-semibold">{d.formDisplayName ?? "Unnamed form"}</span>
                        <span className="text-sm text-muted">
                          field <code>{d.fieldDefinitionId}</code> · {d.protectionDirection === "Encrypt" ? "Encryption required" : "Encryption released"}
                        </span>
                        <Badge tone={state.tone}>{state.label}</Badge>
                        {a ? <Badge tone={attestationBadge(a.attestationComplete).tone}>{attestationBadge(a.attestationComplete).label}</Badge> : null}
                      </div>
                      <p className="text-sm text-muted">{state.explanation}</p>
                      {a ? (
                        <details className="text-sm">
                          <summary className="cursor-pointer underline underline-offset-4">Attestation counts</summary>
                          <DescriptionList
                            items={[
                              { term: "Attested", detail: `${getActiveFormatter().instant(a.attestedOn)} · audit journal revision ${String(a.auditJournalRevision)}` },
                              { term: "Expected records", detail: String(a.expectedRecordCount) },
                              { term: "Accepted / compliant", detail: `${String(a.acceptedRecordCount)} / ${String(a.compliantRecordCount)}` },
                              { term: "Pending / quarantined", detail: `${String(a.pendingCreationRecordCount)} / ${String(a.quarantinedRecordCount)}` },
                              { term: "Unverified / not yet censused", detail: `${String(a.unverifiedRecordCount)} / ${String(a.notYetCensusedRecordCount)}` },
                              { term: "Retired records / retained history", detail: `${String(a.retiredRecordCount)} / ${String(a.retainedHistoryRevisionCount)}` },
                              ...(a.attestationIncompleteReasonCode ? [{ term: "Incomplete because", detail: a.attestationIncompleteReasonCode }] : []),
                            ]}
                          />
                        </details>
                      ) : null}
                    </li>
                  );
                })}
              </ul>
            )}
            <p className="text-xs text-muted">
              {coverage.coverageStatement} {coverage.formNameStatement}
            </p>
          </>
        );
      }}
    </SectionWidget>
  );
}

/** Existing search-approval endpoint and its existing live check (OD-6). */
function SearchApprovalsWidget() {
  const session = useSession();
  const api = useApi();
  const allowed = session.hasCapability("security.administer");
  const query = useQuery({
    queryKey: ["organization", "search-approvals"],
    queryFn: ({ signal }) => api.organization.searchApprovals(signal).then((r) => r.result),
    enabled: allowed,
  });
  const totals = query.data ? approvalTotals(query.data.forms) : null;
  return (
    <Card title="Search approvals" actions={allowed ? <RefreshButton label="search approvals" query={query} /> : undefined}>
      {!allowed ? (
        <p className="text-sm text-muted">{LIVE_CHECK_UNAVAILABLE}</p>
      ) : query.isPending ? (
        <LoadingBlock label="Loading search approvals" />
      ) : query.isError ? (
        widgetFailure(query.error) === "forbidden" ? <p className="text-sm text-muted">{LIVE_CHECK_UNAVAILABLE}</p> : <ErrorPanel error={query.error} onRetry={() => void query.refetch()} />
      ) : (
        <div className="flex flex-col gap-3">
          <div className="grid grid-cols-2 gap-3">
            <KpiTile label="Active approvals" value={getActiveFormatter().number(totals!.active)} detail={`${String(totals!.forms)} form(s)`} />
            <KpiTile label="Invalidated approvals" value={getActiveFormatter().number(totals!.invalidated)} />
          </div>
          {!query.data.approvalOperational ? <StatusMessage tone="warning" title="Approvals are not operational">{query.data.approvalUnavailableReason ?? "New approvals cannot be recorded right now."}</StatusMessage> : null}
          <Link to="/app/organization/search-approvals" className="underline underline-offset-4">
            Manage search approvals
          </Link>
        </div>
      )}
    </Card>
  );
}

/** Existing incident summary endpoint and its existing live check (OD-6). */
function IncidentsWidget() {
  const session = useSession();
  const api = useApi();
  const allowed = session.hasCapability("security.administer");
  const query = useQuery({
    queryKey: ["security", "incident-summary", "security-home"],
    queryFn: ({ signal }) => api.security.incidentSummary({ periodDays: 30 }, signal).then((r) => r.result),
    enabled: allowed,
  });
  const data = query.data;
  return (
    <Card title="Security incidents" actions={allowed ? <RefreshButton label="security incidents" query={query} /> : undefined}>
      {!allowed ? (
        <p className="text-sm text-muted">{LIVE_CHECK_UNAVAILABLE}</p>
      ) : query.isPending ? (
        <LoadingBlock label="Loading security incidents" />
      ) : query.isError ? (
        widgetFailure(query.error) === "forbidden" ? <p className="text-sm text-muted">{LIVE_CHECK_UNAVAILABLE}</p> : <ErrorPanel error={query.error} onRetry={() => void query.refetch()} />
      ) : (
        <div className="flex flex-col gap-3">
          <div className="grid grid-cols-2 gap-3">
            <KpiTile label="Open" value={getActiveFormatter().number(data!.openIncidentCount)} />
            <KpiTile label="Under investigation" value={getActiveFormatter().number(data!.investigatingIncidentCount)} />
            <KpiTile label="Critical" value={getActiveFormatter().number(data!.criticalIncidentCount)} />
            <KpiTile label="Detected in the last 30 days" value={getActiveFormatter().number(data!.recentIncidentCount)} />
          </div>
          <p className="text-xs text-muted">Incidents are the application's triage records of verification problems; they are not independently verified evidence.</p>
          <Link to="/app/security/incidents" className="underline underline-offset-4">
            All incidents
          </Link>
        </div>
      )}
    </Card>
  );
}

import { useApi } from "@enterpriseforms/app-runtime";
import { Card, ColumnChart, DonutChart, KpiTile, LoadingBlock, Select, browserTimeZone, formatCalendarDate } from "@enterpriseforms/ui";
import { useQuery } from "@tanstack/react-query";
import { Link } from "@tanstack/react-router";
import { useState } from "react";
import { ErrorPanel } from "../../support";
import { IncidentList } from "./IncidentList";

const PERIODS = [
  { value: "7", label: "Last 7 days" },
  { value: "30", label: "Last 30 days" },
  { value: "90", label: "Last 90 days" },
];

/**
 * Security monitoring for Security Administrators (shown only with the security.administer capability; every request is
 * authorized again by the server, which answers 403 to anyone else). Summary, charts and the incident list load
 * independently. An empty list is never presented as proof that the data is verified or secure.
 */
export function SecurityMonitoringSection() {
  const api = useApi();
  const [period, setPeriod] = useState<"7" | "30" | "90">("30");
  const timeZone = browserTimeZone();
  const summary = useQuery({
    queryKey: ["security", "incident-summary", period, timeZone],
    queryFn: ({ signal }) => api.security.incidentSummary({ periodDays: Number(period) as 7 | 30 | 90, ...(timeZone ? { timeZone } : {}) }, signal).then((r) => r.result),
  });
  const data = summary.data;
  const kpi = (value: number | string | undefined) => (value === undefined ? "" : Number(value).toLocaleString());
  return (
    <section aria-labelledby="security-monitoring-heading" className="flex flex-col gap-4">
      <div className="flex flex-wrap items-end justify-between gap-3">
        <div className="min-w-0">
          <h2 id="security-monitoring-heading" className="text-lg font-semibold">
            Security monitoring
          </h2>
          <p className="text-help text-muted">Verification problems observed by the application. Visible to Security Administrators only.</p>
        </div>
        <div className="flex flex-wrap items-end gap-3">
          <div className="w-44">
            <Select label="Period" value={period} options={PERIODS} onValueChange={(value) => setPeriod(value as "7" | "30" | "90")} />
          </div>
          <Link to="/app/security/incidents" className="inline-flex min-h-control items-center underline underline-offset-4">
            All incidents
          </Link>
        </div>
      </div>
      {summary.isError ? <ErrorPanel error={summary.error} onRetry={() => void summary.refetch()} /> : null}
      <div className="grid grid-cols-2 gap-3 md:grid-cols-3 xl:grid-cols-5">
        <KpiTile label="Open incidents" value={kpi(data?.openIncidentCount)} loading={summary.isPending} failed={summary.isError} />
        <KpiTile label="Confirmed integrity failures" value={kpi(data?.confirmedIntegrityFailureCount)} detail="All time" loading={summary.isPending} failed={summary.isError} />
        <KpiTile label="Under investigation" value={kpi(data?.investigatingIncidentCount)} loading={summary.isPending} failed={summary.isError} />
        <KpiTile label="Resolved" value={kpi(data?.resolvedIncidentCount)} detail={data ? `${Number(data.falsePositiveIncidentCount)} false positive` : undefined} loading={summary.isPending} failed={summary.isError} />
        <KpiTile label="Detected in period" value={kpi(data?.recentIncidentCount)} detail={PERIODS.find((p) => p.value === period)?.label} loading={summary.isPending} failed={summary.isError} />
      </div>
      <div className="grid grid-cols-1 gap-4 lg:grid-cols-[minmax(0,2fr)_minmax(0,3fr)]">
        <Card title="Incidents by status">
          {summary.isPending ? (
            <LoadingBlock label="Loading incident status" />
          ) : data ? (
            <DonutChart
              title="Incidents by status"
              totalLabel="incidents"
              emptyMessage="No incidents recorded."
              slices={[
                { key: "open", label: "Open", value: Number(data.openIncidentCount), color: "chart-2" },
                { key: "investigating", label: "Investigating", value: Number(data.investigatingIncidentCount), color: "chart-1" },
                { key: "resolved", label: "Resolved", value: Number(data.resolvedIncidentCount), color: "chart-3" },
                { key: "false-positive", label: "False positive", value: Number(data.falsePositiveIncidentCount), color: "chart-neutral" },
              ]}
            />
          ) : null}
        </Card>
        <Card title="Incidents detected over time">
          {summary.isPending ? (
            <LoadingBlock label="Loading detections" />
          ) : data ? (
            <ColumnChart
              title="Incidents detected over time"
              valueNoun="incidents"
              stacked
              emptyMessage="No incidents detected in this period."
              buckets={data.detections.map((b) => ({ key: b.bucketStartDate, label: formatCalendarDate(b.bucketStartDate), fullLabel: Number(b.bucketDayCount) > 1 ? `Week of ${formatCalendarDate(b.bucketStartDate, "long")}` : formatCalendarDate(b.bucketStartDate, "long") }))}
              series={[
                { key: "integrity", label: "Integrity", color: "chart-2", values: data.detections.map((b) => Number(b.integrityIncidentCount)) },
                { key: "operational", label: "Operational", color: "chart-1", values: data.detections.map((b) => Number(b.operationalIncidentCount)) },
              ]}
            />
          ) : null}
        </Card>
      </div>
      {data ? <p className="text-help text-muted">{data.coverageStatement}</p> : null}
      <Card title="Security incidents">
        <IncidentList caption="Security incidents list" compact />
      </Card>
    </section>
  );
}

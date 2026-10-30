import { useApi } from "@enterpriseforms/app-runtime";
import { Card, ColumnChart, DonutChart, KpiTile, LoadingBlock, browserTimeZone, formatCalendarDate } from "@enterpriseforms/ui";
import { useQuery } from "@tanstack/react-query";
import { ErrorPanel } from "../../support";

export type DashboardPeriod = 7 | 30 | 90;

export const PERIOD_OPTIONS: Array<{ value: string; label: string }> = [
  { value: "7", label: "Last 7 days" },
  { value: "30", label: "Last 30 days" },
  { value: "90", label: "Last 90 days" },
];

/** Local midnight `days - 1` days ago: the start of a period that includes today (matches the server's calendar buckets). */
export function periodStart(days: DashboardPeriod, now = new Date()): string {
  const start = new Date(now.getFullYear(), now.getMonth(), now.getDate());
  start.setDate(start.getDate() - (days - 1));
  return start.toISOString();
}

const count = (value: number | string | undefined) => (value === undefined ? "" : Number(value).toLocaleString());

const bucketLabels = (buckets: ReadonlyArray<{ bucketStartDate: string; bucketDayCount: number | string }>) =>
  buckets.map((b) => ({
    key: b.bucketStartDate,
    label: formatCalendarDate(b.bucketStartDate),
    fullLabel: Number(b.bucketDayCount) > 1 ? `Week of ${formatCalendarDate(b.bucketStartDate, "long")}` : formatCalendarDate(b.bucketStartDate, "long"),
  }));

/**
 * KPI row and charts. Four independent requests (records, uploads, forms, activity): each tile and chart shows its own
 * loading or failure, so one failing source never blanks the others. All numbers are the caller's own authorized data.
 */
export function DashboardAnalytics({ period }: { period: DashboardPeriod }) {
  const api = useApi();
  const timeZone = browserTimeZone();
  const periodQuery = { periodDays: period, ...(timeZone ? { timeZone } : {}) };
  const records = useQuery({ queryKey: ["dashboard", "record-statistics", period, timeZone], queryFn: ({ signal }) => api.dashboard.recordStatistics(periodQuery, signal).then((r) => r.result) });
  const uploads = useQuery({ queryKey: ["dashboard", "upload-statistics", period, timeZone], queryFn: ({ signal }) => api.dashboard.uploadStatistics(periodQuery, signal).then((r) => r.result) });
  const forms = useQuery({ queryKey: ["dashboard", "forms-total"], queryFn: ({ signal }) => api.dashboard.forms({ pageSize: 1, access: "create", status: "published" }, signal).then((r) => r.result) });
  const from = periodStart(period);
  const activity = useQuery({ queryKey: ["dashboard", "activity-total", period], queryFn: ({ signal }) => api.dashboard.activity({ pageSize: 1, from }, signal).then((r) => r.result) });
  const periodLabel = PERIOD_OPTIONS.find((p) => Number(p.value) === period)?.label ?? "";
  const r = records.data;
  const u = uploads.data;

  return (
    <section aria-label="Summary" className="flex flex-col gap-4">
      <div className="grid grid-cols-2 gap-3 md:grid-cols-4 xl:grid-cols-7">
        <KpiTile label="Accessible records" value={count(r?.accessibleRecordCount)} detail={r && Number(r.archivedRecordCount) > 0 ? `${count(r.archivedRecordCount)} archived` : "Top-level records"} loading={records.isPending} failed={records.isError} />
        <KpiTile label="Draft" value={count(r?.draftRecordCount)} loading={records.isPending} failed={records.isError} />
        <KpiTile label="Submitted" value={count(r?.submittedRecordCount)} loading={records.isPending} failed={records.isError} />
        <KpiTile label="Permitted forms" value={count(forms.data?.totalCount)} detail="Published, you can create records" loading={forms.isPending} failed={forms.isError} />
        <KpiTile label="Your activity" value={count(activity.data?.totalCount)} detail={periodLabel} loading={activity.isPending} failed={activity.isError} />
        <KpiTile label="Uploads" value={count(u?.totalUploadCount)} detail={periodLabel} loading={uploads.isPending} failed={uploads.isError} />
        <KpiTile label="Failed uploads" value={count(u?.failedUploadCount)} detail={periodLabel} loading={uploads.isPending} failed={uploads.isError} />
      </div>
      <div className="grid grid-cols-1 gap-4 lg:grid-cols-2 2xl:grid-cols-[minmax(0,2fr)_minmax(0,3fr)_minmax(0,3fr)]">
        <Card title="Records by status">
          {records.isPending ? <LoadingBlock label="Loading records by status" /> : null}
          {records.isError ? <ErrorPanel error={records.error} onRetry={() => void records.refetch()} /> : null}
          {r ? (
            <>
              <DonutChart
                title="Records by status"
                totalLabel="records"
                emptyMessage="You have no records yet."
                slices={[
                  { key: "draft", label: "Draft", value: Number(r.draftRecordCount), color: "chart-1" },
                  { key: "submitted", label: "Submitted", value: Number(r.submittedRecordCount), color: "chart-2" },
                  { key: "archived", label: "Archived", value: Number(r.archivedRecordCount), color: "chart-3" },
                  ...(Number(r.otherStatusRecordCount) > 0 ? [{ key: "other", label: "Other", value: Number(r.otherStatusRecordCount), color: "chart-neutral" as const }] : []),
                ]}
              />
              {Number(r.unavailableRecordCount) > 0 ? <p className="text-help text-muted">{count(r.unavailableRecordCount)} accessible records have no listing details and are not counted. Open My Records to check them.</p> : null}
            </>
          ) : null}
        </Card>
        <Card title="Record activity">
          {records.isPending ? <LoadingBlock label="Loading record activity" /> : null}
          {records.isError ? <ErrorPanel error={records.error} onRetry={() => void records.refetch()} /> : null}
          {r ? (
            <ColumnChart
              title={`Record activity, ${periodLabel.toLowerCase()}`}
              valueNoun="record events"
              buckets={bucketLabels(r.activity)}
              series={[
                { key: "created", label: "Created", color: "chart-1", values: r.activity.map((b) => Number(b.createdRecordCount)) },
                { key: "submitted", label: "Submitted", color: "chart-2", values: r.activity.map((b) => Number(b.submittedRecordCount)) },
              ]}
              emptyMessage="No records were created or submitted in this period."
            />
          ) : null}
        </Card>
        <Card title="Upload activity" className="lg:col-span-2 2xl:col-span-1">
          {uploads.isPending ? <LoadingBlock label="Loading upload activity" /> : null}
          {uploads.isError ? <ErrorPanel error={uploads.error} onRetry={() => void uploads.refetch()} /> : null}
          {u ? (
            <ColumnChart
              title={`Uploads by outcome, ${periodLabel.toLowerCase()}`}
              valueNoun="uploads"
              stacked
              buckets={bucketLabels(u.activity)}
              series={[
                { key: "completed", label: "Completed", color: "chart-3", values: u.activity.map((b) => Number(b.completedUploadCount)) },
                { key: "in-progress", label: "In progress", color: "chart-1", values: u.activity.map((b) => Number(b.inProgressUploadCount)) },
                { key: "failed", label: "Failed", color: "chart-2", values: u.activity.map((b) => Number(b.failedUploadCount)) },
                { key: "cancelled", label: "Cancelled or expired", color: "chart-neutral", values: u.activity.map((b) => Number(b.cancelledUploadCount)) },
              ]}
              emptyMessage="You uploaded no documents in this period."
            />
          ) : null}
        </Card>
      </div>
      {r ? <p className="text-help text-muted">{r.scopeStatement}</p> : null}
    </section>
  );
}

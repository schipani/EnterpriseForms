import { useId, useState, type KeyboardEvent } from "react";

/**
 * Lightweight, dependency-free charts for the dashboard. Colors come from the fixed --ui-chart-* tokens, assigned to a
 * series by the caller in a fixed order (never by rank), so a filter never repaints the survivors. Every chart has a
 * visible legend with values (identity is never color alone), a keyboard-reachable tooltip, and a table alternative;
 * text always uses text tokens, never a series color. No animation.
 */

export type ChartColor = "chart-1" | "chart-2" | "chart-3" | "chart-4" | "chart-neutral";

const colorVar = (color: ChartColor) => `var(--ui-${color})`;

const numberFormat = new Intl.NumberFormat();

export interface DonutSlice {
  key: string;
  label: string;
  value: number;
  color: ChartColor;
}

/** Part-to-whole of a few categories. The legend lists every category with its count and share. */
export function DonutChart({ title, slices, totalLabel, emptyMessage = "Nothing to show yet." }: { title: string; slices: DonutSlice[]; totalLabel: string; emptyMessage?: string }) {
  const [active, setActive] = useState<string | null>(null);
  const total = slices.reduce((sum, slice) => sum + Math.max(0, slice.value), 0);
  const radius = 44;
  const circumference = 2 * Math.PI * radius;
  const gap = total > 0 && slices.filter((s) => s.value > 0).length > 1 ? 2 : 0;
  const summary = total === 0 ? `${title}: ${emptyMessage}` : `${title}: ${slices.map((s) => `${s.label} ${numberFormat.format(s.value)}`).join(", ")}; total ${numberFormat.format(total)}.`;
  const focused = slices.find((s) => s.key === active);
  let offset = 0;
  return (
    <figure className="flex min-w-0 flex-col items-center gap-4 sm:flex-row sm:items-center">
      <div className="relative h-40 w-40 shrink-0">
        <svg viewBox="0 0 120 120" className="h-full w-full -rotate-90" role="img" aria-label={summary}>
          <circle cx="60" cy="60" r={radius} fill="none" stroke="var(--ui-chart-grid)" strokeWidth="14" />
          {total > 0
            ? slices.map((slice) => {
                if (slice.value <= 0) {
                  return null;
                }
                const length = (slice.value / total) * circumference;
                const dash = Math.max(0, length - gap);
                const element = (
                  <circle
                    key={slice.key}
                    cx="60"
                    cy="60"
                    r={radius}
                    fill="none"
                    stroke={colorVar(slice.color)}
                    strokeWidth={active === slice.key ? 18 : 14}
                    strokeDasharray={`${dash} ${circumference - dash}`}
                    strokeDashoffset={-offset}
                    onMouseEnter={() => setActive(slice.key)}
                    onMouseLeave={() => setActive(null)}
                  >
                    <title>{`${slice.label}: ${numberFormat.format(slice.value)} (${Math.round((slice.value / total) * 100)}%)`}</title>
                  </circle>
                );
                offset += length;
                return element;
              })
            : null}
        </svg>
        <div aria-hidden="true" className="pointer-events-none absolute inset-0 flex flex-col items-center justify-center text-center">
          <span className="text-2xl font-semibold tabular-nums text-body">{numberFormat.format(focused ? focused.value : total)}</span>
          <span className="max-w-24 text-help text-muted">{focused ? focused.label : totalLabel}</span>
        </div>
      </div>
      <figcaption className="min-w-0 flex-1">
        <span className="sr-only">{title}</span>
        {total === 0 ? <p className="text-help text-muted">{emptyMessage}</p> : null}
        <ul className="flex flex-col gap-1.5" aria-label={`${title} legend`}>
          {slices.map((slice) => (
            <li key={slice.key} className={`flex items-center gap-2 rounded-control px-2 py-1 text-control ${active === slice.key ? "bg-page" : ""}`} onMouseEnter={() => setActive(slice.key)} onMouseLeave={() => setActive(null)}>
              <span aria-hidden="true" className="h-3 w-3 shrink-0 rounded-sm" style={{ background: colorVar(slice.color) }} />
              <span className="min-w-0 flex-1 text-body">{slice.label}</span>
              <span className="tabular-nums font-medium text-body">{numberFormat.format(slice.value)}</span>
              <span className="w-10 text-right tabular-nums text-help text-muted">{total > 0 ? `${Math.round((slice.value / total) * 100)}%` : "–"}</span>
            </li>
          ))}
        </ul>
      </figcaption>
    </figure>
  );
}

export interface ColumnSeries {
  key: string;
  label: string;
  color: ChartColor;
  /** One value per bucket, aligned with `buckets`. */
  values: number[];
}

export interface ColumnBucket {
  key: string;
  /** Short axis label, e.g. "Oct 28". */
  label: string;
  /** Full label for tooltips and the table, e.g. "Week of Oct 28, 2026". */
  fullLabel: string;
}

function niceMaximum(value: number): number {
  if (value <= 4) {
    return 4;
  }
  const magnitude = 10 ** Math.floor(Math.log10(value));
  const step = [1, 2, 2.5, 5, 10].map((m) => m * magnitude).find((candidate) => candidate * 4 >= value) ?? 10 * magnitude;
  return step * 4;
}

/**
 * Counts over time. `stacked` stacks the series in one column per bucket (part of a whole, e.g. uploads by outcome);
 * otherwise series sit side by side (independent measures, e.g. created vs submitted). Arrow keys move between buckets
 * when the plot has focus; the tooltip is announced politely.
 */
export function ColumnChart({ title, buckets, series, stacked = false, valueNoun, emptyMessage = "No activity in this period." }: { title: string; buckets: ColumnBucket[]; series: ColumnSeries[]; stacked?: boolean; valueNoun: string; emptyMessage?: string }) {
  const [active, setActive] = useState<number | null>(null);
  const tooltipId = useId();
  const bucketTotals = buckets.map((_, index) => series.reduce((sum, s) => sum + (s.values[index] ?? 0), 0));
  const grandTotal = bucketTotals.reduce((a, b) => a + b, 0);
  const peak = stacked ? Math.max(0, ...bucketTotals) : Math.max(0, ...series.flatMap((s) => s.values));
  const maximum = niceMaximum(peak);
  const ticks = [maximum, maximum / 2, 0];
  const labelEvery = Math.max(1, Math.ceil(buckets.length / 7));
  const describe = (index: number) =>
    `${buckets[index]!.fullLabel}: ${series.map((s) => `${s.label} ${numberFormat.format(s.values[index] ?? 0)}`).join(", ")}${stacked ? `; total ${numberFormat.format(bucketTotals[index] ?? 0)}` : ""}`;
  const onKeyDown = (event: KeyboardEvent<HTMLDivElement>) => {
    if (buckets.length === 0) {
      return;
    }
    const current = active ?? buckets.length - 1;
    const next = event.key === "ArrowRight" ? Math.min(buckets.length - 1, current + 1) : event.key === "ArrowLeft" ? Math.max(0, current - 1) : event.key === "Home" ? 0 : event.key === "End" ? buckets.length - 1 : null;
    if (next !== null) {
      event.preventDefault();
      setActive(next);
    }
  };
  return (
    <figure className="flex min-w-0 flex-col gap-3">
      {series.length > 1 ? (
        <ul className="flex flex-wrap gap-x-4 gap-y-1" aria-label={`${title} legend`}>
          {series.map((s) => (
            <li key={s.key} className="flex items-center gap-2 text-help text-body">
              <span aria-hidden="true" className="h-3 w-3 rounded-sm" style={{ background: colorVar(s.color) }} />
              {s.label}
              <span className="tabular-nums text-muted">{numberFormat.format(s.values.reduce((a, b) => a + b, 0))}</span>
            </li>
          ))}
        </ul>
      ) : null}
      <div className="relative">
        <div className="flex gap-2">
          <div aria-hidden="true" className="flex h-44 w-8 shrink-0 flex-col justify-between text-right text-help tabular-nums text-muted">
            {ticks.map((tick) => (
              <span key={tick} className="-translate-y-1/2 first:translate-y-0 last:translate-y-0">
                {numberFormat.format(tick)}
              </span>
            ))}
          </div>
          <div
            role="group"
            tabIndex={0}
            aria-label={`${title}. ${grandTotal === 0 ? emptyMessage : `${numberFormat.format(grandTotal)} ${valueNoun} in total. Use the left and right arrow keys to read each period.`}`}
            aria-describedby={active !== null ? tooltipId : undefined}
            onKeyDown={onKeyDown}
            onFocus={() => setActive((current) => current ?? (buckets.length > 0 ? buckets.length - 1 : null))}
            onBlur={() => setActive(null)}
            onMouseLeave={() => setActive(null)}
            className="relative flex h-44 min-w-0 flex-1 items-end gap-px border-b border-l border-[var(--ui-chart-grid)] focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus"
          >
            {ticks.slice(0, 2).map((tick) => (
              <span key={tick} aria-hidden="true" className="pointer-events-none absolute inset-x-0 border-t border-[var(--ui-chart-grid)]" style={{ bottom: `${(tick / maximum) * 100}%` }} />
            ))}
            {buckets.map((bucket, index) => (
              <div key={bucket.key} className={`relative flex h-full min-w-0 flex-1 items-end justify-center ${active === index ? "bg-page" : ""}`} onMouseEnter={() => setActive(index)}>
                {stacked ? (
                  <div className="flex h-full w-full max-w-6 flex-col-reverse justify-start">
                    {series.map((s) => {
                      const v = s.values[index] ?? 0;
                      return v > 0 ? <div key={s.key} className="w-full first:rounded-t-none last:rounded-t-[4px]" style={{ height: `${(v / maximum) * 100}%`, background: colorVar(s.color), borderTop: "2px solid var(--ui-surface)" }} /> : null;
                    })}
                  </div>
                ) : (
                  <div className="flex h-full w-full items-end justify-center gap-0.5">
                    {series.map((s) => {
                      const v = s.values[index] ?? 0;
                      return <div key={s.key} className="w-full max-w-3 rounded-t-[4px]" style={{ height: v > 0 ? `${(v / maximum) * 100}%` : "0", background: colorVar(s.color) }} />;
                    })}
                  </div>
                )}
              </div>
            ))}
            {grandTotal === 0 ? <p className="absolute inset-0 flex items-center justify-center text-help text-muted">{emptyMessage}</p> : null}
          </div>
        </div>
        <div aria-hidden="true" className="ml-10 mt-1 flex gap-px">
          {buckets.map((bucket, index) => (
            <span key={bucket.key} className="min-w-0 flex-1 overflow-visible whitespace-nowrap text-center text-help text-muted">
              {index % labelEvery === 0 || index === buckets.length - 1 ? bucket.label : ""}
            </span>
          ))}
        </div>
        {active !== null && buckets[active] ? (
          <div id={tooltipId} role="status" className="pointer-events-none absolute right-0 top-0 z-10 max-w-[16rem] rounded-control border border-line bg-surface px-3 py-2 text-help text-body shadow-sm">
            <p className="font-semibold">{buckets[active]!.fullLabel}</p>
            {series.map((s) => (
              <p key={s.key} className="flex items-center gap-2">
                <span aria-hidden="true" className="h-2.5 w-2.5 rounded-sm" style={{ background: colorVar(s.color) }} />
                <span className="flex-1">{s.label}</span>
                <span className="tabular-nums font-medium">{numberFormat.format(s.values[active] ?? 0)}</span>
              </p>
            ))}
            <span className="sr-only">{describe(active)}</span>
          </div>
        ) : null}
      </div>
      <details className="text-help">
        <summary className="cursor-pointer text-muted underline-offset-4 hover:underline">Show data table</summary>
        <div className="mt-2 max-h-64 overflow-auto rounded-control border border-line">
          <table className="w-full border-collapse text-left text-help">
            <caption className="sr-only">{title}</caption>
            <thead className="sticky top-0 bg-page">
              <tr>
                <th scope="col" className="px-2 py-1 font-semibold">
                  Period
                </th>
                {series.map((s) => (
                  <th key={s.key} scope="col" className="px-2 py-1 text-right font-semibold">
                    {s.label}
                  </th>
                ))}
                {stacked ? (
                  <th scope="col" className="px-2 py-1 text-right font-semibold">
                    Total
                  </th>
                ) : null}
              </tr>
            </thead>
            <tbody>
              {buckets.map((bucket, index) => (
                <tr key={bucket.key} className="border-t border-line">
                  <th scope="row" className="px-2 py-1 font-normal">
                    {bucket.fullLabel}
                  </th>
                  {series.map((s) => (
                    <td key={s.key} className="px-2 py-1 text-right tabular-nums">
                      {numberFormat.format(s.values[index] ?? 0)}
                    </td>
                  ))}
                  {stacked ? <td className="px-2 py-1 text-right tabular-nums">{numberFormat.format(bucketTotals[index] ?? 0)}</td> : null}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </details>
    </figure>
  );
}

import { useEffect, useState, type ReactNode } from "react";
import { Button } from "./Button";

/**
 * Summary metric (dashboard KPI). The value is plain text in body ink; loading and failure are explicit so a missing
 * number is never shown as zero.
 */
export function KpiTile({ label, value, detail, loading = false, failed = false, icon }: { label: string; value: ReactNode; detail?: ReactNode; loading?: boolean; failed?: boolean; icon?: ReactNode }) {
  return (
    <div className="flex min-w-0 flex-col gap-1 rounded-panel border border-line bg-surface p-4" aria-busy={loading || undefined}>
      <p className="flex items-center gap-2 text-label font-medium text-muted">
        {icon ? <span aria-hidden="true" className="text-muted">{icon}</span> : null}
        <span className="min-w-0">{label}</span>
      </p>
      {loading ? (
        <p className="h-8 w-16 animate-pulse rounded-control bg-page" aria-label={`${label}: loading`} />
      ) : failed ? (
        <p className="text-control text-muted">Unavailable</p>
      ) : (
        <p className="text-2xl font-semibold leading-tight tabular-nums text-body">{value}</p>
      )}
      {detail && !loading && !failed ? <p className="text-help text-muted">{detail}</p> : null}
    </div>
  );
}

export interface PagerProps {
  /** 1-based position of the first row on this page (0 when empty), as returned by the server. */
  pageStart: number;
  itemCount: number;
  totalCount: number;
  pageSize: number;
  hasPrevious: boolean;
  hasNext: boolean;
  onPrevious: () => void;
  onNext: () => void;
  busy?: boolean;
  /** Accessible name of the navigation landmark, e.g. "Upload activity pages". */
  label: string;
  /** Plural noun for the count, e.g. "uploads". */
  noun?: string;
}

/** Independent server-side pagination for one list: position, totals, Previous/Next disabled at the ends. */
export function Pager({ pageStart, itemCount, totalCount, pageSize, hasPrevious, hasNext, onPrevious, onNext, busy = false, label, noun = "results" }: PagerProps) {
  const pages = Math.max(1, Math.ceil(totalCount / Math.max(1, pageSize)));
  const page = totalCount === 0 ? 1 : Math.min(pages, Math.floor((pageStart - 1) / Math.max(1, pageSize)) + 1);
  const last = totalCount === 0 ? 0 : pageStart + itemCount - 1;
  return (
    <nav aria-label={label} className="flex flex-wrap items-center justify-between gap-3">
      <p className="text-help text-muted" aria-live="polite">
        {totalCount === 0 ? `No ${noun}` : `Showing ${pageStart}–${last} of ${totalCount} ${noun}`}
        <span aria-hidden="true"> · </span>
        <span>
          Page {page} of {pages}
        </span>
      </p>
      <div className="flex gap-2">
        <Button variant="secondary" size="sm" disabled={!hasPrevious || busy} onClick={onPrevious}>
          Previous
        </Button>
        <Button variant="secondary" size="sm" disabled={!hasNext || busy} onClick={onNext}>
          Next
        </Button>
      </div>
    </nav>
  );
}

/** The value after it has stopped changing for `delayMilliseconds` (free-text filters: one request per pause, not per keystroke). */
export function useDebouncedValue<T>(value: T, delayMilliseconds = 350): T {
  const [settled, setSettled] = useState(value);
  useEffect(() => {
    const timer = setTimeout(() => setSettled(value), delayMilliseconds);
    return () => clearTimeout(timer);
  }, [value, delayMilliseconds]);
  return settled;
}

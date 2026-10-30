/** Generic date helpers for filters and charts (no business knowledge). */

/** The browser's IANA time zone, or undefined when it cannot be resolved (the server then uses UTC and says so). */
export function browserTimeZone(): string | undefined {
  try {
    return Intl.DateTimeFormat().resolvedOptions().timeZone || undefined;
  } catch {
    return undefined;
  }
}

/**
 * Instants for an inclusive local-date filter: `from` is local midnight of the start date, `before` is local midnight of
 * the day after the end date (exclusive). Dates are `yyyy-mm-dd` from a date input; empty values are omitted.
 */
export function localDateRange(fromDate: string, toDate: string): { from?: string; before?: string } {
  const result: { from?: string; before?: string } = {};
  const parse = (value: string) => {
    const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(value);
    return match ? new Date(Number(match[1]), Number(match[2]) - 1, Number(match[3])) : null;
  };
  const start = parse(fromDate);
  const end = parse(toDate);
  if (start) {
    result.from = start.toISOString();
  }
  if (end) {
    end.setDate(end.getDate() + 1);
    result.before = end.toISOString();
  }
  return result;
}

/** A calendar date (`yyyy-mm-dd`, already in the chart's zone) formatted for an axis ("Oct 28") or a tooltip. */
export function formatCalendarDate(isoDate: string, style: "short" | "long" = "short"): string {
  const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(isoDate);
  if (!match) {
    return isoDate;
  }
  // Noon UTC formatted in UTC: the calendar date never shifts with the viewer's zone.
  const date = new Date(Date.UTC(Number(match[1]), Number(match[2]) - 1, Number(match[3]), 12));
  return new Intl.DateTimeFormat(undefined, style === "short" ? { month: "short", day: "numeric", timeZone: "UTC" } : { year: "numeric", month: "short", day: "numeric", timeZone: "UTC" }).format(date);
}

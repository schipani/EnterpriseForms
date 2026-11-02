import { presentError } from "@enterpriseforms/app-runtime";
import { ActionIcons, Button, Card, LoadingBlock, Pager } from "@enterpriseforms/ui";
import { Link } from "@tanstack/react-router";
import { useEffect, useState, type ReactNode } from "react";
import { ErrorPanel } from "../../support";

/** Server page shape shared by every dashboard list (PB-06 opaque cursors). */
export interface ServerPage {
  totalCount: number | string;
  pageStart: number | string;
  pageSize: number | string;
  nextToken?: string | null;
  previousToken?: string | null;
}

const RESTART_CODES = new Set(["CURSOR_INVALID", "CURSOR_SCOPE_MISMATCH", "LIST_CHANGED"]);

/**
 * Page state of one dashboard list. Each section owns one: its own cursor, reset to page 1 by any filter change
 * (`change`). Pair it with {@link useCursorRestart} so an expired or invalidated cursor restarts the list from page 1.
 */
export function usePageCursor() {
  const [cursor, setCursor] = useState<string | undefined>(undefined);
  return {
    cursor,
    setCursor,
    change: (apply: () => void) => {
      apply();
      setCursor(undefined);
    },
  };
}

/** Restart a list at page 1 when its cursor expired, belonged to other filters, or access changed meanwhile. */
export function useCursorRestart(error: unknown, paging: { cursor: string | undefined; setCursor: (cursor: string | undefined) => void }) {
  const code = error ? presentError(error).code : null;
  const { cursor, setCursor } = paging;
  useEffect(() => {
    if (code && RESTART_CODES.has(code) && cursor) {
      setCursor(undefined);
    }
  }, [code, cursor, setCursor]);
}

/** One dashboard data section: title, link to the full page, compact filters, loading/error/empty states and a pager. */
export function DashboardSection({
  id,
  title,
  description,
  fullPage,
  filters,
  filtered,
  onClear,
  loading,
  error,
  onRetry,
  page,
  itemCount,
  noun,
  busy,
  onPage,
  children,
}: {
  id: string;
  title: string;
  description: string;
  fullPage?: { to: string; label: string };
  filters: ReactNode;
  filtered: boolean;
  onClear: () => void;
  loading: boolean;
  error: unknown;
  onRetry: () => void;
  page: ServerPage | undefined;
  itemCount: number;
  noun: string;
  busy: boolean;
  onPage: (token: string | undefined) => void;
  children: ReactNode;
}) {
  const showError = Boolean(error) && !RESTART_CODES.has(presentError(error).code);
  return (
    <Card
      aria-labelledby={`${id}-title`}
      title={<span id={`${id}-title`}>{title}</span>}
      actions={
        fullPage ? (
          <Link to={fullPage.to} className="inline-flex min-h-control-compact items-center text-control underline underline-offset-4">
            {fullPage.label}
          </Link>
        ) : undefined
      }
    >
      <p className="-mt-2 text-help text-muted">{description}</p>
      <div role="search" aria-label={`Filter ${title}`} className="flex flex-col gap-2">
        {filters}
        {filtered ? (
          <div>
            <Button variant="ghost" size="sm" icon={ActionIcons.reset} onClick={onClear}>
              Clear filters
            </Button>
          </div>
        ) : null}
      </div>
      <div aria-busy={busy || undefined} className="flex min-w-0 flex-col gap-3">
        {loading ? <LoadingBlock label={`Loading ${title.toLowerCase()}`} /> : null}
        {showError ? <ErrorPanel error={error} onRetry={onRetry} /> : null}
        {children}
        {page && Number(page.totalCount) > 0 ? (
          <Pager
            label={`${title} pages`}
            noun={noun}
            pageStart={Number(page.pageStart)}
            itemCount={itemCount}
            totalCount={Number(page.totalCount)}
            pageSize={Number(page.pageSize)}
            hasPrevious={Boolean(page.previousToken)}
            hasNext={Boolean(page.nextToken)}
            busy={busy}
            onPrevious={() => onPage(page.previousToken ?? undefined)}
            onNext={() => onPage(page.nextToken ?? undefined)}
          />
        ) : null}
      </div>
    </Card>
  );
}

import type { ReactNode } from "react";

export interface DataTableColumn<T> {
  key: string;
  header: string;
  render: (row: T) => ReactNode;
  /** Column carrying the row's primary name/link; rendered as a row header. */
  isRowHeader?: boolean;
  /**
   * Marks the standardized row-actions column (brief C1-UX §D): the table always renders it last, right-aligned,
   * and — when `stickyActions` is set on the table — pinned at the right edge on wide viewports. Build its content
   * with {@link RowActions}: one visible primary action plus an overflow menu for the rest. Server-side permission
   * checks remain authoritative; hiding a button here is a convenience, never the security boundary.
   */
  isActions?: boolean;
  className?: string;
}

/** Normalizes column order so the standardized Actions column (if any) is always last, as the C1-UX contract requires. */
function orderColumns<T>(columns: DataTableColumn<T>[]): DataTableColumn<T>[] {
  const actionsIndex = columns.findIndex((column) => column.isActions);
  if (actionsIndex === -1 || actionsIndex === columns.length - 1) {
    return columns;
  }
  const rest = columns.filter((_, index) => index !== actionsIndex);
  return [...rest, columns[actionsIndex]!];
}

/** Semantic table that scrolls inside its own container on narrow viewports; the page never scrolls horizontally. */
export function DataTable<T>({ caption, columns, rows, rowKey, emptyMessage = "Nothing to show.", stickyActions = false }: { caption: string; columns: DataTableColumn<T>[]; rows: T[]; rowKey: (row: T) => string; emptyMessage?: string; stickyActions?: boolean }) {
  const ordered = orderColumns(columns);
  const cellClass = (column: DataTableColumn<T>) => `${column.isActions ? `text-right${stickyActions ? " sticky right-0 bg-surface" : ""}` : ""} ${column.className ?? ""}`;
  return (
    // A horizontally scrollable region must be reachable by keyboard (WCAG 2.1.1 / axe scrollable-region-focusable).
    <div role="region" aria-label={caption} tabIndex={0} className="w-full overflow-x-auto rounded border border-line focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus">
      <table className="w-full min-w-[32rem] border-collapse text-left text-sm">
        <caption className="sr-only">{caption}</caption>
        <thead className="bg-page">
          <tr>
            {ordered.map((column) => (
              <th key={column.key} scope="col" className={`border-b border-line px-3 py-2 font-semibold ${column.isActions ? `text-right${stickyActions ? " sticky right-0 bg-page" : ""}` : ""} ${column.className ?? ""}`}>
                {column.header}
              </th>
            ))}
          </tr>
        </thead>
        <tbody>
          {rows.length === 0 ? (
            <tr>
              <td colSpan={ordered.length} className="px-3 py-4 text-muted">
                {emptyMessage}
              </td>
            </tr>
          ) : (
            rows.map((row) => (
              <tr key={rowKey(row)} className="border-b border-line last:border-b-0">
                {ordered.map((column) =>
                  column.isRowHeader ? (
                    <th key={column.key} scope="row" className={`px-3 py-2 font-medium ${cellClass(column)}`}>
                      {column.render(row)}
                    </th>
                  ) : (
                    <td key={column.key} className={`px-3 py-2 ${cellClass(column)}`}>
                      {column.render(row)}
                    </td>
                  ),
                )}
              </tr>
            ))
          )}
        </tbody>
      </table>
    </div>
  );
}

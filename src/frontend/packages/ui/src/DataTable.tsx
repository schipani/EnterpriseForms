import type { ReactNode } from "react";

export interface DataTableColumn<T> {
  key: string;
  header: string;
  render: (row: T) => ReactNode;
  /** Column carrying the row's primary name/link; rendered as a row header. */
  isRowHeader?: boolean;
  className?: string;
}

/** Semantic table that scrolls inside its own container on narrow viewports; the page never scrolls horizontally. */
export function DataTable<T>({ caption, columns, rows, rowKey, emptyMessage = "Nothing to show." }: { caption: string; columns: DataTableColumn<T>[]; rows: T[]; rowKey: (row: T) => string; emptyMessage?: string }) {
  return (
    // A horizontally scrollable region must be reachable by keyboard (WCAG 2.1.1 / axe scrollable-region-focusable).
    <div role="region" aria-label={caption} tabIndex={0} className="w-full overflow-x-auto rounded border border-line focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus">
      <table className="w-full min-w-[32rem] border-collapse text-left text-sm">
        <caption className="sr-only">{caption}</caption>
        <thead className="bg-page">
          <tr>
            {columns.map((column) => (
              <th key={column.key} scope="col" className={`border-b border-line px-3 py-2 font-semibold ${column.className ?? ""}`}>
                {column.header}
              </th>
            ))}
          </tr>
        </thead>
        <tbody>
          {rows.length === 0 ? (
            <tr>
              <td colSpan={columns.length} className="px-3 py-4 text-muted">
                {emptyMessage}
              </td>
            </tr>
          ) : (
            rows.map((row) => (
              <tr key={rowKey(row)} className="border-b border-line last:border-b-0">
                {columns.map((column) =>
                  column.isRowHeader ? (
                    <th key={column.key} scope="row" className={`px-3 py-2 font-medium ${column.className ?? ""}`}>
                      {column.render(row)}
                    </th>
                  ) : (
                    <td key={column.key} className={`px-3 py-2 ${column.className ?? ""}`}>
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

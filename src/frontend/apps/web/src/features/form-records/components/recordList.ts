/** My Records URL state (filters and sort are shareable; the page cursor is not — it is bound to the session and expires). */
export interface RecordsSearch {
  form?: string | undefined;
  status?: string | undefined;
  children?: boolean | undefined;
  archived?: boolean | undefined;
  sort?: string | undefined;
  size?: number | undefined;
}

export const RECORD_STATUSES = ["Draft", "Submitted", "Archived"] as const;

const STATUS_LABELS: Record<string, string> = { Draft: "Draft", Submitted: "Submitted", Archived: "Archived", Deleted: "Deleted" };

export function recordStatusLabel(status: string): string {
  return STATUS_LABELS[status] ?? status;
}

export interface SortChoice {
  key: string;
  label: string;
  sortBy: "modifiedOn" | "createdOn" | "formName" | "recordStatus";
  sortDirection: "asc" | "desc";
}

/** The first choice is the default (most recently changed first). */
export const SORT_CHOICES: readonly SortChoice[] = [
  { key: "modified-desc", label: "Last changed (newest first)", sortBy: "modifiedOn", sortDirection: "desc" },
  { key: "modified-asc", label: "Last changed (oldest first)", sortBy: "modifiedOn", sortDirection: "asc" },
  { key: "created-desc", label: "Created (newest first)", sortBy: "createdOn", sortDirection: "desc" },
  { key: "created-asc", label: "Created (oldest first)", sortBy: "createdOn", sortDirection: "asc" },
  { key: "form-asc", label: "Form (A to Z)", sortBy: "formName", sortDirection: "asc" },
  { key: "form-desc", label: "Form (Z to A)", sortBy: "formName", sortDirection: "desc" },
  { key: "status-asc", label: "Status (A to Z)", sortBy: "recordStatus", sortDirection: "asc" },
];

export function sortChoiceFor(key: string | undefined): SortChoice {
  return SORT_CHOICES.find((choice) => choice.key === key) ?? SORT_CHOICES[0]!;
}

/** Parses untrusted URL search into a well-formed state; anything unknown is dropped. */
export function parseRecordsSearch(search: Record<string, unknown>): RecordsSearch {
  const result: RecordsSearch = {};
  if (typeof search["form"] === "string" && /^[0-9a-f-]{36}$/i.test(search["form"])) {
    result.form = search["form"];
  }
  if (typeof search["status"] === "string" && (RECORD_STATUSES as readonly string[]).includes(search["status"])) {
    result.status = search["status"];
  }
  if (search["children"] === true || search["children"] === "true") {
    result.children = true;
  }
  if (search["archived"] === true || search["archived"] === "true") {
    result.archived = true;
  }
  if (typeof search["sort"] === "string" && SORT_CHOICES.some((choice) => choice.key === search["sort"])) {
    result.sort = search["sort"];
  }
  const size = Number(search["size"]);
  if ([10, 50, 100].includes(size)) {
    result.size = size;
  }
  return result;
}

export function formatDateTime(instant: string): string {
  const parsed = Date.parse(instant);
  return Number.isNaN(parsed) ? instant : new Date(parsed).toLocaleString(undefined, { dateStyle: "medium", timeStyle: "short" });
}

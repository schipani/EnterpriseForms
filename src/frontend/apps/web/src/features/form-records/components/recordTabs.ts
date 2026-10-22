/** Record workspace sections (C-30), addressable by URL so links can open a section directly. */
export const RECORD_TABS = ["details", "related", "documents", "sharing", "activity"] as const;

export type RecordTab = (typeof RECORD_TABS)[number];

export interface RecordTabSearch {
  tab?: RecordTab | undefined;
  /** Set by the editor after a successful save; the record page confirms it once. */
  saved?: boolean | undefined;
}

export function parseRecordTabSearch(search: Record<string, unknown>): RecordTabSearch {
  const result: RecordTabSearch = {};
  if (typeof search["tab"] === "string" && (RECORD_TABS as readonly string[]).includes(search["tab"])) {
    result.tab = search["tab"] as RecordTab;
  }
  if (search["saved"] === true || search["saved"] === "true") {
    result.saved = true;
  }
  return result;
}

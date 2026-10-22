/**
 * Route-search parsers for the records routes, a separate public entrypoint so the app shell can validate URLs without
 * pulling the record pages out of their lazy-loaded chunk (ADR-ARCH-001 §4U.4).
 */
export { parseRecordsSearch, type RecordsSearch } from "./components/recordList";
export { parseRecordTabSearch, type RecordTabSearch } from "./components/recordTabs";

/**
 * `search` feature (PG-015 authorized search, Phase C7 Advanced Field Search). Public entrypoint: the Advanced Search panel,
 * its condition model and the typed-failure presentation. Server I/O stays with the page that owns the results table.
 */
export { AdvancedFieldSearch, type AdvancedFieldSearchProps } from "./components/AdvancedFieldSearch";
export { MAX_CONDITIONS_V1, operatorLabel, toFieldSearchRequest, type ConditionDraft, type SearchFilters } from "./components/conditionModel";
export { presentSearchFailure, type SearchFailure } from "./components/searchFailure";

import { OperationError, presentError } from "@enterpriseforms/app-runtime";

/**
 * How a failed advanced search is explained (ADR-PB-004, CLAUDE-DESIGN.MD §0C). A failure is never presented as "no records
 * found": when completeness was not proven there is no result, and the message says so. Field existence is not hinted at.
 */
export interface SearchFailure {
  code: string;
  title: string;
  message: string;
  /** Offer "Run search again" (the search can be restarted as is). */
  canRetry: boolean;
}

export function presentSearchFailure(error: unknown): SearchFailure {
  const presented = presentError(error);
  const problem = error instanceof OperationError ? error.problem : undefined;
  const reason = problem?.unavailable_reason;
  switch (presented.code) {
    case "SEARCH_VERIFICATION_UNAVAILABLE":
      return reason === "TRUSTED_SEARCH_NOT_ENABLED"
        ? { code: presented.code, title: "Advanced field search is not available yet", message: "Searching inside records stays off until results can be independently verified as complete. Nothing was searched, so this is not a list of matching records.", canRetry: false }
        : { code: presented.code, title: "Search results could not be verified", message: "The records in this form could not all be checked right now, so no results are shown — this does not mean nothing matched. Try again in a few minutes.", canRetry: true };
    case "SEARCH_VERIFICATION_LIMIT":
      return { code: presented.code, title: "This search is too large to verify", message: "Too many records would need to be checked to give a complete answer, so no results are shown. Narrow the search: choose a status or add another condition.", canRetry: false };
    case "SEARCH_SNAPSHOT_CHANGED":
      return { code: presented.code, title: "Records changed during the search", message: "Access or content changed while the search ran, so its results were discarded. Run the search again to get a consistent answer.", canRetry: true };
    case "SEARCH_FIELD_NOT_ALLOWED":
      return { code: presented.code, title: "A condition cannot be used", message: "One of the conditions uses a field you cannot search. Choose fields from the list for the selected form and try again.", canRetry: false };
    case "SEARCH_OPERATOR_NOT_SUPPORTED":
      return { code: presented.code, title: "A condition is not supported", message: "One of the conditions cannot be used with its field. Choose a condition from the list.", canRetry: false };
    case "SEARCH_REQUEST_INVALID":
      return { code: presented.code, title: "Check the search conditions", message: presented.detail ? capitalize(presented.detail) + "." : "A condition is incomplete.", canRetry: false };
    default:
      return { code: presented.code, title: presented.title, message: "The search could not be run. No results are shown.", canRetry: presented.kind === "unavailable" };
  }
}

function capitalize(text: string): string {
  return text.length > 0 ? text[0]!.toUpperCase() + text.slice(1) : text;
}

import { OperationError } from "@enterpriseforms/work-queue";

export interface PresentedError {
  code: string;
  status: number;
  title: string;
  detail: string;
  correlationId: string | undefined;
  /** Record fields named by the server (C-31); empty when the failure is not about specific fields. */
  fieldKeys: string[];
  kind: "access-denied" | "session-expired" | "not-found" | "conflict" | "validation" | "unavailable" | "other";
}

const titles: Record<string, string> = {
  FORBIDDEN: "You do not have permission for this action",
  UNAUTHORIZED: "Your session has ended",
  NOT_FOUND: "Not found",
  CONTENT_REVISION_CONFLICT: "Someone else changed this record",
  DRAFT_REVISION_CONFLICT: "The draft changed since you loaded it",
  BRANDING_REVISION_CONFLICT: "Branding changed since you loaded it",
  VERSION_CONFLICT: "A newer version was published",
  PRECONDITION_FAILED: "The record changed since you loaded it",
  VALIDATION_FAILED: "Some values are not valid",
  INVALID_REQUEST: "The request is not valid",
  LAYOUT_INVALID: "The layout is not valid",
  NO_DRAFT: "There is no draft to publish",
  NO_DRAFT_CHANGES: "The draft is identical to the published version",
  RELATIONSHIP_CAPACITY_EXCEEDED: "This list is full",
  FILE_TOO_LARGE: "The file is larger than this collection allows",
  FILE_COUNT_EXCEEDED: "This collection already holds the maximum number of files",
  CONTENT_TYPE_NOT_ALLOWED: "This file type is not allowed here",
  UPLOAD_INCOMPLETE: "Some chunks have not arrived yet",
  DOCUMENT_REJECTED: "The document did not pass scanning",
  UNKNOWN_PERSONA: "That development persona does not exist",
  INTEGRITY_VERIFICATION_FAILED: "Integrity verification failed",
  NO_COMMITTED_CONTENT: "This record has no committed content yet",
  COMMAND_PENDING: "The authority is still applying a security change",
  STORE_UNAVAILABLE: "The service is temporarily unavailable",
  SIGNING_UNAVAILABLE: "The signing service is unavailable",
  AUTHORITY_UNAVAILABLE: "The authorization authority is unavailable",
  MALWARE_DETECTED: "The file was rejected by the malware scanner",
  SCAN_ERROR: "The file could not be scanned",
  CSRF_TOKEN_INVALID: "Your session token is stale; reload the page",
  SESSION_STATE_UNAVAILABLE: "Your session could not be verified",
  ORGANIZATION_OPTION_DISABLED: "A selected option is no longer available",
  THEME_CONTRAST_FAILED: "The colours do not meet the accessibility floor",
  QUEUE_FULL: "Too many pending requests; try again shortly",
  NETWORK_ERROR: "The server could not be reached",
  TENANT_SWITCH: "The organization context changed",
  SESSION_CLEARED: "The session was cleared",
  LIST_CHANGED: "Access changed while you were browsing",
  CURSOR_INVALID: "This page of the list has expired",
  CURSOR_SCOPE_MISMATCH: "This page belongs to different filters",
  RECORD_LIST_TOO_LARGE: "Too many records to list completely",
  AUTHORIZATION_STATE_CHANGED: "Access changed while this was loading",
  AUTHORITY_NOT_READY: "Access is being updated",
  LIMIT_EXCEEDED: "The request is larger than the system allows",
  FIELD_READ_ONLY: "A read-only field cannot be changed",
  FIELD_HIDDEN_BY_RULE: "A hidden field cannot be changed",
  DEPENDENT_OPTION_INVALID: "A choice does not fit the selected parent value",
  FIELD_NOT_EDITABLE: "This field cannot be changed with your access",
  CONTRIBUTOR_WRITES_DISABLED: "Changing shared fields is not enabled",
  CHANGE_NOT_PERMITTED: "This change is not permitted with your access",
  CLEAR_NOT_PERMITTED: "A shared value can be changed but not cleared",
  CHANGE_AFFECTS_OTHER_FIELDS: "This change would affect fields you cannot change",
  FIELD_PROTECTION_MIGRATION_PENDING: "This field's protection is being updated; try again later",
  NO_CHANGE: "There are no changes to save",
  RECORD_NOT_EDITABLE: "This record is locked",
  LIFECYCLE_TRANSITION_NOT_ALLOWED: "That action is not available for this record",
  LIFECYCLE_STATE_CONFLICT: "The record's status changed",
  REASON_REQUIRED: "A reason is required",
  UPLOADS_IN_PROGRESS: "Documents are still being uploaded",
};

/** Maps a coordinator failure into safe, user-facing copy. Internal details stay in the correlation id only. */
export function presentError(error: unknown): PresentedError {
  if (error instanceof OperationError) {
    const code = error.errorCode;
    const status = error.status;
    const kind: PresentedError["kind"] = status === 401 ? "session-expired" : status === 403 ? "access-denied" : status === 404 ? "not-found" : status === 409 || status === 412 || status === 428 ? "conflict" : status === 400 || status === 422 ? "validation" : status === 503 || status === 0 ? "unavailable" : "other";
    return { code, status, title: titles[code] ?? (kind === "unavailable" ? "The service is temporarily unavailable" : "The request failed"), detail: error.problem?.detail ?? error.message, correlationId: error.correlationId, fieldKeys: Array.isArray(error.problem?.field_keys) ? error.problem.field_keys.filter((key): key is string => typeof key === "string") : [], kind };
  }
  return { code: "UNKNOWN", status: 0, title: "Something went wrong", detail: error instanceof Error ? error.message : String(error), correlationId: undefined, fieldKeys: [], kind: "other" };
}

import { ConsoleApiError } from "../api/client";

export interface ErrorView {
  title: string;
  message: string;
  tone: "warning" | "danger";
  /** The operator should reload the data (changed or stale), not simply retry. */
  reload: boolean;
  retryable: boolean;
  reference?: string | undefined;
}

/**
 * Explicit handling of 401, 403, 409, 423 and service-unavailable answers. Messages are written for the operator and never
 * echo server detail that could carry identifiers beyond the safe correlation id.
 */
export function describeError(error: unknown): ErrorView {
  if (!(error instanceof ConsoleApiError)) {
    return { title: "Something went wrong", message: "The request could not be completed.", tone: "danger", reload: false, retryable: true };
  }
  const reference = error.correlationId;
  switch (error.code) {
    case "APPROVAL_STATEMENT_CHANGED":
      return { title: "The request changed", message: "The request changed since you opened it. Nothing was recorded. Reload it and review the current details before deciding.", tone: "warning", reload: true, retryable: false, reference };
    case "MEMBERSHIP_REQUEST_EXPIRED":
      return { title: "The request expired", message: "This request expired, so it can no longer be decided. Reload to see its current status.", tone: "warning", reload: true, retryable: false, reference };
    case "MEMBERSHIP_REQUEST_NOT_PENDING":
      return { title: "The request is no longer pending", message: "It was already decided, cancelled or replaced. Reload to see its current status.", tone: "warning", reload: true, retryable: false, reference };
    case "OPERATOR_ALREADY_APPROVED":
      return { title: "You already approved this request", message: "A second approval must come from a different operator.", tone: "warning", reload: true, retryable: false, reference };
    case "OPERATOR_CONFLICT_OF_INTEREST":
      return { title: "Conflict of interest", message: "You cannot decide a request for an organization or person you are connected to.", tone: "danger", reload: false, retryable: false, reference };
    case "NO_PENDING_APPROVAL":
      return { title: "No pending approval to withdraw", message: "You have no pending approval of this request. Reload to see its current status.", tone: "warning", reload: true, retryable: false, reference };
    case "OPERATOR_EVIDENCE_REPLAYED":
    case "OPERATOR_EVIDENCE_INVALID":
    case "OPERATOR_EVIDENCE_IDENTITY_MISMATCH":
      return { title: "The decision was refused", message: "The Authority did not accept the approval evidence. Nothing was recorded. Try again; if it persists, tell another operator.", tone: "danger", reload: false, retryable: true, reference };
    case "PLATFORM_JOURNAL_BEHIND_ARCHIVE":
    case "PLATFORM_JOURNAL_QUARANTINED":
      return { title: "Platform operations are closed", message: "The Authority detected a possible database rollback and closed platform decisions (fail closed). Nothing was changed. This needs a security-operator recovery, not a retry.", tone: "danger", reload: false, retryable: false, reference };
    case "PLATFORM_JOURNAL_UNVERIFIABLE":
    case "PLATFORM_JOURNAL_ARCHIVE_PENDING":
      return { title: "Temporarily unavailable", message: "The Authority cannot confirm the independent audit archive right now, so it refuses to act (fail closed). Nothing was recorded. Try again shortly.", tone: "warning", reload: false, retryable: true, reference };
    case "ORGANIZATION_KEY_NOT_PROVISIONED":
      return { title: "The organization key is not provisioned yet", message: "Key custody has not provisioned this organization's key, so it cannot be initialized. Nothing was changed. Ask the key custodian, then reload.", tone: "warning", reload: true, retryable: false, reference };
    case "ORGANIZATION_ONBOARDING_EXPIRED":
      return { title: "The request expired", message: "This onboarding request expired, so it can no longer be decided. Reload to see its current status.", tone: "warning", reload: true, retryable: false, reference };
    case "ORGANIZATION_ONBOARDING_NOT_PENDING":
      return { title: "The request does not allow this now", message: "It was already decided, cancelled or initialized. Reload to see its current status.", tone: "warning", reload: true, retryable: false, reference };
    case "ORGANIZATION_NAME_CONFLICT":
      return { title: "Organization name already in use", message: "Another organization or pending request already uses this name. Choose a different name.", tone: "warning", reload: false, retryable: false, reference };
    case "ORGANIZATION_OWNER_IS_OPERATOR":
      return { title: "An owner is a Platform Operator", message: "A Platform Operator cannot be an initial owner of an organization they onboard. Choose a different owner.", tone: "danger", reload: false, retryable: false, reference };
    case "SIGNING_UNAVAILABLE":
      return { title: "Service unavailable", message: "Signing is not available right now. Nothing was recorded. Try again shortly.", tone: "warning", reload: false, retryable: true, reference };
    case "INVALID_REQUEST":
      return { title: "The request was not valid", message: "Check the entered values and try again.", tone: "danger", reload: false, retryable: false, reference };
    case "PLATFORM_OPERATORS_UNAVAILABLE":
      return { title: "Operator decisions are not available", message: "The platform plane is not enabled in this environment.", tone: "danger", reload: false, retryable: false, reference };
    case "OPERATOR_SIGNING_UNAVAILABLE":
    case "AUTHORITY_UNAVAILABLE":
    case "NETWORK":
      return { title: "Service unavailable", message: "A service the console depends on did not answer. Nothing was recorded. Try again shortly.", tone: "warning", reload: false, retryable: true, reference };
    case "PLATFORM_CONSOLE_CREDENTIAL_REJECTED":
      return { title: "Console configuration problem", message: "The Authority refused the console's own credential. This is a deployment fault, not a problem with your sign-in.", tone: "danger", reload: false, retryable: false, reference };
    case "CSRF_TOKEN_INVALID":
    case "ORIGIN_NOT_ALLOWED":
      return { title: "The request was refused", message: "The security check on this page failed. Reload the page and try again.", tone: "danger", reload: true, retryable: false, reference };
    case "RATE_LIMITED":
      return { title: "Too many requests", message: "Wait a minute and try again.", tone: "warning", reload: false, retryable: true, reference };
    case "NOT_FOUND":
      return { title: "Request not found", message: "This request does not exist or is not available to you.", tone: "warning", reload: false, retryable: false, reference };
    default:
      break;
  }
  if (error.status === 403) {
    return { title: "Not allowed", message: "You are not permitted to do this.", tone: "danger", reload: false, retryable: false, reference };
  }
  if (error.status === 409) {
    return { title: "The request changed", message: "Reload it and review the current details before deciding.", tone: "warning", reload: true, retryable: false, reference };
  }
  if (error.status >= 500 || error.status === 423) {
    return { title: "Service unavailable", message: "The service could not complete the request. Nothing was recorded. Try again shortly.", tone: "warning", reload: false, retryable: true, reference };
  }
  return { title: "The request was refused", message: "It could not be completed.", tone: "danger", reload: false, retryable: false, reference };
}

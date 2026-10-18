import { presentError } from "@enterpriseforms/app-runtime";
import { Button, StatusMessage } from "@enterpriseforms/ui";
import { Link } from "@tanstack/react-router";

/**
 * One presentation for every failed request: access denied, not found, conflict, validation, degraded service.
 * Copy is derived from the Problem Details code only; correlation ids are shown so support can trace without leaking.
 */
export function ErrorPanel({ error, onRetry, retryLabel = "Try again" }: { error: unknown; onRetry?: (() => void) | undefined; retryLabel?: string }) {
  const presented = presentError(error);
  const tone = presented.kind === "validation" ? "warning" : presented.kind === "not-found" ? "info" : "danger";
  const actions =
    presented.kind === "access-denied" ? (
      <Link to="/app/dashboard" className="underline underline-offset-4">
        Back to dashboard
      </Link>
    ) : presented.kind === "session-expired" ? (
      <Link to="/login" className="underline underline-offset-4">
        Sign in again
      </Link>
    ) : onRetry ? (
      <Button size="sm" variant="secondary" onClick={onRetry}>
        {retryLabel}
      </Button>
    ) : undefined;
  return (
    <StatusMessage tone={tone} title={presented.title} actions={actions}>
      <p>{presented.kind === "access-denied" ? "The authorization authority denied this request. Nothing was changed." : presented.detail}</p>
      <p className="mt-1 text-xs">
        Code {presented.code}
        {presented.correlationId ? ` · correlation ${presented.correlationId}` : ""}
      </p>
    </StatusMessage>
  );
}

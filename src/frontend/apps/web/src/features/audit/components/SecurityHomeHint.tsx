import { OperationError } from "@enterpriseforms/app-runtime";
import { Link } from "@tanstack/react-router";

/** Whether a failed audit request was refused because the organization's authorization gate is closed. */
export function isGateClosed(error: unknown): boolean {
  return error instanceof OperationError && error.errorCode === "AUTHORITY_GATE_CLOSED";
}

/**
 * C8 M4-4 (OD-5): a path from the audit explorer to the Security Home when audit is unavailable because the gate is closed, or when
 * the audit permission cannot be confirmed. It is a link only: the Security Home authorizes each of its sections itself, and this
 * grants nothing.
 */
export function SecurityHomeHint() {
  return (
    <p className="text-sm">
      If the organization&apos;s authorization gate is closed, audit access cannot be confirmed or served. Security Administrators and Security Auditors can see the gate and archive state on the{" "}
      <Link to="/app/security" className="underline underline-offset-4">
        Security home
      </Link>
      .
    </p>
  );
}

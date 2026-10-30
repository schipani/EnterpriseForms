import type { TeamMembershipOperationResponse } from "@enterpriseforms/api-client";
import { OperationError } from "@enterpriseforms/app-runtime";

/**
 * C8-M1-01 (Design R, R-4): what a team membership change means to the administrator. The server records each change as a
 * durable operation and its Worker completes it from the Authority's confirmed state, whether or not this page stays open.
 * The page only reads that operation: it never re-sends the command. Only a projected operation is "effective"; an ambiguous
 * failure (network, gate closed, 5xx) is never read as success or failure.
 */
export type MembershipOutcome =
  | { kind: "effective" }
  | { kind: "pending"; detail?: string }
  | { kind: "attention"; detail: string }
  | { kind: "rejected"; detail: string }
  | { kind: "failed"; detail: string };

export interface PendingMembershipOperation {
  readonly teamId: string;
  readonly userId: string;
  readonly displayName: string;
  readonly operation: "add" | "remove";
  /** The original Idempotency-Key: finds this browser's own operation on the server after a lost response. Never re-sent. */
  readonly idempotencyKey: string;
  readonly startedOn: number;
  /** The server operation, once known; polled read-only. */
  readonly operationId?: string;
}

/** Server operation state → outcome. The roster changes only when the Worker projected the Authority-confirmed state. */
export function classifyOperation(operation: Pick<TeamMembershipOperationResponse, "operationState" | "rejectionReasonCode" | "errorCode">): MembershipOutcome {
  switch (operation.operationState) {
    case "Projected":
      return { kind: "effective" };
    case "Rejected":
      return { kind: "rejected", detail: `The Authorization Authority refused the change (${operation.rejectionReasonCode ?? "rejected"})` };
    case "AuthorityFailed":
      return { kind: "failed", detail: "The Authorization Authority could not complete the change; an operator must investigate" };
    case "NotCommitted":
      return { kind: "failed", detail: "The Authorization Authority never received the change; it is not sent again — start a new change if it is still wanted" };
    case "ReconciliationError":
      return { kind: "attention", detail: `The server could not confirm the change (${operation.errorCode ?? "check failed"}); nothing was changed on the roster` };
    default:
      return { kind: "pending" };
  }
}

export function classifyResponse(response: { status: number; result?: { operation?: Pick<TeamMembershipOperationResponse, "operationState" | "rejectionReasonCode" | "errorCode"> } | undefined }): MembershipOutcome {
  return response.status === 200 && response.result?.operation ? classifyOperation(response.result.operation) : { kind: "pending" };
}

export function classifyError(error: unknown): MembershipOutcome {
  if (error instanceof OperationError) {
    const detail = error.problem?.detail ?? error.message;
    if (error.errorCode === "PROJECTION_DIVERGENCE" || error.errorCode === "OPERATION_NOT_COMMITTED") {
      return { kind: "failed", detail };
    }
    if (error.status === 422 || error.status === 403 || error.status === 404 || error.status === 409) {
      return { kind: "rejected", detail };
    }
    return { kind: "pending", detail };
  }
  // A transport failure is ambiguous: the change may have been recorded and submitted.
  return { kind: "pending" };
}

/**
 * A failed status read. A 404 for a key lookup means the server never recorded this browser's request — it records the
 * operation before submitting anything — so nothing was sent. Everything else is ambiguous and stays pending.
 */
export function classifyReadError(error: unknown, byKey: boolean): MembershipOutcome {
  if (byKey && error instanceof OperationError && error.status === 404) {
    return { kind: "failed", detail: "The request never reached the server; nothing was sent or changed" };
  }
  return { kind: "pending" };
}

const storageKey = (teamId: string) => `ef.pendingMembership.${teamId}`;

/** Safe session metadata only (no tokens, no content). Survives a reload of this tab; never trusted for authorization. */
export function loadPending(teamId: string): PendingMembershipOperation[] {
  try {
    const raw = window.sessionStorage.getItem(storageKey(teamId));
    const parsed: unknown = raw ? JSON.parse(raw) : [];
    return Array.isArray(parsed) ? parsed.filter(isOperation).filter((o) => o.teamId === teamId) : [];
  } catch {
    return [];
  }
}

export function savePending(teamId: string, operations: readonly PendingMembershipOperation[]): void {
  try {
    if (operations.length === 0) {
      window.sessionStorage.removeItem(storageKey(teamId));
    } else {
      window.sessionStorage.setItem(storageKey(teamId), JSON.stringify(operations));
    }
  } catch {
    // Storage unavailable: the in-memory list still drives this page view, and the server finishes the change regardless.
  }
}

function isOperation(value: unknown): value is PendingMembershipOperation {
  const o = value as Partial<PendingMembershipOperation> | null;
  return (
    !!o &&
    typeof o.teamId === "string" &&
    typeof o.userId === "string" &&
    typeof o.displayName === "string" &&
    (o.operation === "add" || o.operation === "remove") &&
    typeof o.idempotencyKey === "string" &&
    o.idempotencyKey.length >= 8 &&
    typeof o.startedOn === "number" &&
    (o.operationId === undefined || typeof o.operationId === "string")
  );
}

/** Status-read cadence: 2 s growing to 10 s; stops automatically after ~5 minutes (then a manual check is offered). */
export function nextDelayMs(attempt: number): number {
  return Math.min(10_000, 2_000 + attempt * 1_000);
}

export const MAXIMUM_AUTOMATIC_ATTEMPTS = 40;

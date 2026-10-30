import { OperationError } from "@enterpriseforms/app-runtime";

/**
 * C8-M1-01: what a team membership command outcome means to the administrator. Only an Authority-confirmed Effective
 * response is "effective"; an ambiguous failure (network, gate closed, 5xx) is never read as success or failure: the
 * command may still be committed, so it stays "pending" and is re-checked with the SAME idempotency key.
 */
export type MembershipOutcome =
  | { kind: "effective" }
  | { kind: "pending"; detail?: string }
  | { kind: "rejected"; detail: string }
  | { kind: "failed"; detail: string };

export interface PendingMembershipOperation {
  readonly teamId: string;
  readonly userId: string;
  readonly displayName: string;
  readonly operation: "add" | "remove";
  /** The original Idempotency-Key. Re-checking with it addresses the one committed Authority command; it never creates another. */
  readonly idempotencyKey: string;
  readonly startedOn: number;
}

export function classifyResponse(response: { status: number; result?: { command?: { effective?: boolean } } | undefined }): MembershipOutcome {
  return response.status === 200 && response.result?.command?.effective === true ? { kind: "effective" } : { kind: "pending" };
}

export function classifyError(error: unknown): MembershipOutcome {
  if (error instanceof OperationError) {
    const detail = error.problem?.detail ?? error.message;
    if (error.errorCode === "PROJECTION_DIVERGENCE") {
      return { kind: "failed", detail };
    }
    if (error.status === 422 || error.status === 403 || error.status === 404 || error.status === 409) {
      return { kind: "rejected", detail };
    }
    return { kind: "pending", detail };
  }
  // A transport failure is ambiguous: the command may have been committed.
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
    // Storage unavailable: the in-memory list still drives this page view.
  }
}

function isOperation(value: unknown): value is PendingMembershipOperation {
  const o = value as Partial<PendingMembershipOperation> | null;
  return !!o && typeof o.teamId === "string" && typeof o.userId === "string" && typeof o.displayName === "string" && (o.operation === "add" || o.operation === "remove") && typeof o.idempotencyKey === "string" && o.idempotencyKey.length >= 8 && typeof o.startedOn === "number";
}

/** Re-check cadence: 2 s growing to 10 s; stops automatically after ~5 minutes (then a manual check is offered). */
export function nextDelayMs(attempt: number): number {
  return Math.min(10_000, 2_000 + attempt * 1_000);
}

export const MAXIMUM_AUTOMATIC_ATTEMPTS = 40;

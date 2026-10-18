/**
 * Request coordinator contracts (CLAUDE-DESIGN.MD §5.4, PB07_BROWSER_REQUEST_COORDINATOR_STANDARD.md).
 * Operations name a registered endpoint key, never a URL; the worker performs same-origin fetches with the BFF
 * session cookie and the CSRF header. Nothing here is a security boundary — the BFF and Authority re-authorize.
 */
export type OperationLane = "security" | "interactive-read" | "interactive-write" | "background-read" | "file-transfer" | "polling";

export type HttpMethod = "GET" | "POST" | "PUT" | "PATCH" | "DELETE";

export type ApiOperation = {
  operationId: string;
  lane: OperationLane;
  method: HttpMethod;
  /** Key into the compiled endpoint registry (no arbitrary URLs). */
  endpointKey: string;
  /** Path parameters substituted into the registered template; values are URL-encoded. */
  pathParams?: Record<string, string>;
  query?: Record<string, string | number | boolean | undefined>;
  /** JSON payload (structured-clone safe) or raw bytes for chunk uploads. */
  payload?: unknown;
  binaryPayload?: ArrayBuffer;
  expectedRevision?: string;
  idempotencyKey?: string;
  /** Serializes writes per resource (one interactive-write at a time per key). */
  resourceKey?: string;
  timeoutMilliseconds?: number;
  /** Set when the response is not JSON (e.g. 204) or should be returned as text. */
  responseKind?: "json" | "text" | "none";
};

export type OperationState = "Created" | "Queued" | "Running" | "Retrying" | "Succeeded" | "Failed" | "Cancelled" | "AwaitingUserAction";

export type ProblemDetails = {
  type?: string;
  title?: string;
  status?: number;
  detail?: string;
  correlation_id?: string;
  command_id?: string;
  command_status?: string;
  retryable?: boolean;
};

export type OperationSuccess = { type: "succeeded"; operationId: string; status: number; result: unknown; etag?: string };
export type OperationFailure = { type: "failed"; operationId: string; status: number; errorCode: string; retryable: boolean; problem?: ProblemDetails; correlationId?: string };
export type OperationProgress = { type: "progress"; operationId: string; completedBytes: number; totalBytes: number };
export type OperationStateChange = { type: "state"; operationId: string; state: OperationState };
export type OperationEvent = OperationSuccess | OperationFailure | OperationProgress | OperationStateChange;

export type SecurityContext = {
  /** Organization of the current session; cleared on logout/tenant switch, which cancels everything queued. */
  organizationId: string | null;
  csrfToken: string | null;
  /** Authorization epoch supplied by bootstrap; a change invalidates queued work. */
  authorizationEpoch: string | null;
};

export type OperationCommand =
  | { type: "enqueue"; operation: ApiOperation }
  | { type: "cancel"; operationId: string }
  | { type: "set-security-context"; context: SecurityContext }
  | { type: "clear-security-context" };

export type LaneBudget = { concurrency: number; priority: number };

/** Initial tuning from CLAUDE-DESIGN.MD §5.1; adjusted only through measured benchmarks. */
export const DEFAULT_LANE_BUDGETS: Record<OperationLane, LaneBudget> = {
  security: { concurrency: 2, priority: 0 },
  "interactive-read": { concurrency: 4, priority: 1 },
  "interactive-write": { concurrency: 2, priority: 1 },
  "background-read": { concurrency: 3, priority: 2 },
  "file-transfer": { concurrency: 4, priority: 3 },
  polling: { concurrency: 2, priority: 4 },
};

export class OperationError extends Error {
  readonly status: number;
  readonly errorCode: string;
  readonly retryable: boolean;
  readonly problem: ProblemDetails | undefined;
  readonly correlationId: string | undefined;

  constructor(failure: OperationFailure) {
    super(failure.problem?.detail ?? failure.errorCode);
    this.name = "OperationError";
    this.status = failure.status;
    this.errorCode = failure.errorCode;
    this.retryable = failure.retryable;
    this.problem = failure.problem;
    this.correlationId = failure.correlationId;
  }
}

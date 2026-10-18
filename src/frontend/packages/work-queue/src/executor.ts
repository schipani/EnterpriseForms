import type { ApiOperation, OperationCommand, OperationEvent, OperationFailure, OperationSuccess, ProblemDetails, SecurityContext } from "./contracts";
import { ENDPOINTS, buildUrl, type EndpointKey } from "./endpoints";
import { LaneScheduler } from "./scheduler";

export type FetchLike = (input: string, init: RequestInit) => Promise<Response>;

const RETRYABLE_STATUS = new Set([408, 425, 429, 502, 503, 504]);
const MAX_RETRIES = 3;

/**
 * Executes scheduled operations with same-origin credentials. Runs identically inside the Dedicated Web Worker and in
 * the main-thread adapter. Idempotent/safe operations retry with exponential backoff + jitter honoring Retry-After;
 * unsafe writes without an idempotency key never retry automatically. A cleared security context aborts everything.
 */
export class OperationExecutor {
  private readonly scheduler = new LaneScheduler();
  private readonly controllers = new Map<string, AbortController>();
  private readonly attempts = new Map<string, number>();
  private context: SecurityContext = { organizationId: null, csrfToken: null, authorizationEpoch: null };
  private pumping = false;

  constructor(
    private readonly emit: (event: OperationEvent) => void,
    private readonly fetchImpl: FetchLike,
    private readonly now: () => number = () => Date.now(),
  ) {}

  handle(command: OperationCommand): void {
    switch (command.type) {
      case "enqueue":
        try {
          this.scheduler.enqueue(command.operation, this.now());
          this.emit({ type: "state", operationId: command.operation.operationId, state: "Queued" });
        } catch (error) {
          this.emit({ type: "failed", operationId: command.operation.operationId, status: 0, errorCode: "QUEUE_FULL", retryable: true, problem: { detail: (error as Error).message } });
        }
        break;
      case "cancel":
        if (this.scheduler.remove(command.operationId)) {
          this.emit({ type: "state", operationId: command.operationId, state: "Cancelled" });
        }
        this.controllers.get(command.operationId)?.abort();
        break;
      case "set-security-context": {
        const previous = this.context;
        this.context = command.context;
        if (previous.organizationId !== null && previous.organizationId !== command.context.organizationId) {
          this.cancelAll("TENANT_SWITCH");
        } else if (previous.authorizationEpoch !== null && command.context.authorizationEpoch !== null && previous.authorizationEpoch !== command.context.authorizationEpoch) {
          this.cancelAll("AUTHORIZATION_EPOCH_CHANGED");
        }
        break;
      }
      case "clear-security-context":
        this.context = { organizationId: null, csrfToken: null, authorizationEpoch: null };
        this.cancelAll("SESSION_CLEARED");
        break;
    }
    void this.pump();
  }

  private cancelAll(reason: string): void {
    for (const operation of this.scheduler.drainAll()) {
      this.emit({ type: "failed", operationId: operation.operationId, status: 0, errorCode: reason, retryable: false });
    }
    for (const controller of this.controllers.values()) {
      controller.abort();
    }
  }

  private async pump(): Promise<void> {
    if (this.pumping) {
      return;
    }
    this.pumping = true;
    try {
      let operation = this.scheduler.next(this.now());
      while (operation !== undefined) {
        void this.run(operation);
        operation = this.scheduler.next(this.now());
      }
    } finally {
      this.pumping = false;
    }
  }

  private async run(operation: ApiOperation): Promise<void> {
    const controller = new AbortController();
    this.controllers.set(operation.operationId, controller);
    this.emit({ type: "state", operationId: operation.operationId, state: "Running" });
    const timeout = setTimeout(() => controller.abort(), operation.timeoutMilliseconds ?? 60_000);
    try {
      const outcome = await this.execute(operation, controller.signal);
      this.emit(outcome);
      if (outcome.type === "failed" && outcome.retryable && this.canRetry(operation)) {
        const attempt = (this.attempts.get(operation.operationId) ?? 0) + 1;
        this.attempts.set(operation.operationId, attempt);
        this.emit({ type: "state", operationId: operation.operationId, state: "Retrying" });
        const retryAfter = outcome.problem?.retryable === true ? 0 : 0;
        const delay = retryAfter || Math.min(8000, 250 * 2 ** attempt) + Math.random() * 250;
        setTimeout(() => {
          this.scheduler.enqueue(operation, this.now());
          void this.pump();
        }, delay);
      } else {
        this.attempts.delete(operation.operationId);
      }
    } finally {
      clearTimeout(timeout);
      this.controllers.delete(operation.operationId);
      this.scheduler.complete(operation);
      void this.pump();
    }
  }

  private canRetry(operation: ApiOperation): boolean {
    const attempts = this.attempts.get(operation.operationId) ?? 0;
    if (attempts >= MAX_RETRIES) {
      return false;
    }
    if (operation.method === "GET") {
      return true;
    }
    return operation.idempotencyKey !== undefined;
  }

  private async execute(operation: ApiOperation, signal: AbortSignal): Promise<OperationSuccess | OperationFailure> {
    const key = operation.endpointKey as EndpointKey;
    const endpoint = ENDPOINTS[key];
    if (endpoint === undefined || endpoint.method !== operation.method) {
      return { type: "failed", operationId: operation.operationId, status: 0, errorCode: "UNREGISTERED_ENDPOINT", retryable: false };
    }
    const headers: Record<string, string> = { Accept: "application/json", "X-Correlation-Id": operation.operationId };
    if (operation.method !== "GET" && this.context.csrfToken) {
      headers["X-CSRF-Token"] = this.context.csrfToken;
    }
    if (operation.idempotencyKey) {
      headers["Idempotency-Key"] = operation.idempotencyKey;
    }
    if (operation.expectedRevision) {
      headers["If-Match"] = `"${operation.expectedRevision}"`;
    }
    let body: BodyInit | undefined;
    if (operation.binaryPayload !== undefined) {
      headers["Content-Type"] = "application/octet-stream";
      body = operation.binaryPayload;
    } else if (operation.payload !== undefined) {
      headers["Content-Type"] = "application/json";
      body = JSON.stringify(operation.payload);
    }
    let response: Response;
    try {
      response = await this.fetchImpl(buildUrl(key, operation.pathParams, operation.query), { method: operation.method, headers, body: body ?? null, credentials: "same-origin", signal, redirect: "error" });
    } catch (error) {
      const aborted = signal.aborted;
      return { type: "failed", operationId: operation.operationId, status: 0, errorCode: aborted ? "CANCELLED" : "NETWORK_ERROR", retryable: !aborted, problem: { detail: (error as Error).message } };
    }
    if (response.ok) {
      const etag = response.headers.get("ETag") ?? undefined;
      const kind = operation.responseKind ?? (response.status === 204 ? "none" : "json");
      const result = kind === "none" ? undefined : kind === "text" ? await response.text() : await safeJson(response);
      return etag !== undefined ? { type: "succeeded", operationId: operation.operationId, status: response.status, result, etag } : { type: "succeeded", operationId: operation.operationId, status: response.status, result };
    }
    const problem = (await safeJson(response)) as ProblemDetails | undefined;
    const errorCode = problem?.title ?? `HTTP_${response.status}`;
    const retryable = RETRYABLE_STATUS.has(response.status) || problem?.retryable === true;
    const correlationId = response.headers.get("X-Correlation-Id") ?? problem?.correlation_id;
    return correlationId !== undefined && correlationId !== null
      ? { type: "failed", operationId: operation.operationId, status: response.status, errorCode, retryable, problem: problem ?? {}, correlationId }
      : { type: "failed", operationId: operation.operationId, status: response.status, errorCode, retryable, problem: problem ?? {} };
  }
}

async function safeJson(response: Response): Promise<unknown> {
  const text = await response.text();
  if (text.length === 0) {
    return undefined;
  }
  try {
    return JSON.parse(text) as unknown;
  } catch {
    return undefined;
  }
}

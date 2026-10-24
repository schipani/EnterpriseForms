import { OperationError, type ApiOperation, type OperationCommand, type OperationEvent, type SecurityContext } from "./contracts";
import { OperationExecutor } from "./executor";

export interface IRequestCoordinator {
  execute<TResponse>(operation: Omit<ApiOperation, "operationId"> & { operationId?: string }, signal?: AbortSignal): Promise<{ result: TResponse; etag?: string; status: number }>;
  cancel(operationId: string): void;
  subscribe(listener: (event: OperationEvent) => void): () => void;
  setSecurityContext(context: SecurityContext): void;
  clearSecurityContext(): void;
  readonly transport: "worker" | "main-thread";
}

type Pending = { resolve: (value: { result: unknown; etag?: string; status: number }) => void; reject: (error: Error) => void };

function newOperationId(): string {
  return typeof crypto !== "undefined" && "randomUUID" in crypto ? crypto.randomUUID() : `${Date.now()}-${Math.random().toString(16).slice(2)}`;
}

/**
 * App-shell coordinator (ADR-PB-007): instantiated once before the authenticated bootstrap; survives SPA navigation.
 * Prefers a Dedicated Web Worker; falls back to the in-process executor with identical queue semantics.
 */
export class RequestCoordinator implements IRequestCoordinator {
  private readonly pending = new Map<string, Pending>();
  private readonly listeners = new Set<(event: OperationEvent) => void>();
  private readonly worker: Worker | undefined;
  private readonly inline: OperationExecutor | undefined;
  readonly transport: "worker" | "main-thread";

  constructor(options: { createWorker?: () => Worker; fetchImpl?: (input: string, init: RequestInit) => Promise<Response> } = {}) {
    let worker: Worker | undefined;
    if (options.createWorker) {
      try {
        worker = options.createWorker();
      } catch {
        worker = undefined;
      }
    }
    if (worker) {
      this.worker = worker;
      this.transport = "worker";
      worker.onmessage = (message: MessageEvent<{ v: number; event: OperationEvent }>) => {
        if (message.data?.v === 1) {
          this.onEvent(message.data.event);
        }
      };
      worker.onerror = () => {
        // The worker is a scheduler only; a crash fails queued operations so the UI can retry (PB-07 WRK-07).
        for (const [operationId, pending] of this.pending) {
          pending.reject(new OperationError({ type: "failed", operationId, status: 0, errorCode: "WORKER_CRASHED", retryable: true }));
        }
        this.pending.clear();
      };
    } else {
      this.transport = "main-thread";
      this.inline = new OperationExecutor((event) => this.onEvent(event), options.fetchImpl ?? ((input, init) => fetch(input, init)));
    }
  }

  execute<TResponse>(operation: Omit<ApiOperation, "operationId"> & { operationId?: string }, signal?: AbortSignal): Promise<{ result: TResponse; etag?: string; status: number }> {
    const full: ApiOperation = { ...operation, operationId: operation.operationId ?? newOperationId() };
    return new Promise((resolve, reject) => {
      this.pending.set(full.operationId, { resolve: resolve as Pending["resolve"], reject });
      signal?.addEventListener("abort", () => this.cancel(full.operationId), { once: true });
      this.send({ type: "enqueue", operation: full });
    });
  }

  cancel(operationId: string): void {
    this.send({ type: "cancel", operationId });
  }

  subscribe(listener: (event: OperationEvent) => void): () => void {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  }

  setSecurityContext(context: SecurityContext): void {
    this.send({ type: "set-security-context", context });
  }

  clearSecurityContext(): void {
    this.send({ type: "clear-security-context" });
  }

  private send(command: OperationCommand): void {
    if (this.worker) {
      const transfer = command.type === "enqueue" && command.operation.binaryPayload ? [command.operation.binaryPayload] : [];
      this.worker.postMessage({ v: 1, command }, transfer);
    } else {
      this.inline!.handle(command);
    }
  }

  private onEvent(event: OperationEvent): void {
    for (const listener of this.listeners) {
      listener(event);
    }
    if (event.type === "succeeded") {
      const pending = this.pending.get(event.operationId);
      this.pending.delete(event.operationId);
      pending?.resolve(event.etag !== undefined ? { result: event.result, etag: event.etag, status: event.status } : { result: event.result, status: event.status });
    } else if (event.type === "failed" && (!event.retryable || event.final)) {
      const pending = this.pending.get(event.operationId);
      this.pending.delete(event.operationId);
      pending?.reject(new OperationError(event));
    } else if (event.type === "failed" && event.retryable) {
      // retried by the executor; the last failure arrives marked final (after MAX_RETRIES, or when it cannot be retried)
      const pending = this.pending.get(event.operationId);
      if (pending && event.errorCode === "CANCELLED") {
        this.pending.delete(event.operationId);
        pending.reject(new OperationError({ ...event, retryable: false }));
      }
    } else if (event.type === "state" && event.state === "Cancelled") {
      const pending = this.pending.get(event.operationId);
      this.pending.delete(event.operationId);
      pending?.reject(new OperationError({ type: "failed", operationId: event.operationId, status: 0, errorCode: "CANCELLED", retryable: false }));
    }
  }
}

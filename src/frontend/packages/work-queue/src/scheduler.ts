import { DEFAULT_LANE_BUDGETS, type ApiOperation, type LaneBudget, type OperationLane } from "./contracts";

export type ScheduledEntry = { operation: ApiOperation; enqueuedAt: number; sequence: number };

/**
 * Bounded priority scheduler with aging and per-resource write serialization (PB-07): lanes have concurrency budgets;
 * the next runnable operation is the oldest in the highest-priority lane with capacity, except that an operation
 * waiting longer than the aging threshold is promoted so low lanes are never starved. Pure logic, no I/O (unit-tested).
 */
export class LaneScheduler {
  private readonly queues = new Map<OperationLane, ScheduledEntry[]>();
  private readonly running = new Map<OperationLane, number>();
  private readonly runningResources = new Set<string>();
  private readonly runningIds = new Map<string, OperationLane>();
  private sequence = 0;

  constructor(
    private readonly budgets: Record<OperationLane, LaneBudget> = DEFAULT_LANE_BUDGETS,
    private readonly globalConcurrency = 8,
    private readonly agingMilliseconds = 10_000,
    private readonly maxQueued = 500,
  ) {}

  enqueue(operation: ApiOperation, now: number): void {
    const queued = [...this.queues.values()].reduce((count, queue) => count + queue.length, 0);
    if (queued >= this.maxQueued) {
      throw new Error("Request queue is full (backpressure)");
    }
    const queue = this.queues.get(operation.lane) ?? [];
    queue.push({ operation, enqueuedAt: now, sequence: this.sequence++ });
    this.queues.set(operation.lane, queue);
  }

  remove(operationId: string): boolean {
    for (const queue of this.queues.values()) {
      const index = queue.findIndex((entry) => entry.operation.operationId === operationId);
      if (index >= 0) {
        queue.splice(index, 1);
        return true;
      }
    }
    return false;
  }

  drainAll(): ApiOperation[] {
    const all = [...this.queues.values()].flatMap((queue) => queue.map((entry) => entry.operation));
    this.queues.clear();
    return all;
  }

  /** Returns the next operation to run (and marks it running), or undefined when budgets are exhausted. */
  next(now: number): ApiOperation | undefined {
    if (this.runningIds.size >= this.globalConcurrency) {
      return undefined;
    }
    const candidates: { entry: ScheduledEntry; effectivePriority: number }[] = [];
    for (const [lane, queue] of this.queues) {
      const budget = this.budgets[lane];
      if ((this.running.get(lane) ?? 0) >= budget.concurrency) {
        continue;
      }
      for (const entry of queue) {
        const resource = entry.operation.resourceKey;
        if (resource !== undefined && this.runningResources.has(resource)) {
          continue;
        }
        const aged = now - entry.enqueuedAt >= this.agingMilliseconds;
        // Aged work jumps to the top so no lane starves; aging is rare (>= agingMilliseconds) and bounded by lane concurrency.
        candidates.push({ entry, effectivePriority: aged ? 0 : budget.priority });
        break; // one candidate per lane: oldest runnable entry
      }
    }
    if (candidates.length === 0) {
      return undefined;
    }
    candidates.sort((a, b) => a.effectivePriority - b.effectivePriority || a.entry.enqueuedAt - b.entry.enqueuedAt || a.entry.sequence - b.entry.sequence);
    const chosen = candidates[0]!.entry;
    const queue = this.queues.get(chosen.operation.lane)!;
    queue.splice(queue.indexOf(chosen), 1);
    this.running.set(chosen.operation.lane, (this.running.get(chosen.operation.lane) ?? 0) + 1);
    this.runningIds.set(chosen.operation.operationId, chosen.operation.lane);
    if (chosen.operation.resourceKey !== undefined) {
      this.runningResources.add(chosen.operation.resourceKey);
    }
    return chosen.operation;
  }

  complete(operation: ApiOperation): void {
    if (!this.runningIds.delete(operation.operationId)) {
      return;
    }
    this.running.set(operation.lane, Math.max(0, (this.running.get(operation.lane) ?? 1) - 1));
    if (operation.resourceKey !== undefined) {
      this.runningResources.delete(operation.resourceKey);
    }
  }

  get queuedCount(): number {
    return [...this.queues.values()].reduce((count, queue) => count + queue.length, 0);
  }

  get runningCount(): number {
    return this.runningIds.size;
  }

  isRunning(operationId: string): boolean {
    return this.runningIds.has(operationId);
  }
}

import { describe, expect, it } from "vitest";
import type { ApiOperation } from "./contracts";
import { LaneScheduler } from "./scheduler";

function op(id: string, lane: ApiOperation["lane"], resourceKey?: string): ApiOperation {
  return resourceKey !== undefined
    ? { operationId: id, lane, method: "GET", endpointKey: "session.get", resourceKey }
    : { operationId: id, lane, method: "GET", endpointKey: "session.get" };
}

describe("LaneScheduler (PB-07 fairness and budgets)", () => {
  it("prefers interactive reads over file transfers and respects lane concurrency", () => {
    const scheduler = new LaneScheduler();
    for (let i = 0; i < 6; i++) {
      scheduler.enqueue(op(`t${i}`, "file-transfer"), 0);
    }
    scheduler.enqueue(op("r1", "interactive-read"), 1);
    expect(scheduler.next(2)?.operationId).toBe("r1");
    const transfers = [scheduler.next(2), scheduler.next(2), scheduler.next(2), scheduler.next(2), scheduler.next(2)].map((o) => o?.operationId);
    expect(transfers).toEqual(["t0", "t1", "t2", "t3", undefined]); // file-transfer concurrency 4
  });

  it("serializes writes per resource key", () => {
    const scheduler = new LaneScheduler();
    scheduler.enqueue(op("w1", "interactive-write", "record:1"), 0);
    scheduler.enqueue(op("w2", "interactive-write", "record:1"), 0);
    scheduler.enqueue(op("w3", "interactive-write", "record:2"), 0);
    const first = scheduler.next(1)!;
    const second = scheduler.next(1)!;
    expect([first.operationId, second.operationId]).toEqual(["w1", "w3"]);
    expect(scheduler.next(1)).toBeUndefined();
    scheduler.complete(first);
    expect(scheduler.next(2)?.operationId).toBe("w2");
  });

  it("ages starved low-priority work so it is not blocked forever", () => {
    const scheduler = new LaneScheduler();
    scheduler.enqueue(op("poll", "polling"), 0);
    scheduler.enqueue(op("read", "interactive-read"), 20_000);
    expect(scheduler.next(20_001)?.operationId).toBe("poll"); // waited > aging threshold: promoted above the fresh read
  });

  it("applies backpressure when the queue is full", () => {
    const scheduler = new LaneScheduler(undefined, 8, 10_000, 2);
    scheduler.enqueue(op("a", "background-read"), 0);
    scheduler.enqueue(op("b", "background-read"), 0);
    expect(() => scheduler.enqueue(op("c", "background-read"), 0)).toThrow(/backpressure/);
  });

  it("drains everything on security-context changes", () => {
    const scheduler = new LaneScheduler();
    scheduler.enqueue(op("a", "polling"), 0);
    scheduler.enqueue(op("b", "interactive-read"), 0);
    expect(scheduler.drainAll().map((o) => o.operationId).sort()).toEqual(["a", "b"]);
    expect(scheduler.queuedCount).toBe(0);
  });
});

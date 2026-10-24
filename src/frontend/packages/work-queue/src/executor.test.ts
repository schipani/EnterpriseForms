import { describe, expect, it, vi } from "vitest";
import type { OperationEvent } from "./contracts";
import { OperationExecutor } from "./executor";

function jsonResponse(status: number, body: unknown, headers: Record<string, string> = {}): Response {
  return new Response(body === undefined ? null : JSON.stringify(body), { status, headers: { "Content-Type": "application/json", ...headers } });
}

async function settle(events: OperationEvent[], predicate: (e: OperationEvent) => boolean, timeoutMs = 2000): Promise<OperationEvent> {
  const started = Date.now();
  while (Date.now() - started < timeoutMs) {
    const found = events.find(predicate);
    if (found) {
      return found;
    }
    await new Promise((resolve) => setTimeout(resolve, 5));
  }
  throw new Error("event not observed: " + JSON.stringify(events));
}

describe("OperationExecutor (same-origin transport policy)", () => {
  it("sends CSRF and idempotency headers for mutations and parses ETags", async () => {
    const calls: { url: string; init: RequestInit }[] = [];
    const events: OperationEvent[] = [];
    const executor = new OperationExecutor(
      (e) => events.push(e),
      async (url, init) => {
        calls.push({ url, init });
        return jsonResponse(200, { ok: true }, { ETag: '"7"' });
      },
    );
    executor.handle({ type: "set-security-context", context: { organizationId: "org", csrfToken: "csrf-1", authorizationEpoch: "1" } });
    executor.handle({ type: "enqueue", operation: { operationId: "op1", lane: "interactive-write", method: "PATCH", endpointKey: "records.update", pathParams: { formRecordId: "r1" }, payload: { a: 1 }, idempotencyKey: "idem-00000001", expectedRevision: "7" } });
    const done = await settle(events, (e) => e.type === "succeeded");
    expect(done).toMatchObject({ type: "succeeded", etag: '"7"' });
    const headers = calls[0]!.init.headers as Record<string, string>;
    expect(calls[0]!.url).toBe("/api/v1/form-records/r1");
    expect(headers["X-CSRF-Token"]).toBe("csrf-1");
    expect(headers["Idempotency-Key"]).toBe("idem-00000001");
    expect(headers["If-Match"]).toBe('"7"');
    expect(calls[0]!.init.credentials).toBe("same-origin");
  });

  it("refuses unregistered endpoints and never builds arbitrary URLs", async () => {
    const events: OperationEvent[] = [];
    const executor = new OperationExecutor((e) => events.push(e), vi.fn());
    executor.handle({ type: "enqueue", operation: { operationId: "x", lane: "interactive-read", method: "GET", endpointKey: "https://evil.example/steal" } });
    const failed = await settle(events, (e) => e.type === "failed");
    expect(failed).toMatchObject({ errorCode: "UNREGISTERED_ENDPOINT", retryable: false });
  });

  it("maps Problem Details to a non-retryable failure with the error code", async () => {
    const events: OperationEvent[] = [];
    const executor = new OperationExecutor((e) => events.push(e), async () => jsonResponse(409, { title: "CONTENT_REVISION_CONFLICT", status: 409, detail: "stale" }, { "X-Correlation-Id": "c-1" }));
    executor.handle({ type: "enqueue", operation: { operationId: "y", lane: "interactive-write", method: "PATCH", endpointKey: "records.update", pathParams: { formRecordId: "r1" }, payload: {} } });
    const failed = await settle(events, (e) => e.type === "failed");
    expect(failed).toMatchObject({ errorCode: "CONTENT_REVISION_CONFLICT", status: 409, retryable: false, correlationId: "c-1" });
  });

  it("retries idempotent reads on 503 with backoff and does not retry unsafe writes", async () => {
    let getCalls = 0;
    let postCalls = 0;
    const events: OperationEvent[] = [];
    const executor = new OperationExecutor((e) => events.push(e), async (_url, init) => {
      if (init.method === "GET") {
        getCalls++;
        return getCalls < 2 ? jsonResponse(503, { title: "AUTHORITY_UNAVAILABLE", retryable: true }) : jsonResponse(200, { fine: true });
      }
      postCalls++;
      return jsonResponse(503, { title: "AUTHORITY_UNAVAILABLE" });
    });
    executor.handle({ type: "enqueue", operation: { operationId: "g", lane: "interactive-read", method: "GET", endpointKey: "session.get" } });
    await settle(events, (e) => e.type === "succeeded" && e.operationId === "g", 5000);
    expect(getCalls).toBe(2);
    executor.handle({ type: "enqueue", operation: { operationId: "p", lane: "interactive-write", method: "POST", endpointKey: "teams.create", payload: {} } });
    await settle(events, (e) => e.type === "failed" && e.operationId === "p");
    await new Promise((resolve) => setTimeout(resolve, 50));
    expect(postCalls).toBe(1);
  });

  it("marks the last failure final when retries are exhausted, so the caller is settled instead of waiting forever", async () => {
    let calls = 0;
    const events: OperationEvent[] = [];
    const executor = new OperationExecutor((e) => events.push(e), async () => {
      calls++;
      return jsonResponse(503, { title: "AUTHORITY_UNAVAILABLE" });
    });
    executor.handle({ type: "enqueue", operation: { operationId: "down", lane: "interactive-read", method: "GET", endpointKey: "records.list", query: { q: "REC-11111111" } } });
    const last = await settle(events, (e) => e.type === "failed" && e.operationId === "down" && e.final === true, 15000);
    expect(last).toMatchObject({ errorCode: "AUTHORITY_UNAVAILABLE", status: 503, retryable: true, final: true });
    expect(calls).toBe(4); // the first attempt and three retries
    expect(events.filter((e) => e.type === "failed" && e.operationId === "down" && e.final === true)).toHaveLength(1);
    expect(events.filter((e) => e.type === "failed" && e.operationId === "down" && !e.final)).toHaveLength(3);
  }, 20000);

  it("clears the queue when the security context is cleared", async () => {
    const events: OperationEvent[] = [];
    const executor = new OperationExecutor((e) => events.push(e), async () => {
      await new Promise((resolve) => setTimeout(resolve, 200));
      return jsonResponse(200, {});
    });
    for (let i = 0; i < 6; i++) {
      executor.handle({ type: "enqueue", operation: { operationId: `q${i}`, lane: "polling", method: "GET", endpointKey: "uploads.mine" } });
    }
    executor.handle({ type: "clear-security-context" });
    const failed = await settle(events, (e) => e.type === "failed" && e.errorCode === "SESSION_CLEARED");
    expect(failed.type).toBe("failed");
  });
});

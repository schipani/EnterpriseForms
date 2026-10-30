import { OperationError } from "@enterpriseforms/app-runtime";
import { beforeEach, describe, expect, it } from "vitest";
import { classifyError, classifyResponse, loadPending, nextDelayMs, savePending, type PendingMembershipOperation } from "./membershipOutcome";

const failure = (status: number, errorCode: string) => new OperationError({ type: "failed", operationId: "op", status, errorCode, retryable: false, problem: { title: errorCode, detail: `d ${errorCode}` } });

describe("membership outcome classification (C8-M1-01)", () => {
  it("is effective only for a 200 whose command the Authority reports effective", () => {
    expect(classifyResponse({ status: 200, result: { command: { effective: true } } }).kind).toBe("effective");
    expect(classifyResponse({ status: 200, result: { command: { effective: false } } }).kind).toBe("pending");
    expect(classifyResponse({ status: 202, result: { command: { effective: true } } }).kind).toBe("pending");
    expect(classifyResponse({ status: 200 }).kind).toBe("pending");
  });

  it("distinguishes rejected, failed and ambiguous errors and never reads ambiguity as success", () => {
    expect(classifyError(failure(422, "SELF_MEMBERSHIP_FORBIDDEN")).kind).toBe("rejected");
    expect(classifyError(failure(403, "FORBIDDEN")).kind).toBe("rejected");
    expect(classifyError(failure(409, "IDEMPOTENCY_CONFLICT")).kind).toBe("rejected");
    expect(classifyError(failure(503, "PROJECTION_DIVERGENCE")).kind).toBe("failed");
    expect(classifyError(failure(503, "AUTHORITY_NOT_READY")).kind).toBe("pending");
    expect(classifyError(failure(500, "INTERNAL")).kind).toBe("pending");
    expect(classifyError(new TypeError("network")).kind).toBe("pending");
  });

  it("backs off from 2 s to 10 s", () => {
    expect(nextDelayMs(0)).toBe(2000);
    expect(nextDelayMs(3)).toBe(5000);
    expect(nextDelayMs(100)).toBe(10000);
  });
});

describe("pending operation persistence", () => {
  const op: PendingMembershipOperation = { teamId: "t1", userId: "u1", displayName: "Casey", operation: "add", idempotencyKey: "0199a2b3-key-0001", startedOn: 1 };
  beforeEach(() => window.sessionStorage.clear());

  it("round-trips the original idempotency key and drops malformed or foreign entries", () => {
    savePending("t1", [op]);
    expect(loadPending("t1")).toEqual([op]);
    expect(loadPending("t2")).toEqual([]);
    window.sessionStorage.setItem("ef.pendingMembership.t1", JSON.stringify([{ ...op, idempotencyKey: "x" }, { ...op, teamId: "other" }, "junk"]));
    expect(loadPending("t1")).toEqual([]);
    savePending("t1", []);
    expect(window.sessionStorage.getItem("ef.pendingMembership.t1")).toBeNull();
  });
});

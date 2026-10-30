import { OperationError } from "@enterpriseforms/app-runtime";
import { beforeEach, describe, expect, it } from "vitest";
import { classifyError, classifyOperation, classifyReadError, classifyResponse, loadPending, nextDelayMs, savePending, type PendingMembershipOperation } from "./membershipOutcome";

const failure = (status: number, errorCode: string) => new OperationError({ type: "failed", operationId: "op", status, errorCode, retryable: false, problem: { title: errorCode, detail: `d ${errorCode}` } });
const operation = (operationState: string, extra: { rejectionReasonCode?: string | null; errorCode?: string | null } = {}) => ({ operationState, rejectionReasonCode: extra.rejectionReasonCode ?? null, errorCode: extra.errorCode ?? null });

describe("membership outcome classification (C8-M1-01, Design R)", () => {
  it("is effective only for a 200 whose server operation the Worker projected from the Authority's confirmed state", () => {
    expect(classifyResponse({ status: 200, result: { operation: operation("Projected") } }).kind).toBe("effective");
    // An Authority-effective command whose projection has not happened yet is still pending: the roster has not changed.
    expect(classifyResponse({ status: 200, result: { operation: operation("AwaitingEffective") } }).kind).toBe("pending");
    expect(classifyResponse({ status: 200, result: { operation: operation("AwaitingCommit") } }).kind).toBe("pending");
    expect(classifyResponse({ status: 202, result: { operation: operation("Projected") } }).kind).toBe("pending");
    expect(classifyResponse({ status: 200 }).kind).toBe("pending");
  });

  it("maps every terminal and attention state of the server operation and never reads an unfinished one as done", () => {
    expect(classifyOperation(operation("Rejected", { rejectionReasonCode: "FORBIDDEN" }))).toEqual({ kind: "rejected", detail: expect.stringContaining("FORBIDDEN") });
    expect(classifyOperation(operation("AuthorityFailed")).kind).toBe("failed");
    expect(classifyOperation(operation("NotCommitted")).kind).toBe("failed");
    expect(classifyOperation(operation("ReconciliationError", { errorCode: "LOCAL_WATERMARK_AHEAD" }))).toEqual({ kind: "attention", detail: expect.stringContaining("LOCAL_WATERMARK_AHEAD") });
    for (const unfinished of ["AwaitingCommit", "AwaitingEffective", "AwaitingQuarantineClearance", "SomethingNew"]) {
      expect(classifyOperation(operation(unfinished)).kind).toBe("pending");
    }
  });

  it("distinguishes rejected, failed and ambiguous send errors and never reads ambiguity as success", () => {
    expect(classifyError(failure(422, "SELF_MEMBERSHIP_FORBIDDEN")).kind).toBe("rejected");
    expect(classifyError(failure(403, "FORBIDDEN")).kind).toBe("rejected");
    expect(classifyError(failure(409, "IDEMPOTENCY_KEY_REUSED")).kind).toBe("rejected");
    expect(classifyError(failure(503, "PROJECTION_DIVERGENCE")).kind).toBe("failed");
    expect(classifyError(failure(409, "OPERATION_NOT_COMMITTED")).kind).toBe("failed");
    expect(classifyError(failure(503, "AUTHORITY_NOT_READY")).kind).toBe("pending");
    expect(classifyError(failure(500, "INTERNAL")).kind).toBe("pending");
    expect(classifyError(new TypeError("network")).kind).toBe("pending");
  });

  it("treats a missing operation as never sent only for this browser's own key; other read failures stay pending", () => {
    expect(classifyReadError(failure(404, "NOT_FOUND"), true).kind).toBe("failed");
    expect(classifyReadError(failure(404, "NOT_FOUND"), false).kind).toBe("pending");
    expect(classifyReadError(failure(503, "AUTHORITY_NOT_READY"), true).kind).toBe("pending");
    expect(classifyReadError(failure(403, "FORBIDDEN"), false).kind).toBe("pending");
    expect(classifyReadError(new TypeError("network"), true).kind).toBe("pending");
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

  it("round-trips the original idempotency key and the server operation id and drops malformed or foreign entries", () => {
    savePending("t1", [op, { ...op, idempotencyKey: "0199a2b3-key-0002", operationId: "0199a2b3-op" }]);
    expect(loadPending("t1")).toEqual([op, { ...op, idempotencyKey: "0199a2b3-key-0002", operationId: "0199a2b3-op" }]);
    expect(loadPending("t2")).toEqual([]);
    window.sessionStorage.setItem("ef.pendingMembership.t1", JSON.stringify([{ ...op, idempotencyKey: "x" }, { ...op, teamId: "other" }, { ...op, operationId: 7 }, "junk"]));
    expect(loadPending("t1")).toEqual([]);
    savePending("t1", []);
    expect(window.sessionStorage.getItem("ef.pendingMembership.t1")).toBeNull();
  });
});

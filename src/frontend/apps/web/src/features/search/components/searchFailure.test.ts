import { OperationError } from "@enterpriseforms/app-runtime";
import { describe, expect, it } from "vitest";
import { presentSearchFailure } from "./searchFailure";

function failure(status: number, errorCode: string, extra: Record<string, unknown> = {}) {
  return new OperationError({ type: "failed", operationId: "op", status, errorCode, retryable: false, problem: { title: errorCode, detail: "condition 2: enter a value", ...extra } });
}

describe("advanced search failure messages", () => {
  it("explains that search is not enabled yet without offering a retry", () => {
    const presented = presentSearchFailure(failure(503, "SEARCH_VERIFICATION_UNAVAILABLE", { unavailable_reason: "TRUSTED_SEARCH_NOT_ENABLED" }));
    expect(presented.title).toBe("Advanced field search is not available yet");
    expect(presented.canRetry).toBe(false);
  });

  it("offers a retry when verification is temporarily unavailable or the snapshot changed", () => {
    expect(presentSearchFailure(failure(503, "SEARCH_VERIFICATION_UNAVAILABLE")).canRetry).toBe(true);
    expect(presentSearchFailure(failure(409, "SEARCH_SNAPSHOT_CHANGED")).canRetry).toBe(true);
  });

  it("explains a busy refusal (conditions kept, retry offered) and an integrity failure (no retry, never 'no records')", () => {
    const busy = presentSearchFailure(failure(503, "SEARCH_VERIFICATION_UNAVAILABLE", { unavailable_reason: "SEARCH_CAPACITY_EXCEEDED" }));
    expect(busy.title).toBe("Search is busy");
    expect(busy.canRetry).toBe(true);
    for (const reason of ["RECORD_QUARANTINED", "RECORD_VERIFICATION_FAILED"]) {
      const integrity = presentSearchFailure(failure(503, "SEARCH_VERIFICATION_UNAVAILABLE", { unavailable_reason: reason }));
      expect(integrity.title).toBe("A record could not be verified");
      expect(integrity.canRetry).toBe(false);
      expect(integrity.message).toContain("does not mean nothing matched");
    }
  });

  it("asks to narrow the search when the verification limit is reached", () => {
    const presented = presentSearchFailure(failure(422, "SEARCH_VERIFICATION_LIMIT", { limit_kind: "records" }));
    expect(presented.message).toContain("Narrow the search");
    expect(presented.canRetry).toBe(false);
  });

  it("never presents a failure as an empty result and never names a hidden field", () => {
    for (const [status, code] of [[503, "SEARCH_VERIFICATION_UNAVAILABLE"], [422, "SEARCH_VERIFICATION_LIMIT"], [409, "SEARCH_SNAPSHOT_CHANGED"], [422, "SEARCH_FIELD_NOT_ALLOWED"], [422, "SEARCH_OPERATOR_NOT_SUPPORTED"], [500, "INTERNAL"]] as const) {
      const presented = presentSearchFailure(failure(status, code));
      expect(presented.title).not.toMatch(/no records/i);
      expect(presented.message).not.toMatch(/no (matching )?records (were )?found/i);
    }
    expect(presentSearchFailure(failure(422, "SEARCH_FIELD_NOT_ALLOWED")).message).toBe("One of the conditions uses a field you cannot search. Choose fields from the list for the selected form and try again.");
  });

  it("shows the server's position-only validation detail", () => {
    expect(presentSearchFailure(failure(422, "SEARCH_REQUEST_INVALID")).message).toBe("Condition 2: enter a value.");
  });
});

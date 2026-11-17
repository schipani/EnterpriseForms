import { describe, expect, it } from "vitest";
import { resultBasisCopy, resultBasisOf } from "./resultBasis";

// IX-37 (indexed search design §6.6): an indexed result is never labelled as verified.
describe("field-search result basis", () => {
  it("reads the engine, treating an absent or unknown value as the verified scan (older servers)", () => {
    expect(resultBasisOf("AuthorityAcceptedContent")).toBe("AuthorityAcceptedContent");
    expect(resultBasisOf("VerifiedScan")).toBe("VerifiedScan");
    expect(resultBasisOf(undefined)).toBe("VerifiedScan");
    expect(resultBasisOf(null)).toBe("VerifiedScan");
    expect(resultBasisOf("something-else")).toBe("VerifiedScan");
  });

  it("never describes an indexed result as verified", () => {
    const indexed = resultBasisCopy("AuthorityAcceptedContent");
    expect(indexed.emptyMessage).not.toMatch(/verif/i);
    expect(indexed.emptyMessage).toContain("last accepted");
  });

  it("keeps the verified-scan wording for the verified engine", () => {
    expect(resultBasisCopy("VerifiedScan").emptyMessage).toBe("The search was complete and verified for the records you can access.");
    expect(resultBasisCopy(undefined).emptyMessage).toBe("The search was complete and verified for the records you can access.");
  });
});

import { describe, expect, it } from "vitest";
import { parseLookupInput, referenceFor } from "./recordList";

describe("Find a record input (Phase C6 quick lookup)", () => {
  const id = "975a62c5-c1db-8456-99ca-c53c017ef770";

  it("accepts a short reference in any case and normalizes it", () => {
    expect(parseLookupInput("REC-017EF770")).toEqual({ kind: "Reference", value: "REC-017EF770" });
    expect(parseLookupInput("  rec-017ef770 ")).toEqual({ kind: "Reference", value: "REC-017EF770" });
  });

  it("accepts a full record id with hyphens, without hyphens or in braces", () => {
    expect(parseLookupInput(id)).toEqual({ kind: "RecordId", value: id });
    expect(parseLookupInput(id.toUpperCase())).toEqual({ kind: "RecordId", value: id });
    expect(parseLookupInput(id.replace(/-/g, ""))).toEqual({ kind: "RecordId", value: id });
    expect(parseLookupInput(`{${id}}`)).toEqual({ kind: "RecordId", value: id });
  });

  it("rejects anything that is not an exact reference or id, so no free-text search is ever sent", () => {
    for (const text of ["", "   ", "REC-017EF77", "REC-017EF7701", "REC-XYZ12345", "017EF770", "Lifecycle case", "975a62c5-c1db-8456-99ca", "00000000-0000-0000-0000-000000000000", "x".repeat(65)]) {
      expect(parseLookupInput(text), text).toBeNull();
    }
  });

  it("derives the same short reference as the server", () => {
    expect(referenceFor(id)).toBe("REC-017EF770");
  });
});

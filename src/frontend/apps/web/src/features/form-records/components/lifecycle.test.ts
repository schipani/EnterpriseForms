import { describe, expect, it } from "vitest";
import { TRANSITIONS } from "./LifecycleActions";
import { parseRecordsSearch } from "./recordList";

describe("record lifecycle actions (Phase C6 M1)", () => {
  it("covers exactly the four server transitions, each with its own confirmation", () => {
    expect(TRANSITIONS.map((copy) => copy.name)).toEqual(["Submit", "Reopen", "Archive", "Unarchive"]);
    expect(TRANSITIONS.map((copy) => copy.transition)).toEqual(["submit", "reopen", "archive", "unarchive"]);
    expect(new Set(TRANSITIONS.map((copy) => copy.title)).size).toBe(4);
    for (const copy of TRANSITIONS) {
      expect(copy.title.endsWith("?")).toBe(true);
      expect(copy.body.length).toBeGreaterThan(20);
      expect(copy.done.length).toBeGreaterThan(0);
    }
  });

  it("requires a reason to reopen, offers one when archiving and asks for none otherwise", () => {
    const reasons = Object.fromEntries(TRANSITIONS.map((copy) => [copy.name, copy.reason ?? "none"]));
    expect(reasons).toEqual({ Submit: "none", Reopen: "required", Archive: "optional", Unarchive: "none" });
  });

  it("states the lock when submitting and that archiving deletes nothing", () => {
    expect(TRANSITIONS.find((copy) => copy.name === "Submit")!.body).toMatch(/locked/);
    expect(TRANSITIONS.find((copy) => copy.name === "Archive")!.body).toMatch(/Nothing is deleted/);
  });
});

describe("My Records archived toggle (Phase C6 M1)", () => {
  it("is a shareable URL flag and anything else is dropped", () => {
    expect(parseRecordsSearch({ archived: true }).archived).toBe(true);
    expect(parseRecordsSearch({ archived: "true" }).archived).toBe(true);
    expect(parseRecordsSearch({ archived: "yes" }).archived).toBeUndefined();
    expect(parseRecordsSearch({}).archived).toBeUndefined();
  });
});

import { describe, expect, it } from "vitest";
import { contrastRatio, defaultTheme, meetsContrast, validateThemePairs } from "./contrast";

describe("WCAG contrast utilities", () => {
  it("computes the canonical black-on-white ratio", () => {
    expect(contrastRatio("#000000", "#ffffff")).toBeCloseTo(21, 5);
    expect(contrastRatio("#ffffff", "#000000")).toBeCloseTo(21, 5);
  });

  it("accepts the approved near-black primary button", () => {
    expect(meetsContrast("#ffffff", "#111111", "normal-text")).toBe(true);
    expect(contrastRatio("#ffffff", "#111111")).toBeGreaterThan(17);
  });

  it("rejects insufficient text contrast", () => {
    expect(meetsContrast("#777777", "#ffffff", "normal-text")).toBe(false);
    expect(meetsContrast("#777777", "#ffffff", "large-text")).toBe(true);
  });

  it("rejects non-opaque or malformed colors", () => {
    expect(() => contrastRatio("#fff", "#000000")).toThrow();
    expect(() => contrastRatio("#11111180", "#000000")).toThrow();
    expect(() => contrastRatio("red", "#000000")).toThrow();
  });

  it("validates the approved default theme with no failures", () => {
    expect(validateThemePairs(defaultTheme)).toEqual([]);
  });

  it("rejects the PB-14 reference divider colour as a form-control boundary", () => {
    const failures = validateThemePairs({ ...defaultTheme, inputBorder: "#cbd5e1" });
    expect(failures.map((f) => f.pair)).toEqual(["input border vs surface", "input border vs page"]);
  });

  it("reports every failing pair for an inaccessible organization theme", () => {
    const failures = validateThemePairs({ ...defaultTheme, primaryBackground: "#ffcc00", primaryForeground: "#ffffff", focus: "#f8fafc" });
    const pairs = failures.map((f) => f.pair);
    expect(pairs).toContain("primary button label");
    expect(pairs).toContain("focus ring vs surface");
    expect(pairs).toContain("focus ring vs page");
    for (const failure of failures) {
      expect(failure.ratio).toBeLessThan(failure.required);
    }
  });
});

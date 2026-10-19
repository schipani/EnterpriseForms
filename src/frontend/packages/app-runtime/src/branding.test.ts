import type { BrandingResponse } from "@enterpriseforms/api-client";
import { describe, expect, it } from "vitest";
import { applyBranding } from "./branding";

const base: BrandingResponse = {
  organizationDisplayName: "Dev Org",
  hasLogo: false,
  logoAlternativeText: null,
  primaryButtonBackgroundColor: "#111111",
  primaryButtonForegroundColor: "#FFFFFF",
  secondaryButtonBackgroundColor: "#FFFFFF",
  secondaryButtonForegroundColor: "#111111",
  secondaryButtonBorderColor: "#111111",
  organizationDefaultTextScalePercent: 100,
  organizationColorMode: "Light",
  brandingRevision: 1,
};

describe("applyBranding (ADR-PB-014 floor)", () => {
  it("applies validated organization colours and the text scale", () => {
    const root = document.createElement("div");
    const outcome = applyBranding({ ...base, primaryButtonBackgroundColor: "#1D4ED8" }, 125, root);
    expect(outcome.applied).toBe(true);
    expect(root.style.getPropertyValue("--ui-primary-bg")).toBe("#1d4ed8");
    expect(root.style.getPropertyValue("--ui-font-scale")).toBe("1.25");
  });

  it("falls back to the approved default when a pair fails contrast", () => {
    const root = document.createElement("div");
    const outcome = applyBranding({ ...base, primaryButtonBackgroundColor: "#FFFF00", primaryButtonForegroundColor: "#FFFFFF" }, 100, root);
    expect(outcome.applied).toBe(false);
    expect(outcome.failures).toContain("primary button label");
    expect(root.style.getPropertyValue("--ui-primary-bg")).toBe("#111111");
  });
});

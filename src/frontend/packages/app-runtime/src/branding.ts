import type { BrandingResponse } from "@enterpriseforms/api-client";
import { defaultTheme, validateThemePairs, type OpaqueHex, type ThemeTokens } from "@enterpriseforms/design-tokens";

/** Maps server branding to the validated token set; unknown tokens keep the approved default (ADR-PB-014). */
export function themeFromBranding(branding: BrandingResponse): ThemeTokens {
  return {
    ...defaultTheme,
    primaryBackground: branding.primaryButtonBackgroundColor.toLowerCase() as OpaqueHex,
    primaryForeground: branding.primaryButtonForegroundColor.toLowerCase() as OpaqueHex,
    secondaryBackground: branding.secondaryButtonBackgroundColor.toLowerCase() as OpaqueHex,
    secondaryForeground: branding.secondaryButtonForegroundColor.toLowerCase() as OpaqueHex,
    secondaryBorder: branding.secondaryButtonBorderColor.toLowerCase() as OpaqueHex,
  };
}

/**
 * Applies branding as CSS custom properties. The server already validated contrast before persisting; the client
 * re-validates and falls back to the approved default rather than painting an inaccessible pair.
 */
export function applyBranding(branding: BrandingResponse | null, textScalePercent: number, root: HTMLElement = document.documentElement): { applied: boolean; failures: string[] } {
  const theme = branding ? themeFromBranding(branding) : defaultTheme;
  const failures = validateThemePairs(theme).map((failure) => failure.pair);
  const effective = failures.length === 0 ? theme : defaultTheme;
  root.style.setProperty("--ui-primary-bg", effective.primaryBackground);
  root.style.setProperty("--ui-primary-fg", effective.primaryForeground);
  root.style.setProperty("--ui-secondary-bg", effective.secondaryBackground);
  root.style.setProperty("--ui-secondary-fg", effective.secondaryForeground);
  root.style.setProperty("--ui-secondary-border", effective.secondaryBorder);
  const scale = Math.min(2, Math.max(1, textScalePercent / 100));
  root.style.setProperty("--ui-font-scale", String(scale));
  return { applied: failures.length === 0, failures };
}

export function resetBranding(root: HTMLElement = document.documentElement): void {
  for (const name of ["--ui-primary-bg", "--ui-primary-fg", "--ui-secondary-bg", "--ui-secondary-fg", "--ui-secondary-border", "--ui-font-scale"]) {
    root.style.removeProperty(name);
  }
}

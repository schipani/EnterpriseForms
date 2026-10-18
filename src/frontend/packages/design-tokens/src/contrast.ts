/**
 * Pure WCAG 2.x contrast utilities (from docs/ui/PB14_COLOR_CONTRAST.reference.ts). Algorithmic validation is one
 * acceptance step only; focus visibility, forced-colors, reflow and human assistive-technology review remain required
 * (ADR-PB-014). This module is not a WCAG/AODA certifier.
 */
export type OpaqueHex = `#${string}`;

export type ContrastKind = "normal-text" | "large-text" | "essential-nontext";

function channelToLinear(value: number): number {
  const normalized = value / 255;
  return normalized <= 0.04045 ? normalized / 12.92 : ((normalized + 0.055) / 1.055) ** 2.4;
}

function rgb(hex: string): [number, number, number] {
  if (!/^#[0-9a-fA-F]{6}$/.test(hex)) {
    throw new Error("Only #RRGGBB opaque colors are permitted");
  }
  return [1, 3, 5].map((offset) => parseInt(hex.slice(offset, offset + 2), 16)) as [number, number, number];
}

export function luminance(hex: string): number {
  const [red, green, blue] = rgb(hex).map(channelToLinear) as [number, number, number];
  return 0.2126 * red + 0.7152 * green + 0.0722 * blue;
}

export function contrastRatio(foregroundHex: string, backgroundHex: string): number {
  const foreground = luminance(foregroundHex);
  const background = luminance(backgroundHex);
  const high = Math.max(foreground, background);
  const low = Math.min(foreground, background);
  return (high + 0.05) / (low + 0.05);
}

export function requiredRatio(kind: ContrastKind): number {
  return kind === "normal-text" ? 4.5 : 3;
}

export function meetsContrast(foreground: string, background: string, kind: ContrastKind): boolean {
  return contrastRatio(foreground, background) + 1e-10 >= requiredRatio(kind);
}

/** Token names an organization theme may override. Anything else is rejected before validation. */
export interface ThemeTokens {
  primaryBackground: OpaqueHex;
  primaryForeground: OpaqueHex;
  secondaryBackground: OpaqueHex;
  secondaryForeground: OpaqueHex;
  secondaryBorder: OpaqueHex;
  page: OpaqueHex;
  surface: OpaqueHex;
  bodyText: OpaqueHex;
  mutedText: OpaqueHex;
  /** Decorative divider; not relied on as a component boundary, so no contrast requirement. */
  border: OpaqueHex;
  /** Boundary of form controls; must reach 3:1 against the surface (WCAG 1.4.11). */
  inputBorder: OpaqueHex;
  focus: OpaqueHex;
}

export const defaultTheme: ThemeTokens = {
  primaryBackground: "#111111",
  primaryForeground: "#ffffff",
  secondaryBackground: "#ffffff",
  secondaryForeground: "#111111",
  secondaryBorder: "#111111",
  page: "#f8fafc",
  surface: "#ffffff",
  bodyText: "#111827",
  mutedText: "#334155",
  border: "#cbd5e1",
  inputBorder: "#64748b",
  focus: "#1d4ed8",
};

export interface ContrastFailure {
  pair: string;
  foreground: string;
  background: string;
  kind: ContrastKind;
  ratio: number;
  required: number;
}

/**
 * Required pairs for any accepted theme. The server performs the same validation before persisting a theme.
 * The focus ring is rendered with a 2px offset (styles.css), so it is evaluated against the surrounding page and
 * surface, not against the button fill. Phase 0 finding: the PB-14 reference `--ui-border` (#cbd5e1) is 1.48:1 on
 * white and cannot be the sole form-control boundary; `inputBorder` was added for that role (design approval pending).
 */
export function validateThemePairs(theme: ThemeTokens): ContrastFailure[] {
  const checks: Array<[string, string, string, ContrastKind]> = [
    ["primary button label", theme.primaryForeground, theme.primaryBackground, "normal-text"],
    ["secondary button label", theme.secondaryForeground, theme.secondaryBackground, "normal-text"],
    ["secondary button border vs surface", theme.secondaryBorder, theme.surface, "essential-nontext"],
    ["body text on page", theme.bodyText, theme.page, "normal-text"],
    ["body text on surface", theme.bodyText, theme.surface, "normal-text"],
    ["muted text on surface", theme.mutedText, theme.surface, "normal-text"],
    ["muted text on page", theme.mutedText, theme.page, "normal-text"],
    ["input border vs surface", theme.inputBorder, theme.surface, "essential-nontext"],
    ["input border vs page", theme.inputBorder, theme.page, "essential-nontext"],
    ["focus ring vs surface", theme.focus, theme.surface, "essential-nontext"],
    ["focus ring vs page", theme.focus, theme.page, "essential-nontext"],
  ];
  const failures: ContrastFailure[] = [];
  for (const [pair, foreground, background, kind] of checks) {
    const ratio = contrastRatio(foreground, background);
    const required = requiredRatio(kind);
    if (ratio + 1e-10 < required) {
      failures.push({ pair, foreground, background, kind, ratio, required });
    }
  }
  return failures;
}

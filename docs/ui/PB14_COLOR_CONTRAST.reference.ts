/** Reference-only pure WCAG contrast utilities; not a complete WCAG/AODA certifier. */
export type OpaqueHex = `#${string}`;

function channelToLinear(value: number): number {
  const normalized = value / 255;
  return normalized <= 0.04045 ? normalized / 12.92 : ((normalized + 0.055) / 1.055) ** 2.4;
}

function rgb(hex: string): [number, number, number] {
  if (!/^#[0-9a-fA-F]{6}$/.test(hex)) throw new Error("Only #RRGGBB opaque colors are permitted");
  return [1, 3, 5].map((offset) => parseInt(hex.slice(offset, offset + 2), 16)) as [number, number, number];
}

export function luminance(hex: string): number {
  const [red, green, blue] = rgb(hex).map(channelToLinear);
  return 0.2126 * red + 0.7152 * green + 0.0722 * blue;
}

export function contrastRatio(foregroundHex: string, backgroundHex: string): number {
  const foreground = luminance(foregroundHex);
  const background = luminance(backgroundHex);
  const high = Math.max(foreground, background);
  const low = Math.min(foreground, background);
  return (high + 0.05) / (low + 0.05);
}

export function meetsContrast(foreground: string, background: string, kind: "normal-text" | "large-text" | "essential-nontext"): boolean {
  const required = kind === "normal-text" ? 4.5 : 3;
  return contrastRatio(foreground, background) + 1e-10 >= required;
}

// Algorithmic validation for each state/pair is only one acceptance step. Validate
// focus visibility, keyboard, forced-colors, contrast against adjoining surfaces,
// disabled behavior, alpha compositing, browser reflow and human accessibility review.
// Avoid allowing user-authored CSS or granting full style property overrides.

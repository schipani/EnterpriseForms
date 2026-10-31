import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { PresenceIndicator } from "./PresenceIndicator";

describe("PresenceIndicator", () => {
  it.each([
    ["Available", "Available"],
    ["Away", "Away"],
    ["Offline", "Offline"],
    ["Unknown", "Presence unknown"],
  ] as const)("shows the text label for %s so colour is never the only signal", (state, label) => {
    const { container } = render(<PresenceIndicator state={state} />);
    expect(screen.getByText(label)).toBeVisible();
    expect(container.querySelector("[data-presence]")).toHaveAttribute("data-presence", state);
    // the coloured dot is decoration for assistive technology
    expect(container.querySelector('[aria-hidden="true"]')).not.toBeNull();
  });

  it("keeps the label for assistive technology and as the tooltip when the visible label is hidden", () => {
    const { container } = render(<PresenceIndicator state="Away" showLabel={false} />);
    expect(screen.getByText("Away")).toHaveClass("sr-only");
    expect(container.querySelector("[data-presence]")).toHaveAttribute("title", "Away");
  });

  it("distinguishes the states by shape as well as colour", () => {
    const dots = (["Available", "Away", "Offline", "Unknown"] as const).map((state) => {
      const { container, unmount } = render(<PresenceIndicator state={state} />);
      const dot = container.querySelector('[aria-hidden="true"]')!.className;
      unmount();
      return dot;
    });
    expect(new Set(dots).size).toBe(4);
    expect(dots[3]).toContain("border-dashed"); // Unknown is a dashed ring, never the plain Offline ring
  });
});

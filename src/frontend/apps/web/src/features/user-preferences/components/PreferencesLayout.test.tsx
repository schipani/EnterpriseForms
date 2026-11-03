import { render, screen, within } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { PREFERENCES_NAVIGATION, PreferencesLayout } from "./PreferencesLayout";

vi.mock("@tanstack/react-router", () => ({ Link: ({ to, children, className }: { to: string; children: React.ReactNode; className?: string }) => <a href={to} className={className}>{children}</a> }));

describe("PreferencesLayout", () => {
  it("offers the three preference areas in a left navigation and renders the page content beside it", () => {
    render(
      <PreferencesLayout>
        <p>content</p>
      </PreferencesLayout>,
    );
    expect(screen.getByRole("heading", { level: 1, name: "Preferences" })).toBeInTheDocument();
    const nav = screen.getByRole("navigation", { name: "Preferences" });
    const links = within(nav).getAllByRole("link");
    expect(links.map((l) => l.textContent)).toEqual(["Regional settings", "Language", "Text size"]);
    expect(links.map((l) => l.getAttribute("href"))).toEqual(["/app/settings/preferences", "/app/settings/preferences/language", "/app/settings/preferences/text-size"]);
    expect(screen.getByText("content")).toBeInTheDocument();
  });

  it("marks only the first area as an exact match so the Text size route does not also light up the first link", () => {
    expect(PREFERENCES_NAVIGATION.map((i) => Boolean(i.exact))).toEqual([true, false, false]);
  });
});

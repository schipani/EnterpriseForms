import { render, screen, within } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { UserLayout } from "./UserLayout";

vi.mock("@tanstack/react-router", () => ({ Link: ({ to, children, className }: { to: string; children: React.ReactNode; className?: string }) => <a href={to} className={className}>{children}</a> }));

const renderLayout = () =>
  render(
    <UserLayout title="My profile">
      <p>content</p>
    </UserLayout>,
  );

describe("UserLayout", () => {
  it("shows the page title, the content and a User navigation with the two groups in order", () => {
    renderLayout();
    expect(screen.getByRole("heading", { level: 1, name: "My profile" })).toBeInTheDocument();
    expect(screen.getByText("content")).toBeInTheDocument();
    const nav = screen.getByRole("navigation", { name: "User" });
    const headings = within(nav).getAllByRole("heading", { level: 2 });
    expect(headings.map((h) => h.textContent)).toEqual(["User", "Experience & Preferences"]);
    const lists = within(nav).getAllByRole("list");
    expect(lists.map((l) => within(l).getAllByRole("link").map((a) => [a.textContent, a.getAttribute("href")]))).toEqual([
      [["My Profile", "/app/user"]],
      [
        ["Regional Settings", "/app/user/regional"],
        ["Language", "/app/user/language"],
        ["Text Size", "/app/user/text-size"],
      ],
    ]);
    expect(lists.map((l) => l.getAttribute("aria-labelledby"))).toEqual(headings.map((h) => h.id));
  });

  it("uses the same sizes as the Organization sidebar (18rem column, 18px group icons, 16px item icons, 44px items) and the Lucide icons asked for", () => {
    const { container } = renderLayout();
    expect(container.querySelector("nav")!.parentElement!.className).toContain("lg:grid-cols-[18rem_minmax(0,1fr)]");
    const nav = screen.getByRole("navigation", { name: "User" });
    const icons = [...nav.querySelectorAll("svg")];
    expect(icons.map((i) => i.getAttribute("width"))).toEqual(["18", "16", "18", "16", "16", "16"]);
    expect(icons.every((i) => i.getAttribute("aria-hidden") === "true" && i.getAttribute("stroke-width") === "2")).toBe(true);
    const names = icons.map((s) => [...s.classList].find((c) => c.startsWith("lucide-") && c !== "lucide")?.replace(/^lucide-/, ""));
    expect(names.slice(1)).toEqual(["user-round", "palette", "globe", "languages", "accessibility"]);
    for (const link of within(nav).getAllByRole("link")) {
      expect(link.className).toContain("min-h-11");
      expect(link.className).toContain("aria-[current=page]:font-semibold"); // the active item is read from aria-current, not a separate state
    }
    expect(nav.querySelectorAll("a span")[0]!.className).toContain("group-aria-[current=page]:border-primary"); // the black underline
  });
});
